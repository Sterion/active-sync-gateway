using System.Net;
using System.Text;
using System.Xml.Linq;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using ActiveSync.Core.State;
using ActiveSync.Protocol.Wbxml;
using Microsoft.Extensions.Options;
using MimeKit;

namespace ActiveSync.Server.Eas.Handlers;

/// <summary>Shared plumbing for SendMail / SmartReply / SmartForward (MS-ASCMD ComposeMail).</summary>
public abstract class ComposeMailHandlerBase(
	FolderService folders,
	IOptions<ActiveSyncOptions> options,
	ILogger logger) : IEasCommandHandler
{
	protected static readonly XNamespace CM = EasNamespaces.ComposeMail;
	protected FolderService Folders => folders;

	public abstract string Command { get; }

	public async Task HandleAsync(EasContext context, CancellationToken ct)
	{
		ComposeRequest? request = await ParseAsync(context);

		if (options.Value.ReadOnly)
		{
			(string to, string subject) = await PeekHeadersAsync(request?.Mime, ct);
			logger.LogInformation("Read-only: rejecting {Command} from {User}: to {To}, subject {Subject}",
				Command, context.Device.UserName, to, subject);
			await WriteErrorAsync(context, "120"); // mail submission failed
			return;
		}

		// 16.x requests may legitimately carry no MIME: SmartForward with Forwardees, or
		// SendMail sourcing a stored draft — BuildOutgoingAsync produces the bytes then.
		if (request is null ||
		    (request.Mime.Length == 0 && request.Forwardees.Count == 0 && request.SourceItemId is null))
		{
			await WriteErrorAsync(context, "103"); // invalid MIME
			return;
		}

		try
		{
			byte[] outgoing = await BuildOutgoingAsync(context, request, ct);
			if (outgoing.Length == 0)
			{
				await WriteErrorAsync(context, "103");
				return;
			}

			await context.Session.Mail.SendAsync(outgoing, ct);
			Core.Observability.GatewayMetrics.RecordMailSent(context.Device.UserName, Command switch
			{
				"SmartReply" => "smart_reply",
				"SmartForward" => "smart_forward",
				_ => "send"
			});
			(string to, string subject) = await PeekHeadersAsync(outgoing, ct);
			logger.LogInformation("{Command} by {User}: to {To}, subject {Subject}",
				Command, context.Device.UserName, to, subject);
			if (request.SaveInSent)
				await context.Session.Mail.SaveToSentAsync(outgoing, ct);
			await MarkSourceAsync(context, request, ct);
			await context.WriteEmptyAsync(); // success = empty 200
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			logger.LogError(ex, "{Command} failed for {User}", Command, context.Device.UserName);
			await WriteErrorAsync(context, "120"); // mail submission failed
		}
	}

	/// <summary>Extracts To/Subject from a MIME blob for log headlines; never throws.</summary>
	private static async Task<(string To, string Subject)> PeekHeadersAsync(byte[]? mime, CancellationToken ct)
	{
		if (mime is not { Length: > 0 })
			return ("?", "?");
		try
		{
			using MemoryStream stream = new(mime);
			MimeMessage message = await MimeMessage.LoadAsync(stream, ct);
			// Client-supplied header text — sanitized so it cannot forge log lines.
			return (LogText.Clean(message.To.ToString(), 128), LogText.Clean(message.Subject, 128));
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			return ("?", "?");
		}
	}

	/// <summary>Transforms the client MIME (e.g. appends the original for SmartForward).</summary>
	protected abstract Task<byte[]> BuildOutgoingAsync(EasContext context, ComposeRequest request, CancellationToken ct);

	/// <summary>Flags the source message (answered/forwarded) after successful submission.</summary>
	protected virtual Task MarkSourceAsync(EasContext context, ComposeRequest request, CancellationToken ct)
	{
		return Task.CompletedTask;
	}

	private async Task<ComposeRequest?> ParseAsync(EasContext context)
	{
		string contentType = context.Http.Request.ContentType ?? "";
		if (contentType.Contains("message/rfc822", StringComparison.OrdinalIgnoreCase))
		{
			// Protocol 12.x: raw MIME body, options in the query string.
			byte[] raw = await context.ReadRawBodyAsync();
			return new ComposeRequest(
				raw,
				context.Parameters.SaveInSent,
				false,
				context.Parameters.CollectionId,
				context.Parameters.ItemId);
		}

		XDocument? doc = await context.ReadRequestAsync();
		if (doc?.Root is null)
			return null;
		XElement? root = doc.Root;
		XElement? mimeElement = root.Element(CM + "Mime");
		byte[] mime = [];
		if (mimeElement is not null)
		{
			if ((string?)mimeElement.Attribute(EasNamespaces.OpaqueAttribute) == "1")
			{
				// A crafted WBXML body can concatenate opaque/string segments into invalid
				// base64 — treat that as malformed MIME (status 103), not an endpoint 500.
				if (!TryDecodeBase64(mimeElement.Value, out byte[]? decoded))
					return null;
				mime = decoded;
			}
			else
			{
				mime = Encoding.UTF8.GetBytes(mimeElement.Value);
			}
		}

		// 16.x SmartForward without a body: recipients come as Forwardees instead of MIME.
		List<(string Name, string Email)> forwardees = root.Element(CM + "Forwardees")?
			.Elements(CM + "Forwardee")
			.Select(f => (f.Element(CM + "Name")?.Value ?? "", f.Element(CM + "Email")?.Value ?? ""))
			.Where(f => f.Item2.Length > 0)
			.ToList() ?? [];

		XElement? source = root.Element(CM + "Source");
		return new ComposeRequest(
			mime,
			root.Element(CM + "SaveInSentItems") is not null,
			root.Element(CM + "ReplaceMime") is not null,
			source?.Element(CM + "FolderId")?.Value,
			source?.Element(CM + "ItemId")?.Value,
			forwardees);
	}

	private static bool TryDecodeBase64(string value, out byte[] decoded)
	{
		try
		{
			decoded = Convert.FromBase64String(value);
			return true;
		}
		catch (FormatException)
		{
			decoded = [];
			return false;
		}
	}

	private async Task WriteErrorAsync(EasContext context, string status)
	{
		await context.WriteResponseAsync(new XDocument(
			new XElement(CM + Command, new XElement(CM + "Status", status))));
	}

	protected async Task<(string FolderBackendKey, string ItemKey)?> ResolveSourceAsync(
		EasContext context, ComposeRequest request, CancellationToken ct)
	{
		if (request.SourceFolderId is null || request.SourceItemId is null)
			return null;
		(UserFolder Folder, IContentStore Store)? resolved = await Folders.ResolveCollectionAsync(
			context.Session, context.Device.UserName, request.SourceFolderId, ct);
		if (resolved is null)
			return null;
		string? itemKey = await Folders.ResolveItemKeyAsync(
			resolved.Value.Folder, resolved.Value.Store, request.SourceItemId, ct);
		return itemKey is null ? null : (resolved.Value.Folder.BackendKey, itemKey);
	}

	protected sealed record ComposeRequest(
		byte[] Mime,
		bool SaveInSent,
		bool ReplaceMime,
		string? SourceFolderId,
		string? SourceItemId,
		IReadOnlyList<(string Name, string Email)> Forwardees)
	{
		public ComposeRequest(
			byte[] mime, bool saveInSent, bool replaceMime, string? sourceFolderId, string? sourceItemId)
			: this(mime, saveInSent, replaceMime, sourceFolderId, sourceItemId, [])
		{
		}
	}
}

public sealed class SendMailHandler(
	FolderService folders,
	IOptions<ActiveSyncOptions> options,
	ILogger<SendMailHandler> logger)
	: ComposeMailHandlerBase(folders, options, logger)
{
	public override string Command => "SendMail";

	protected override async Task<byte[]> BuildOutgoingAsync(
		EasContext context, ComposeRequest request, CancellationToken ct)
	{
		if (request.Mime.Length > 0)
			return request.Mime;

		// 16.x: SendMail without MIME submits a stored draft (Source > FolderId/ItemId).
		(string FolderBackendKey, string ItemKey)? source = await ResolveSourceAsync(context, request, ct);
		if (source is null)
			return [];
		return await context.Session.Mail.GetRawMessageAsync(
			source.Value.FolderBackendKey, source.Value.ItemKey, ct) ?? [];
	}

	protected override async Task MarkSourceAsync(EasContext context, ComposeRequest request, CancellationToken ct)
	{
		// A draft that was submitted by reference is consumed by the send.
		if (request.Mime.Length > 0 || request.SourceFolderId is null || request.SourceItemId is null)
			return;
		(UserFolder Folder, IContentStore Store)? resolved = await Folders.ResolveCollectionAsync(
			context.Session, context.Device.UserName, request.SourceFolderId, ct);
		if (resolved is null)
			return;
		string? itemKey = await Folders.ResolveItemKeyAsync(
			resolved.Value.Folder, resolved.Value.Store, request.SourceItemId, ct);
		if (itemKey is not null)
			await resolved.Value.Store.DeleteItemAsync(resolved.Value.Folder.BackendKey, itemKey, ct, true);
	}
}

public sealed class SmartReplyHandler(
	FolderService folders,
	IOptions<ActiveSyncOptions> options,
	ILogger<SmartReplyHandler> logger)
	: ComposeMailHandlerBase(folders, options, logger)
{
	public override string Command => "SmartReply";

	protected override async Task<byte[]> BuildOutgoingAsync(
		EasContext context, ComposeRequest request, CancellationToken ct)
	{
		if (request.ReplaceMime)
			return request.Mime;
		(string FolderBackendKey, string ItemKey)? source = await ResolveSourceAsync(context, request, ct);
		if (source is null)
			return request.Mime;
		byte[]? original = await context.Session.Mail.GetRawMessageAsync(source.Value.FolderBackendKey,
			source.Value.ItemKey, ct);
		if (original is null)
			return request.Mime;

		using MemoryStream clientStream = new(request.Mime);
		MimeMessage message = await MimeMessage.LoadAsync(clientStream, ct);
		using MemoryStream originalStream = new(original);
		MimeMessage originalMessage = await MimeMessage.LoadAsync(originalStream, ct);

		string quoted = BuildQuote(originalMessage);
		TextPart? textBody = message.BodyParts.OfType<TextPart>().FirstOrDefault(p => p.IsPlain);
		TextPart? htmlBody = message.BodyParts.OfType<TextPart>().FirstOrDefault(p => p.IsHtml);
		if (textBody is not null)
			textBody.Text = textBody.Text + "\r\n\r\n" + quoted;
		if (htmlBody is not null)
		{
			string encoded = WebUtility.HtmlEncode(quoted).Replace("\r\n", "<br/>");
			htmlBody.Text = htmlBody.Text + "<br/><br/>" + encoded;
		}

		if (textBody is null && htmlBody is null)
		{
			MimeEntity? body = message.Body;
			Multipart multipart = new("mixed") { new TextPart("plain") { Text = quoted } };
			if (body is not null)
				multipart.Insert(0, body);
			message.Body = multipart;
		}

		using MemoryStream output = new();
		await message.WriteToAsync(output, ct);
		return output.ToArray();
	}

	private static string BuildQuote(MimeMessage original)
	{
		StringBuilder sb = new();
		sb.AppendLine("-----Original Message-----");
		sb.AppendLine($"From: {original.From}");
		sb.AppendLine($"Sent: {original.Date:R}");
		sb.AppendLine($"To: {original.To}");
		sb.AppendLine($"Subject: {original.Subject}");
		sb.AppendLine();
		sb.AppendLine(original.TextBody ?? "");
		return sb.ToString();
	}

	protected override async Task MarkSourceAsync(EasContext context, ComposeRequest request, CancellationToken ct)
	{
		(string FolderBackendKey, string ItemKey)? source = await ResolveSourceAsync(context, request, ct);
		if (source is not null)
			await context.Session.Mail.SetAnsweredAsync(
				source.Value.FolderBackendKey, source.Value.ItemKey, false, ct);
	}
}

public sealed class SmartForwardHandler(
	FolderService folders,
	IOptions<ActiveSyncOptions> options,
	ILogger<SmartForwardHandler> logger)
	: ComposeMailHandlerBase(folders, options, logger)
{
	public override string Command => "SmartForward";

	protected override async Task<byte[]> BuildOutgoingAsync(
		EasContext context, ComposeRequest request, CancellationToken ct)
	{
		if (request.ReplaceMime)
			return request.Mime;
		(string FolderBackendKey, string ItemKey)? source = await ResolveSourceAsync(context, request, ct);
		if (source is null)
			return request.Mime;
		byte[]? original = await context.Session.Mail.GetRawMessageAsync(
			source.Value.FolderBackendKey, source.Value.ItemKey, ct);
		if (original is null)
			return request.Mime;

		// 16.x body-less forward: the server composes the whole message to the Forwardees.
		if (request.Mime.Length == 0 && request.Forwardees.Count > 0)
		{
			using MemoryStream sourceStream = new(original);
			MimeMessage forwarded = await MimeMessage.LoadAsync(sourceStream, ct);
			MimeMessage envelope = new();
			if (context.Session.MailAddress is { } fromAddress)
				envelope.From.Add(MailboxAddress.Parse(fromAddress));
			foreach ((string name, string email) in request.Forwardees)
				envelope.To.Add(new MailboxAddress(name, email));
			envelope.Subject = forwarded.Subject?.StartsWith("FW:", StringComparison.OrdinalIgnoreCase) == true
				? forwarded.Subject
				: $"FW: {forwarded.Subject}";
			Multipart forwardBody = new("mixed")
			{
				new TextPart("plain") { Text = "" },
				new MessagePart
				{
					Message = forwarded,
					ContentDisposition = new ContentDisposition(ContentDisposition.Attachment)
					{
						FileName = (forwarded.Subject ?? "forwarded") + ".eml"
					}
				}
			};
			envelope.Body = forwardBody;
			using MemoryStream forwardOut = new();
			await envelope.WriteToAsync(forwardOut, ct);
			return forwardOut.ToArray();
		}

		using MemoryStream clientStream = new(request.Mime);
		MimeMessage message = await MimeMessage.LoadAsync(clientStream, ct);
		using MemoryStream originalStream = new(original);
		MimeMessage originalMessage = await MimeMessage.LoadAsync(originalStream, ct);

		MessagePart attachment = new() { Message = originalMessage };
		attachment.ContentDisposition = new ContentDisposition(ContentDisposition.Attachment)
		{
			FileName = (originalMessage.Subject ?? "forwarded") + ".eml"
		};
		if (message.Body is Multipart { ContentType.MediaSubtype: "mixed" } mixed)
		{
			mixed.Add(attachment);
		}
		else
		{
			Multipart multipart = new("mixed");
			if (message.Body is not null)
				multipart.Add(message.Body);
			multipart.Add(attachment);
			message.Body = multipart;
		}

		using MemoryStream output = new();
		await message.WriteToAsync(output, ct);
		return output.ToArray();
	}

	protected override async Task MarkSourceAsync(EasContext context, ComposeRequest request, CancellationToken ct)
	{
		(string FolderBackendKey, string ItemKey)? source = await ResolveSourceAsync(context, request, ct);
		if (source is not null)
			await context.Session.Mail.SetAnsweredAsync(
				source.Value.FolderBackendKey, source.Value.ItemKey, true, ct);
	}
}

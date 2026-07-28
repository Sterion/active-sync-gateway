using System.Net;
using System.Text;
using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using ActiveSync.Core.State;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;
using Microsoft.Extensions.Options;
using MimeKit;

namespace ActiveSync.Server.Eas.Handlers;

/// <summary>Shared plumbing for SendMail / SmartReply / SmartForward (MS-ASCMD ComposeMail).</summary>
public abstract class ComposeMailHandlerBase(
	FolderService folders,
	IOptionsSnapshot<ActiveSyncOptions> options,
	ILogger logger) : IEasCommandHandler
{
	protected static readonly XNamespace CM = EasNamespaces.ComposeMail;
	protected FolderService Folders => folders;
	// F12: exposed so a derived handler's post-send logic (SendMailHandler.MarkSourceAsync) can
	// consult the read-only grant without also capturing its OWN copy of the primary constructor
	// parameter (which would trigger CS9107 — captured by both this type and the base class).
	protected IOptionsSnapshot<ActiveSyncOptions> Options => options;

	public abstract string Command { get; }

	public async Task HandleAsync(EasContext context, CancellationToken ct)
	{
		ComposeRequest? request = await ParseAsync(context);

		if (options.Value.ReadOnly)
		{
			(string to, string subject) = await PeekHeadersAsync(request?.Mime, ct);
			logger.LogInformation("Read-only: rejecting {Command} from {User}: to {To}, subject {Subject}",
				Command, context.UserName, to, subject);
			await WriteErrorAsync(context, "120", request); // mail submission failed
			return;
		}

		if (request is null)
		{
			await WriteErrorAsync(context, "103", request); // invalid XML: the request itself did not parse
			return;
		}

		// 16.x requests may legitimately carry no MIME: SmartForward with Forwardees, or
		// SendMail sourcing a stored draft — BuildOutgoingAsync produces the bytes then. A request
		// that decoded fine but carries none of the three is not an XML problem — it is an
		// empty/invalid MIME submission (F19: MS-ASCMD common status 107, not 103).
		if (request.Mime.Length == 0 && request.Forwardees.Count == 0 && request.SourceItemId is null)
		{
			await WriteErrorAsync(context, "107", request); // invalid MIME
			return;
		}

		// Building the outgoing bytes and the submit itself are the only steps whose failure means
		// the mail did NOT go out — they alone map to Status 120. Everything after the submit is
		// best-effort: once the mail is accepted, reporting a failure would make the client resend
		// and the recipient receive it twice (F30).
		byte[]? outgoing;
		try
		{
			outgoing = await BuildOutgoingAsync(context, request, ct);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			logger.LogError(ex, "{Command}: building the outgoing message failed for {User}",
				Command, context.UserName);
			await WriteErrorAsync(context, "120", request); // mail submission failed
			return;
		}

		if (outgoing is null)
		{
			// The client referenced a source item (reply-to / forward-of) that no longer resolves —
			// a stale ServerId, a moved or re-listed item. Sending the typed text alone would
			// silently drop the quote/forwarded message, so fail the command (F29).
			logger.LogWarning(
				"{Command} for {User}: the referenced source item could not be resolved; not sending a degraded message",
				Command, context.UserName);
			await WriteErrorAsync(context, "150", request); // MS-ASCMD: the referenced original item was not found
			return;
		}

		if (outgoing.Length == 0)
		{
			await WriteErrorAsync(context, "107", request); // F19: empty MIME, not an XML problem
			return;
		}

		// F1: MS-ASCMD makes ClientId a required child of SendMail/SmartForward/SmartReply
		// precisely so a lost 200 (Wi-Fi→LTE handover, a proxy timeout, the gateway restarting
		// between SendAsync and the response write) can be retried without duplicating the mail —
		// this is the same durable claim-before/mark-after shape the Sync draft-submit path already
		// uses (TryClaimSendAsync/MarkSendCompletedAsync). "compose" is a fixed collection namespace
		// (this path has no Sync collection/SyncKey of its own) and 0 a fixed generation, so the key
		// is just (device, Command, ClientId). A request with no ClientId (the 12.x raw form, which
		// carries dedup information nowhere) always falls through to a real send.
		string? clientId = request.ClientId;
		if (clientId is { Length: > 0 } &&
		    await context.State.TryClaimSendAsync(
			    context.Device, "compose", 0, $"{Command}:{clientId}", ct) == SendClaimOutcome.AlreadySent)
		{
			await context.WriteEmptyAsync(); // already sent on a prior attempt — acknowledge, don't resend
			return;
		}

		try
		{
			await context.Session.MailSubmit.SendAsync(outgoing, ct);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			logger.LogError(ex, "{Command} failed for {User}", Command, context.UserName);
			await WriteErrorAsync(context, "120", request); // mail submission failed
			return;
		}

		if (clientId is { Length: > 0 })
			await context.State.MarkSendCompletedAsync(context.Device, "compose", 0, $"{Command}:{clientId}", ct);

		Core.Observability.GatewayMetrics.RecordMailSent(context.UserName, Command switch
		{
			"SmartReply" => "smart_reply",
			"SmartForward" => "smart_forward",
			_ => "send"
		});

		// Past the submit the mail is out. Filing to Sent and flagging the source are best-effort;
		// a failure (including a cancellation of the now-pointless follow-up) must NOT turn a sent
		// message into a reported failure. Swallow everything and always return the success 200.
		try
		{
			(string to, string subject) = await PeekHeadersAsync(outgoing, ct);
			logger.LogInformation("{Command} by {User}: to {To}, subject {Subject}",
				Command, context.UserName, to, subject);
			if (request.SaveInSent)
				await context.Session.MailStore.SaveToSentAsync(outgoing, ct);
			await MarkSourceAsync(context, request, ct);
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex,
				"{Command} sent for {User} but a post-submit step (file to Sent / flag source) failed",
				Command, context.UserName);
		}

		await context.WriteEmptyAsync(); // success = empty 200
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

	/// <summary>
	///   Transforms the client MIME (e.g. appends the original for SmartForward). Returns
	///   <c>null</c> when the request REFERENCED a source item (SmartReply/SmartForward) that
	///   could not be resolved — the caller then fails the command rather than send a degraded
	///   message (a reply with no quote, a forward with nothing forwarded).
	/// </summary>
	protected abstract Task<byte[]?> BuildOutgoingAsync(EasContext context, ComposeRequest request, CancellationToken ct);

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
				context.Parameters.ItemId,
				[],
				// 12.x carries no ClientId anywhere (options ride the query string) — the record's
				// ClientId stays null, so the F1 dedup guard above always falls through to a real send.
				null,
				IsRawForm: true);
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
			forwardees,
			root.Element(CM + "ClientId")?.Value);
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

	private async Task WriteErrorAsync(EasContext context, string status, ComposeRequest? request)
	{
		// F26: the 12.x raw message/rfc822 form has no ComposeMail WBXML response shape at all —
		// MS-ASHTTP defines success as an empty 200 and failures as HTTP status codes for that form.
		// A null request (the request itself failed to parse) never reaches here as a raw form —
		// ParseAsync's 12.x branch always returns a non-null ComposeRequest.
		if (request?.IsRawForm == true)
		{
			context.Http.Response.StatusCode = status switch
			{
				"103" or "107" => StatusCodes.Status400BadRequest, // malformed / empty MIME
				"150" => StatusCodes.Status404NotFound, // referenced source item not found
				_ => StatusCodes.Status500InternalServerError // mail submission failed, etc.
			};
			return;
		}

		await context.WriteResponseAsync(new XDocument(
			new XElement(CM + Command, new XElement(CM + "Status", status))));
	}

	// F4: BuildOutgoingAsync resolves the referenced source to build the quote/attachment, then
	// MarkSourceAsync resolves the SAME source again to flag it (answered/forwarded) — a second
	// DB round trip (and, for a DAV store, a second backend lookup) per send for no reason: the
	// source never changes within one HandleAsync call. Cache the outcome (including a "not
	// found" miss) so the second call is free. The cached shape carries the resolved UserFolder +
	// IContentStore (not just the backend-key string) because SendMailHandler.MarkSourceAsync (F29)
	// needs the folder's Type (Drafts check) and the store itself (DeleteItemAsync) — a plain
	// (string, string) tuple could not serve that caller too.
	private bool _sourceResolveAttempted;
	private (UserFolder Folder, IContentStore Store, string ItemKey)? _resolvedSource;

	protected async Task<(UserFolder Folder, IContentStore Store, string ItemKey)?> ResolveSourceAsync(
		EasContext context, ComposeRequest request, CancellationToken ct)
	{
		if (_sourceResolveAttempted)
			return _resolvedSource;
		_sourceResolveAttempted = true;

		if (request.SourceFolderId is null || request.SourceItemId is null)
			return _resolvedSource = null;
		(UserFolder Folder, IContentStore Store)? resolved = await Folders.ResolveCollectionAsync(
			context.Session, context.UserId, request.SourceFolderId, ct);
		if (resolved is null)
			return _resolvedSource = null;
		string? itemKey = await Folders.ResolveItemKeyAsync(
			resolved.Value.Folder, resolved.Value.Store, request.SourceItemId, ct);
		return _resolvedSource = itemKey is null ? null : (resolved.Value.Folder, resolved.Value.Store, itemKey);
	}

	protected sealed record ComposeRequest(
		byte[] Mime,
		bool SaveInSent,
		bool ReplaceMime,
		string? SourceFolderId,
		string? SourceItemId,
		IReadOnlyList<(string Name, string Email)> Forwardees,
		// F1: null on the 12.x raw wire form (which carries no ClientId anywhere) — the dedup guard
		// in HandleAsync always falls through to a real send in that case.
		string? ClientId = null,
		// F26: true for the 12.x raw message/rfc822 form. MS-ASHTTP defines that form's errors as
		// HTTP status codes with no body — WriteErrorAsync must not write a 14.x/16.x ComposeMail
		// WBXML response for it.
		bool IsRawForm = false)
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
	IOptionsSnapshot<ActiveSyncOptions> options,
	ILogger<SendMailHandler> logger)
	: ComposeMailHandlerBase(folders, options, logger)
{
	public override string Command => "SendMail";

	protected override async Task<byte[]?> BuildOutgoingAsync(
		EasContext context, ComposeRequest request, CancellationToken ct)
	{
		if (request.Mime.Length > 0)
			return request.Mime;

		// 16.x: SendMail without MIME submits a stored draft (Source > FolderId/ItemId). An
		// unresolvable draft yields empty bytes → Status 107 (F19; already a clean failure, never a
		// degraded send), so this path does not need the source-not-found sentinel.
		(UserFolder Folder, IContentStore Store, string ItemKey)? source =
			await ResolveSourceAsync(context, request, ct);
		if (source is null)
			return [];
		return await context.Session.MailStore.GetRawMessageAsync(
			source.Value.Folder.BackendKey, source.Value.ItemKey, ct) ?? [];
	}

	protected override async Task MarkSourceAsync(EasContext context, ComposeRequest request, CancellationToken ct)
	{
		// A draft that was submitted by reference is consumed by the send.
		if (request.Mime.Length > 0 || request.SourceFolderId is null || request.SourceItemId is null)
			return;
		// F29: reuse BuildOutgoingAsync's resolution (which already ran for this same request when
		// Mime is empty, the only way this method is reached) instead of resolving the source again.
		(UserFolder Folder, IContentStore Store, string ItemKey)? source =
			await ResolveSourceAsync(context, request, ct);
		if (source is null)
			return;
		// F12: nothing else enforces the 16.x "submit a stored draft" flow's assumption that Source
		// names a draft — a client (or a bug) pointing SendMail at an ordinary message would
		// otherwise get it re-sent AND hard-deleted with no tombstone and no Trash copy. Only a
		// genuine Drafts item may be consumed by the send; anything else is left untouched. Also
		// honour the per-folder write grant the same way every other mutating handler does — the
		// global ReadOnly check above the send only covers the SEND, not this post-send delete.
		if (source.Value.Folder.Type != EasFolderType.Drafts ||
		    WritePermission.IsBlocked(context, Options.Value, source.Value.Folder))
			return;
		await source.Value.Store.DeleteItemAsync(source.Value.Folder.BackendKey, source.Value.ItemKey, true, ct);
	}
}

public sealed class SmartReplyHandler(
	FolderService folders,
	IOptionsSnapshot<ActiveSyncOptions> options,
	ILogger<SmartReplyHandler> logger)
	: ComposeMailHandlerBase(folders, options, logger)
{
	public override string Command => "SmartReply";

	protected override async Task<byte[]?> BuildOutgoingAsync(
		EasContext context, ComposeRequest request, CancellationToken ct)
	{
		if (request.ReplaceMime)
			return request.Mime;
		// No source referenced: send the client's MIME as-is (nothing to quote).
		if (request.SourceFolderId is null || request.SourceItemId is null)
			return request.Mime;
		// A source WAS referenced but could not be resolved / fetched — fail rather than send a
		// reply with the quoted original silently missing (F29).
		(UserFolder Folder, IContentStore Store, string ItemKey)? source =
			await ResolveSourceAsync(context, request, ct);
		if (source is null)
			return null;
		byte[]? original = await context.Session.MailStore.GetRawMessageAsync(source.Value.Folder.BackendKey,
			source.Value.ItemKey, ct);
		if (original is null)
			return null;

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
		(UserFolder Folder, IContentStore Store, string ItemKey)? source =
			await ResolveSourceAsync(context, request, ct);
		// F25: the global ReadOnly check above only covers the SEND — this post-send write into the
		// source folder must honour a per-folder read-only/share grant too, the same way
		// SendMailHandler's post-send delete already does.
		if (source is not null && !WritePermission.IsBlocked(context, Options.Value, source.Value.Folder.BackendKey))
			await context.Session.MailStore.SetAnsweredAsync(
				source.Value.Folder.BackendKey, source.Value.ItemKey, forwarded: false, ct);
	}
}

public sealed class SmartForwardHandler(
	FolderService folders,
	IOptionsSnapshot<ActiveSyncOptions> options,
	ILogger<SmartForwardHandler> logger)
	: ComposeMailHandlerBase(folders, options, logger)
{
	public override string Command => "SmartForward";

	protected override async Task<byte[]?> BuildOutgoingAsync(
		EasContext context, ComposeRequest request, CancellationToken ct)
	{
		if (request.ReplaceMime)
			return request.Mime;
		// No source referenced: send the client's MIME as-is (nothing to forward).
		if (request.SourceFolderId is null || request.SourceItemId is null)
			return request.Mime;
		// A source WAS referenced but could not be resolved / fetched — fail rather than forward a
		// message with the forwarded content silently missing (F29).
		(UserFolder Folder, IContentStore Store, string ItemKey)? source =
			await ResolveSourceAsync(context, request, ct);
		if (source is null)
			return null;
		byte[]? original = await context.Session.MailStore.GetRawMessageAsync(
			source.Value.Folder.BackendKey, source.Value.ItemKey, ct);
		if (original is null)
			return null;

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
		(UserFolder Folder, IContentStore Store, string ItemKey)? source =
			await ResolveSourceAsync(context, request, ct);
		// F25: see SmartReplyHandler.MarkSourceAsync — the same per-folder grant must gate this
		// post-send write, not just the global ReadOnly flag the send itself already checked.
		if (source is not null && !WritePermission.IsBlocked(context, Options.Value, source.Value.Folder.BackendKey))
			await context.Session.MailStore.SetAnsweredAsync(
				source.Value.Folder.BackendKey, source.Value.ItemKey, forwarded: true, ct);
	}
}

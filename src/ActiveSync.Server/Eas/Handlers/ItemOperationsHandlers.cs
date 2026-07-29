using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Core.Options;
using ActiveSync.Core.State;
using ActiveSync.Eas.Conversion;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;
using ActiveSync.Server.Eas.Content;
using Microsoft.Extensions.Options;
using MimeKit;

namespace ActiveSync.Server.Eas.Handlers;

public sealed class ItemOperationsHandler(
	FolderService folders,
	IOptionsSnapshot<ActiveSyncOptions> options,
	ILogger<ItemOperationsHandler> logger) : IEasCommandHandler
{
	private static readonly XNamespace IO = EasNamespaces.ItemOperations;
	private static readonly XNamespace AS = EasNamespaces.AirSync;
	private static readonly XNamespace ASB = EasNamespaces.AirSyncBase;

	public string Command => "ItemOperations";

	public async Task HandleAsync(EasContext context, CancellationToken ct)
	{
		XDocument? request = await context.ReadRequestAsync();
		if (request?.Root is null)
		{
			context.Http.Response.StatusCode = StatusCodes.Status400BadRequest;
			return;
		}

		List<XElement> responseChildren = new();
		foreach (XElement operation in request.Root.Elements())
			switch (operation.Name.LocalName)
			{
				case "Fetch":
					responseChildren.Add(await HandleFetchAsync(context, operation, ct));
					break;
				case "EmptyFolderContents":
					responseChildren.Add(await HandleEmptyFolderAsync(context, operation, ct));
					break;
				default:
					responseChildren.Add(new XElement(IO + operation.Name.LocalName,
						new XElement(IO + "Status", "2"))); // protocol error
					break;
			}

		await context.WriteResponseAsync(new XDocument(
			new XElement(IO + "ItemOperations",
				new XElement(IO + "Status", "1"),
				new XElement(IO + "Response", responseChildren))));
	}

	private async Task<XElement> HandleFetchAsync(EasContext context, XElement fetch, CancellationToken ct)
	{
		// A malformed reference or a backend hiccup must fail this one Fetch (Status 6),
		// not turn the whole ItemOperations request into an HTTP 500.
		try
		{
			return await FetchCoreAsync(context, fetch, ct);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			logger.LogWarning(ex, "ItemOperations Fetch failed");
			return new XElement(IO + "Fetch", new XElement(IO + "Status", "6"));
		}
	}

	private async Task<XElement> FetchCoreAsync(EasContext context, XElement fetch, CancellationToken ct)
	{
		string? fileReference = fetch.Element(ASB + "FileReference")?.Value;
		string? collectionId = fetch.Element(AS + "CollectionId")?.Value;
		string? serverId = fetch.Element(AS + "ServerId")?.Value;
		string? longId = fetch.Element(IO + "LongId")?.Value ?? fetch.Element(EasNamespaces.Search + "LongId")?.Value;

		XElement Failure(string status)
		{
			return new XElement(IO + "Fetch", new XElement(IO + "Status", status));
		}

		if (fileReference is not null)
		{
			// "calatt::<serverId>::<index>" = inline calendar-event attachment (16.x);
			// everything else is a mail attachment reference.
			bool isCalendarAttachment =
				fileReference.StartsWith(CalendarConverter.AttachmentReferencePrefix, StringComparison.Ordinal);
			// A mail FileReference is client-supplied and names a backend folder directly, just
			// like LongId below — its shape alone is not a membership test. Gate it through the
			// same per-user folder registry before asking the backend.
			if (!isCalendarAttachment && !await IsAttachmentFolderRegisteredAsync(context, fileReference, ct))
				return Failure("6");
			BackendAttachment? attachment = isCalendarAttachment
				? await FetchCalendarAttachmentAsync(context, fileReference, ct)
				: await FetchMailAttachmentAsync(context, fileReference, ct);
			if (attachment is null)
				return Failure("6");
			XElement data = new(IO + "Data", Convert.ToBase64String(attachment.Content.Span));
			data.SetAttributeValue(EasNamespaces.OpaqueAttribute, "1");
			return new XElement(IO + "Fetch",
				new XElement(IO + "Status", "1"),
				new XElement(ASB + "FileReference", fileReference),
				new XElement(IO + "Properties",
					new XElement(ASB + "ContentType", attachment.ContentType),
					data));
		}

		// LongId from Search results: DelimitedKey.Encode(folderBackendKey, itemKey)
		if (longId is not null)
		{
			string[]? parts = DelimitedKey.Decode(longId, 2);
			if (parts is null)
				return Failure("2");
			// The LongId is client-supplied and names a backend key directly, so the store's
			// own OwnsKey() is only a shape test — it says "imap:..." is mine, not
			// "this folder is yours". The per-user folder registry is what says that, and it
			// is the same gate CollectionId Fetch goes through.
			List<UserFolder> registry = await context.State.GetFoldersAsync(context.UserId, ct);
			if (!registry.Any(f => string.Equals(f.BackendKey, parts[0], StringComparison.Ordinal)))
			{
				logger.LogWarning("ItemOperations Fetch refused: LongId names folder {BackendKey}, " +
				                  "which is not in {User}'s registry",
					LogText.Clean(parts[0], 128), context.UserName);
				return Failure("6");
			}

			IContentStore? searchStore = context.Session.GetStoreForKey(new FolderKey(parts[0]));
			if (searchStore is null)
				return Failure("6");
			ContentAdapter adapter = ContentAdapter.For(context.Session, searchStore, options.Value.Eas);
			BodyPreference longIdPreference = ParseBodyPreference(
				fetch.Element(IO + "Options"), context.Version >= EasVersion.V160);
			object? found = await adapter.GetItemAsync(parts[0], parts[1], ct);
			List<XElement>? rendered = found is null
				? null
				: adapter.Render(found, longIdPreference, parts[0], parts[1]);
			if (rendered is null)
				return Failure("6");
			return new XElement(IO + "Fetch",
				new XElement(IO + "Status", "1"),
				new XElement(IO + "LongId", longId),
				new XElement(AS + "Class", adapter.EasClass),
				new XElement(IO + "Properties", rendered));
		}

		if (collectionId is null || serverId is null)
			return Failure("2");

		(UserFolder Folder, ContentAdapter Store)? resolved = await folders.ResolveCollectionAsync(
			context.Session, context.UserId, collectionId, ct);
		if (resolved is null)
			return Failure("6");
		(UserFolder folder, ContentAdapter store) = resolved.Value;
		string? itemKey = await folders.ResolveItemKeyAsync(folder, serverId, ct);
		if (itemKey is null)
			return Failure("6");

		BodyPreference bodyPreference = ParseBodyPreference(
			fetch.Element(IO + "Options"), context.Version >= EasVersion.V160);
		object? item = await store.GetItemAsync(folder.BackendKey, itemKey, ct);
		List<XElement>? properties = item is null
			? null
			: store.Render(item, bodyPreference, folder.BackendKey, itemKey);
		if (properties is null)
			return Failure("6");

		return new XElement(IO + "Fetch",
			new XElement(IO + "Status", "1"),
			new XElement(AS + "CollectionId", collectionId),
			new XElement(AS + "ServerId", serverId),
			new XElement(AS + "Class", store.EasClass),
			new XElement(IO + "Properties", properties));
	}

	/// <summary>Resolves "calatt::&lt;serverId&gt;::&lt;index&gt;" to inline event-attachment bytes.</summary>
	private async Task<BackendAttachment?> FetchCalendarAttachmentAsync(
		EasContext context, string fileReference, CancellationToken ct)
	{
		string tail = fileReference[CalendarConverter.AttachmentReferencePrefix.Length..];
		int lastSeparator = tail.LastIndexOf("::", StringComparison.Ordinal);
		if (lastSeparator <= 0 || !int.TryParse(tail[(lastSeparator + 2)..], out int index))
			return null;
		string serverId = tail[..lastSeparator];
		int colon = serverId.IndexOf(':');
		if (colon <= 0)
			return null;

		(UserFolder Folder, ContentAdapter Store)? resolved = await folders.ResolveCollectionAsync(
			context.Session, context.UserId, serverId[..colon], ct);
		if (resolved is null || resolved.Value.Store.Store is not ICalendarAttachmentSource source)
			return null;
		string? itemKey = await folders.ResolveItemKeyAsync(resolved.Value.Folder, serverId, ct);
		if (itemKey is null)
			return null;
		return await source.GetEventAttachmentAsync(
			new FolderKey(resolved.Value.Folder.BackendKey), new ItemKey(itemKey), index, ct);
	}

	/// <summary>
	///   Resolves a mail-attachment FileReference host-side: fetch the raw message via the
	///   mailbox operations and extract the Nth attachment with MimeKit. The FileReference and
	///   its index semantics ("position in MimeMessage.Attachments") are host knowledge — a store
	///   never sees either.
	/// </summary>
	internal static async Task<BackendAttachment?> FetchMailAttachmentAsync(
		EasContext context, string fileReference, CancellationToken ct)
	{
		if (MailFileReference.TryParse(fileReference) is not { } reference)
			return null; // hand-crafted reference: same answer as an attachment that no longer exists

		ReadOnlyMemory<byte>? raw;
		try
		{
			raw = await context.Session.Mailbox.GetRawMessageAsync(
				new FolderKey(reference.FolderBackendKey), new ItemKey(reference.ItemKey), ct);
		}
		catch (BackendItemNotFoundException)
		{
			return null; // hand-crafted item key inside the reference
		}

		if (raw is not { } rawBytes)
			return null;

		using MemoryStream stream = new(rawBytes.ToArray());
		MimeMessage message = await MimeMessage.LoadAsync(stream, ct);
		MimeEntity? attachment = message.Attachments.Skip(reference.AttachmentIndex).FirstOrDefault();
		if (attachment is not MimePart { Content: not null } part)
			return null;
		using MemoryStream decoded = new();
		await part.Content.DecodeToAsync(decoded, ct);
		return new BackendAttachment { ContentType = part.ContentType.MimeType, Content = decoded.ToArray() };
	}

	private async Task<XElement> HandleEmptyFolderAsync(EasContext context, XElement operation, CancellationToken ct)
	{
		string collectionId = operation.Element(AS + "CollectionId")?.Value ?? "";
		(UserFolder Folder, ContentAdapter Store)? resolved = await folders.ResolveCollectionAsync(
			context.Session, context.UserId, collectionId, ct);
		// Distinct statuses for distinct causes so the client can tell them apart: 6 unresolvable,
		// 2 not a mail folder OR read-only/access-denied (emptying is a bulk delete, so a read-only
		// grant on the folder blocks it just like global ReadOnly mode does) — AGENTS.md's
		// documented read-only scheme is explicit that EmptyFolderContents answers the TERMINAL
		// status 2 here, not 3; 3 is reserved for a genuine, retryable backend failure —
		// a client that read a blocked bulk delete as retryable would retry it every sync round
		// against a gateway that will never allow it, and never see a refusal.
		string? failure =
			resolved is null ? "6"
			: resolved.Value.Store.EasClass != EasClass.Email ? "2"
			: WritePermission.IsBlocked(context, options.Value, resolved.Value.Folder) ? "2"
			: null;
		if (failure is not null || resolved is null)
			return new XElement(IO + "EmptyFolderContents",
				new XElement(IO + "Status", failure ?? "6"),
				new XElement(AS + "CollectionId", collectionId));
		// A backend hiccup here must fail just this ItemOperations child (a retryable status),
		// not escape unhandled and turn the whole request into an HTTP 500 — matching the
		// try/catch HandleFetchAsync already wraps its own core in.
		try
		{
			await context.Session.Mailbox.EmptyFolderAsync(new FolderKey(resolved.Value.Folder.BackendKey), ct);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			logger.LogWarning(ex, "ItemOperations EmptyFolderContents failed");
			return new XElement(IO + "EmptyFolderContents",
				new XElement(IO + "Status", "3"),
				new XElement(AS + "CollectionId", collectionId));
		}
		return new XElement(IO + "EmptyFolderContents",
			new XElement(IO + "Status", "1"),
			new XElement(AS + "CollectionId", collectionId));
	}

	/// <summary>
	///   The body preference a Fetch renders with. Internal (not private) so the version gate
	///   below — the one a hard-coded false used to break — is directly assertable.
	/// </summary>
	internal static BodyPreference ParseBodyPreference(XElement? options, bool eas16)
	{
		// AirSyncBase body Type codes (MS-ASAIRS): 1 = plain text, 2 = HTML, 3 = RTF,
		// 4 = MIME. Default to 2 (HTML) when the client sends no preference.
		// `eas16` (context.Version >= EasVersion.V160) must reach the CONVERSION the same way it
		// does through Sync (version gating rides the host-side BodyPreference) — a hard-coded
		// false here silently drops airsyncbase:Location and event attachments for a 16.x client
		// fetching outside Sync.
		XElement? preference = options?.Elements(ASB + "BodyPreference").FirstOrDefault();
		if (preference is null)
			return new BodyPreference { Type = BodyType.Html, Eas16 = eas16 };
		int type = int.TryParse(preference.Element(ASB + "Type")?.Value, out int t) ? t : 2;
		long? truncation = long.TryParse(preference.Element(ASB + "TruncationSize")?.Value, out long tr)
			? tr
			: null;
		return new BodyPreference
		{
			Type = EasBodyTypes.FromWire(type),
			TruncationSize = truncation,
			Eas16 = eas16
		};
	}

	/// <summary>
	///   A mail-attachment FileReference ("{imapBackendKey}|{uid}|{attachmentIndex}",
	///   DelimitedKey-encoded) is client-supplied and names a backend folder directly — the same
	///   shape-vs-membership gap the LongId branch above closes. Shared with
	///   <see cref="GetAttachmentHandler" />, the other command that resolves a FileReference.
	/// </summary>
	internal static async Task<bool> IsAttachmentFolderRegisteredAsync(
		EasContext context, string fileReference, CancellationToken ct)
	{
		if (MailFileReference.TryParse(fileReference) is not { } reference)
			return false; // malformed reference — same answer as "not found" to the caller
		List<UserFolder> registry = await context.State.GetFoldersAsync(context.UserId, ct);
		return registry.Any(f => string.Equals(f.BackendKey, reference.FolderBackendKey, StringComparison.Ordinal));
	}
}

/// <summary>GetAttachment (legacy, pre-14.0): returns raw attachment bytes over HTTP.</summary>
public sealed class GetAttachmentHandler : IEasCommandHandler
{
	public string Command => "GetAttachment";

	public async Task HandleAsync(EasContext context, CancellationToken ct)
	{
		string? fileReference = context.Parameters.AttachmentName;
		if (string.IsNullOrEmpty(fileReference))
		{
			context.Http.Response.StatusCode = StatusCodes.Status400BadRequest;
			return;
		}

		// Same folder-registry gate as ItemOperations Fetch's FileReference path — a
		// client-supplied reference names a backend folder directly.
		if (!await ItemOperationsHandler.IsAttachmentFolderRegisteredAsync(context, fileReference, ct))
		{
			context.Http.Response.StatusCode = StatusCodes.Status404NotFound;
			return;
		}

		BackendAttachment? attachment =
			await ItemOperationsHandler.FetchMailAttachmentAsync(context, fileReference, ct);

		if (attachment is null)
		{
			context.Http.Response.StatusCode = StatusCodes.Status404NotFound;
			return;
		}

		// The content type comes from inside an untrusted email — make sure nothing
		// renders it inline in a browser context.
		context.Http.Response.Headers.ContentDisposition = "attachment";
		await context.WriteBinaryAsync(attachment.Content.ToArray(), attachment.ContentType);
	}
}

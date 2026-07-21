using System.Xml.Linq;
using ActiveSync.Backends.Converters;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using ActiveSync.Core.State;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;
using Microsoft.Extensions.Options;

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
			BackendAttachment? attachment =
				fileReference.StartsWith(CalendarConverter.AttachmentReferencePrefix, StringComparison.Ordinal)
					? await FetchCalendarAttachmentAsync(context, fileReference, ct)
					: await context.Session.MailStore.GetAttachmentAsync(fileReference, ct);
			if (attachment is null)
				return Failure("6");
			XElement data = new(IO + "Data", Convert.ToBase64String(attachment.Content));
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
			IContentStore? searchStore = context.Session.GetStoreForBackendKey(parts[0]);
			if (searchStore is null)
				return Failure("6");
			BodyPreference options = ParseBodyPreference(fetch.Element(IO + "Options"));
			BackendItem? found = await searchStore.GetItemAsync(parts[0], parts[1], options, ct);
			if (found is null)
				return Failure("6");
			return new XElement(IO + "Fetch",
				new XElement(IO + "Status", "1"),
				new XElement(IO + "LongId", longId),
				new XElement(AS + "Class", searchStore.EasClass),
				new XElement(IO + "Properties", found.ApplicationData));
		}

		if (collectionId is null || serverId is null)
			return Failure("2");

		(UserFolder Folder, IContentStore Store)? resolved = await folders.ResolveCollectionAsync(
			context.Session, context.Device.UserName, collectionId, ct);
		if (resolved is null)
			return Failure("6");
		(UserFolder folder, IContentStore store) = resolved.Value;
		string? itemKey = await folders.ResolveItemKeyAsync(folder, store, serverId, ct);
		if (itemKey is null)
			return Failure("6");

		BodyPreference bodyPreference = ParseBodyPreference(fetch.Element(IO + "Options"));
		BackendItem? item = await store.GetItemAsync(folder.BackendKey, itemKey, bodyPreference, ct);
		if (item is null)
			return Failure("6");

		return new XElement(IO + "Fetch",
			new XElement(IO + "Status", "1"),
			new XElement(AS + "CollectionId", collectionId),
			new XElement(AS + "ServerId", serverId),
			new XElement(AS + "Class", store.EasClass),
			new XElement(IO + "Properties", item.ApplicationData));
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

		(UserFolder Folder, IContentStore Store)? resolved = await folders.ResolveCollectionAsync(
			context.Session, context.Device.UserName, serverId[..colon], ct);
		if (resolved is null || resolved.Value.Store is not ICalendarAttachmentSource source)
			return null;
		string? itemKey = await folders.ResolveItemKeyAsync(
			resolved.Value.Folder, resolved.Value.Store, serverId, ct);
		if (itemKey is null)
			return null;
		return await source.GetEventAttachmentAsync(resolved.Value.Folder.BackendKey, itemKey, index, ct);
	}

	private async Task<XElement> HandleEmptyFolderAsync(EasContext context, XElement operation, CancellationToken ct)
	{
		string collectionId = operation.Element(AS + "CollectionId")?.Value ?? "";
		(UserFolder Folder, IContentStore Store)? resolved = await folders.ResolveCollectionAsync(
			context.Session, context.Device.UserName, collectionId, ct);
		// Emptying is a bulk delete: a read-only grant on the folder blocks it just like
		// global ReadOnly mode does.
		if (resolved is null || resolved.Value.Store.EasClass != EasClass.Email ||
		    WritePermission.IsBlocked(context, options.Value, resolved.Value.Folder))
			return new XElement(IO + "EmptyFolderContents",
				new XElement(IO + "Status", "2"),
				new XElement(AS + "CollectionId", collectionId));
		await context.Session.MailStore.EmptyFolderAsync(resolved.Value.Folder.BackendKey, ct);
		return new XElement(IO + "EmptyFolderContents",
			new XElement(IO + "Status", "1"),
			new XElement(AS + "CollectionId", collectionId));
	}

	private static BodyPreference ParseBodyPreference(XElement? options)
	{
		// AirSyncBase body Type codes (MS-ASAIRS): 1 = plain text, 2 = HTML, 3 = RTF,
		// 4 = MIME. Default to 2 (HTML) when the client sends no preference.
		XElement? preference = options?.Elements(ASB + "BodyPreference").FirstOrDefault();
		if (preference is null)
			return new BodyPreference(2, null, false);
		int type = int.TryParse(preference.Element(ASB + "Type")?.Value, out int t) ? t : 2;
		long? truncation = long.TryParse(preference.Element(ASB + "TruncationSize")?.Value, out long tr)
			? tr
			: null;
		return new BodyPreference(type, truncation, false);
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

		BackendAttachment? attachment;
		try
		{
			attachment = await context.Session.MailStore.GetAttachmentAsync(fileReference, ct);
		}
		catch (BackendItemNotFoundException)
		{
			attachment = null; // hand-crafted item key inside the reference
		}

		if (attachment is null)
		{
			context.Http.Response.StatusCode = StatusCodes.Status404NotFound;
			return;
		}

		// The content type comes from inside an untrusted email — make sure nothing
		// renders it inline in a browser context.
		context.Http.Response.Headers.ContentDisposition = "attachment";
		await context.WriteBinaryAsync(attachment.Content, attachment.ContentType);
	}
}

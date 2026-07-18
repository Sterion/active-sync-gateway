using System.Xml.Linq;
using ActiveSync.Core.Backend;
using ActiveSync.Core.State;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;

namespace ActiveSync.Server.Eas.Handlers;

/// <summary>
///   Find (MS-ASCMD 2.2.1.2, EAS 16.1): the modern mailbox/GAL search. Same engine as
///   Search — IMAP server-side search and the CardDAV GAL — with the Find response shape
///   (SearchId echo, Result with Preview/HasAttachments for mail, GAL properties incl.
///   photos for people).
/// </summary>
public sealed class FindHandler(FolderService folders, ILogger<FindHandler> logger) : IEasCommandHandler
{
	private const int MaxFetch = 500;
	private static readonly XNamespace F = EasNamespaces.Find;
	private static readonly XNamespace AS = EasNamespaces.AirSync;
	private static readonly XNamespace ASB = EasNamespaces.AirSyncBase;

	public string Command => "Find";

	public async Task HandleAsync(EasContext context, CancellationToken ct)
	{
		XDocument? request = await context.ReadRequestAsync();
		XElement? root = request?.Root;
		string searchId = root?.Element(F + "SearchId")?.Value ?? "";
		XElement? execute = root?.Element(F + "ExecuteSearch");
		XElement? mailbox = execute?.Element(F + "MailBoxSearchCriterion");
		XElement? gal = execute?.Element(F + "GalSearchCriterion");
		if (execute is null || (mailbox is null && gal is null))
		{
			await WriteAsync(context, searchId, "2", null); // protocol error
			return;
		}

		XElement criterion = mailbox ?? gal!;
		string freeText = criterion.Element(F + "Query")?.Descendants(F + "FreeText").FirstOrDefault()?.Value
		                  ?? criterion.Element(F + "Query")?.Value ?? "";
		(int start, int pageSize) = ParseRange(criterion.Element(F + "Options")?.Element(F + "Range")?.Value);
		int fetch = Math.Min(start + pageSize, MaxFetch);

		try
		{
			List<XElement> results = mailbox is not null
				? await SearchMailboxAsync(context, mailbox, freeText, start, pageSize, fetch, ct)
				: await SearchGalAsync(context, gal!, freeText, start, pageSize, fetch, ct);

			XElement response = new(F + "Response",
				new XElement(F + "Status", "1"),
				results,
				new XElement(F + "Range", $"{start}-{start + Math.Max(results.Count - 1, 0)}"),
				new XElement(F + "Total", (start + results.Count).ToString()));
			await WriteAsync(context, searchId, "1", response);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			logger.LogError(ex, "Find failed");
			await WriteAsync(context, searchId, "2", null);
		}
	}

	private async Task<List<XElement>> SearchMailboxAsync(
		EasContext context, XElement criterion, string freeText, int start, int pageSize, int fetch,
		CancellationToken ct)
	{
		string? folderBackendKey = null;
		UserFolder? searchFolder = null;
		string? collectionId = criterion.Element(F + "Query")?
			.Descendants(AS + "CollectionId").FirstOrDefault()?.Value;
		// DeepTraversal means "whole mailbox" — which is also the default when no
		// CollectionId narrows the scope, so only the narrowing case needs handling.
		bool deepTraversal = criterion.Element(F + "Options")?.Element(F + "DeepTraversal") is not null;
		if (collectionId is not null && !deepTraversal)
		{
			(UserFolder Folder, IContentStore Store)? resolved = await folders.ResolveCollectionAsync(
				context.Session, context.Device.UserName, collectionId, ct);
			if (resolved is not null)
			{
				searchFolder = resolved.Value.Folder;
				folderBackendKey = resolved.Value.Folder.BackendKey;
			}
		}

		IContentStore mailStore = context.Session.GetStoreForClass(EasClass.Email)!;
		IReadOnlyList<(string FolderBackendKey, string ItemKey)> hits =
			await context.Session.Mail.SearchAsync(folderBackendKey, freeText, null, fetch, ct);

		List<XElement> results = new();
		foreach ((string hitFolderKey, string itemKey) in hits.Skip(start).Take(pageSize))
		{
			BackendItem? item = await mailStore.GetItemAsync(
				hitFolderKey, itemKey, new BodyPreference(1, 1024, false, true), ct);
			if (item is null)
				continue;

			// Preview and HasAttachments live in the Find namespace; the rest of the item
			// rides along as its regular ApplicationData elements.
			string preview = item.ApplicationData
				.FirstOrDefault(e => e.Name == ASB + "Body")?
				.Element(ASB + "Data")?.Value ?? "";
			bool hasAttachments = item.ApplicationData.Any(e => e.Name == ASB + "Attachments");

			XElement properties = new(F + "Properties", item.ApplicationData);
			if (preview.Length > 0)
				properties.Add(new XElement(F + "Preview",
					preview.Length > 255 ? preview[..255] : preview));
			properties.Add(new XElement(F + "HasAttachments", hasAttachments ? "1" : "0"));

			XElement result = new(F + "Result",
				new XElement(AS + "Class", EasClass.Email),
				new XElement(F + "Properties", properties.Elements()));
			if (searchFolder is not null)
			{
				result.AddFirst(new XElement(AS + "CollectionId", searchFolder.ServerId));
				result.AddFirst(new XElement(AS + "ServerId",
					await folders.ComposeServerIdAsync(searchFolder, mailStore, itemKey, ct)));
			}

			results.Add(result);
		}

		return results;
	}

	private static async Task<List<XElement>> SearchGalAsync(
		EasContext context, XElement criterion, string freeText, int start, int pageSize, int fetch,
		CancellationToken ct)
	{
		GalPhotoRequest? photos = null;
		if (criterion.Element(F + "Options")?.Element(F + "Picture") is XElement picture)
			photos = new GalPhotoRequest(
				int.TryParse(picture.Element(F + "MaxSize")?.Value, out int maxSize) ? maxSize : null,
				int.TryParse(picture.Element(F + "MaxPictures")?.Value, out int maxCount) ? maxCount : null);

		IContactOperations? contacts = context.Session.Contacts;
		IReadOnlyList<IReadOnlyList<XElement>> hits = contacts is null
			? []
			: await contacts.SearchGalAsync(freeText, fetch, photos, ct);

		return hits.Skip(start).Take(pageSize)
			.Select(properties => new XElement(F + "Result",
				new XElement(F + "Properties", properties)))
			.ToList();
	}

	private static (int Start, int PageSize) ParseRange(string? range)
	{
		int start = 0, end = 24;
		if (range is not null)
		{
			string[] parts = range.Split('-');
			if (parts.Length == 2 && int.TryParse(parts[0], out int s) && int.TryParse(parts[1], out int e))
			{
				start = Math.Max(0, s);
				end = e;
			}
		}

		return (start, Math.Clamp(end - start + 1, 1, 100));
	}

	private static Task WriteAsync(EasContext context, string searchId, string status, XElement? response)
	{
		XElement find = new(F + "Find", new XElement(F + "Status", status));
		if (searchId.Length > 0)
			find.AddFirst(new XElement(F + "SearchId", searchId));
		if (response is not null)
			find.Add(response);
		return context.WriteResponseAsync(new XDocument(find));
	}
}

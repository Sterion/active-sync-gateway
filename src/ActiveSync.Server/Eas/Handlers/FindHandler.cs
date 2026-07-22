using System.Xml.Linq;
using ActiveSync.Contracts;
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
		// Paging at/beyond the fetch cap can never serve anything — refuse it without a wasted
		// backend call rather than fetching the whole cap and Skip()-ing it all away (F41).
		if (start >= MaxFetch)
		{
			await WriteAsync(context, searchId, "1",
				new XElement(F + "Response",
					new XElement(F + "Status", "1"),
					new XElement(F + "Total", "0")));
			return;
		}

		try
		{
			(List<XElement> results, int total) = mailbox is not null
				? await SearchMailboxAsync(context, mailbox, freeText, start, pageSize, fetch, ct)
				: await SearchGalAsync(context, gal!, freeText, start, pageSize, fetch, ct);

			XElement response = new(F + "Response",
				new XElement(F + "Status", "1"),
				results);
			// Omit Range entirely when empty — "0-0" claims one result was returned (F37).
			if (results.Count > 0)
				response.Add(new XElement(F + "Range", $"{start}-{start + results.Count - 1}"));
			// Total is the number of matches FOUND (capped by the fetch limit), not start+served,
			// which stops the client after the first page (F36).
			response.Add(new XElement(F + "Total", total.ToString()));
			await WriteAsync(context, searchId, "1", response);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			logger.LogError(ex, "Find failed");
			await WriteAsync(context, searchId, "2", null);
		}
	}

	private async Task<(List<XElement> Results, int Total)> SearchMailboxAsync(
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
			await context.Session.MailStore.SearchAsync(folderBackendKey, freeText, null, fetch, ct);

		// Fetch the page's bodies in ONE batched call per folder rather than a sequential
		// GetItemAsync per hit (F40).
		BodyPreference bodyPreference = new(1, 1024, false, true);
		List<(string FolderKey, string ItemKey)> page = hits.Skip(start).Take(pageSize).ToList();
		Dictionary<(string, string), BackendItem?> fetched = new();
		foreach (IGrouping<string, (string FolderKey, string ItemKey)> group in page.GroupBy(h => h.FolderKey))
		{
			IReadOnlyList<string> keys = group.Select(h => h.ItemKey).ToList();
			IReadOnlyDictionary<string, BackendItem?> items =
				await mailStore.GetItemsAsync(group.Key, keys, bodyPreference, ct);
			foreach ((string folderKey, string itemKey) in group)
				fetched[(folderKey, itemKey)] = items.GetValueOrDefault(itemKey);
		}

		List<XElement> results = new();
		foreach ((string hitFolderKey, string itemKey) in page)
		{
			BackendItem? item = fetched.GetValueOrDefault((hitFolderKey, itemKey));
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

			// MS-ASCMD Find Result child order: Class, ServerId, CollectionId, Properties. Build
			// them in that order rather than prepending ServerId/CollectionId after the fact (F38).
			List<XElement> children = [new XElement(AS + "Class", EasClass.Email)];
			if (searchFolder is not null)
			{
				children.Add(new XElement(AS + "ServerId",
					await folders.ComposeServerIdAsync(searchFolder, mailStore, itemKey, ct)));
				children.Add(new XElement(AS + "CollectionId", searchFolder.ServerId));
			}

			children.Add(new XElement(F + "Properties", properties.Elements()));
			results.Add(new XElement(F + "Result", children));
		}

		return (results, hits.Count);
	}

	private static async Task<(List<XElement> Results, int Total)> SearchGalAsync(
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

		List<XElement> results = hits.Skip(start).Take(pageSize)
			.Select(properties => new XElement(F + "Result",
				new XElement(F + "Properties", properties)))
			.ToList();
		return (results, hits.Count);
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

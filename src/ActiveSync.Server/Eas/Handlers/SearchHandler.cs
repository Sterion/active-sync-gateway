using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.State;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;

namespace ActiveSync.Server.Eas.Handlers;

/// <summary>Search (MS-ASCMD 2.2.1.16): Mailbox and GAL stores.</summary>
public sealed class SearchHandler(FolderService folders, ILogger<SearchHandler> logger) : IEasCommandHandler
{
	private static readonly XNamespace S = EasNamespaces.Search;
	private static readonly XNamespace AS = EasNamespaces.AirSync;

	public string Command => "Search";

	public async Task HandleAsync(EasContext context, CancellationToken ct)
	{
		XDocument? request = await context.ReadRequestAsync();
		XElement? store = request?.Root?.Element(S + "Store");
		if (store is null)
		{
			await WriteAsync(context, "2", []);
			return;
		}

		string storeName = store.Element(S + "Name")?.Value ?? "";
		XElement? query = store.Element(S + "Query");
		string freeText = query?.Descendants(S + "FreeText").FirstOrDefault()?.Value
		                  ?? query?.Value ?? "";
		// Range is "start-end" (inclusive). Clamp the page and cap the fetch so a client
		// cannot request an unbounded result set; skip `start` server-side for real paging.
		(int start, int pageSize) = ParseRange(store.Element(S + "Options")?.Element(S + "Range")?.Value);
		int fetch = Math.Min(start + pageSize, MaxFetch);
		// Paging at/beyond the fetch cap can never return anything (the backend fetches at most
		// MaxFetch, then Skip(start) drops them all) — refuse it without a wasted backend call
		// rather than returning an empty page indistinguishable from "no more results" (F41).
		if (start >= MaxFetch)
		{
			await WriteAsync(context, "1", []);
			return;
		}

		try
		{
			if (storeName.Equals("GAL", StringComparison.OrdinalIgnoreCase))
			{
				// Optional contact photos (MS-ASCMD 14.1): Options > Picture (MaxSize, MaxPictures).
				GalPhotoRequest? photos = null;
				if (store.Element(S + "Options")?.Element(S + "Picture") is XElement picture)
					photos = new GalPhotoRequest(
						int.TryParse(picture.Element(S + "MaxSize")?.Value, out int maxSize) ? maxSize : null,
						int.TryParse(picture.Element(S + "MaxPictures")?.Value, out int maxCount) ? maxCount : null);

				IContactOperations? contacts = context.Session.Contacts;
				IReadOnlyList<IReadOnlyList<XElement>> hits = contacts is null
					? []
					: await contacts.SearchGalAsync(freeText, fetch, photos, ct);
				List<XElement> results = hits.Skip(start).Take(pageSize)
					.Select(properties => new XElement(S + "Result",
						new XElement(S + "Properties", properties))).ToList();
				await WriteAsync(context, "1", results, start, hits.Count);
			}
			else // Mailbox
			{
				string? folderBackendKey = null;
				string? collectionId = query?.Descendants(AS + "CollectionId").FirstOrDefault()?.Value;
				UserFolder? searchFolder = null;
				IContentStore? mailStore = context.Session.GetStoreForClass(EasClass.Email);
				if (collectionId is not null)
				{
					(UserFolder Folder, IContentStore Store)? resolved = await folders.ResolveCollectionAsync(
						context.Session, context.Device.UserName, collectionId, ct);
					if (resolved is not null)
					{
						searchFolder = resolved.Value.Folder;
						folderBackendKey = resolved.Value.Folder.BackendKey;
					}
				}

				IReadOnlyList<(string FolderBackendKey, string ItemKey)> hits =
					await context.Session.MailStore.SearchAsync(folderBackendKey, freeText, null, fetch, ct);
				// Skip the requested offset, then fetch the page's bodies in ONE batched call per
				// folder instead of a sequential GetItemAsync per hit (F40).
				List<(string FolderKey, string ItemKey)> page =
					hits.Skip(start).Take(pageSize).ToList();
				Dictionary<(string, string), BackendItem?> fetched = new();
				foreach (IGrouping<string, (string FolderKey, string ItemKey)> group in page.GroupBy(h => h.FolderKey))
				{
					IReadOnlyList<string> keys = group.Select(h => h.ItemKey).ToList();
					IReadOnlyDictionary<string, BackendItem?> items = await mailStore!.GetItemsAsync(
						group.Key, keys, new BodyPreference(1, 1024, false), ct);
					foreach ((string folderKey, string itemKey) in group)
						fetched[(folderKey, itemKey)] = items.GetValueOrDefault(itemKey);
				}

				List<XElement> results = new();
				foreach ((string hitFolderKey, string itemKey) in page)
				{
					BackendItem? item = fetched.GetValueOrDefault((hitFolderKey, itemKey));
					if (item is null)
						continue;
					string longId = DelimitedKey.Encode(hitFolderKey, itemKey);
					XElement result = new(S + "Result",
						new XElement(AS + "Class", EasClass.Email),
						new XElement(S + "LongId", longId),
						new XElement(S + "Properties", item.ApplicationData));
					if (searchFolder is not null)
						result.Add(new XElement(AS + "CollectionId", searchFolder.ServerId));
					results.Add(result);
				}

				await WriteAsync(context, "1", results, start, hits.Count);
			}
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			logger.LogError(ex, "Search failed");
			await WriteAsync(context, "3", []);
		}
	}

	private const int DefaultPageSize = 100;
	private const int MaxPageSize = 100;
	private const int MaxFetch = 500;

	private static (int Start, int PageSize) ParseRange(string? range)
	{
		int start = 0;
		int end = DefaultPageSize - 1;
		if (range is not null)
		{
			string[] parts = range.Split('-');
			if (parts.Length == 2 && int.TryParse(parts[0], out int s) && int.TryParse(parts[1], out int e))
			{
				start = Math.Max(0, s);
				end = e;
			}
		}

		int pageSize = Math.Clamp(end - start + 1, 1, MaxPageSize);
		return (start, pageSize);
	}

	private static Task WriteAsync(
		EasContext context, string status, List<XElement> results, int start = 0, int total = 0)
	{
		XElement response = new(S + "Response",
			new XElement(S + "Store",
				new XElement(S + "Status", status),
				results,
				// Echo the ACTUAL served window (start .. start+served-1), not a fabricated 0-N.
				results.Count > 0
					? new XElement(S + "Range", $"{start}-{start + results.Count - 1}")
					: null,
				// Total is the number of matches FOUND (capped by the fetch limit), not the served
				// page size — reporting the page size makes the client stop after page 1 (F36).
				new XElement(S + "Total", total.ToString())));
		return context.WriteResponseAsync(new XDocument(
			new XElement(S + "Search",
				new XElement(S + "Status", "1"),
				response)));
	}
}

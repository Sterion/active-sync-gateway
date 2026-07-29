using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Core.Options;
using ActiveSync.Core.State;
using ActiveSync.Eas.Conversion;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;
using ActiveSync.Server.Eas.Content;
using Microsoft.Extensions.Options;

namespace ActiveSync.Server.Eas.Handlers;

/// <summary>Search (MS-ASCMD 2.2.1.16): Mailbox and GAL stores.</summary>
public sealed class SearchHandler(
	FolderService folders, IOptionsSnapshot<ActiveSyncOptions> options, ILogger<SearchHandler> logger)
	: IEasCommandHandler
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
		// rather than returning an empty page indistinguishable from "no more results".
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
					photos = new GalPhotoRequest
					{
						MaxSizeBytes = int.TryParse(picture.Element(S + "MaxSize")?.Value, out int maxSize)
							? maxSize
							: null,
						MaxCount = int.TryParse(picture.Element(S + "MaxPictures")?.Value, out int maxCount)
							? maxCount
							: null
					};

				IDirectoryOperations? contacts = context.Session.Contacts;
				IReadOnlyList<GalEntry> hits = contacts is null
					? []
					: await contacts.SearchGalAsync(freeText, fetch, photos, ct);
				// The store hands over typed entries (it enforced the photo limits); the host owns
				// the gal:-namespace wire shape, statuses included.
				List<XElement> results = hits.Skip(start).Take(pageSize)
					.Select(entry => new XElement(S + "Result",
						new XElement(S + "Properties", GalXml.ToGalProperties(entry)))).ToList();
				await WriteAsync(context, "1", results, start, hits.Count);
			}
			else // Mailbox
			{
				FolderKey? folderBackendKey = null;
				string? collectionId = query?.Descendants(AS + "CollectionId").FirstOrDefault()?.Value;
				UserFolder? searchFolder = null;
				IContentStore mailStore = context.Session.Mail;
				ContentAdapter adapter = ContentAdapter.For(context.Session, mailStore, options.Value.Eas);
				if (collectionId is not null)
				{
					(UserFolder Folder, ContentAdapter Store)? resolved = await folders.ResolveCollectionAsync(
						context.Session, context.UserId, collectionId, ct);
					if (resolved is not null)
					{
						searchFolder = resolved.Value.Folder;
						folderBackendKey = new FolderKey(resolved.Value.Folder.BackendKey);
					}
				}

				IReadOnlyList<SearchHit> hits =
					await context.Session.Mailbox.SearchAsync(folderBackendKey, freeText, null, fetch, ct);
				// Skip the requested offset, then fetch the page's bodies in ONE batched call per
				// folder instead of a sequential GetItemAsync per hit.
				List<SearchHit> page = hits.Skip(start).Take(pageSize).ToList();
				BodyPreference preview = PreviewPreference(context.Version);
				Dictionary<(string, string), object?> fetched = new();
				foreach (IGrouping<FolderKey, SearchHit> group in page.GroupBy(h => h.Folder))
				{
					IReadOnlyList<string> keys = group.Select(h => h.Item.Value).ToList();
					IReadOnlyDictionary<string, object?> items =
						await adapter.GetItemsAsync(group.Key.Value, keys, ct);
					foreach (SearchHit hit in group)
						fetched[(hit.Folder.Value, hit.Item.Value)] = items.GetValueOrDefault(hit.Item.Value);
				}

				List<XElement> results = new();
				foreach (SearchHit hit in page)
				{
					object? item = fetched.GetValueOrDefault((hit.Folder.Value, hit.Item.Value));
					List<XElement>? rendered = item is null
						? null
						: adapter.Render(item, preview, hit.Folder.Value, hit.Item.Value);
					if (rendered is null)
						continue;
					string longId = DelimitedKey.Encode(hit.Folder.Value, hit.Item.Value);
					XElement result = new(S + "Result",
						new XElement(AS + "Class", EasClass.Email),
						new XElement(S + "LongId", longId),
						new XElement(S + "Properties", rendered));
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

	/// <summary>
	///   The body preference a mailbox Search hit is rendered with: a short plain-text preview,
	///   and the version gate riding the same way it does through Sync/ItemOperations — a
	///   hard-coded false would silently drop 16.x-only shapes from Search results too. Named
	///   (rather than inline) so the gate itself is directly assertable.
	/// </summary>
	internal static BodyPreference PreviewPreference(EasVersion version)
	{
		return new BodyPreference
		{
			Type = BodyType.PlainText,
			TruncationSize = 1024,
			Eas16 = version >= EasVersion.V160
		};
	}

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
				// page size — reporting the page size makes the client stop after page 1.
				new XElement(S + "Total", total.ToString())));
		return context.WriteResponseAsync(new XDocument(
			new XElement(S + "Search",
				new XElement(S + "Status", "1"),
				response)));
	}
}

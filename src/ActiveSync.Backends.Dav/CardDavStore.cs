using System.Xml.Linq;
using ActiveSync.Backends.Common.Converters;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using Microsoft.Extensions.Logging;

namespace ActiveSync.Backends.Dav;

/// <summary>
///   Contacts content store over CardDAV. Item keys are server hrefs; revisions are ETags; the
///   payload is the stored vCard verbatim.
/// </summary>
public sealed class CardDavStore(
	WebDavClient dav,
	DavServerOptions options,
	BackendCredentials credentials,
	ILogger logger,
	int pollSeconds)
	: DavStoreBase<ContactItem>(dav, options, credentials, logger, pollSeconds), IContactStore, IDirectoryOperations
{
	public const string KeyPrefix = "carddav:";

	protected override string Prefix => KeyPrefix;
	protected override string MediaType => "text/vcard";
	protected override string FileExtension => ".vcf";
	protected override string WellKnownPath => "/.well-known/carddav";
	protected override XName HomeSetProperty => DavNs.CardDav + "addressbook-home-set";
	protected override string? HomeSetDiscoveryLogLabel => "CardDAV";
	protected override string ProtocolLabel => "CardDAV";
	protected override string ItemNoun => "contact";
	protected override string ItemNounPlural => "contacts";
	protected override string CollectionKindPlural => "address books";
	protected override string CtagLabel => "CardDAV";

	// ---------- IDirectoryOperations (GAL search) ----------

	/// <summary>
	///   Searches every address book and returns typed GAL entries. The photo limits are enforced
	///   HERE (the store holds the request and counts granted photos across the whole result set);
	///   the host maps <see cref="GalPictureStatus" /> onto the MS-ASCMD wire statuses.
	/// </summary>
	/// <param name="query">The free-text query to match entries against.</param>
	/// <param name="maxResults">The most entries to return.</param>
	/// <param name="photos">The photo request, or <c>null</c> when pictures were not requested.</param>
	/// <param name="ct">Cancellation token for the backend round-trip.</param>
	/// <returns>The matching entries.</returns>
	public async Task<IReadOnlyList<GalEntry>> SearchGalAsync(
		string query, int maxResults, GalPhotoRequest? photos, CancellationToken ct)
	{
		List<GalEntry> results = new();
		int photosGranted = 0;
		foreach (BackendFolder folder in await ListFoldersAsync(ct).ConfigureAwait(false))
		{
			// One addressbook-query REPORT returns the matching vCards INLINE (address-data),
			// instead of listing every href and then a serial GET per contact — the single largest
			// DAV performance defect. A server that rejects the REPORT (throws) falls back to the
			// per-contact GET path below; a server that ignores the filter and returns everything is
			// still correct because BuildGalEntry filters client-side.
			IReadOnlyList<string>? cards = await QueryGalCardsAsync(folder.Key, query, ct)
				.ConfigureAwait(false);
			IEnumerable<string> contents = cards ?? await GetCardsByEnumerationAsync(folder.Key, ct)
				.ConfigureAwait(false);

			foreach (string vcf in contents)
			{
				if (results.Count >= maxResults)
					return results;
				GalEntry? gal = ContactConverter.BuildGalEntry(
					vcf, query, photos is not null, photos?.MaxSizeBytes,
					photosGranted >= (photos?.MaxCount ?? int.MaxValue), out bool granted);
				if (gal is null)
					continue;
				if (granted)
					photosGranted++;
				results.Add(gal);
			}
		}

		return results;
	}

	/// <summary>
	///   Runs a CardDAV <c>addressbook-query</c> REPORT with a server-side FN/EMAIL <c>contains</c>
	///   filter, requesting the vCard inline via <c>address-data</c>. Returns the vCard bodies, or
	///   null when the server rejects the REPORT (so the caller falls back to per-contact GETs).
	/// </summary>
	private async Task<IReadOnlyList<string>?> QueryGalCardsAsync(
		FolderKey folder, string query, CancellationToken ct)
	{
		string collection = FromBackendKey(folder.Value);
		XElement body = new(DavNs.CardDav + "addressbook-query",
			new XAttribute(XNamespace.Xmlns + "D", DavNs.D.NamespaceName),
			new XAttribute(XNamespace.Xmlns + "C", DavNs.CardDav.NamespaceName),
			new XElement(DavNs.D + "prop",
				new XElement(DavNs.D + "getetag"),
				new XElement(DavNs.CardDav + "address-data")),
			new XElement(DavNs.CardDav + "filter", new XAttribute("test", "anyof"),
				PropFilter("FN", query),
				PropFilter("EMAIL", query),
				PropFilter("NICKNAME", query)));

		List<DavResource> resources;
		try
		{
			resources = await Dav.ReportAsync(collection, 1, body, ct).ConfigureAwait(false);
		}
		catch (BackendException)
		{
			return null; // server without addressbook-query support — fall back to GETs
		}

		List<string> cards = new();
		int nonCollectionResponses = 0;
		foreach (DavResource resource in resources)
		{
			if (PathsEqual(resource.Href, collection))
				continue;
			nonCollectionResponses++;
			string? data = resource.Propstat.Descendants(DavNs.CardDav + "address-data").FirstOrDefault()?.Value;
			if (!string.IsNullOrWhiteSpace(data))
				cards.Add(data);
		}

		// A server can accept the REPORT and answer a well-formed 207 whose propstats carry
		// getetag but no address-data at all (unsupported or silently dropped) — that is an
		// EMPTY-BUT-NON-NULL list, indistinguishable here from "genuinely zero matches". The caller
		// (SearchGalAsync) treats null as "fall back to per-contact enumeration" and a non-null empty
		// list as "no results" — so without this check, GAL search returns nothing, forever, with no
		// error and no log line, against any server that behaves this way.
		if (nonCollectionResponses > 0 && cards.Count == 0)
		{
			Logger.LogDebug(
				"CardDAV: addressbook-query REPORT for {Collection} returned {Count} resource(s) but no " +
				"address-data body; falling back to per-contact GET", collection, nonCollectionResponses);
			return null;
		}

		return cards;

		static XElement PropFilter(string name, string text) =>
			new(DavNs.CardDav + "prop-filter", new XAttribute("name", name),
				new XElement(DavNs.CardDav + "text-match",
					new XAttribute("collation", "i;unicode-casemap"),
					new XAttribute("match-type", "contains"),
					text));
	}

	/// <summary>The fallback when the server has no addressbook-query support: list every href, then a GET per contact.</summary>
	private async Task<IReadOnlyList<string>> GetCardsByEnumerationAsync(
		FolderKey folder, CancellationToken ct)
	{
		IReadOnlyDictionary<ItemKey, ItemRevision> revisions =
			await GetItemRevisionsAsync(folder, ContentFilter.All, ct).ConfigureAwait(false);
		List<string> cards = new();
		foreach (ItemKey href in revisions.Keys)
		{
			(string Content, string? ETag)? item = await Dav.GetAsync(href.Value, ct).ConfigureAwait(false);
			if (item is not null)
				cards.Add(item.Value.Content);
		}

		return cards;
	}

	// The stored document IS the payload — identity in both directions (round-trip fidelity).
	protected override ContactItem ToItem(string content)
	{
		return new ContactItem { VCard = content };
	}

	protected override string PayloadOf(ContactItem item)
	{
		return item.VCard;
	}

	protected override string? ExtractUid(string content)
	{
		return ContactConverter.ExtractUid(content);
	}

	protected override XElement BuildUidQueryBody(string uid)
	{
		return new XElement(DavNs.CardDav + "addressbook-query",
			new XElement(DavNs.D + "prop", new XElement(DavNs.D + "getetag")),
			new XElement(DavNs.CardDav + "filter",
				new XElement(DavNs.CardDav + "prop-filter", new XAttribute("name", "UID"),
					new XElement(DavNs.CardDav + "text-match", uid))));
	}

	/// <inheritdoc />
	public override async Task<IReadOnlyList<BackendFolder>> ListFoldersAsync(CancellationToken ct)
	{
		string home = await GetHomeSetAsync(ct).ConfigureAwait(false);
		XElement body = new(DavNs.D + "propfind",
			new XElement(DavNs.D + "prop",
				new XElement(DavNs.D + "resourcetype"),
				new XElement(DavNs.D + "displayname")));
		List<DavResource> resources = await Dav.PropfindAsync(home, 1, body, ct).ConfigureAwait(false);

		List<BackendFolder> folders = new();
		bool first = true;
		// Multistatus order is server whim, and the first address book below becomes THE
		// default contacts folder — sort by href so the pick is stable across sessions and
		// servers (CalDavStore already does this for the default calendar).
		foreach (DavResource resource in resources.OrderBy(r => r.Href, StringComparer.OrdinalIgnoreCase))
		{
			XElement? type = resource.Propstat.Descendants(DavNs.D + "resourcetype").FirstOrDefault();
			if (type?.Element(DavNs.CardDav + "addressbook") is null)
				continue;
			string? name = resource.Propstat.Descendants(DavNs.D + "displayname").FirstOrDefault()?.Value;
			if (string.IsNullOrWhiteSpace(name))
				name = resource.Href.TrimEnd('/').Split('/').LastOrDefault() ?? "Contacts";
			folders.Add(new BackendFolder
			{
				Key = new FolderKey(ToBackendKey(resource.Href)),
				DisplayName = name,
				Type = first ? FolderType.Contacts : FolderType.UserContacts
			});
			first = false;
		}

		return folders;
	}

	/// <inheritdoc />
	public override async Task<IReadOnlyDictionary<ItemKey, ItemRevision>> GetItemRevisionsAsync(
		FolderKey folder, ContentFilter filter, CancellationToken ct)
	{
		string collection = FromBackendKey(folder.Value);
		XElement body = new(DavNs.D + "propfind",
			new XElement(DavNs.D + "prop", new XElement(DavNs.D + "getetag")));
		List<DavResource> resources = await Dav.PropfindAsync(collection, 1, body, ct).ConfigureAwait(false);

		Dictionary<ItemKey, ItemRevision> map = new();
		foreach (DavResource resource in resources)
		{
			if (PathsEqual(resource.Href, collection))
				continue;
			string? etag = resource.Propstat.Descendants(DavNs.D + "getetag").FirstOrDefault()?.Value;
			if (etag is not null)
				map[new ItemKey(resource.Href)] = new ItemRevision(etag);
		}

		return map;
	}
}

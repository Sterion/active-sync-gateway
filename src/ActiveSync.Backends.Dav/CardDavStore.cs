using System.Xml.Linq;
using ActiveSync.Backends.Converters;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Protocol;
using Microsoft.Extensions.Logging;

namespace ActiveSync.Backends.Dav;

/// <summary>Contacts content store over CardDAV. Item keys are server hrefs; revisions are ETags.</summary>
public sealed class CardDavStore(
	WebDavClient dav,
	DavServerOptions options,
	BackendCredentials credentials,
	ILogger logger)
	: DavStoreBase(dav, options, credentials, logger), IContactOperations
{
	public const string KeyPrefix = "carddav:";

	protected override string Prefix => KeyPrefix;
	public override string EasClass => Protocol.EasClass.Contacts;
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

	// ---------- IContactOperations (GAL search) ----------

	public async Task<IReadOnlyList<IReadOnlyList<XElement>>> SearchGalAsync(
		string query, int maxResults, GalPhotoRequest? photos, CancellationToken ct)
	{
		List<IReadOnlyList<XElement>> results = new();
		int photosGranted = 0;
		foreach (BackendFolder folder in await ListFoldersAsync(ct).ConfigureAwait(false))
		{
			// H14: one addressbook-query REPORT returns the matching vCards INLINE (address-data),
			// instead of listing every href and then a serial GET per contact — the single largest
			// DAV performance defect. A server that rejects the REPORT (throws) falls back to the
			// per-contact GET path below; a server that ignores the filter and returns everything is
			// still correct because ToGalEntry filters client-side.
			IReadOnlyList<string>? cards = await QueryGalCardsAsync(folder.BackendKey, query, ct)
				.ConfigureAwait(false);
			IEnumerable<string> contents = cards ?? await GetCardsByEnumerationAsync(folder.BackendKey, ct)
				.ConfigureAwait(false);

			foreach (string vcf in contents)
			{
				if (results.Count >= maxResults)
					return results;
				List<XElement>? gal = ContactConverter.ToGalEntry(vcf, query);
				if (gal is null)
					continue;
				if (photos is not null &&
				    ContactConverter.AppendGalPicture(gal, vcf, photos.MaxSizeBytes,
					    photosGranted >= (photos.MaxCount ?? int.MaxValue)))
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
		string folderBackendKey, string query, CancellationToken ct)
	{
		string collection = FromBackendKey(folderBackendKey);
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
		foreach (DavResource resource in resources)
		{
			if (PathsEqual(resource.Href, collection))
				continue;
			string? data = resource.Propstat.Descendants(DavNs.CardDav + "address-data").FirstOrDefault()?.Value;
			if (!string.IsNullOrWhiteSpace(data))
				cards.Add(data);
		}

		return cards;

		static XElement PropFilter(string name, string text) =>
			new(DavNs.CardDav + "prop-filter", new XAttribute("name", name),
				new XElement(DavNs.CardDav + "text-match",
					new XAttribute("collation", "i;unicode-casemap"),
					new XAttribute("match-type", "contains"),
					text));
	}

	/// <summary>The pre-H14 fallback: list every href, then a GET per contact.</summary>
	private async Task<IReadOnlyList<string>> GetCardsByEnumerationAsync(
		string folderBackendKey, CancellationToken ct)
	{
		IReadOnlyDictionary<string, string> revisions =
			await GetItemRevisionsAsync(folderBackendKey, ContentFilter.All, ct).ConfigureAwait(false);
		List<string> cards = new();
		foreach (string href in revisions.Keys)
		{
			(string Content, string? ETag)? item = await Dav.GetAsync(href, ct).ConfigureAwait(false);
			if (item is not null)
				cards.Add(item.Value.Content);
		}

		return cards;
	}

	protected override IReadOnlyList<XElement>? ToApplicationData(string content, BodyPreference bodyPreference)
	{
		return ContactConverter.ToApplicationData(content, bodyPreference);
	}

	protected override string FromApplicationData(XElement applicationData, string uid, string? existingContent)
	{
		return ContactConverter.FromApplicationData(applicationData, uid, existingContent);
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
		// H22: multistatus order is server whim, and the first address book below becomes THE
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
			folders.Add(new BackendFolder(
				ToBackendKey(resource.Href),
				name,
				null,
				first ? EasFolderType.Contacts : EasFolderType.UserContacts,
				Protocol.EasClass.Contacts));
			first = false;
		}

		return folders;
	}

	public override async Task<IReadOnlyDictionary<string, string>> GetItemRevisionsAsync(
		string folderBackendKey, ContentFilter filter, CancellationToken ct)
	{
		string collection = FromBackendKey(folderBackendKey);
		XElement body = new(DavNs.D + "propfind",
			new XElement(DavNs.D + "prop", new XElement(DavNs.D + "getetag")));
		List<DavResource> resources = await Dav.PropfindAsync(collection, 1, body, ct).ConfigureAwait(false);

		Dictionary<string, string> map = new(StringComparer.Ordinal);
		foreach (DavResource resource in resources)
		{
			if (PathsEqual(resource.Href, collection))
				continue;
			string? etag = resource.Propstat.Descendants(DavNs.D + "getetag").FirstOrDefault()?.Value;
			if (etag is not null)
				map[resource.Href] = etag;
		}

		return map;
	}
}

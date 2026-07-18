using System.Xml.Linq;
using ActiveSync.Backends.Converters;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
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
			IReadOnlyDictionary<string, string> revisions =
				await GetItemRevisionsAsync(folder.BackendKey, ContentFilter.All, ct)
					.ConfigureAwait(false);
			foreach (string href in revisions.Keys)
			{
				if (results.Count >= maxResults)
					return results;
				(string Content, string? ETag)? item = await Dav.GetAsync(href, ct).ConfigureAwait(false);
				if (item is null)
					continue;
				List<XElement>? gal = ContactConverter.ToGalEntry(item.Value.Content, query);
				if (gal is null)
					continue;
				if (photos is not null &&
				    ContactConverter.AppendGalPicture(gal, item.Value.Content, photos.MaxSizeBytes,
					    photosGranted >= (photos.MaxCount ?? int.MaxValue)))
					photosGranted++;
				results.Add(gal);
			}
		}

		return results;
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
		foreach (DavResource resource in resources)
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

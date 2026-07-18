using System.Xml.Linq;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using Microsoft.Extensions.Logging;

namespace ActiveSync.Backends.Dav;

/// <summary>
///   Shared implementation for the CalDAV/CardDAV content stores (calendar, contacts, tasks).
///   Item keys are server hrefs and revisions are ETags; the pieces that genuinely differ per
///   content class — folder discovery, the revision listing, the UID query body, and the
///   iCal/vCard converter — are abstract hooks. The create/update flow (including the canonical
///   -href resolution that copes with servers rewriting PUT targets) and the throw-stubs for
///   unsupported folder operations live here once.
/// </summary>
public abstract class DavStoreBase(
	WebDavClient dav,
	DavServerOptions options,
	BackendCredentials credentials,
	ILogger logger) : IContentStore
{
	private string? _homeSet;

	protected WebDavClient Dav => dav;
	protected DavServerOptions Options => options;
	protected BackendCredentials Credentials => credentials;
	protected ILogger Logger => logger;

	// ---- per-content-class hooks ----

	/// <summary>Backend-key prefix (e.g. "caldav:"); exposed publicly as a const on each store.</summary>
	protected abstract string Prefix { get; }

	/// <summary>PUT media type ("text/calendar" | "text/vcard").</summary>
	protected abstract string MediaType { get; }

	/// <summary>New-resource href extension (".ics" | ".vcf").</summary>
	protected abstract string FileExtension { get; }

	/// <summary>RFC 6764 discovery path ("/.well-known/caldav" | "/.well-known/carddav").</summary>
	protected abstract string WellKnownPath { get; }

	/// <summary>Home-set property queried during discovery (calendar-home-set | addressbook-home-set).</summary>
	protected abstract XName HomeSetProperty { get; }

	/// <summary>Label for the "Discovered … home set" info log, or null to stay silent.</summary>
	protected virtual string? HomeSetDiscoveryLogLabel => null;

	/// <summary>Protocol label used in log/exception text ("CalDAV" | "CardDAV" | "CalDAV tasks").</summary>
	protected abstract string ProtocolLabel { get; }

	/// <summary>Singular item noun for messages ("event" | "contact" | "task").</summary>
	protected abstract string ItemNoun { get; }

	/// <summary>Plural item noun ("events" | "contacts" | "tasks").</summary>
	protected abstract string ItemNounPlural { get; }

	/// <summary>Plural collection noun ("calendars" | "address books" | "task collections").</summary>
	protected abstract string CollectionKindPlural { get; }

	/// <summary>Label passed to the ctag poller ("CalDAV" | "CardDAV" | "CalDAV-Tasks").</summary>
	protected abstract string CtagLabel { get; }

	// ---- shared plumbing ----

	private string ItemNounCapitalized =>
		char.ToUpperInvariant(ItemNoun[0]) + ItemNoun[1..];

	public abstract string EasClass { get; }

	public bool OwnsBackendKey(string backendKey)
	{
		return backendKey.StartsWith(Prefix, StringComparison.Ordinal);
	}

	public abstract Task<IReadOnlyList<BackendFolder>> ListFoldersAsync(CancellationToken ct);

	public abstract Task<IReadOnlyDictionary<string, string>> GetItemRevisionsAsync(
		string folderBackendKey, ContentFilter filter, CancellationToken ct);

	public async Task<BackendItem?> GetItemAsync(
		string folderBackendKey, string itemKey, BodyPreference bodyPreference, CancellationToken ct)
	{
		(string Content, string? ETag)? result = await dav.GetAsync(itemKey, ct).ConfigureAwait(false);
		if (result is null)
			return null;
		IReadOnlyList<XElement>? elements = ToApplicationData(result.Value.Content, bodyPreference);
		return elements is null ? null : new BackendItem(elements);
	}

	public async Task<(string ItemKey, string Revision)> CreateItemAsync(
		string folderBackendKey, XElement applicationData, CancellationToken ct)
	{
		string collection = FromBackendKey(folderBackendKey);
		// Listing snapshot from before the PUT — the fallback way to spot where the server
		// actually stored the new resource.
		IReadOnlyDictionary<string, string> before =
			await GetItemRevisionsAsync(folderBackendKey, ContentFilter.All, ct).ConfigureAwait(false);

		string uid = Guid.NewGuid().ToString();
		string content = FromApplicationData(applicationData, uid, null);
		string putHref = $"{collection.TrimEnd('/')}/{uid}{FileExtension}";
		string? putETag = await dav.PutAsync(putHref, content, MediaType, null, true, ct)
			.ConfigureAwait(false);

		(string href, string? listedETag) = await ResolveStoredHrefAsync(
			folderBackendKey, collection, putHref, uid, before, ct).ConfigureAwait(false);
		// Prefer the etag as the LISTING reports it — that is what future diffs compare.
		string etag = listedETag
		              ?? (PathsEqual(href, putHref) ? putETag : null)
		              ?? await dav.GetPropertyAsync(href, DavNs.D + "getetag", ct).ConfigureAwait(false)
		              ?? Guid.NewGuid().ToString();
		return (href, etag);
	}

	public async Task<string> UpdateItemAsync(
		string folderBackendKey, string itemKey, XElement applicationData, CancellationToken ct)
	{
		(string Content, string? ETag) existing = await dav.GetAsync(itemKey, ct).ConfigureAwait(false)
		                                          ?? throw new BackendItemNotFoundException(
			                                          $"{ItemNounCapitalized} {itemKey} no longer exists.");
		string uid = ExtractUid(existing.Content) ?? Guid.NewGuid().ToString();
		string content = FromApplicationData(applicationData, uid, existing.Content);
		string? etag = await dav.PutAsync(itemKey, content, MediaType, existing.ETag, false, ct)
			.ConfigureAwait(false);
		return etag ?? await dav.GetPropertyAsync(itemKey, DavNs.D + "getetag", ct).ConfigureAwait(false)
			?? Guid.NewGuid().ToString();
	}

	public Task DeleteItemAsync(string folderBackendKey, string itemKey, CancellationToken ct, bool permanent = false)
	{
		return dav.DeleteAsync(itemKey, ct); // DAV deletes are always permanent
	}

	public Task<string> MoveItemAsync(
		string sourceFolderBackendKey, string itemKey, string destinationFolderBackendKey, CancellationToken ct)
	{
		throw new BackendException(
			$"Moving {ItemNounPlural} between {CollectionKindPlural} is not supported.");
	}

	public Task<string> CreateFolderAsync(string? parentBackendKey, string displayName, CancellationToken ct)
	{
		throw new BackendException($"Creating {CollectionKindPlural} is not supported.");
	}

	public Task RenameFolderAsync(string backendKey, string newDisplayName, CancellationToken ct)
	{
		throw new BackendException($"Renaming {CollectionKindPlural} is not supported.");
	}

	public Task DeleteFolderAsync(string backendKey, CancellationToken ct)
	{
		throw new BackendException($"Deleting {CollectionKindPlural} is not supported.");
	}

	public Task<IReadOnlyList<string>> WaitForChangesAsync(
		IReadOnlyList<string> folderBackendKeys, TimeSpan timeout, CancellationToken ct)
	{
		return DavDiscovery.PollCtagsAsync(
			dav, folderBackendKeys, FromBackendKey, timeout, logger, CtagLabel, credentials.UserName, ct);
	}

	protected abstract IReadOnlyList<XElement>? ToApplicationData(string content, BodyPreference bodyPreference);
	protected abstract string FromApplicationData(XElement applicationData, string uid, string? existingContent);
	protected abstract string? ExtractUid(string content);

	/// <summary>The REPORT body that locates an item by UID within a collection.</summary>
	protected abstract XElement BuildUidQueryBody(string uid);

	protected string ToBackendKey(string href)
	{
		return Prefix + href;
	}

	protected string FromBackendKey(string key)
	{
		return key.StartsWith(Prefix, StringComparison.Ordinal)
			? key[Prefix.Length..]
			: throw new BackendException($"Not a {ProtocolLabel} key: {key}");
	}

	/// <summary>Href equality that ignores a trailing slash.</summary>
	protected static bool PathsEqual(string a, string b)
	{
		return a.TrimEnd('/').Equals(b.TrimEnd('/'), StringComparison.Ordinal);
	}

	protected async Task<string> GetHomeSetAsync(CancellationToken ct)
	{
		if (_homeSet is not null)
			return _homeSet;
		if (!string.IsNullOrEmpty(options.HomeSetPath))
		{
			_homeSet = DavDiscovery.ExpandTemplate(options.HomeSetPath, credentials.UserName);
			return _homeSet;
		}

		_homeSet = await DavDiscovery.DiscoverHomeSetAsync(dav, WellKnownPath, HomeSetProperty, ct)
			.ConfigureAwait(false);
		if (HomeSetDiscoveryLogLabel is { } label)
			logger.LogInformation("Discovered {Protocol} home set {HomeSet} for {User}",
				label, _homeSet, credentials.UserName);
		return _homeSet;
	}

	/// <summary>
	///   Determines the href the server actually stored a just-created resource under. Some
	///   servers (Axigen) rewrite the PUT target to their own canonical href — tracked blindly,
	///   the next diff would see an alien Add plus a Delete of the item the client just created,
	///   duplicating it on the device. Tries a UID query first, then falls back to diffing the
	///   collection listing from before the PUT; the listing is the same call the sync diff uses,
	///   so an adopted key always matches future diffs.
	/// </summary>
	protected async Task<(string Href, string? ETag)> ResolveStoredHrefAsync(
		string folderBackendKey, string collection, string putHref, string uid,
		IReadOnlyDictionary<string, string> before, CancellationToken ct)
	{
		// Trust the UID query only when it points at the PUT target or at a genuinely new
		// resource — weak servers ignore the filter and return pre-existing items.
		(string Href, string? ETag)? byUid = await FindByUidAsync(collection, uid, ct).ConfigureAwait(false);
		if (byUid is { } hit &&
		    (PathsEqual(hit.Href, putHref) || !before.ContainsKey(hit.Href)))
		{
			if (!PathsEqual(hit.Href, putHref))
				logger.LogDebug("{Protocol} stored {PutHref} under canonical href {CanonicalHref}",
					ProtocolLabel, putHref, hit.Href);
			return (hit.Href, hit.ETag);
		}

		IReadOnlyDictionary<string, string> after =
			await GetItemRevisionsAsync(folderBackendKey, ContentFilter.All, ct).ConfigureAwait(false);
		string? exact = after.Keys.FirstOrDefault(k => PathsEqual(k, putHref));
		if (exact is not null)
			return (exact, after[exact]);

		List<string> appeared = after.Keys.Where(k => !before.ContainsKey(k)).ToList();
		if (appeared.Count == 1)
		{
			logger.LogDebug(
				"{Protocol} stored {PutHref} under canonical href {CanonicalHref} (found via listing diff)",
				ProtocolLabel, putHref, appeared[0]);
			return (appeared[0], after[appeared[0]]);
		}

		logger.LogWarning(
			"{Protocol}: created {ItemNoun} {PutHref} could not be located in the collection listing " +
			"({AppearedCount} new entries); the next sync may briefly duplicate the item",
			ProtocolLabel, ItemNoun, putHref, appeared.Count);
		return (putHref, null);
	}

	/// <summary>Finds the canonical href (and etag) of the item with the given UID.</summary>
	protected async Task<(string Href, string? ETag)?> FindByUidAsync(
		string collection, string uid, CancellationToken ct)
	{
		List<DavResource> resources;
		try
		{
			resources = await dav.ReportAsync(collection, 1, BuildUidQueryBody(uid), ct).ConfigureAwait(false);
		}
		catch (BackendException)
		{
			return null; // server without UID-query support — keep the PUT href
		}

		DavResource? hit = resources.FirstOrDefault(r => !PathsEqual(r.Href, collection));
		if (hit is null)
			return null;
		return (hit.Href, hit.Propstat.Descendants(DavNs.D + "getetag").FirstOrDefault()?.Value);
	}
}

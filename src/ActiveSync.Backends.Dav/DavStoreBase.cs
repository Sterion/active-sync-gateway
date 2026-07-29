using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using Microsoft.Extensions.Logging;

namespace ActiveSync.Backends.Dav;

/// <summary>
///   Shared implementation for the CalDAV/CardDAV content stores (calendar, contacts, tasks).
///   Item keys are server hrefs and revisions are ETags; the pieces that genuinely differ per
///   content class — folder discovery, the revision listing, the UID query body, and the payload
///   record the class trades in — are abstract hooks. The store trades in the class's PAYLOAD
///   (iCalendar/vCard text) verbatim: what the host hands over is exactly what it stores and
///   exactly what it hands back. The create/update flow (including the canonical-href resolution
///   that copes with servers rewriting PUT targets) lives here once.
/// </summary>
/// <typeparam name="TItem">The content class's payload record.</typeparam>
public abstract class DavStoreBase<TItem>(
	WebDavClient dav,
	DavServerOptions options,
	BackendCredentials credentials,
	ILogger logger,
	int pollSeconds) : IContentStore<TItem> where TItem : class
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

	/// <inheritdoc />
	public bool OwnsKey(FolderKey key)
	{
		return key.Value.StartsWith(Prefix, StringComparison.Ordinal);
	}

	/// <inheritdoc />
	public abstract Task<IReadOnlyList<BackendFolder>> ListFoldersAsync(CancellationToken ct);

	/// <inheritdoc />
	public abstract Task<IReadOnlyDictionary<ItemKey, ItemRevision>> GetItemRevisionsAsync(
		FolderKey folder, ContentFilter filter, CancellationToken ct);

	/// <inheritdoc />
	public async Task<TItem?> GetItemAsync(FolderKey folder, ItemKey item, CancellationToken ct)
	{
		// The stored document IS the payload — round-trip fidelity: what the host handed over is
		// exactly what it gets back, with no parse/serialize pass in between.
		(string Content, string? ETag)? result = await dav.GetAsync(item.Value, ct).ConfigureAwait(false);
		return result is null ? null : ToItem(result.Value.Content);
	}

	/// <inheritdoc />
	public async Task<(ItemKey Key, ItemRevision Revision)> CreateItemAsync(
		FolderKey folder, TItem item, CancellationToken ct)
	{
		string collection = FromBackendKey(folder.Value);
		string content = PayloadOf(item);
		// The href is named after the payload's OWN uid (the host embeds one when it builds the
		// document), so the resource name and the document agree — which is what FindByUidAsync
		// below relies on. A payload without a readable uid falls back to a fresh guid.
		string uid = TryExtractUid(content) ?? Guid.NewGuid().ToString();
		string putHref = $"{collection.TrimEnd('/')}/{Uri.EscapeDataString(uid)}{FileExtension}";
		string? putETag = await dav.PutAsync(putHref, content, MediaType, null, true, ct)
			.ConfigureAwait(false);

		(string href, string? listedETag) = await ResolveStoredHrefAsync(
			folder, collection, putHref, uid, ct).ConfigureAwait(false);
		// Prefer the etag as the LISTING reports it — that is what future diffs compare.
		string etag = listedETag
		              ?? (PathsEqual(href, putHref) ? putETag : null)
		              ?? await dav.GetPropertyAsync(href, DavNs.D + "getetag", ct).ConfigureAwait(false)
		              ?? UnknownRevision;
		return (new ItemKey(href), new ItemRevision(etag));
	}

	/// <inheritdoc />
	public async Task<ItemRevision> UpdateItemAsync(
		FolderKey folder, ItemKey item, TItem value, ItemRevision? expected, CancellationToken ct)
	{
		// The host merged the client's partial data already — `value` is the COMPLETE payload, so
		// there is nothing to read back first (the pre-contract code fetched the stored document
		// only to feed the converter's ghosting merge, which now runs host-side).
		//
		// `expected` is honoured as an If-Match precondition: DAV can check it, so it does. A 412
		// surfaces as BackendPreconditionFailedException (WebDavClient maps it), and the host
		// re-fetches, re-merges and retries once. A null `expected` writes unconditionally.
		string content = PayloadOf(value);
		string? etag = await dav.PutAsync(item.Value, content, MediaType, expected?.Value, false, ct)
			.ConfigureAwait(false);
		return new ItemRevision(
			etag ?? await dav.GetPropertyAsync(item.Value, DavNs.D + "getetag", ct).ConfigureAwait(false)
			?? UnknownRevision);
	}

	/// <summary>
	///   Fallback revision when the server exposes no ETag at all for a just-created/updated
	///   item — neither on the PUT/response headers nor via a direct <c>getetag</c> PROPFIND. A
	///   fresh <c>Guid.NewGuid()</c> here looked exactly like a genuine opaque ETag while being
	///   unable to ever equal one (indistinguishable in a snapshot dump or log line from a real
	///   value, yet guaranteed to differ from whatever the next listing reports); this fixed,
	///   unmistakable placeholder is honest about what it is instead. It is intentionally NOT a
	///   valid ETag shape (no quotes) so it can never collide with one.
	/// </summary>
	private const string UnknownRevision = "!etag-unknown";

	/// <inheritdoc />
	public Task DeleteItemAsync(FolderKey folder, ItemKey item, bool permanent, CancellationToken ct)
	{
		return dav.DeleteAsync(item.Value, ct); // DAV deletes are always permanent
	}

	// DAV stores support neither cross-collection item move nor client folder mutation over
	// ActiveSync, so they implement neither IItemMoveOperations nor IFolderOperations (rather than
	// carrying throw-stubs). The host answers MoveItems/Folder* with the unsupported status.

	/// <inheritdoc />
	public async Task<IReadOnlyList<FolderKey>> WaitForChangesAsync(
		IReadOnlyList<FolderKey> folders, TimeSpan timeout, CancellationToken ct)
	{
		// The ctag poller keeps its internal string keys; the typed keys are wrapped here, at the
		// contract boundary.
		IReadOnlyList<string> keys = folders.Select(f => f.Value).ToList();
		IReadOnlyList<string> changed = await DavDiscovery.PollCtagsAsync(
			dav, keys, FromBackendKey, timeout, pollSeconds, logger, CtagLabel,
			credentials.UserName, ct).ConfigureAwait(false);
		return changed.Select(k => new FolderKey(k)).ToList();
	}

	/// <summary>The typed payload for a stored document (the payload IS the document).</summary>
	/// <param name="content">The stored iCalendar/vCard text.</param>
	protected abstract TItem ToItem(string content);

	/// <summary>The document to store for a complete payload (the payload IS the document).</summary>
	/// <param name="item">The complete payload the host handed over.</param>
	protected abstract string PayloadOf(TItem item);

	/// <summary>The payload's own UID, used to name a new resource's href; null when unreadable.</summary>
	/// <param name="content">The stored iCalendar/vCard text.</param>
	protected abstract string? ExtractUid(string content);

	/// <summary>The REPORT body that locates an item by UID within a collection.</summary>
	/// <param name="uid">The payload UID to search for.</param>
	protected abstract XElement BuildUidQueryBody(string uid);

	/// <summary><see cref="ExtractUid" /> that answers null for an unparsable document.</summary>
	private string? TryExtractUid(string content)
	{
		try
		{
			return ExtractUid(content);
		}
		catch (Exception)
		{
			return null;
		}
	}

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
	///   duplicating it on the device. Tries a UID query first; a hit at a different href is
	///   verified by fetching and checking its content (a full listing fetched
	///   AFTER the PUT already contains the new resource under whatever href it landed at, so
	///   presence in that listing can no longer distinguish "genuinely new" from "pre-existing";
	///   there is no valid pre-PUT baseline left to diff against once that fetch is lazy, so
	///   content is the only thing that still proves it's actually our item). Falls back to a
	///   content scan of the post-PUT listing when the UID query is unsupported or unverified. A
	///   well-behaved server (UID query resolves the exact PUT href) returns after one REPORT —
	///   no listing enumeration, no content fetch.
	/// </summary>
	protected async Task<(string Href, string? ETag)> ResolveStoredHrefAsync(
		FolderKey folder, string collection, string putHref, string uid, CancellationToken ct)
	{
		// Trust the UID query only when it points at the PUT target, or when a fetch of its
		// content confirms our uid — weak servers ignore the filter and echo back an unrelated
		// (possibly pre-existing) item, so a href mismatch alone proves nothing.
		(string Href, string? ETag)? byUid = await FindByUidAsync(collection, uid, ct).ConfigureAwait(false);
		if (byUid is { } hit)
		{
			if (PathsEqual(hit.Href, putHref))
				return (hit.Href, hit.ETag);

			(bool verified, string? verifiedETag) = await TryVerifyByContentAsync(hit.Href, uid, ct)
				.ConfigureAwait(false);
			if (verified)
			{
				logger.LogDebug("{Protocol} stored {PutHref} under canonical href {CanonicalHref}",
					ProtocolLabel, putHref, hit.Href);
				return (hit.Href, verifiedETag ?? hit.ETag);
			}
		}

		// Try the PUT target directly before paying for a full listing. On a server whose
		// listings AND UID-query index lag a PUT (Axigen indexes asynchronously — see AGENTS.md),
		// the UID query above and the listing below can both miss the item for up to ~a minute,
		// but a direct GET of putHref does not depend on either index — it resolves the common
		// case (server honoured the PUT target) in exactly one GET, with no listing enumeration
		// and no content scan at all.
		(bool putVerified, string? putVerifiedETag) = await TryVerifyByContentAsync(putHref, uid, ct)
			.ConfigureAwait(false);
		if (putVerified)
			return (putHref, putVerifiedETag);

		IReadOnlyDictionary<ItemKey, ItemRevision> after =
			await GetItemRevisionsAsync(folder, ContentFilter.All, ct).ConfigureAwait(false);
		foreach (ItemKey listed in after.Keys)
			if (PathsEqual(listed.Value, putHref))
				return (listed.Value, after[listed].Value);

		// Last resort: the UID query missed, the direct PUT-href GET missed (a genuinely rewritten
		// href, not just index lag), and the naive href isn't in the listing either — the only
		// remaining way to identify our item is by content, scanning the candidates the post-PUT
		// listing gave us. Bounded so a large collection cannot turn one create into thousands of
		// GETs when the server neither honoured the PUT target nor supports a UID query.
		foreach (ItemKey candidate in after.Keys.Take(ContentScanCeiling))
		{
			(bool verified, string? verifiedETag) = await TryVerifyByContentAsync(candidate.Value, uid, ct)
				.ConfigureAwait(false);
			if (verified)
			{
				logger.LogDebug(
					"{Protocol} stored {PutHref} under canonical href {CanonicalHref} (found via content scan)",
					ProtocolLabel, putHref, candidate.Value);
				return (candidate.Value, verifiedETag ?? after[candidate].Value);
			}
		}

		logger.LogWarning(
			"{Protocol}: created {ItemNoun} {PutHref} could not be located in the collection listing " +
			"({Count} candidates scanned); the next sync may briefly duplicate the item",
			ProtocolLabel, ItemNoun, putHref, Math.Min(after.Count, ContentScanCeiling));
		return (putHref, null);
	}

	/// <summary>
	///   Ceiling on the last-resort per-item content scan in <see cref="ResolveStoredHrefAsync" /> —
	///   bounds the worst case (server neither honours the PUT target nor supports a UID
	///   query) to a small, fixed number of GETs rather than one per item in the collection.
	/// </summary>
	private const int ContentScanCeiling = 50;

	/// <summary>
	///   Fetches <paramref name="href" /> and reports whether its content's UID matches
	///   <paramref name="uid" /> (with the fetched ETag, which may itself legitimately be null —
	///   kept separate from the verified flag so "no ETag" is never mistaken for "not verified").
	///   Not verified when the fetch 404s or the content belongs to a different item.
	/// </summary>
	private async Task<(bool Verified, string? ETag)> TryVerifyByContentAsync(
		string href, string uid, CancellationToken ct)
	{
		(string Content, string? ETag)? fetched = await dav.GetAsync(href, ct).ConfigureAwait(false);
		bool verified = fetched is { } f && string.Equals(TryExtractUid(f.Content), uid, StringComparison.Ordinal);
		return (verified, verified ? fetched!.Value.ETag : null);
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

using System.Text.Json;
using ActiveSync.Contracts;

namespace ActiveSync.Backends.Jmap;

/// <summary>
///   Contacts content store over JMAP (RFC 9610 / JSContact RFC 9553). Folder keys are
///   <c>jmap-contact:{addressBookId}</c>; item keys are ContactCard ids. Item revisions hash
///   the card JSON (JMAP exposes no per-card ETag). The contract's currency is vCard, so cards
///   bridge JSContact ⇄ vCard (<see cref="JsContactConverter" />) — the EAS half is the host's.
///   Also serves GAL search for ResolveRecipients/Search.
/// </summary>
public sealed class JmapContactStore(JmapClient client, int pollSeconds)
	: IContactStore, IDirectoryOperations, IItemMoveOperations
{
	public const string KeyPrefix = "jmap-contact:";

	private static readonly string[] Cap = [JmapCapabilities.Core, JmapCapabilities.Contacts];

	private string? _account;

	// The full account listing (state + cards) cached on the store instance. GetItemRevisionsAsync
	// is invoked once PER FOLDER within one Sync round, and SearchGalAsync adds another caller — without
	// this, M address books cost M full downloads of the same N cards. A cheap state-only check
	// (StateAsync) decides whether the cached list is still current before paying for a real download.
	private List<JsonElement>? _cachedCards;
	private string? _cachedCardsState;

	/// <inheritdoc />
	public bool OwnsKey(FolderKey key) => key.Value.StartsWith(KeyPrefix, StringComparison.Ordinal);

	/// <inheritdoc />
	public async Task<IReadOnlyList<BackendFolder>> ListFoldersAsync(CancellationToken ct)
	{
		string account = await AccountAsync(ct).ConfigureAwait(false);
		using JmapResponse response = await client.CallAsync(Cap, "AddressBook/get", new Dictionary<string, object?>
		{
			["accountId"] = account,
			["ids"] = null,
			["properties"] = new[] { "id", "name", "isDefault" }
		}, ct).ConfigureAwait(false);

		List<BackendFolder> result = new();
		foreach (JsonElement book in response.Arguments("0").GetProperty("list").EnumerateArray())
		{
			string id = book.GetProperty("id").GetString()!;
			bool isDefault = book.TryGetProperty("isDefault", out JsonElement d) && d.ValueKind == JsonValueKind.True;
			result.Add(new BackendFolder
			{
				Key = new FolderKey(KeyPrefix + id),
				DisplayName = book.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? id : id,
				Type = isDefault ? FolderType.Contacts : FolderType.UserContacts
			});
		}

		return result;
	}

	/// <inheritdoc />
	public async Task<IReadOnlyDictionary<ItemKey, ItemRevision>> GetItemRevisionsAsync(
		FolderKey folder, ContentFilter filter, CancellationToken ct)
	{
		string account = await AccountAsync(ct).ConfigureAwait(false);
		string bookId = FromKey(folder.Value);
		// Contacts have no EAS FilterType, so ContentFilter.ForClass(Contacts, …) is always
		// ContentFilter.All — there is no date window to apply here (CardDavStore likewise doesn't
		// filter contacts). Only the JMAP calendar store gained a filter.
		_ = filter;
		// ContactCard/get ids:null returns every card reliably; ContactCard/query is FTS-backed
		// and eventually-consistent (returns serverUnavailable right after a write), so listing
		// filters the full get by addressBookIds client-side instead.
		List<JsonElement> cards = await AllCardsAsync(account, ct).ConfigureAwait(false);
		return cards.Where(c => InBook(c, bookId))
			.ToDictionary(c => new ItemKey(c.GetProperty("id").GetString()!), c => new ItemRevision(Revision(c)));
	}

	/// <inheritdoc />
	public async Task<ContactItem?> GetItemAsync(FolderKey folder, ItemKey item, CancellationToken ct)
	{
		JsonElement? card = await GetCardAsync(item.Value, ct).ConfigureAwait(false);
		return card is { } c ? new ContactItem { VCard = JsContactConverter.ToVCard(c) } : null;
	}

	/// <inheritdoc />
	public async Task<(ItemKey Key, ItemRevision Revision)> CreateItemAsync(
		FolderKey folder, ContactItem item, CancellationToken ct)
	{
		// The host already built the COMPLETE vCard; this only bridges it to JSContact.
		string account = await AccountAsync(ct).ConfigureAwait(false);
		Dictionary<string, object?> card = JsContactConverter.FromVCard(item.VCard, null);
		card["addressBookIds"] = new Dictionary<string, object?> { [FromKey(folder.Value)] = true };
		using JmapResponse response = await client.CallAsync(Cap, "ContactCard/set", new Dictionary<string, object?>
		{
			["accountId"] = account,
			["create"] = new Dictionary<string, object?> { ["c"] = card }
		}, ct).ConfigureAwait(false);
		JsonElement args = response.Arguments("0");
		if (!args.TryGetProperty("created", out JsonElement created) || !created.TryGetProperty("c", out JsonElement made))
			throw new BackendException("JMAP ContactCard/set did not report the created card.");
		string id = made.GetProperty("id").GetString()!;
		JsonElement? full = await GetCardAsync(id, ct).ConfigureAwait(false);
		return (new ItemKey(id), new ItemRevision(full is { } f ? Revision(f) : "0"));
	}

	/// <inheritdoc />
	public async Task<ItemRevision> UpdateItemAsync(
		FolderKey folder, ItemKey item, ContactItem value, ItemRevision? expected, CancellationToken ct)
	{
		// The host merged already — `value` is the COMPLETE vCard. The existing card is still
		// fetched, but only to preserve the JSContact members the vCard bridge cannot express.
		string account = await AccountAsync(ct).ConfigureAwait(false);
		JsonElement? existing = await GetCardAsync(item.Value, ct).ConfigureAwait(false);
		// The `expected` precondition is honoured because the read above already paid for it: a
		// mismatch means the card moved underneath the host's merge basis, and the host re-fetches,
		// re-merges and retries once.
		if (expected is { } expectedRevision && existing is { } current &&
		    !string.Equals(Revision(current), expectedRevision.Value, StringComparison.Ordinal))
			throw new BackendPreconditionFailedException(
				$"JMAP ContactCard {item.Value} is no longer at the expected revision.");

		Dictionary<string, object?> card = JsContactConverter.FromVCard(value.VCard, existing);
		using JmapResponse response = await client.CallAsync(Cap, "ContactCard/set", new Dictionary<string, object?>
		{
			["accountId"] = account,
			["update"] = new Dictionary<string, object?> { [item.Value] = card }
		}, ct).ConfigureAwait(false);
		EnsureNotIn(response.Arguments("0"), "notUpdated", item.Value);
		JsonElement? full = await GetCardAsync(item.Value, ct).ConfigureAwait(false);
		return new ItemRevision(full is { } f ? Revision(f) : "0");
	}

	/// <inheritdoc />
	public async Task DeleteItemAsync(
		FolderKey folder, ItemKey item, bool permanent, CancellationToken ct)
	{
		string account = await AccountAsync(ct).ConfigureAwait(false);
		using JmapResponse response = await client.CallAsync(Cap, "ContactCard/set", new Dictionary<string, object?>
		{
			["accountId"] = account,
			["destroy"] = new[] { item.Value }
		}, ct).ConfigureAwait(false);
		EnsureNotIn(response.Arguments("0"), "notDestroyed", item.Value);
	}

	/// <inheritdoc />
	public async Task<(ItemKey Key, ItemRevision Revision)> MoveItemAsync(
		FolderKey source, ItemKey item, FolderKey destination, CancellationToken ct)
	{
		string account = await AccountAsync(ct).ConfigureAwait(false);
		using JmapResponse response = await client.CallAsync(Cap, "ContactCard/set", new Dictionary<string, object?>
		{
			["accountId"] = account,
			["update"] = new Dictionary<string, object?>
			{
				[item.Value] = new Dictionary<string, object?>
				{
					["addressBookIds"] = new Dictionary<string, object?> { [FromKey(destination.Value)] = true }
				}
			}
		}, ct).ConfigureAwait(false);
		EnsureNotIn(response.Arguments("0"), "notUpdated", item.Value);
		// Report the item's REAL revision at the destination, not a placeholder the caller
		// would otherwise have to invent (see UpdateItemAsync above for the identical shape).
		JsonElement? full = await GetCardAsync(item.Value, ct).ConfigureAwait(false);
		return (item, new ItemRevision(full is { } f ? Revision(f) : "0"));
	}

	// JMAP address-book folder mutation over ActiveSync is not supported, so this store does
	// not implement IFolderOperations (it does support item move — IItemMoveOperations above).

	/// <inheritdoc />
	public async Task<IReadOnlyList<FolderKey>> WaitForChangesAsync(
		IReadOnlyList<FolderKey> folders, TimeSpan timeout, CancellationToken ct)
	{
		string account = await AccountAsync(ct).ConfigureAwait(false);
		Dictionary<FolderKey, string> baseline = await TokensAsync(account, folders, ct).ConfigureAwait(false);
		DateTime deadline = DateTime.UtcNow + timeout;
		int delaySeconds = 1;
		int ceiling = Math.Max(1, pollSeconds);
		while (DateTime.UtcNow < deadline)
		{
			TimeSpan remaining = deadline - DateTime.UtcNow;
			TimeSpan delay = TimeSpan.FromSeconds(Math.Min(delaySeconds, ceiling));
			if (delay > remaining) delay = remaining;
			if (delay > TimeSpan.Zero) await Task.Delay(delay, ct).ConfigureAwait(false);
			delaySeconds = Math.Min(delaySeconds * 2, ceiling);

			Dictionary<FolderKey, string> current = await TokensAsync(account, folders, ct).ConfigureAwait(false);
			List<FolderKey> changed = folders
				.Where(k => baseline.GetValueOrDefault(k) != current.GetValueOrDefault(k))
				.ToList();
			if (changed.Count > 0)
				return changed;
		}

		return [];
	}

	// ---------- IDirectoryOperations (GAL) ----------

	/// <summary>
	///   Searches every address book and returns typed GAL entries, matched client-side against
	///   the cached full listing.
	/// </summary>
	/// <param name="query">The free-text query to match entries against.</param>
	/// <param name="maxResults">The most entries to return.</param>
	/// <param name="photos">The photo request, or <c>null</c> when pictures were not requested.</param>
	/// <param name="ct">Cancellation token for the backend round-trip.</param>
	/// <returns>The matching entries.</returns>
	public async Task<IReadOnlyList<GalEntry>> SearchGalAsync(
		string query, int maxResults, GalPhotoRequest? photos, CancellationToken ct)
	{
		// ContactCard/query is FTS-backed and eventually-consistent; GAL matches the full
		// get client-side instead (address books are small, and this never returns stale
		// "serverUnavailable").
		string account = await AccountAsync(ct).ConfigureAwait(false);
		List<JsonElement> cards = await AllCardsAsync(account, ct).ConfigureAwait(false);
		List<GalEntry> results = new();
		foreach (JsonElement card in cards)
		{
			GalEntry entry = JsContactConverter.ToGalEntry(card);
			bool matches = new[] { entry.DisplayName, entry.EmailAddress, entry.FirstName, entry.LastName,
					entry.Phone, entry.Company }
				.Any(v => v is not null && v.Contains(query, StringComparison.OrdinalIgnoreCase));
			if (matches)
			{
				// This bridge never reads a JSContact "media" member into a picture, so a requested
				// photo is always "has none" — the client asked, and the host must still emit an
				// explicit MS-ASCMD status (173) rather than silence, which is what the typed
				// None status carries.
				results.Add(photos is null
					? entry
					: entry with { Picture = new GalPictureResult { Status = GalPictureStatus.None } });
			}

			if (results.Count >= maxResults)
				break;
		}

		return results;
	}

	// ---------- helpers ----------

	public static string FromKey(string backendKey) =>
		backendKey.StartsWith(KeyPrefix, StringComparison.Ordinal)
			? backendKey[KeyPrefix.Length..]
			: throw new BackendException($"Not a JMAP contact folder key: {backendKey}");

	private async Task<JsonElement?> GetCardAsync(string itemKey, CancellationToken ct)
	{
		string account = await AccountAsync(ct).ConfigureAwait(false);
		using JmapResponse response = await client.CallAsync(Cap, "ContactCard/get", new Dictionary<string, object?>
		{
			["accountId"] = account,
			["ids"] = new[] { itemKey }
		}, ct).ConfigureAwait(false);
		JsonElement list = response.Arguments("0").GetProperty("list");
		return list.GetArrayLength() == 0 ? null : list[0].Clone();
	}

	private async Task<Dictionary<FolderKey, string>> TokensAsync(
		string account, IReadOnlyList<FolderKey> folders, CancellationToken ct)
	{
		// The wait token is the account-level ContactCard state instead of a SHA-256 over the
		// full body of every card, which used to be re-downloaded on every poll tick. The state is
		// account-wide, so a change in one address book shifts every watched book's token — the
		// wait over-notifies rather than misses (the safe direction). Mirrors the mail store's own
		// state-token wait.
		string state = await StateAsync(account, ct).ConfigureAwait(false);
		Dictionary<FolderKey, string> tokens = new();
		foreach (FolderKey folder in folders)
			tokens[folder] = state;
		return tokens;
	}

	// ContactCard/get with an empty id list returns just the current account-level state — no
	// card bodies — so this is cheap enough to call before every full download to decide whether
	// the cache is still current.
	private async Task<string> StateAsync(string account, CancellationToken ct)
	{
		using JmapResponse response = await client.CallAsync(Cap, "ContactCard/get", new Dictionary<string, object?>
		{
			["accountId"] = account,
			["ids"] = Array.Empty<string>()
		}, ct).ConfigureAwait(false);
		JsonElement args = response.Arguments("0");
		return args.TryGetProperty("state", out JsonElement s) ? s.GetString() ?? "" : "";
	}

	// Caches the full account listing on the store instance, keyed by the ContactCard state, so
	// a Sync round with M address books (GetItemRevisionsAsync is invoked once per folder, and
	// SearchGalAsync adds another caller) costs at most one real download, not M.
	private async Task<List<JsonElement>> AllCardsAsync(string account, CancellationToken ct)
	{
		string state = await StateAsync(account, ct).ConfigureAwait(false);
		if (_cachedCards is not null && string.Equals(_cachedCardsState, state, StringComparison.Ordinal))
			return _cachedCards;

		List<JsonElement> cards = await FetchAllCardsAsync(account, ct).ConfigureAwait(false);
		_cachedCards = cards;
		_cachedCardsState = state;
		return cards;
	}

	private async Task<List<JsonElement>> FetchAllCardsAsync(string account, CancellationToken ct)
	{
		JmapSessionResource session = await client.GetSessionAsync(ct).ConfigureAwait(false);
		if (session.CoreLimits.MaxObjectsInGet == int.MaxValue)
			return await FetchAllCardsUnboundedAsync(account, ct).ConfigureAwait(false);

		try
		{
			// A server that declares a finite maxObjectsInGet answers requestTooLarge to a
			// blind "ids:null" over a large address book — page the ids through ContactCard/query
			// (position-based, restarting on a queryState shift, the same protection the mail store's
			// Email/query paging uses) and fetch each page's bodies in maxObjectsInGet batches.
			return await FetchAllCardsPagedAsync(account, session, ct).ConfigureAwait(false);
		}
		catch (BackendException)
		{
			// ContactCard/query is FTS-backed and eventually-consistent on some servers (documented
			// above as answering serverUnavailable right after a write) — fall back to the simple,
			// always-consistent ids:null get rather than failing the whole folder sync over the
			// paging optimization.
			return await FetchAllCardsUnboundedAsync(account, ct).ConfigureAwait(false);
		}
	}

	private async Task<List<JsonElement>> FetchAllCardsUnboundedAsync(string account, CancellationToken ct)
	{
		using JmapResponse response = await client.CallAsync(Cap, "ContactCard/get", new Dictionary<string, object?>
		{
			["accountId"] = account,
			["ids"] = null
		}, ct).ConfigureAwait(false);
		return response.Arguments("0").GetProperty("list").EnumerateArray().Select(e => e.Clone()).ToList();
	}

	private async Task<List<JsonElement>> FetchAllCardsPagedAsync(
		string account, JmapSessionResource session, CancellationToken ct)
	{
		int page = Math.Max(1, Math.Min(500, session.CoreLimits.MaxObjectsInGet));
		List<JsonElement> cards = new();
		int position = 0;
		int previousPosition = -1;
		string? queryState = null;
		int restartsRemaining = 3;
		while (true)
		{
			JmapCall query = new("ContactCard/query", new Dictionary<string, object?>
			{
				["accountId"] = account,
				["position"] = position,
				["limit"] = page
			}, "0");
			JmapCall get = new("ContactCard/get", new Dictionary<string, object?>
			{
				["accountId"] = account,
				["#ids"] = ResultRef("0", "ContactCard/query", "/ids")
			}, "1");

			using JmapResponse response = await client.InvokeAsync(Cap, [query, get], ct).ConfigureAwait(false);
			JsonElement queryArgs = response.Arguments("0");

			// Same defence as the mail store's paging: a concurrent write can shift the (unsorted,
			// server-defined) result order between pages, so a queryState change restarts the whole
			// enumeration from position 0 instead of risking a dropped or duplicated card.
			string? currentState =
				queryArgs.TryGetProperty("queryState", out JsonElement qsEl) ? qsEl.GetString() : null;
			if (queryState is not null &&
			    !string.Equals(queryState, currentState, StringComparison.Ordinal) &&
			    restartsRemaining > 0)
			{
				restartsRemaining--;
				cards.Clear();
				position = 0;
				previousPosition = -1;
				queryState = null;
				continue;
			}

			queryState = currentState;
			foreach (JsonElement card in response.Arguments("1").GetProperty("list").EnumerateArray())
				cards.Add(card.Clone());

			int returned = queryArgs.GetProperty("ids").GetArrayLength();
			int reported = queryArgs.TryGetProperty("position", out JsonElement pos) && pos.TryGetInt32(out int pv)
				? pv
				: position;
			if (reported <= previousPosition)
				break; // a server that never advances position must not spin this loop forever
			previousPosition = reported;
			position = reported + returned;
			if (returned == 0)
				break;
			if (queryArgs.TryGetProperty("total", out JsonElement tot) && tot.TryGetInt64(out long total) &&
			    position >= total)
				break;
		}

		return cards;
	}

	private static Dictionary<string, object?> ResultRef(string resultOf, string name, string path)
	{
		return new Dictionary<string, object?> { ["resultOf"] = resultOf, ["name"] = name, ["path"] = path };
	}

	private static bool InBook(JsonElement card, string bookId)
	{
		return card.TryGetProperty("addressBookIds", out JsonElement books) && books.ValueKind == JsonValueKind.Object &&
		       books.TryGetProperty(bookId, out JsonElement v) && v.ValueKind == JsonValueKind.True;
	}

	private async Task<string> AccountAsync(CancellationToken ct)
	{
		if (_account is not null)
			return _account;
		JmapSessionResource session = await client.GetSessionAsync(ct).ConfigureAwait(false);
		// A server without the contacts capability gets a clear error, not an opaque 400 from
		// a request built with using:[…contacts] it never advertised support for.
		session.RequireCapability(JmapCapabilities.Contacts);
		return _account = session.PrimaryAccount(JmapCapabilities.Contacts);
	}

	// Hash a canonical form (members sorted), not the raw text, so a server re-ordering the same
	// card JSON does not flip the revision and re-sync the whole address book.
	private static string Revision(JsonElement card) => JmapRevision.Compute(card);

	private static void EnsureNotIn(JsonElement setResult, string bucket, string id)
	{
		if (setResult.TryGetProperty(bucket, out JsonElement failures) &&
		    failures.ValueKind == JsonValueKind.Object && failures.TryGetProperty(id, out JsonElement error))
		{
			string type = error.TryGetProperty("type", out JsonElement t) ? t.GetString() ?? "unknown" : "unknown";
			// A notFound SetError means the card is gone; surface it as not-found so the host
			// reconciles (re-add/delete) rather than treating the update/delete as a transient error.
			throw string.Equals(type, "notFound", StringComparison.Ordinal)
				? new BackendItemNotFoundException($"JMAP ContactCard {id} no longer exists.")
				: new BackendException($"JMAP ContactCard/set failed for '{id}': {type}.");
		}
	}
}

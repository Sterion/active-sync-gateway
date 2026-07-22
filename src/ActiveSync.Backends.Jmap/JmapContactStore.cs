using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;

namespace ActiveSync.Backends.Jmap;

/// <summary>
///   Contacts content store over JMAP (RFC 9610 / JSContact RFC 9553). Folder keys are
///   <c>jmap-contact:{addressBookId}</c>; item keys are ContactCard ids. Item revisions hash
///   the card JSON (JMAP exposes no per-card ETag). Also serves GAL search for
///   ResolveRecipients/Search.
/// </summary>
public sealed class JmapContactStore(JmapClient client, int pollSeconds)
	: IContentStore, IContactOperations, IItemMoveOperations
{
	public const string KeyPrefix = "jmap-contact:";

	private static readonly string[] Cap = [JmapCapabilities.Core, JmapCapabilities.Contacts];
	private static readonly XNamespace Gal = EasNamespaces.Gal;

	private string? _account;

	public string EasClass => Protocol.EasClass.Contacts;

	public bool OwnsBackendKey(string backendKey) => backendKey.StartsWith(KeyPrefix, StringComparison.Ordinal);

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
			result.Add(new BackendFolder(
				KeyPrefix + id,
				book.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? id : id,
				null,
				isDefault ? EasFolderType.Contacts : EasFolderType.UserContacts,
				Protocol.EasClass.Contacts));
		}

		return result;
	}

	public async Task<IReadOnlyDictionary<string, string>> GetItemRevisionsAsync(
		string folderBackendKey, ContentFilter filter, CancellationToken ct)
	{
		string account = await AccountAsync(ct).ConfigureAwait(false);
		string bookId = FromKey(folderBackendKey);
		// H29: Contacts have no EAS FilterType, so ContentFilter.ForClass(Contacts, …) is always
		// ContentFilter.All — there is no date window to apply here (CardDavStore likewise doesn't
		// filter contacts). Only the JMAP calendar store gained a filter.
		_ = filter;
		// ContactCard/get ids:null returns every card reliably; ContactCard/query is FTS-backed
		// and eventually-consistent (returns serverUnavailable right after a write), so listing
		// filters the full get by addressBookIds client-side instead.
		List<JsonElement> cards = await GetAllCardsAsync(account, ct).ConfigureAwait(false);
		return cards.Where(c => InBook(c, bookId))
			.ToDictionary(c => c.GetProperty("id").GetString()!, Revision, StringComparer.Ordinal);
	}

	public async Task<BackendItem?> GetItemAsync(
		string folderBackendKey, string itemKey, BodyPreference bodyPreference, CancellationToken ct)
	{
		JsonElement? card = await GetCardAsync(itemKey, ct).ConfigureAwait(false);
		return card is { } c ? new BackendItem(JsContactConverter.ToApplicationData(c, bodyPreference)) : null;
	}

	public async Task<(string ItemKey, string Revision)> CreateItemAsync(
		string folderBackendKey, XElement applicationData, CancellationToken ct)
	{
		string account = await AccountAsync(ct).ConfigureAwait(false);
		Dictionary<string, object?> card = JsContactConverter.FromApplicationData(applicationData, null);
		card["addressBookIds"] = new Dictionary<string, object?> { [FromKey(folderBackendKey)] = true };
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
		return (id, full is { } f ? Revision(f) : "0");
	}

	public async Task<string> UpdateItemAsync(
		string folderBackendKey, string itemKey, XElement applicationData, CancellationToken ct)
	{
		string account = await AccountAsync(ct).ConfigureAwait(false);
		JsonElement? existing = await GetCardAsync(itemKey, ct).ConfigureAwait(false);
		Dictionary<string, object?> card = JsContactConverter.FromApplicationData(applicationData, existing);
		using JmapResponse response = await client.CallAsync(Cap, "ContactCard/set", new Dictionary<string, object?>
		{
			["accountId"] = account,
			["update"] = new Dictionary<string, object?> { [itemKey] = card }
		}, ct).ConfigureAwait(false);
		EnsureNotIn(response.Arguments("0"), "notUpdated", itemKey);
		JsonElement? full = await GetCardAsync(itemKey, ct).ConfigureAwait(false);
		return full is { } f ? Revision(f) : "0";
	}

	public async Task DeleteItemAsync(
		string folderBackendKey, string itemKey, bool permanent, CancellationToken ct)
	{
		string account = await AccountAsync(ct).ConfigureAwait(false);
		using JmapResponse response = await client.CallAsync(Cap, "ContactCard/set", new Dictionary<string, object?>
		{
			["accountId"] = account,
			["destroy"] = new[] { itemKey }
		}, ct).ConfigureAwait(false);
		EnsureNotIn(response.Arguments("0"), "notDestroyed", itemKey);
	}

	public async Task<string> MoveItemAsync(
		string sourceFolderBackendKey, string itemKey, string destinationFolderBackendKey, CancellationToken ct)
	{
		string account = await AccountAsync(ct).ConfigureAwait(false);
		using JmapResponse response = await client.CallAsync(Cap, "ContactCard/set", new Dictionary<string, object?>
		{
			["accountId"] = account,
			["update"] = new Dictionary<string, object?>
			{
				[itemKey] = new Dictionary<string, object?>
				{
					["addressBookIds"] = new Dictionary<string, object?> { [FromKey(destinationFolderBackendKey)] = true }
				}
			}
		}, ct).ConfigureAwait(false);
		EnsureNotIn(response.Arguments("0"), "notUpdated", itemKey);
		return itemKey;
	}

	// K58: JMAP address-book folder mutation over ActiveSync is not supported, so this store does
	// not implement IFolderOperations (it does support item move — IItemMoveOperations above).

	public async Task<IReadOnlyList<string>> WaitForChangesAsync(
		IReadOnlyList<string> folderBackendKeys, TimeSpan timeout, CancellationToken ct)
	{
		string account = await AccountAsync(ct).ConfigureAwait(false);
		Dictionary<string, string> baseline = await TokensAsync(account, folderBackendKeys, ct).ConfigureAwait(false);
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

			Dictionary<string, string> current = await TokensAsync(account, folderBackendKeys, ct).ConfigureAwait(false);
			List<string> changed = folderBackendKeys
				.Where(k => baseline.GetValueOrDefault(k) != current.GetValueOrDefault(k))
				.ToList();
			if (changed.Count > 0)
				return changed;
		}

		return [];
	}

	// ---------- IContactOperations (GAL) ----------

	public async Task<IReadOnlyList<IReadOnlyList<XElement>>> SearchGalAsync(
		string query, int maxResults, GalPhotoRequest? photos, CancellationToken ct)
	{
		// ContactCard/query is FTS-backed and eventually-consistent; GAL matches the full
		// get client-side instead (address books are small, and this never returns stale
		// "serverUnavailable").
		string account = await AccountAsync(ct).ConfigureAwait(false);
		List<JsonElement> cards = await GetAllCardsAsync(account, ct).ConfigureAwait(false);
		List<IReadOnlyList<XElement>> results = new();
		foreach (JsonElement card in cards)
		{
			List<XElement> entry = GalEntry(card);
			bool matches = entry.Any(e => e.Value.Contains(query, StringComparison.OrdinalIgnoreCase));
			if (matches)
				results.Add(entry);
			if (results.Count >= maxResults)
				break;
		}

		return results;
	}

	private static List<XElement> GalEntry(JsonElement card)
	{
		List<XElement> data = JsContactConverter.ToApplicationData(card, BodyPreference.PlainText);

		string? First(string local) =>
			data.FirstOrDefault(e => e.Name.LocalName == local)?.Value;

		List<XElement> entry = new();
		string display = First("FileAs")
			?? string.Join(" ", new[] { First("FirstName"), First("LastName") }.Where(v => !string.IsNullOrEmpty(v)));
		entry.Add(new XElement(Gal + "DisplayName", display));
		if (First("Email1Address") is { } email) entry.Add(new XElement(Gal + "EmailAddress", email));
		if (First("FirstName") is { } first) entry.Add(new XElement(Gal + "FirstName", first));
		if (First("LastName") is { } last) entry.Add(new XElement(Gal + "LastName", last));
		if (First("MobilePhoneNumber") is { } phone) entry.Add(new XElement(Gal + "Phone", phone));
		if (First("CompanyName") is { } company) entry.Add(new XElement(Gal + "Company", company));
		return entry;
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

	private async Task<Dictionary<string, string>> TokensAsync(
		string account, IReadOnlyList<string> folderBackendKeys, CancellationToken ct)
	{
		// Per-folder change token = a hash of that book's (id:revision) set from the full get.
		List<JsonElement> cards = await GetAllCardsAsync(account, ct).ConfigureAwait(false);
		Dictionary<string, string> tokens = new(StringComparer.Ordinal);
		foreach (string folderKey in folderBackendKeys)
		{
			string bookId = FromKey(folderKey);
			string joined = string.Join(";", cards.Where(c => InBook(c, bookId))
				.Select(c => $"{c.GetProperty("id").GetString()}={Revision(c)}")
				.OrderBy(s => s, StringComparer.Ordinal));
			tokens[folderKey] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined)), 0, 8);
		}

		return tokens;
	}

	private async Task<List<JsonElement>> GetAllCardsAsync(string account, CancellationToken ct)
	{
		using JmapResponse response = await client.CallAsync(Cap, "ContactCard/get", new Dictionary<string, object?>
		{
			["accountId"] = account,
			["ids"] = null
		}, ct).ConfigureAwait(false);
		return response.Arguments("0").GetProperty("list").EnumerateArray().Select(e => e.Clone()).ToList();
	}

	private static bool InBook(JsonElement card, string bookId)
	{
		return card.TryGetProperty("addressBookIds", out JsonElement books) && books.ValueKind == JsonValueKind.Object &&
		       books.TryGetProperty(bookId, out JsonElement v) && v.ValueKind == JsonValueKind.True;
	}

	private async Task<string> AccountAsync(CancellationToken ct)
	{
		return _account ??= (await client.GetSessionAsync(ct).ConfigureAwait(false))
			.PrimaryAccount(JmapCapabilities.Contacts);
	}

	private static string Revision(JsonElement card)
	{
		byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(card.GetRawText()));
		return Convert.ToHexString(hash, 0, 8);
	}

	private static void EnsureNotIn(JsonElement setResult, string bucket, string id)
	{
		if (setResult.TryGetProperty(bucket, out JsonElement failures) &&
		    failures.ValueKind == JsonValueKind.Object && failures.TryGetProperty(id, out JsonElement error))
		{
			string type = error.TryGetProperty("type", out JsonElement t) ? t.GetString() ?? "unknown" : "unknown";
			// H20: a notFound SetError means the card is gone; surface it as not-found so the host
			// reconciles (re-add/delete) rather than treating the update/delete as a transient error.
			throw string.Equals(type, "notFound", StringComparison.Ordinal)
				? new BackendItemNotFoundException($"JMAP ContactCard {id} no longer exists.")
				: new BackendException($"JMAP ContactCard/set failed for '{id}': {type}.");
		}
	}
}

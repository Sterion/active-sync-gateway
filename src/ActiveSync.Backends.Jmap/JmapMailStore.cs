using System.Text.Json;
using ActiveSync.Contracts;

namespace ActiveSync.Backends.Jmap;

/// <summary>
///   Email content store + mailbox side-operations over JMAP (RFC 8621). Item keys are
///   JMAP <c>Email</c> ids (globally stable, so a move keeps the same key); folder keys are
///   <c>jmap-mail:{mailboxId}</c>. The store trades in the raw RFC822 blob
///   (<c>Email.blobId</c>) and typed flags — it neither reads nor writes EAS XML (the host owns
///   that conversion). The JMAP account id is resolved lazily from the cached session, so
///   construction stays I/O-free.
/// </summary>
/// <remarks>
///   Split by concern across three partials, mirroring the IMAP precedent
///   (<c>ImapMailBackend.Watch.cs</c>): this file holds folder/item CRUD + listing; free-text
///   search is in <c>JmapMailStore.Search.cs</c>; the Ping/Sync change-wait engine is in
///   <c>JmapMailStore.Watch.cs</c>. One type, no API change.
/// </remarks>
public sealed partial class JmapMailStore(
	JmapClient client,
	int pollSeconds,
	Func<DateTime, CancellationToken, Task>? waitForPush = null)
	: IMailStore, IMailboxOperations, IItemMoveOperations, IFolderOperations
{
	public const string KeyPrefix = "jmap-mail:";

	private static readonly string[] CapMail = [JmapCapabilities.Core, JmapCapabilities.Mail];

	private string? _account;

	// The mailbox role map (id→role and role→id) is stable for a session's lifetime — trash,
	// drafts and sent do not move — so it is resolved once and cached rather than re-listing every
	// mailbox (Mailbox/get ids:null) on every delete/create/draft-edit. The session is recycled on
	// a config change, which is when a re-resolve would matter. Guarded by _rolesGate so concurrent
	// Sync/Ping on one session do not each issue the load.
	private readonly SemaphoreSlim _rolesGate = new(1, 1);
	private Dictionary<string, string?>? _mailboxRole;
	private Dictionary<string, string>? _roleMailbox;

	/// <inheritdoc />
	public bool OwnsKey(FolderKey key)
	{
		return key.Value.StartsWith(KeyPrefix, StringComparison.Ordinal);
	}

	// ---------- folders ----------

	/// <inheritdoc />
	public async Task<IReadOnlyList<BackendFolder>> ListFoldersAsync(CancellationToken ct)
	{
		string account = await AccountAsync(ct).ConfigureAwait(false);
		using JmapResponse response = await client.CallAsync(CapMail, "Mailbox/get", new Dictionary<string, object?>
		{
			["accountId"] = account,
			["ids"] = null,
			["properties"] = new[] { "id", "name", "parentId", "role" }
		}, ct).ConfigureAwait(false);

		List<BackendFolder> result = new();
		foreach (JsonElement mailbox in response.Arguments("0").GetProperty("list").EnumerateArray())
		{
			string id = mailbox.GetProperty("id").GetString()!;
			string? parentId = mailbox.TryGetProperty("parentId", out JsonElement p) ? p.GetString() : null;
			string? role = mailbox.TryGetProperty("role", out JsonElement r) ? r.GetString() : null;
			result.Add(new BackendFolder
			{
				Key = new FolderKey(ToKey(id)),
				DisplayName = mailbox.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? id : id,
				ParentKey = parentId is null ? null : new FolderKey(ToKey(parentId)),
				Type = RoleToFolderType(role)
			});
		}

		return result;
	}

	/// <inheritdoc />
	public async Task<FolderKey> CreateFolderAsync(FolderKey? parent, string displayName, CancellationToken ct)
	{
		string account = await AccountAsync(ct).ConfigureAwait(false);
		Dictionary<string, object?> create = new() { ["name"] = displayName };
		if (parent is { } parentKey)
			create["parentId"] = FromKey(parentKey.Value);
		using JmapResponse response = await client.CallAsync(CapMail, "Mailbox/set", new Dictionary<string, object?>
		{
			["accountId"] = account,
			["create"] = new Dictionary<string, object?> { ["new"] = create }
		}, ct).ConfigureAwait(false);
		JsonElement args = response.Arguments("0");
		if (args.TryGetProperty("created", out JsonElement created) &&
		    created.TryGetProperty("new", out JsonElement mailbox) &&
		    mailbox.TryGetProperty("id", out JsonElement id))
			return new FolderKey(ToKey(id.GetString()!));
		throw new BackendException("JMAP Mailbox/set did not report the created mailbox.");
	}

	/// <inheritdoc />
	public async Task RenameFolderAsync(FolderKey folder, string newDisplayName, CancellationToken ct)
	{
		string account = await AccountAsync(ct).ConfigureAwait(false);
		using JmapResponse response = await client.CallAsync(CapMail, "Mailbox/set", new Dictionary<string, object?>
		{
			["accountId"] = account,
			["update"] = new Dictionary<string, object?>
			{
				[FromKey(folder.Value)] = new Dictionary<string, object?> { ["name"] = newDisplayName }
			}
		}, ct).ConfigureAwait(false);
		EnsureUpdated(response.Arguments("0"), FromKey(folder.Value), "Mailbox");
	}

	/// <inheritdoc />
	public async Task DeleteFolderAsync(FolderKey folder, CancellationToken ct)
	{
		string account = await AccountAsync(ct).ConfigureAwait(false);
		using JmapResponse response = await client.CallAsync(CapMail, "Mailbox/set", new Dictionary<string, object?>
		{
			["accountId"] = account,
			["onDestroyRemoveEmails"] = true,
			["destroy"] = new[] { FromKey(folder.Value) }
		}, ct).ConfigureAwait(false);
		EnsureDestroyed(response.Arguments("0"), FromKey(folder.Value), "Mailbox");
	}

	// ---------- items ----------

	/// <inheritdoc />
	public async Task<IReadOnlyDictionary<ItemKey, ItemRevision>> GetItemRevisionsAsync(
		FolderKey folder, ContentFilter filter, CancellationToken ct)
	{
		string account = await AccountAsync(ct).ConfigureAwait(false);
		string mailboxId = FromKey(folder.Value);
		// Page at min(500, maxObjectsInGet). The Email/get back-references up to a page of
		// Email/query ids, so a server advertising a lower maxObjectsInGet would answer
		// requestTooLarge and fail the whole folder sync if we asked for 500 blindly.
		int page = PageSize(await client.GetSessionAsync(ct).ConfigureAwait(false));
		Dictionary<ItemKey, ItemRevision> map = new();
		int position = 0;
		int previousPosition = -1;
		string? queryState = null;
		// Bounded restarts before falling back to whatever the last pass collected, rather than
		// restarting forever against a mailbox that never settles.
		int restartsRemaining = 3;
		while (true)
		{
			JmapCall query = new("Email/query", new Dictionary<string, object?>
			{
				["accountId"] = account,
				["filter"] = MailboxFilter(mailboxId, filter),
				["sort"] = new object[] { new Dictionary<string, object?> { ["property"] = "receivedAt", ["isAscending"] = false } },
				["position"] = position,
				["limit"] = page
			}, "0");
			JmapCall get = new("Email/get", new Dictionary<string, object?>
			{
				["accountId"] = account,
				["#ids"] = ResultRef("0", "Email/query", "/ids"),
				["properties"] = new[] { "id", "keywords" }
			}, "1");

			using JmapResponse response = await client.InvokeAsync(CapMail, [query, get], ct).ConfigureAwait(false);
			JsonElement queryArgs = response.Arguments("0");

			// `queryState` (RFC 8620 §5.5) changes whenever the result set shifts. A concurrent
			// insert/remove earlier in the descending-sort order slides every later item's position,
			// so continuing at our own `position` can skip an item entirely — it lands in the gap
			// between the page we already read and the page we are about to ask for, and never comes
			// back in either. Restart the whole enumeration from position 0 instead of trusting it.
			string? currentState =
				queryArgs.TryGetProperty("queryState", out JsonElement qsEl) ? qsEl.GetString() : null;
			if (queryState is not null &&
			    !string.Equals(queryState, currentState, StringComparison.Ordinal) &&
			    restartsRemaining > 0)
			{
				restartsRemaining--;
				map.Clear();
				position = 0;
				previousPosition = -1;
				queryState = null;
				continue;
			}

			queryState = currentState;
			foreach (JsonElement email in response.Arguments("1").GetProperty("list").EnumerateArray())
				map[new ItemKey(email.GetProperty("id").GetString()!)] =
					new ItemRevision(RevisionOf(KeywordsOf(email)));

			int returned = queryArgs.GetProperty("ids").GetArrayLength();
			// A short page does NOT mean "done" — servers may return fewer than requested. Advance
			// by the server's own reported position (it may clamp ours) and stop only when a page comes
			// back empty, or the server's reported total has been reached. Terminating on
			// `returned < page` truncated the folder, silently dropping the tail.
			int reported = queryArgs.TryGetProperty("position", out JsonElement pos) && pos.TryGetInt32(out int pv)
				? pv
				: position;
			// `total` is normally absent (calculateTotal is never requested), so the
			// `position >= total` guard below is usually dead — termination rests on the position
			// actually advancing. A server that ignores our `position` and keeps reporting the same
			// value must not spin this loop forever.
			if (reported <= previousPosition)
				break;
			previousPosition = reported;
			position = reported + returned;
			if (returned == 0)
				break;
			if (queryArgs.TryGetProperty("total", out JsonElement tot) && tot.TryGetInt64(out long total) &&
			    position >= total)
				break;
		}

		return map;
	}

	/// <inheritdoc />
	public async Task<MailItem?> GetItemAsync(
		FolderKey folder, ItemKey item, MailFetchOptions options, CancellationToken ct)
	{
		string account = await AccountAsync(ct).ConfigureAwait(false);
		JsonElement? email = await GetEmailAsync(account, item.Value, ["id", "blobId", "keywords", "receivedAt"], ct)
			.ConfigureAwait(false);
		if (email is not { } value || !value.TryGetProperty("blobId", out JsonElement blob) || blob.GetString() is not { } blobId)
			return null;

		IReadOnlyList<string> keywords = KeywordsOf(value);
		// Prefer JMAP's own delivery timestamp over the sender-supplied Date: header.
		DateTimeOffset? receivedAt = value.TryGetProperty("receivedAt", out JsonElement receivedAtEl) &&
		                              receivedAtEl.TryGetDateTimeOffset(out DateTimeOffset receivedAtValue)
			? receivedAtValue
			: null;
		// The raw blob is handed over verbatim — the contract's currency is the RFC822 bytes, and
		// a parse/serialize round trip here could silently normalize them.
		byte[] raw = await client.DownloadBlobAsync(account, blobId, ct).ConfigureAwait(false);
		return new MailItem
		{
			Rfc822 = raw,
			Flags = FlagsOf(keywords),
			Categories = CategoriesOf(keywords),
			Received = receivedAt
		};
	}

	/// <inheritdoc />
	public async Task<(ItemKey Key, ItemRevision Revision)> CreateDraftAsync(
		FolderKey folder, MailItem item, CancellationToken ct)
	{
		// EAS 16.x drafts: the only mail a client may create via Sync, and only in Drafts. The
		// host built the complete MIME already — this only imports the bytes.
		string account = await AccountAsync(ct).ConfigureAwait(false);
		string mailboxId = FromKey(folder.Value);
		if (!string.Equals(await RoleOfAsync(account, mailboxId, ct).ConfigureAwait(false), "drafts", StringComparison.Ordinal))
			throw new BackendException("Creating mail items via Sync is only supported in the Drafts folder.");

		string emailId = await ImportAsync(account, item.Rfc822, mailboxId, ct).ConfigureAwait(false);
		return (new ItemKey(emailId), new ItemRevision("000"));
	}

	/// <inheritdoc />
	public async Task<ItemRevision> UpdateFlagsAsync(
		FolderKey folder, ItemKey item, MailFlagsPatch patch, ItemRevision? expected, CancellationToken ct)
	{
		// `expected` is deliberately ignored: the JMAP keyword patch below is a per-property
		// PatchObject with no per-item precondition to hang an ifInState on (the account-level
		// state advances on ANY mail change, so conditioning on it would fail constantly on a busy
		// mailbox) — which the contract says is conforming.
		string account = await AccountAsync(ct).ConfigureAwait(false);
		Dictionary<string, object?> keywordPatch = new();
		if (patch.Read.HasValue)
			keywordPatch["keywords/$seen"] = patch.Read.Value ? true : null;

		if (patch.Flagged.HasValue)
			keywordPatch["keywords/$flagged"] = patch.Flagged.Value ? true : null;

		// Presence-guarded like Read/Flagged: only a supplied category list touches the message's
		// keywords — and only the category-relevant (non-'$') subset, so a client clearing its
		// categories can never strip $forwarded or another system keyword.
		if (patch.Categories.HasValue)
		{
			IReadOnlyList<string> current = await CategoriesOfAsync(account, item.Value, ct).ConfigureAwait(false);
			// '/' and '~' ARE legal JMAP keyword characters (RFC 8621 §4.1.1) but PatchObject
			// keys are JSON Pointers (RFC 8620 §5.3 → RFC 6901), where '/' separates path segments —
			// a category like "Work/Home" must be pointer-escaped via PointerToken below, not
			// dropped. A category containing a character the keyword grammar itself forbids IS
			// dropped, mirroring ImapMailBackend.SanitizeKeyword's drop-don't-mangle rule.
			HashSet<string> wanted = patch.Categories.Value
				.Where(v => v.Length > 0 && !v.StartsWith('$') && IsValidJmapKeyword(v))
				.ToHashSet(StringComparer.OrdinalIgnoreCase);
			foreach (string add in wanted.Where(w => !current.Contains(w, StringComparer.OrdinalIgnoreCase)))
				keywordPatch[$"keywords/{PointerToken(add)}"] = true;
			foreach (string remove in current.Where(c => !wanted.Contains(c)))
				keywordPatch[$"keywords/{PointerToken(remove)}"] = null;
		}

		if (keywordPatch.Count > 0)
		{
			// Batch the Email/set and the trailing Email/get into ONE request instead of two
			// sequential round trips. JMAP runs method calls in order, so the get reflects the set;
			// the item key is known, so the get uses an explicit id list (no result reference needed).
			IReadOnlyList<JmapCall> calls =
			[
				new JmapCall("Email/set", new Dictionary<string, object?>
				{
					["accountId"] = account,
					["update"] = new Dictionary<string, object?> { [item.Value] = keywordPatch }
				}, "0"),
				new JmapCall("Email/get", new Dictionary<string, object?>
				{
					["accountId"] = account,
					["ids"] = new[] { item.Value },
					["properties"] = new[] { "id", "keywords" }
				}, "1")
			];
			using JmapResponse response = await client.InvokeAsync(CapMail, calls, ct).ConfigureAwait(false);
			EnsureUpdated(response.Arguments("0"), item.Value, "Email");
			JsonElement setList = response.Arguments("1").GetProperty("list");
			return new ItemRevision(
				setList.GetArrayLength() == 0 ? "000" : RevisionOf(KeywordsOf(setList[0])));
		}

		JsonElement? updated = await GetEmailAsync(account, item.Value, ["id", "keywords"], ct).ConfigureAwait(false);
		return new ItemRevision(updated is { } e ? RevisionOf(KeywordsOf(e)) : "000");
	}

	/// <inheritdoc />
	public async Task<(ItemKey Key, ItemRevision Revision)> ReplaceDraftAsync(
		FolderKey folder, ItemKey item, MailItem value, CancellationToken ct)
	{
		// EAS 16.x draft edit: the host merged the client's partial data into the stored draft and
		// hands over the complete replacement. The rewrite imports the new message and destroys the
		// old id — the new id is REPORTED (informational; the host keeps the snapshot on the old
		// key, so the next diff re-identifies as Delete+Add, the standard EAS flow).
		string account = await AccountAsync(ct).ConfigureAwait(false);
		string mailboxId = FromKey(folder.Value);
		if (!string.Equals(await RoleOfAsync(account, mailboxId, ct).ConfigureAwait(false), "drafts", StringComparison.Ordinal))
			throw new BackendException("Changing mail content via Sync is only supported in the Drafts folder.");

		string newId = await ImportAsync(account, value.Rfc822, mailboxId, ct).ConfigureAwait(false);
		// Dispose the response and surface a per-item destroy failure instead of leaking
		// the JsonDocument and assuming success — a lingering old draft duplicates the message.
		using JmapResponse destroyOld = await client.CallAsync(CapMail, "Email/set", new Dictionary<string, object?>
		{
			["accountId"] = account,
			["destroy"] = new[] { item.Value }
		}, ct).ConfigureAwait(false);
		EnsureDestroyed(destroyOld.Arguments("0"), item.Value, "Email");
		return (new ItemKey(newId), new ItemRevision("000"));
	}

	/// <inheritdoc />
	public async Task DeleteItemAsync(
		FolderKey folder, ItemKey item, bool permanent, CancellationToken ct)
	{
		string account = await AccountAsync(ct).ConfigureAwait(false);
		string? trashId = permanent ? null : await FindMailboxByRoleAsync(account, "trash", ct).ConfigureAwait(false);
		if (trashId is null || string.Equals(trashId, FromKey(folder.Value), StringComparison.Ordinal))
		{
			// Dispose the response and check the per-item destroy bucket rather than assuming
			// success on a leaked document.
			using JmapResponse destroyResponse = await client.CallAsync(CapMail, "Email/set", new Dictionary<string, object?>
			{
				["accountId"] = account,
				["destroy"] = new[] { item.Value }
			}, ct).ConfigureAwait(false);
			EnsureDestroyed(destroyResponse.Arguments("0"), item.Value, "Email");
			return;
		}

		// Patch only the two affected keys (RFC 8620 §5.3 PatchObject) instead of replacing
		// "mailboxIds" wholesale — a message filed under more than one mailbox (e.g. a label
		// alongside Inbox) must keep every OTHER membership across a single-folder EAS delete.
		using JmapResponse response = await client.CallAsync(CapMail, "Email/set", new Dictionary<string, object?>
		{
			["accountId"] = account,
			["update"] = new Dictionary<string, object?>
			{
				[item.Value] = new Dictionary<string, object?>
				{
					[$"mailboxIds/{FromKey(folder.Value)}"] = null,
					[$"mailboxIds/{trashId}"] = true
				}
			}
		}, ct).ConfigureAwait(false);
		EnsureUpdated(response.Arguments("0"), item.Value, "Email");
	}

	/// <inheritdoc />
	public async Task<(ItemKey Key, ItemRevision Revision)> MoveItemAsync(
		FolderKey source, ItemKey item, FolderKey destination, CancellationToken ct)
	{
		string account = await AccountAsync(ct).ConfigureAwait(false);
		string itemKey = item.Value;
		string sourceId = FromKey(source.Value);
		string destId = FromKey(destination.Value);
		// Same PatchObject shape as DeleteItemAsync above — drop only the source mailbox
		// membership, not every mailbox the message happened to be filed under.
		// Batch the move with a trailing keywords fetch (same shape as UpdateItemAsync) so
		// the caller gets the item's REAL post-move revision in one round trip instead of storing a
		// placeholder that can never match the next listing's revision.
		IReadOnlyList<JmapCall> calls =
		[
			new JmapCall("Email/set", new Dictionary<string, object?>
			{
				["accountId"] = account,
				["update"] = new Dictionary<string, object?>
				{
					[itemKey] = new Dictionary<string, object?>
					{
						[$"mailboxIds/{sourceId}"] = null,
						[$"mailboxIds/{destId}"] = true
					}
				}
			}, "0"),
			new JmapCall("Email/get", new Dictionary<string, object?>
			{
				["accountId"] = account,
				["ids"] = new[] { itemKey },
				["properties"] = new[] { "id", "keywords" }
			}, "1")
		];
		using JmapResponse response = await client.InvokeAsync(CapMail, calls, ct).ConfigureAwait(false);
		EnsureUpdated(response.Arguments("0"), itemKey, "Email");
		JsonElement getList = response.Arguments("1").GetProperty("list");
		string revision = getList.GetArrayLength() == 0 ? "000" : RevisionOf(KeywordsOf(getList[0]));
		return (item, new ItemRevision(revision)); // JMAP Email ids are stable across mailbox moves
	}

	// ---------- IMailboxOperations ----------

	/// <inheritdoc />
	public async Task SaveToSentAsync(ReadOnlyMemory<byte> rfc822, CancellationToken ct)
	{
		string account = await AccountAsync(ct).ConfigureAwait(false);
		string? sentId = await FindMailboxByRoleAsync(account, "sent", ct).ConfigureAwait(false);
		if (sentId is null)
			return;
		string blobId = await client.UploadBlobAsync(account, rfc822.ToArray(), "message/rfc822", ct)
			.ConfigureAwait(false);
		// Dispose the response and surface an import failure — a dropped Save-to-Sent leaves
		// the user's Sent folder missing the message they just sent.
		// Email/import is a Mail-capability method (RFC 8621 §4.8) — no Blob capability
		// needed. Requiring urn:ietf:params:jmap:blob (RFC 9404) here rejected the WHOLE request on
		// any server that does not implement that separate extension.
		using JmapResponse response = await client.CallAsync(CapMail, "Email/import", new Dictionary<string, object?>
		{
			["accountId"] = account,
			["emails"] = new Dictionary<string, object?>
			{
				["sent"] = new Dictionary<string, object?>
				{
					["blobId"] = blobId,
					["mailboxIds"] = new Dictionary<string, object?> { [sentId] = true },
					["keywords"] = new Dictionary<string, object?> { ["$seen"] = true }
				}
			}
		}, ct).ConfigureAwait(false);
		EnsureNotIn(response.Arguments("0"), "notCreated", "sent", "Email", "import");
	}

	/// <inheritdoc />
	public async Task<ReadOnlyMemory<byte>?> GetRawMessageAsync(
		FolderKey folder, ItemKey item, CancellationToken ct)
	{
		string account = await AccountAsync(ct).ConfigureAwait(false);
		byte[]? raw = await GetRawByIdAsync(account, item.Value, ct).ConfigureAwait(false);
		return raw is null ? null : (ReadOnlyMemory<byte>?)raw;
	}

	/// <inheritdoc />
	public async Task SetAnsweredAsync(FolderKey folder, ItemKey item, bool forwarded, CancellationToken ct)
	{
		string account = await AccountAsync(ct).ConfigureAwait(false);
		string keyword = forwarded ? "$forwarded" : "$answered";
		using JmapResponse response = await client.CallAsync(CapMail, "Email/set", new Dictionary<string, object?>
		{
			["accountId"] = account,
			["update"] = new Dictionary<string, object?>
			{
				[item.Value] = new Dictionary<string, object?> { [$"keywords/{keyword}"] = true }
			}
		}, ct).ConfigureAwait(false);
		EnsureUpdated(response.Arguments("0"), item.Value, "Email");
	}

	/// <inheritdoc />
	public async Task EmptyFolderAsync(FolderKey folder, CancellationToken ct)
	{
		string account = await AccountAsync(ct).ConfigureAwait(false);
		string mailboxId = FromKey(folder.Value);
		// Cap the destroy batch at maxObjectsInSet (defaulting to 500) — a batch over the
		// server's limit is rejected wholesale. The loop re-queries from the top after each destroy
		// and stops only when a page comes back empty, never on a short page (which does not mean the
		// folder is empty and previously left messages behind).
		int batch = DestroyBatchSize(await client.GetSessionAsync(ct).ConfigureAwait(false));
		while (true)
		{
			using JmapResponse response = await client.CallAsync(CapMail, "Email/query", new Dictionary<string, object?>
			{
				["accountId"] = account,
				["filter"] = new Dictionary<string, object?> { ["inMailbox"] = mailboxId },
				["limit"] = batch
			}, ct).ConfigureAwait(false);
			string[] ids = response.Arguments("0").GetProperty("ids").EnumerateArray()
				.Select(e => e.GetString()!).ToArray();
			if (ids.Length == 0)
				break;
			// Dispose the response and surface a batch destroy failure rather than looping on
			// a leaked document — a message the server refused to delete would otherwise reappear
			// in the very next Email/query page and spin this loop forever.
			using JmapResponse destroyResponse = await client.CallAsync(CapMail, "Email/set", new Dictionary<string, object?>
			{
				["accountId"] = account,
				["destroy"] = ids
			}, ct).ConfigureAwait(false);
			EnsureNoneFailed(destroyResponse.Arguments("0"), "notDestroyed", "Email", "destroy");
		}
	}

	// ---------- helpers ----------

	public static string ToKey(string mailboxId) => KeyPrefix + mailboxId;

	public static string FromKey(string backendKey)
	{
		return backendKey.StartsWith(KeyPrefix, StringComparison.Ordinal)
			? backendKey[KeyPrefix.Length..]
			: throw new BackendException($"Not a JMAP mail folder key: {backendKey}");
	}

	private async Task<string> AccountAsync(CancellationToken ct)
	{
		if (_account is not null)
			return _account;
		JmapSessionResource session = await client.GetSessionAsync(ct).ConfigureAwait(false);
		// Fail fast if the server does not advertise mail, rather than sending a request it
		// cannot honour and surfacing the opaque error back.
		session.RequireCapability(JmapCapabilities.Mail);
		return _account = session.PrimaryAccount(JmapCapabilities.Mail);
	}

	private async Task<byte[]?> GetRawByIdAsync(string account, string itemKey, CancellationToken ct)
	{
		JsonElement? email = await GetEmailAsync(account, itemKey, ["id", "blobId"], ct).ConfigureAwait(false);
		if (email is { } value && value.TryGetProperty("blobId", out JsonElement blob) && blob.GetString() is { } blobId)
			return await client.DownloadBlobAsync(account, blobId, ct).ConfigureAwait(false);
		return null;
	}

	private async Task<string> ImportAsync(
		string account, ReadOnlyMemory<byte> rfc822, string mailboxId, CancellationToken ct)
	{
		string blobId = await client.UploadBlobAsync(account, rfc822.ToArray(), "message/rfc822", ct)
			.ConfigureAwait(false);
		// Email/import is a Mail-capability method (RFC 8621 §4.8) — no Blob capability
		// needed. Requiring urn:ietf:params:jmap:blob (RFC 9404) here rejected the WHOLE request on
		// any server that does not implement that separate extension.
		using JmapResponse response = await client.CallAsync(CapMail, "Email/import", new Dictionary<string, object?>
		{
			["accountId"] = account,
			["emails"] = new Dictionary<string, object?>
			{
				["draft"] = new Dictionary<string, object?>
				{
					["blobId"] = blobId,
					["mailboxIds"] = new Dictionary<string, object?> { [mailboxId] = true },
					["keywords"] = new Dictionary<string, object?> { ["$draft"] = true }
				}
			}
		}, ct).ConfigureAwait(false);
		JsonElement args = response.Arguments("0");
		if (args.TryGetProperty("created", out JsonElement created) &&
		    created.TryGetProperty("draft", out JsonElement email) &&
		    email.TryGetProperty("id", out JsonElement id))
			return id.GetString()!;
		throw new BackendException("JMAP Email/import did not report the created message.");
	}

	private async Task<JsonElement?> GetEmailAsync(string account, string itemKey, string[] properties, CancellationToken ct)
	{
		using JmapResponse response = await client.CallAsync(CapMail, "Email/get", new Dictionary<string, object?>
		{
			["accountId"] = account,
			["ids"] = new[] { itemKey },
			["properties"] = properties
		}, ct).ConfigureAwait(false);
		JsonElement list = response.Arguments("0").GetProperty("list");
		return list.GetArrayLength() == 0 ? null : list[0].Clone();
	}

	private async Task<IReadOnlyList<string>> CategoriesOfAsync(string account, string itemKey, CancellationToken ct)
	{
		JsonElement? email = await GetEmailAsync(account, itemKey, ["id", "keywords"], ct).ConfigureAwait(false);
		return email is { } e ? CategoriesOf(KeywordsOf(e)) : [];
	}

	/// <summary>The typed contract flags for a message's JMAP keywords.</summary>
	private static MailFlags FlagsOf(IReadOnlyList<string> keywords)
	{
		return new MailFlags
		{
			Seen = keywords.Contains("$seen"),
			Flagged = keywords.Contains("$flagged"),
			Answered = keywords.Contains("$answered"),
			Forwarded = keywords.Contains("$forwarded"),
			Draft = keywords.Contains("$draft")
		};
	}

	/// <summary>The user categories: every keyword that is not a system ('$'-prefixed) one.</summary>
	private static IReadOnlyList<string> CategoriesOf(IReadOnlyList<string> keywords)
	{
		return keywords.Where(k => !k.StartsWith('$')).ToList();
	}

	private async Task<string?> RoleOfAsync(string account, string mailboxId, CancellationToken ct)
	{
		Dictionary<string, string?> byId = await MailboxRolesAsync(account, ct).ConfigureAwait(false);
		return byId.GetValueOrDefault(mailboxId);
	}

	private async Task<string?> FindMailboxByRoleAsync(string account, string role, CancellationToken ct)
	{
		await MailboxRolesAsync(account, ct).ConfigureAwait(false);
		return _roleMailbox!.GetValueOrDefault(role);
	}

	/// <summary>
	///   The cached mailbox-id→role map, resolved once per session with a single
	///   <c>Mailbox/get ids:null</c>. Also populates the reverse role→id map for
	///   <see cref="FindMailboxByRoleAsync" />.
	/// </summary>
	private async Task<Dictionary<string, string?>> MailboxRolesAsync(string account, CancellationToken ct)
	{
		if (_mailboxRole is not null)
			return _mailboxRole;
		await _rolesGate.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			if (_mailboxRole is not null)
				return _mailboxRole;
			using JmapResponse response = await client.CallAsync(CapMail, "Mailbox/get", new Dictionary<string, object?>
			{
				["accountId"] = account,
				["ids"] = null,
				["properties"] = new[] { "id", "role" }
			}, ct).ConfigureAwait(false);
			Dictionary<string, string?> byId = new(StringComparer.Ordinal);
			Dictionary<string, string> byRole = new(StringComparer.Ordinal);
			foreach (JsonElement mailbox in response.Arguments("0").GetProperty("list").EnumerateArray())
			{
				string id = mailbox.GetProperty("id").GetString()!;
				string? role = mailbox.TryGetProperty("role", out JsonElement r) ? r.GetString() : null;
				byId[id] = role;
				if (role is not null)
					byRole[role] = id;
			}

			_roleMailbox = byRole;
			_mailboxRole = byId;
			return _mailboxRole;
		}
		finally
		{
			_rolesGate.Release();
		}
	}

	private static IReadOnlyList<string> KeywordsOf(JsonElement email)
	{
		List<string> keywords = new();
		if (email.TryGetProperty("keywords", out JsonElement k) && k.ValueKind == JsonValueKind.Object)
			foreach (JsonProperty p in k.EnumerateObject())
				if (p.Value.ValueKind == JsonValueKind.True)
					keywords.Add(p.Name);
		return keywords;
	}

	// A mail item's revision: the sync-relevant JMAP keywords as "seen flagged answered"
	// digits, plus "|cat1,cat2" only when category (non-$) keywords exist — so a message
	// with no categories keeps the compact 3-digit form. Kept byte-stable (fixed digit
	// order, sorted categories) because Ping/Sync compares it against the stored snapshot.
	private static string RevisionOf(IReadOnlyList<string> keywords)
	{
		string digits =
			$"{(keywords.Contains("$seen") ? 1 : 0)}{(keywords.Contains("$flagged") ? 1 : 0)}{(keywords.Contains("$answered") ? 1 : 0)}";
		List<string> categories = keywords
			.Where(k => !k.StartsWith('$'))
			.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
			.ToList();
		return categories.Count == 0 ? digits : $"{digits}|{string.Join(',', categories)}";
	}

	// The desired listing page — never larger than 500, and clamped down to the server's
	// maxObjectsInGet so the Email/get back-reference never exceeds what the server accepts.
	private static int PageSize(JmapSessionResource session)
	{
		return Math.Max(1, Math.Min(500, session.CoreLimits.MaxObjectsInGet));
	}

	// The Empty-folder destroy batch — bounded by maxObjectsInSet, since the whole page is destroyed
	// in one Email/set.
	private static int DestroyBatchSize(JmapSessionResource session)
	{
		return Math.Max(1, Math.Min(500, session.CoreLimits.MaxObjectsInSet));
	}

	private static Dictionary<string, object?> MailboxFilter(string mailboxId, ContentFilter filter)
	{
		Dictionary<string, object?> f = new() { ["inMailbox"] = mailboxId };
		if (filter.Since is { } since)
			f["after"] = JmapDate.ToUtc(since.UtcDateTime);
		return f;
	}

	private static Dictionary<string, object?> ResultRef(string resultOf, string name, string path)
	{
		return new Dictionary<string, object?> { ["resultOf"] = resultOf, ["name"] = name, ["path"] = path };
	}

	// RFC 6901 pointer-escapes a keyword before it is spliced into a PatchObject path
	// ("keywords/{token}") — '~' must be escaped FIRST or a keyword already containing a
	// pointer-escape sequence would be double-escaped.
	private static string PointerToken(string keyword) => keyword.Replace("~", "~0").Replace("/", "~1");

	// RFC 8621 §4.1.1: a JMAP keyword MUST NOT contain '(' ')' '{' ']' '%' '*' '"' '\' or any
	// non-ASCII character. '/' and '~' are legal (they only need PointerToken's escaping above).
	private static bool IsValidJmapKeyword(string keyword)
	{
		foreach (char c in keyword)
			if (c <= ' ' || c >= (char)127 || "(){]%*\"\\".Contains(c))
				return false;

		return true;
	}

	private static FolderType RoleToFolderType(string? role)
	{
		return role switch
		{
			"inbox" => FolderType.Inbox,
			"drafts" => FolderType.Drafts,
			"trash" => FolderType.DeletedItems,
			"sent" => FolderType.SentItems,
			_ => FolderType.UserMail
		};
	}

	private static void EnsureUpdated(JsonElement setResult, string id, string kind)
	{
		EnsureNotIn(setResult, "notUpdated", id, kind, "update");
	}

	private static void EnsureDestroyed(JsonElement setResult, string id, string kind)
	{
		EnsureNotIn(setResult, "notDestroyed", id, kind, "destroy");
	}

	private static void EnsureNotIn(JsonElement setResult, string bucket, string id, string kind, string verb)
	{
		if (setResult.TryGetProperty(bucket, out JsonElement failures) &&
		    failures.ValueKind == JsonValueKind.Object &&
		    failures.TryGetProperty(id, out JsonElement error))
			throw SetError(kind, verb, id, error);
	}

	/// <summary>Throws if a batch */set bucket (e.g. notDestroyed over many ids) carries any entry.</summary>
	private static void EnsureNoneFailed(JsonElement setResult, string bucket, string kind, string verb)
	{
		if (setResult.TryGetProperty(bucket, out JsonElement failures) &&
		    failures.ValueKind == JsonValueKind.Object)
			foreach (JsonProperty failure in failures.EnumerateObject())
				throw SetError(kind, verb, failure.Name, failure.Value);
	}

	/// <summary>
	///   Maps a JMAP SetError to an exception. A <c>notFound</c> type becomes
	///   <see cref="BackendItemNotFoundException" /> so the host reconciles an item the server no
	///   longer has (re-add/delete) instead of treating a doomed update/delete as a generic
	///   transient failure — or, worse, as success.
	/// </summary>
	private static BackendException SetError(string kind, string verb, string id, JsonElement error)
	{
		string type = error.TryGetProperty("type", out JsonElement t) ? t.GetString() ?? "unknown" : "unknown";
		return string.Equals(type, "notFound", StringComparison.Ordinal)
			? new BackendItemNotFoundException($"JMAP {kind} {id} no longer exists.")
			: new BackendException($"JMAP {kind}/{verb} failed for '{id}': {type}.");
	}
}

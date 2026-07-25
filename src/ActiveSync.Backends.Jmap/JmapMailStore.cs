using System.Text.Json;
using System.Xml.Linq;
using ActiveSync.Backends.Common.Converters;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;
using MimeKit;

namespace ActiveSync.Backends.Jmap;

/// <summary>
///   Email content store + mail-store side-operations over JMAP (RFC 8621). Item keys are
///   JMAP <c>Email</c> ids (globally stable, so a move keeps the same key); folder keys are
///   <c>jmap-mail:{mailboxId}</c>. Message bodies are reused through the shared MIME
///   converters by downloading the raw RFC822 blob (<c>Email.blobId</c>). The JMAP account id
///   is resolved lazily from the cached session, so construction stays I/O-free.
/// </summary>
/// <remarks>
///   Split by concern across four partials (S4), mirroring the IMAP precedent
///   (<c>ImapMailBackend.Watch.cs</c>): this file holds folder/item CRUD + listing; free-text
///   search is in <c>JmapMailStore.Search.cs</c>; the Ping/Sync change-wait engine is in
///   <c>JmapMailStore.Watch.cs</c>; attachment fetch + the file-reference codec are in
///   <c>JmapMailStore.Attachments.cs</c>. One type, no API change.
/// </remarks>
public sealed partial class JmapMailStore(
	JmapClient client,
	string? mailAddress,
	int pollSeconds,
	Func<DateTime, CancellationToken, Task>? waitForPush = null)
	: IContentStore, IMailStoreOperations, IItemMoveOperations, IFolderOperations
{
	public const string KeyPrefix = "jmap-mail:";

	private static readonly string[] CapMail = [JmapCapabilities.Core, JmapCapabilities.Mail];
	private static readonly string[] CapMailBlob = [JmapCapabilities.Core, JmapCapabilities.Mail, JmapCapabilities.Blob];

	private static readonly XNamespace Email = EasNamespaces.Email;
	private static readonly XNamespace Email2 = EasNamespaces.Email2;
	private static readonly XNamespace AirSyncBase = EasNamespaces.AirSyncBase;

	private string? _account;

	// H25: the mailbox role map (id→role and role→id) is stable for a session's lifetime — trash,
	// drafts and sent do not move — so it is resolved once and cached rather than re-listing every
	// mailbox (Mailbox/get ids:null) on every delete/create/draft-edit. The session is recycled on
	// a config change, which is when a re-resolve would matter. Guarded by _rolesGate so concurrent
	// Sync/Ping on one session do not each issue the load.
	private readonly SemaphoreSlim _rolesGate = new(1, 1);
	private Dictionary<string, string?>? _mailboxRole;
	private Dictionary<string, string>? _roleMailbox;

	public string EasClass => Protocol.EasClass.Email;

	public bool OwnsBackendKey(string backendKey)
	{
		return backendKey.StartsWith(KeyPrefix, StringComparison.Ordinal);
	}

	// ---------- folders ----------

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
			result.Add(new BackendFolder(
				ToKey(id),
				mailbox.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? id : id,
				parentId is null ? null : ToKey(parentId),
				RoleToEasType(role),
				Protocol.EasClass.Email));
		}

		return result;
	}

	public async Task<string> CreateFolderAsync(string? parentBackendKey, string displayName, CancellationToken ct)
	{
		string account = await AccountAsync(ct).ConfigureAwait(false);
		Dictionary<string, object?> create = new() { ["name"] = displayName };
		if (parentBackendKey is not null)
			create["parentId"] = FromKey(parentBackendKey);
		using JmapResponse response = await client.CallAsync(CapMail, "Mailbox/set", new Dictionary<string, object?>
		{
			["accountId"] = account,
			["create"] = new Dictionary<string, object?> { ["new"] = create }
		}, ct).ConfigureAwait(false);
		JsonElement args = response.Arguments("0");
		if (args.TryGetProperty("created", out JsonElement created) &&
		    created.TryGetProperty("new", out JsonElement mailbox) &&
		    mailbox.TryGetProperty("id", out JsonElement id))
			return ToKey(id.GetString()!);
		throw new BackendException("JMAP Mailbox/set did not report the created mailbox.");
	}

	public async Task RenameFolderAsync(string backendKey, string newDisplayName, CancellationToken ct)
	{
		string account = await AccountAsync(ct).ConfigureAwait(false);
		using JmapResponse response = await client.CallAsync(CapMail, "Mailbox/set", new Dictionary<string, object?>
		{
			["accountId"] = account,
			["update"] = new Dictionary<string, object?>
			{
				[FromKey(backendKey)] = new Dictionary<string, object?> { ["name"] = newDisplayName }
			}
		}, ct).ConfigureAwait(false);
		EnsureUpdated(response.Arguments("0"), FromKey(backendKey), "Mailbox");
	}

	public async Task DeleteFolderAsync(string backendKey, CancellationToken ct)
	{
		string account = await AccountAsync(ct).ConfigureAwait(false);
		using JmapResponse response = await client.CallAsync(CapMail, "Mailbox/set", new Dictionary<string, object?>
		{
			["accountId"] = account,
			["onDestroyRemoveEmails"] = true,
			["destroy"] = new[] { FromKey(backendKey) }
		}, ct).ConfigureAwait(false);
		EnsureDestroyed(response.Arguments("0"), FromKey(backendKey), "Mailbox");
	}

	// ---------- items ----------

	public async Task<IReadOnlyDictionary<string, string>> GetItemRevisionsAsync(
		string folderBackendKey, ContentFilter filter, CancellationToken ct)
	{
		string account = await AccountAsync(ct).ConfigureAwait(false);
		string mailboxId = FromKey(folderBackendKey);
		// H8: page at min(500, maxObjectsInGet). The Email/get back-references up to a page of
		// Email/query ids, so a server advertising a lower maxObjectsInGet would answer
		// requestTooLarge and fail the whole folder sync if we asked for 500 blindly.
		int page = PageSize(await client.GetSessionAsync(ct).ConfigureAwait(false));
		Dictionary<string, string> map = new(StringComparer.Ordinal);
		int position = 0;
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
			foreach (JsonElement email in response.Arguments("1").GetProperty("list").EnumerateArray())
				map[email.GetProperty("id").GetString()!] = RevisionOf(KeywordsOf(email));

			JsonElement queryArgs = response.Arguments("0");
			int returned = queryArgs.GetProperty("ids").GetArrayLength();
			// H8: a short page does NOT mean "done" — servers may return fewer than requested. Advance
			// by the server's own reported position (it may clamp ours) and stop only when a page comes
			// back empty, or the server's reported total has been reached. Terminating on
			// `returned < page` truncated the folder, silently dropping the tail.
			int reported = queryArgs.TryGetProperty("position", out JsonElement pos) && pos.TryGetInt32(out int pv)
				? pv
				: position;
			position = reported + returned;
			if (returned == 0)
				break;
			if (queryArgs.TryGetProperty("total", out JsonElement tot) && tot.TryGetInt64(out long total) &&
			    position >= total)
				break;
		}

		return map;
	}

	public async Task<BackendItem?> GetItemAsync(
		string folderBackendKey, string itemKey, BodyPreference bodyPreference, CancellationToken ct)
	{
		string account = await AccountAsync(ct).ConfigureAwait(false);
		JsonElement? email = await GetEmailAsync(account, itemKey, ["id", "blobId", "keywords"], ct).ConfigureAwait(false);
		if (email is not { } value || !value.TryGetProperty("blobId", out JsonElement blob) || blob.GetString() is not { } blobId)
			return null;

		IReadOnlyList<string> keywords = KeywordsOf(value);
		byte[] raw = await client.DownloadBlobAsync(account, blobId, ct).ConfigureAwait(false);
		using MemoryStream stream = new(raw);
		MimeMessage message = await MimeMessage.LoadAsync(stream, ct).ConfigureAwait(false);
		MailConverter.MessageFlags flags = new(
			keywords.Contains("$seen"),
			keywords.Contains("$flagged"),
			keywords.Contains("$answered"),
			keywords.Contains("$forwarded"),
			keywords.Where(k => !k.StartsWith('$')).ToList());
		List<XElement> data = MailConverter.ToApplicationData(
			message, flags, bodyPreference, idx => MakeFileReference(folderBackendKey, itemKey, idx));
		return new BackendItem(data);
	}

	public async Task<(string ItemKey, string Revision)> CreateItemAsync(
		string folderBackendKey, XElement applicationData, CancellationToken ct)
	{
		// EAS 16.x drafts: the only mail a client may create via Sync, and only in Drafts.
		string account = await AccountAsync(ct).ConfigureAwait(false);
		string mailboxId = FromKey(folderBackendKey);
		if (!string.Equals(await RoleOfAsync(account, mailboxId, ct).ConfigureAwait(false), "drafts", StringComparison.Ordinal))
			throw new BackendException("Creating mail items via Sync is only supported in the Drafts folder.");

		MimeMessage draft = DraftMessageBuilder.Build(applicationData, null, mailAddress);
		string emailId = await ImportAsync(account, draft, mailboxId, ct).ConfigureAwait(false);
		return (emailId, "000");
	}

	public async Task<string> UpdateItemAsync(
		string folderBackendKey, string itemKey, XElement applicationData, CancellationToken ct)
	{
		string account = await AccountAsync(ct).ConfigureAwait(false);
		string mailboxId = FromKey(folderBackendKey);

		// EAS 16.x draft edit: content-bearing changes in Drafts rewrite the message (import
		// merged draft, destroy the old id). The new id replaces the old — the snapshot diff
		// turns that into Delete+Add, the standard EAS re-identification flow.
		if (HasDraftContent(applicationData) &&
		    string.Equals(await RoleOfAsync(account, mailboxId, ct).ConfigureAwait(false), "drafts", StringComparison.Ordinal))
		{
			JsonElement? existing = await GetEmailAsync(account, itemKey, ["id", "blobId"], ct).ConfigureAwait(false);
			MimeMessage? original = null;
			if (existing is { } value && value.TryGetProperty("blobId", out JsonElement blob) && blob.GetString() is { } blobId)
			{
				byte[] raw = await client.DownloadBlobAsync(account, blobId, ct).ConfigureAwait(false);
				using MemoryStream stream = new(raw);
				original = await MimeMessage.LoadAsync(stream, ct).ConfigureAwait(false);
			}

			MimeMessage merged = DraftMessageBuilder.Build(applicationData, original, mailAddress);
			await ImportAsync(account, merged, mailboxId, ct).ConfigureAwait(false);
			// H10: dispose the response and surface a per-item destroy failure instead of leaking
			// the JsonDocument and assuming success — a lingering old draft duplicates the message.
			using JmapResponse destroyOld = await client.CallAsync(CapMail, "Email/set", new Dictionary<string, object?>
			{
				["accountId"] = account,
				["destroy"] = new[] { itemKey }
			}, ct).ConfigureAwait(false);
			EnsureDestroyed(destroyOld.Arguments("0"), itemKey, "Email");
			return "000";
		}

		Dictionary<string, object?> patch = new();
		string? read = applicationData.Element(Email + "Read")?.Value;
		if (read is not null)
			patch["keywords/$seen"] = read == "1" ? true : null;

		XElement? flag = applicationData.Element(Email + "Flag");
		if (flag is not null)
			patch["keywords/$flagged"] = flag.Element(Email + "Status")?.Value == "2" ? true : null;

		XElement? categories = applicationData.Element(Email + "Categories");
		if (categories is not null)
		{
			IReadOnlyList<string> current = await CategoriesOfAsync(account, itemKey, ct).ConfigureAwait(false);
			HashSet<string> wanted = categories.Elements(Email + "Category")
				.Select(c => c.Value)
				.Where(v => v.Length > 0 && !v.StartsWith('$'))
				.ToHashSet(StringComparer.OrdinalIgnoreCase);
			foreach (string add in wanted.Where(w => !current.Contains(w, StringComparer.OrdinalIgnoreCase)))
				patch[$"keywords/{add}"] = true;
			foreach (string remove in current.Where(c => !wanted.Contains(c)))
				patch[$"keywords/{remove}"] = null;
		}

		if (patch.Count > 0)
		{
			// H25: batch the Email/set and the trailing Email/get into ONE request instead of two
			// sequential round trips. JMAP runs method calls in order, so the get reflects the set;
			// itemKey is known, so the get uses an explicit id list (no result reference needed).
			IReadOnlyList<JmapCall> calls =
			[
				new JmapCall("Email/set", new Dictionary<string, object?>
				{
					["accountId"] = account,
					["update"] = new Dictionary<string, object?> { [itemKey] = patch }
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
			JsonElement setList = response.Arguments("1").GetProperty("list");
			return setList.GetArrayLength() == 0 ? "000" : RevisionOf(KeywordsOf(setList[0]));
		}

		JsonElement? updated = await GetEmailAsync(account, itemKey, ["id", "keywords"], ct).ConfigureAwait(false);
		return updated is { } e ? RevisionOf(KeywordsOf(e)) : "000";
	}

	public async Task DeleteItemAsync(
		string folderBackendKey, string itemKey, bool permanent, CancellationToken ct)
	{
		string account = await AccountAsync(ct).ConfigureAwait(false);
		string? trashId = permanent ? null : await FindMailboxByRoleAsync(account, "trash", ct).ConfigureAwait(false);
		if (trashId is null || string.Equals(trashId, FromKey(folderBackendKey), StringComparison.Ordinal))
		{
			// H10: dispose the response and check the per-item destroy bucket rather than assuming
			// success on a leaked document.
			using JmapResponse destroyResponse = await client.CallAsync(CapMail, "Email/set", new Dictionary<string, object?>
			{
				["accountId"] = account,
				["destroy"] = new[] { itemKey }
			}, ct).ConfigureAwait(false);
			EnsureDestroyed(destroyResponse.Arguments("0"), itemKey, "Email");
			return;
		}

		// H7: patch only the two affected keys (RFC 8620 §5.3 PatchObject) instead of replacing
		// "mailboxIds" wholesale — a message filed under more than one mailbox (e.g. a label
		// alongside Inbox) must keep every OTHER membership across a single-folder EAS delete.
		using JmapResponse response = await client.CallAsync(CapMail, "Email/set", new Dictionary<string, object?>
		{
			["accountId"] = account,
			["update"] = new Dictionary<string, object?>
			{
				[itemKey] = new Dictionary<string, object?>
				{
					[$"mailboxIds/{FromKey(folderBackendKey)}"] = null,
					[$"mailboxIds/{trashId}"] = true
				}
			}
		}, ct).ConfigureAwait(false);
		EnsureUpdated(response.Arguments("0"), itemKey, "Email");
	}

	public async Task<(string ItemKey, string Revision)> MoveItemAsync(
		string sourceFolderBackendKey, string itemKey, string destinationFolderBackendKey, CancellationToken ct)
	{
		string account = await AccountAsync(ct).ConfigureAwait(false);
		string sourceId = FromKey(sourceFolderBackendKey);
		string destId = FromKey(destinationFolderBackendKey);
		// H7: same PatchObject shape as DeleteItemAsync above — drop only the source mailbox
		// membership, not every mailbox the message happened to be filed under.
		// F5: batch the move with a trailing keywords fetch (same H25 shape as UpdateItemAsync) so
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
		return (itemKey, revision); // JMAP Email ids are stable across mailbox moves
	}

	// ---------- IMailStoreOperations ----------

	public async Task SaveToSentAsync(byte[] mime, CancellationToken ct)
	{
		string account = await AccountAsync(ct).ConfigureAwait(false);
		string? sentId = await FindMailboxByRoleAsync(account, "sent", ct).ConfigureAwait(false);
		if (sentId is null)
			return;
		string blobId = await client.UploadBlobAsync(account, mime, "message/rfc822", ct).ConfigureAwait(false);
		// H10: dispose the response and surface an import failure — a dropped Save-to-Sent leaves
		// the user's Sent folder missing the message they just sent.
		using JmapResponse response = await client.CallAsync(CapMailBlob, "Email/import", new Dictionary<string, object?>
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

	public async Task<byte[]?> GetRawMessageAsync(string folderBackendKey, string itemKey, CancellationToken ct)
	{
		string account = await AccountAsync(ct).ConfigureAwait(false);
		return await GetRawByIdAsync(account, itemKey, ct).ConfigureAwait(false);
	}

	public async Task SetAnsweredAsync(string folderBackendKey, string itemKey, bool forwarded, CancellationToken ct)
	{
		string account = await AccountAsync(ct).ConfigureAwait(false);
		string keyword = forwarded ? "$forwarded" : "$answered";
		using JmapResponse response = await client.CallAsync(CapMail, "Email/set", new Dictionary<string, object?>
		{
			["accountId"] = account,
			["update"] = new Dictionary<string, object?>
			{
				[itemKey] = new Dictionary<string, object?> { [$"keywords/{keyword}"] = true }
			}
		}, ct).ConfigureAwait(false);
		EnsureUpdated(response.Arguments("0"), itemKey, "Email");
	}

	public async Task EmptyFolderAsync(string folderBackendKey, CancellationToken ct)
	{
		string account = await AccountAsync(ct).ConfigureAwait(false);
		string mailboxId = FromKey(folderBackendKey);
		// H8: cap the destroy batch at maxObjectsInSet (defaulting to 500) — a batch over the
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
			// H10: dispose the response and surface a batch destroy failure rather than looping on
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
		// H9: fail fast if the server does not advertise mail, rather than sending a request it
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

	private async Task<string> ImportAsync(string account, MimeMessage message, string mailboxId, CancellationToken ct)
	{
		using MemoryStream stream = new();
		await message.WriteToAsync(stream, ct).ConfigureAwait(false);
		string blobId = await client.UploadBlobAsync(account, stream.ToArray(), "message/rfc822", ct).ConfigureAwait(false);
		using JmapResponse response = await client.CallAsync(CapMailBlob, "Email/import", new Dictionary<string, object?>
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
		return email is { } e ? KeywordsOf(e).Where(k => !k.StartsWith('$')).ToList() : [];
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
	///   The cached mailbox-id→role map (H25), resolved once per session with a single
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
	// maxObjectsInGet so the Email/get back-reference (H8) never exceeds what the server accepts.
	private static int PageSize(JmapSessionResource session)
	{
		return Math.Max(1, Math.Min(500, session.CoreLimits.MaxObjectsInGet));
	}

	// The Empty-folder destroy batch — bounded by maxObjectsInSet, since the whole page is destroyed
	// in one Email/set (H8).
	private static int DestroyBatchSize(JmapSessionResource session)
	{
		return Math.Max(1, Math.Min(500, session.CoreLimits.MaxObjectsInSet));
	}

	private static Dictionary<string, object?> MailboxFilter(string mailboxId, ContentFilter filter)
	{
		Dictionary<string, object?> f = new() { ["inMailbox"] = mailboxId };
		if (filter.SinceUtc is { } since)
			f["after"] = JmapDate.ToUtc(since);
		return f;
	}

	private static Dictionary<string, object?> ResultRef(string resultOf, string name, string path)
	{
		return new Dictionary<string, object?> { ["resultOf"] = resultOf, ["name"] = name, ["path"] = path };
	}

	private static int RoleToEasType(string? role)
	{
		return role switch
		{
			"inbox" => EasFolderType.Inbox,
			"drafts" => EasFolderType.Drafts,
			"trash" => EasFolderType.DeletedItems,
			"sent" => EasFolderType.SentItems,
			_ => EasFolderType.UserMail
		};
	}

	private static bool HasDraftContent(XElement applicationData)
	{
		return applicationData.Element(Email + "To") is not null ||
		       applicationData.Element(Email + "Cc") is not null ||
		       applicationData.Element(Email2 + "Bcc") is not null ||
		       applicationData.Element(Email + "Subject") is not null ||
		       applicationData.Element(AirSyncBase + "Body") is not null ||
		       applicationData.Element(AirSyncBase + "Attachments") is not null;
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
	///   Maps a JMAP SetError to an exception. H20: a <c>notFound</c> type becomes
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

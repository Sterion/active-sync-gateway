using System.Text.Json;
using System.Xml.Linq;
using ActiveSync.Backends.Converters;
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
public sealed class JmapMailStore(
	JmapClient client,
	string? mailAddress,
	int pollSeconds,
	Func<DateTime, CancellationToken, Task>? waitForPush = null) : IContentStore, IMailStoreOperations
{
	public const string KeyPrefix = "jmap-mail:";

	private static readonly string[] CapMail = [JmapCapabilities.Core, JmapCapabilities.Mail];
	private static readonly string[] CapMailBlob = [JmapCapabilities.Core, JmapCapabilities.Mail, JmapCapabilities.Blob];

	private static readonly XNamespace Email = EasNamespaces.Email;
	private static readonly XNamespace Email2 = EasNamespaces.Email2;
	private static readonly XNamespace AirSyncBase = EasNamespaces.AirSyncBase;

	private string? _account;

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
		Dictionary<string, string> map = new(StringComparer.Ordinal);
		int position = 0;
		const int page = 500;
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

			int returned = response.Arguments("0").GetProperty("ids").GetArrayLength();
			position += returned;
			if (returned < page)
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
			await client.CallAsync(CapMail, "Email/set", new Dictionary<string, object?>
			{
				["accountId"] = account,
				["destroy"] = new[] { itemKey }
			}, ct).ConfigureAwait(false);
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
			using JmapResponse response = await client.CallAsync(CapMail, "Email/set", new Dictionary<string, object?>
			{
				["accountId"] = account,
				["update"] = new Dictionary<string, object?> { [itemKey] = patch }
			}, ct).ConfigureAwait(false);
			EnsureUpdated(response.Arguments("0"), itemKey, "Email");
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
			await client.CallAsync(CapMail, "Email/set", new Dictionary<string, object?>
			{
				["accountId"] = account,
				["destroy"] = new[] { itemKey }
			}, ct).ConfigureAwait(false);
			return;
		}

		using JmapResponse response = await client.CallAsync(CapMail, "Email/set", new Dictionary<string, object?>
		{
			["accountId"] = account,
			["update"] = new Dictionary<string, object?>
			{
				[itemKey] = new Dictionary<string, object?>
				{
					["mailboxIds"] = new Dictionary<string, object?> { [trashId] = true }
				}
			}
		}, ct).ConfigureAwait(false);
		EnsureUpdated(response.Arguments("0"), itemKey, "Email");
	}

	public async Task<string> MoveItemAsync(
		string sourceFolderBackendKey, string itemKey, string destinationFolderBackendKey, CancellationToken ct)
	{
		string account = await AccountAsync(ct).ConfigureAwait(false);
		string destId = FromKey(destinationFolderBackendKey);
		using JmapResponse response = await client.CallAsync(CapMail, "Email/set", new Dictionary<string, object?>
		{
			["accountId"] = account,
			["update"] = new Dictionary<string, object?>
			{
				[itemKey] = new Dictionary<string, object?>
				{
					["mailboxIds"] = new Dictionary<string, object?> { [destId] = true }
				}
			}
		}, ct).ConfigureAwait(false);
		EnsureUpdated(response.Arguments("0"), itemKey, "Email");
		return itemKey; // JMAP Email ids are stable across mailbox moves
	}

	public async Task<IReadOnlyList<string>> WaitForChangesAsync(
		IReadOnlyList<string> folderBackendKeys, TimeSpan timeout, CancellationToken ct)
	{
		string account = await AccountAsync(ct).ConfigureAwait(false);
		string[] ids = folderBackendKeys.Select(FromKey).ToArray();
		Dictionary<string, string> baseline = await FolderTokensAsync(account, ids, ct).ConfigureAwait(false);
		DateTime deadline = DateTime.UtcNow + timeout;
		int delaySeconds = 1;
		int ceiling = Math.Max(1, pollSeconds);
		while (DateTime.UtcNow < deadline)
		{
			TimeSpan remaining = deadline - DateTime.UtcNow;
			TimeSpan delay = TimeSpan.FromSeconds(Math.Min(delaySeconds, ceiling));
			if (delay > remaining)
				delay = remaining;
			// The EventSource push, when available, wakes the wait as soon as the server
			// signals a change; the poll (token diff below) stays the correctness backstop.
			DateTime since = DateTime.UtcNow;
			if (delay > TimeSpan.Zero)
			{
				if (waitForPush is not null)
				{
					using CancellationTokenSource race = CancellationTokenSource.CreateLinkedTokenSource(ct);
					await Task.WhenAny(Task.Delay(delay, race.Token), waitForPush(since, race.Token)).ConfigureAwait(false);
					await race.CancelAsync().ConfigureAwait(false);
				}
				else
				{
					await Task.Delay(delay, ct).ConfigureAwait(false);
				}
			}

			delaySeconds = Math.Min(delaySeconds * 2, ceiling);

			Dictionary<string, string> current = await FolderTokensAsync(account, ids, ct).ConfigureAwait(false);
			List<string> changed = folderBackendKeys
				.Where(key => baseline.GetValueOrDefault(FromKey(key)) != current.GetValueOrDefault(FromKey(key)))
				.ToList();
			if (changed.Count > 0)
				return changed;
		}

		return [];
	}

	// ---------- IMailStoreOperations ----------

	public async Task SaveToSentAsync(byte[] mime, CancellationToken ct)
	{
		string account = await AccountAsync(ct).ConfigureAwait(false);
		string? sentId = await FindMailboxByRoleAsync(account, "sent", ct).ConfigureAwait(false);
		if (sentId is null)
			return;
		string blobId = await client.UploadBlobAsync(account, mime, "message/rfc822", ct).ConfigureAwait(false);
		await client.CallAsync(CapMailBlob, "Email/import", new Dictionary<string, object?>
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
	}

	public async Task<byte[]?> GetRawMessageAsync(string folderBackendKey, string itemKey, CancellationToken ct)
	{
		string account = await AccountAsync(ct).ConfigureAwait(false);
		return await GetRawByIdAsync(account, itemKey, ct).ConfigureAwait(false);
	}

	public async Task<BackendAttachment?> GetAttachmentAsync(string fileReference, CancellationToken ct)
	{
		string itemKey;
		int index;
		try
		{
			(_, itemKey, index) = ParseFileReference(fileReference);
		}
		catch (BackendException)
		{
			return null; // hand-crafted reference — same answer as a vanished attachment
		}

		string account = await AccountAsync(ct).ConfigureAwait(false);
		byte[]? raw = await GetRawByIdAsync(account, itemKey, ct).ConfigureAwait(false);
		if (raw is null)
			return null;
		using MemoryStream stream = new(raw);
		MimeMessage message = await MimeMessage.LoadAsync(stream, ct).ConfigureAwait(false);
		if (message.Attachments.Skip(index).FirstOrDefault() is not MimePart { Content: not null } part)
			return null;
		using MemoryStream output = new();
		await part.Content.DecodeToAsync(output, ct).ConfigureAwait(false);
		return new BackendAttachment(part.ContentType.MimeType, output.ToArray());
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

	public async Task<IReadOnlyList<(string FolderBackendKey, string ItemKey)>> SearchAsync(
		string? folderBackendKey, string freeText, DateTime? sinceUtc, int maxResults, CancellationToken ct)
	{
		string account = await AccountAsync(ct).ConfigureAwait(false);
		Dictionary<string, object?> filter = new() { ["text"] = freeText };
		if (folderBackendKey is not null)
			filter["inMailbox"] = FromKey(folderBackendKey);
		if (sinceUtc is { } since)
			filter["after"] = JmapDate.ToUtc(since);

		JmapCall query = new("Email/query", new Dictionary<string, object?>
		{
			["accountId"] = account,
			["filter"] = filter,
			["sort"] = new object[] { new Dictionary<string, object?> { ["property"] = "receivedAt", ["isAscending"] = false } },
			["limit"] = maxResults
		}, "0");
		JmapCall get = new("Email/get", new Dictionary<string, object?>
		{
			["accountId"] = account,
			["#ids"] = ResultRef("0", "Email/query", "/ids"),
			["properties"] = new[] { "id", "mailboxIds" }
		}, "1");

		using JmapResponse response = await client.InvokeAsync(CapMail, [query, get], ct).ConfigureAwait(false);
		List<(string, string)> hits = new();
		foreach (JsonElement email in response.Arguments("1").GetProperty("list").EnumerateArray())
		{
			string id = email.GetProperty("id").GetString()!;
			string folderKey = folderBackendKey ?? FirstMailbox(email);
			if (folderKey.Length > 0)
				hits.Add((folderKey, id));
		}

		return hits;
	}

	public async Task EmptyFolderAsync(string folderBackendKey, CancellationToken ct)
	{
		string account = await AccountAsync(ct).ConfigureAwait(false);
		string mailboxId = FromKey(folderBackendKey);
		while (true)
		{
			using JmapResponse response = await client.CallAsync(CapMail, "Email/query", new Dictionary<string, object?>
			{
				["accountId"] = account,
				["filter"] = new Dictionary<string, object?> { ["inMailbox"] = mailboxId },
				["limit"] = 500
			}, ct).ConfigureAwait(false);
			string[] ids = response.Arguments("0").GetProperty("ids").EnumerateArray()
				.Select(e => e.GetString()!).ToArray();
			if (ids.Length == 0)
				break;
			await client.CallAsync(CapMail, "Email/set", new Dictionary<string, object?>
			{
				["accountId"] = account,
				["destroy"] = ids
			}, ct).ConfigureAwait(false);
			if (ids.Length < 500)
				break;
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

	public static string MakeFileReference(string folderBackendKey, string itemKey, int attachmentIndex)
	{
		return DelimitedKey.Encode(folderBackendKey, itemKey, attachmentIndex.ToString());
	}

	public static (string FolderBackendKey, string ItemKey, int AttachmentIndex) ParseFileReference(string fileReference)
	{
		string[]? parts = DelimitedKey.Decode(fileReference, 3);
		if (parts is null || !int.TryParse(parts[2], out int index) || index < 0)
			throw new BackendException("Malformed file reference.");
		return (parts[0], parts[1], index);
	}

	private async Task<string> AccountAsync(CancellationToken ct)
	{
		return _account ??= (await client.GetSessionAsync(ct).ConfigureAwait(false))
			.PrimaryAccount(JmapCapabilities.Mail);
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
		using JmapResponse response = await client.CallAsync(CapMail, "Mailbox/get", new Dictionary<string, object?>
		{
			["accountId"] = account,
			["ids"] = new[] { mailboxId },
			["properties"] = new[] { "id", "role" }
		}, ct).ConfigureAwait(false);
		JsonElement list = response.Arguments("0").GetProperty("list");
		return list.GetArrayLength() > 0 && list[0].TryGetProperty("role", out JsonElement role) ? role.GetString() : null;
	}

	private async Task<string?> FindMailboxByRoleAsync(string account, string role, CancellationToken ct)
	{
		using JmapResponse response = await client.CallAsync(CapMail, "Mailbox/get", new Dictionary<string, object?>
		{
			["accountId"] = account,
			["ids"] = null,
			["properties"] = new[] { "id", "role" }
		}, ct).ConfigureAwait(false);
		foreach (JsonElement mailbox in response.Arguments("0").GetProperty("list").EnumerateArray())
			if (mailbox.TryGetProperty("role", out JsonElement r) && r.GetString() == role)
				return mailbox.GetProperty("id").GetString();
		return null;
	}

	private async Task<Dictionary<string, string>> FolderTokensAsync(string account, string[] mailboxIds, CancellationToken ct)
	{
		if (mailboxIds.Length == 0)
			return new Dictionary<string, string>();
		using JmapResponse response = await client.CallAsync(CapMail, "Mailbox/get", new Dictionary<string, object?>
		{
			["accountId"] = account,
			["ids"] = mailboxIds,
			["properties"] = new[] { "id", "totalEmails", "unreadEmails" }
		}, ct).ConfigureAwait(false);
		Dictionary<string, string> tokens = new(StringComparer.Ordinal);
		foreach (JsonElement mailbox in response.Arguments("0").GetProperty("list").EnumerateArray())
		{
			string id = mailbox.GetProperty("id").GetString()!;
			long total = mailbox.TryGetProperty("totalEmails", out JsonElement t) ? t.GetInt64() : 0;
			long unread = mailbox.TryGetProperty("unreadEmails", out JsonElement u) ? u.GetInt64() : 0;
			tokens[id] = $"{total}:{unread}";
		}

		return tokens;
	}

	private static string FirstMailbox(JsonElement email)
	{
		if (email.TryGetProperty("mailboxIds", out JsonElement ids) && ids.ValueKind == JsonValueKind.Object)
			foreach (JsonProperty p in ids.EnumerateObject())
				return ToKey(p.Name);
		return "";
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
		{
			string type = error.TryGetProperty("type", out JsonElement t) ? t.GetString() ?? "unknown" : "unknown";
			throw new BackendException($"JMAP {kind}/{verb} failed for '{id}': {type}.");
		}
	}
}

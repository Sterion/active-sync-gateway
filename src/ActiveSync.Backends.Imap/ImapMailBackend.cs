using System.Collections.Concurrent;
using ActiveSync.Backends.Common.Converters;
using ActiveSync.Contracts;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using Microsoft.Extensions.Logging;
using MimeKit;

// MailKit also declares an IMailStore; the contract's store interface is the one this backend implements.
using IMailStore = ActiveSync.Contracts.IMailStore;

namespace ActiveSync.Backends.Imap;

/// <summary>
///   The mail store + mailbox side-operations over IMAP (submission lives in
///   <c>SmtpSubmitBackend</c>). Item keys are UIDVALIDITY-qualified IMAP UIDs (per folder,
///   see <see cref="ToItemKey" />); revisions encode the sync-relevant flags. The store trades
///   in raw RFC822 bytes and typed flags — it neither reads nor writes EAS XML (the host owns
///   that conversion).
/// </summary>
public sealed partial class ImapMailBackend(
	ImapSession session,
	Func<string, ImapIdleWatcher?> idleWatcherProvider,
	ILogger logger,
	Func<ImapStatusPoller?>? statusPollerProvider = null)
	: IMailStore, IMailboxOperations, IItemMoveOperations, IFolderOperations
{
	private static readonly string[] SentNames = ["Sent", "Sent Items", "Sent Messages", "INBOX.Sent"];
	private static readonly string[] TrashNames = ["Trash", "Deleted Items", "Deleted Messages", "INBOX.Trash"];
	private static readonly string[] DraftsNames = ["Drafts", "INBOX.Drafts"];

	/// <summary>
	///   <see cref="FindSpecialFolderAsync" /> result, memoized per resolved <see cref="ImapClient" />
	///   instance — stable for the connection's lifetime, invalidated by comparing against the
	///   CURRENT client (a fresh instance after <see cref="ImapSession" /> reconnects, so the cache
	///   entry for the old, disposed client is simply never matched again rather than needing an
	///   explicit invalidation hook into the session).
	/// </summary>
	private readonly ConcurrentDictionary<SpecialFolder, (ImapClient Client, IMailFolder? Folder)> _specialFolderCache = new();

	// ---------- IContentStore ----------

	public bool OwnsKey(FolderKey key)
	{
		return key.Value.StartsWith(ImapSession.KeyPrefix, StringComparison.Ordinal);
	}

	public Task<IReadOnlyList<BackendFolder>> ListFoldersAsync(CancellationToken ct)
	{
		return session.RunAsync<IReadOnlyList<BackendFolder>>(async client =>
		{
			List<BackendFolder> result = new();

			// Fetch the WHOLE tree in one LIST ("" "namespace/*") instead of walking one level
			// at a time (one GetSubfoldersAsync round trip per folder, recursively) — a mailbox with
			// N folders used to cost N round trips per FolderSync, all held under the per-user
			// session gate, blocking every other device's Sync and the Ping STATUS poll for the
			// duration. MailKit reconstructs ParentFolder/hierarchy from the returned names the same
			// way regardless of how a folder was discovered.
			List<IMailFolder> all = client.PersonalNamespaces.Count > 0
				? (await client.GetFoldersAsync(client.PersonalNamespaces[0], StatusItems.None, false, ct)
					.ConfigureAwait(false)).ToList()
				: [];

			if (!all.Any(f => f.FullName.Equals("INBOX", StringComparison.OrdinalIgnoreCase)))
				all.Insert(0, client.Inbox);

			foreach (IMailFolder folder in all)
			{
				if (folder.Attributes.HasFlag(FolderAttributes.NonExistent))
					continue;
				FolderType type = ClassifyFolder(folder);
				FolderKey? parentKey = folder.ParentFolder is { } parent && !string.IsNullOrEmpty(parent.FullName)
					? new FolderKey(ImapSession.ToBackendKey(parent.FullName))
					: null;
				result.Add(new BackendFolder
				{
					Key = new FolderKey(ImapSession.ToBackendKey(folder.FullName)),
					DisplayName = folder.Name,
					ParentKey = parentKey,
					Type = type
				});
			}

			return result;
		}, ct);
	}

	public Task<IReadOnlyDictionary<ItemKey, ItemRevision>> GetItemRevisionsAsync(
		FolderKey folderKey, ContentFilter filter, CancellationToken ct)
	{
		return session.RunAsync<IReadOnlyDictionary<ItemKey, ItemRevision>>(async client =>
		{
			IMailFolder folder = await ImapSession.OpenFolderAsync(client, folderKey.Value, FolderAccess.ReadOnly, ct)
				.ConfigureAwait(false);
			// A folder that stays selected between calls keeps a FROZEN view: servers
			// only announce newly delivered messages (EXISTS) when given the chance
			// (RFC 3501 NOOP/IDLE), and re-opening an already-open folder is a no-op in
			// MailKit. Without this NOOP, a SEARCH on the shared session can miss mail
			// delivered after the original SELECT indefinitely (observed on Stalwart).
			await client.NoOpAsync(ct).ConfigureAwait(false);
			SearchQuery query = filter.Since is { } since
				? SearchQuery.DeliveredAfter(SearchFloor(since.UtcDateTime))
				: SearchQuery.All;
			IList<UniqueId> uids = await folder.SearchAsync(query, ct).ConfigureAwait(false);
			if (uids.Count == 0)
				return new Dictionary<ItemKey, ItemRevision>();
			// Unlike JMAP's Email/get (bounded by maxObjectsInGet, which forces its own
			// page-at-500 mitigation), IMAP has no per-command object cap, and this FETCH asks
			// only for UID+Flags — no bodies — so even a large mailbox is cheap. Splitting it
			// into multiple session.RunAsync calls would release ImapSession's per-session gate
			// BETWEEN pages, letting a concurrent Sync/Ping interleave — but a delivery or
			// expunge landing between pages would then stitch the revision map together from two
			// different mailbox states, silently breaking the "the revision map is the whole
			// truth" invariant (AGENTS.md, Sync model) that the diff engine relies on. One atomic
			// FETCH trades a longer single gate hold for a revision map that is always internally
			// consistent — deliberately not paged, unlike the paginated mail *listing* it otherwise
			// mirrors.
			IList<IMessageSummary> summaries = await folder
				.FetchAsync(uids, MessageSummaryItems.UniqueId | MessageSummaryItems.Flags, ct)
				.ConfigureAwait(false);
			Dictionary<ItemKey, ItemRevision> map = new(summaries.Count);
			foreach (IMessageSummary summary in summaries)
				map[new ItemKey(ToItemKey(folder, summary.UniqueId))] =
					new ItemRevision(RevisionOf(summary.Flags ?? MessageFlags.None, summary.Keywords));
			return map;
		}, ct);
	}

	// ---------- IMailStore ----------

	public Task<MailItem?> GetItemAsync(
		FolderKey folderKey, ItemKey itemKey, MailFetchOptions options, CancellationToken ct)
	{
		return session.RunAsync<MailItem?>(async client =>
		{
			IMailFolder folder = await ImapSession.OpenFolderAsync(client, folderKey.Value, FolderAccess.ReadOnly, ct)
				.ConfigureAwait(false);
			UniqueId uid = ParseUid(folder, itemKey.Value);
			IList<IMessageSummary> summaries = await folder
				.FetchAsync([uid], MessageSummaryItems.UniqueId | MessageSummaryItems.Flags | MessageSummaryItems.InternalDate, ct)
				.ConfigureAwait(false);
			if (summaries.Count == 0)
				return null;
			byte[]? rfc822 = await ReadRawMessageAsync(folder, uid, ct).ConfigureAwait(false);
			if (rfc822 is null)
				return null;

			MessageFlags flags = summaries[0].Flags ?? MessageFlags.None;
			return new MailItem
			{
				Rfc822 = rfc822,
				Flags = FlagsOf(flags, summaries[0].Keywords),
				Categories = MailKeywords.CategoryKeywords(summaries[0].Keywords),
				Received = summaries[0].InternalDate
			};
		}, ct);
	}

	public Task<(ItemKey Key, ItemRevision Revision)> CreateDraftAsync(
		FolderKey folderKey, MailItem item, CancellationToken ct)
	{
		// EAS 16.x drafts: the only mail class a client may create via Sync. Anything but
		// the Drafts folder keeps the historical refusal (per-item Status 6 upstream).
		return session.RunAsync(async client =>
		{
			IMailFolder folder = await ImapSession.OpenFolderAsync(client, folderKey.Value, FolderAccess.ReadWrite, ct)
				.ConfigureAwait(false);
			if (!IsDraftsFolder(folder))
				throw new BackendException("Creating mail items via Sync is only supported in the Drafts folder.");

			MimeMessage draft = await LoadMessageAsync(item.Rfc822, ct).ConfigureAwait(false);
			UniqueId? uid = await folder.AppendAsync(draft, MessageFlags.Draft, ct).ConfigureAwait(false);
			if (uid is null)
				throw new BackendException("The IMAP server did not report a UID for the appended draft.");
			return (new ItemKey(ToItemKey(folder, uid.Value)), new ItemRevision(RevisionOf(MessageFlags.None)));
		}, ct, idempotent: false); // APPEND: a replay would duplicate the draft
	}

	public Task<ItemRevision> UpdateFlagsAsync(
		FolderKey folderKey, ItemKey itemKey, MailFlagsPatch patch, ItemRevision? expected, CancellationToken ct)
	{
		// `expected` is deliberately ignored: IMAP has no If-Match for a STORE, so this store
		// cannot honour the precondition — which the contract says is conforming.
		return session.RunAsync(async client =>
		{
			IMailFolder folder = await ImapSession.OpenFolderAsync(client, folderKey.Value, FolderAccess.ReadWrite, ct)
				.ConfigureAwait(false);
			UniqueId uid = ParseUid(folder, itemKey.Value);

			if (patch.Read.HasValue)
			{
				if (patch.Read.Value)
					await folder.AddFlagsAsync(uid, MessageFlags.Seen, true, ct).ConfigureAwait(false);
				else
					await folder.RemoveFlagsAsync(uid, MessageFlags.Seen, true, ct).ConfigureAwait(false);
			}

			if (patch.Flagged.HasValue)
			{
				if (patch.Flagged.Value)
					await folder.AddFlagsAsync(uid, MessageFlags.Flagged, true, ct).ConfigureAwait(false);
				else
					await folder.RemoveFlagsAsync(uid, MessageFlags.Flagged, true, ct).ConfigureAwait(false);
			}

			// Presence-guarded like Read/Flagged: only a supplied category list touches the
			// message's custom keywords — and only the category-relevant subset, so a client
			// clearing its categories can never strip $Forwarded or other system keywords.
			// Servers without custom-keyword support are skipped (same tolerant stance as the
			// $Forwarded write in SetAnsweredAsync).
			if (patch.Categories.HasValue)
			{
				if ((folder.PermanentFlags & MessageFlags.UserDefined) != 0)
				{
					HashSet<string> wanted = patch.Categories.Value
						.Select(SanitizeKeyword)
						.Where(k => k.Length > 0)
						.ToHashSet(StringComparer.OrdinalIgnoreCase);
					IList<IMessageSummary> current = await folder
						.FetchAsync([uid], MessageSummaryItems.UniqueId | MessageSummaryItems.Flags, ct)
						.ConfigureAwait(false);
					IReadOnlyList<string> existing =
						MailKeywords.CategoryKeywords(current.FirstOrDefault()?.Keywords);
					HashSet<string> toAdd = wanted
						.Where(k => !existing.Contains(k, StringComparer.OrdinalIgnoreCase))
						.ToHashSet();
					HashSet<string> toRemove = existing
						.Where(k => !wanted.Contains(k))
						.ToHashSet();
					if (toAdd.Count > 0)
						await folder.AddFlagsAsync(uid, MessageFlags.None, toAdd, true, ct).ConfigureAwait(false);
					if (toRemove.Count > 0)
						await folder.RemoveFlagsAsync(uid, MessageFlags.None, toRemove, true, ct).ConfigureAwait(false);
				}
				else
				{
					logger.LogDebug("Server does not accept custom keywords; Categories change skipped");
				}
			}

			IList<IMessageSummary> summaries = await folder
				.FetchAsync([uid], MessageSummaryItems.UniqueId | MessageSummaryItems.Flags, ct)
				.ConfigureAwait(false);
			return new ItemRevision(summaries.Count > 0
				? RevisionOf(summaries[0].Flags ?? MessageFlags.None, summaries[0].Keywords)
				: "000");
		}, ct); // pure flag changes are idempotent and retry normally
	}

	public Task<(ItemKey Key, ItemRevision Revision)> ReplaceDraftAsync(
		FolderKey folderKey, ItemKey itemKey, MailItem value, CancellationToken ct)
	{
		// EAS 16.x draft edit: the host merged the client's partial data into the stored draft
		// and hands over the complete replacement. The old UID vanishes and the new one appears —
		// the snapshot diff turns that into Delete+Add for the client, which is the standard EAS
		// re-identification flow (the returned key is informational and must NOT be
		// echo-suppressed into the snapshot).
		return session.RunAsync(async client =>
		{
			IMailFolder folder = await ImapSession.OpenFolderAsync(client, folderKey.Value, FolderAccess.ReadWrite, ct)
				.ConfigureAwait(false);
			if (!IsDraftsFolder(folder))
				throw new BackendException("Changing mail content via Sync is only supported in the Drafts folder.");

			MimeMessage merged = await LoadMessageAsync(value.Rfc822, ct).ConfigureAwait(false);

			// Delete the OLD uid before appending the new one. A fault after the delete but
			// before the append LOSES the edit (the old message is simply gone) instead of
			// DUPLICATING it — the reverse order left a mid-fault mailbox with BOTH the original
			// (still addressable by the client's stale item key) and the freshly-appended copy,
			// so a client retry re-executed the whole rewrite and appended a SECOND stray on
			// every subsequent retry. With delete-first, a retry against an already-deleted uid
			// finds nothing to remove (the host's merge-from-nothing already rebuilt the draft
			// from the client's data alone), so it converges to exactly one final copy no matter
			// how many times the delete-but-not-append window is hit. An old uid from an earlier
			// UIDVALIDITY generation is reported as item-gone by ParseUid upstream of any I/O.
			await folder.AddFlagsAsync(ParseUid(folder, itemKey.Value), MessageFlags.Deleted, true, ct)
				.ConfigureAwait(false);
			await ExpungeUidAsync(folder, ParseUid(folder, itemKey.Value), ct).ConfigureAwait(false);
			UniqueId? uid = await folder.AppendAsync(merged, MessageFlags.Draft, ct).ConfigureAwait(false);
			if (uid is null)
				throw new BackendException("The IMAP server did not report a UID for the rewritten draft.");
			return (new ItemKey(ToItemKey(folder, uid.Value)), new ItemRevision(RevisionOf(MessageFlags.None)));
		}, ct, idempotent: false); // delete+append is not replayable
	}

	public Task DeleteItemAsync(FolderKey folderKey, ItemKey itemKey, bool permanent, CancellationToken ct)
	{
		return session.RunAsync(async client =>
		{
			IMailFolder folder = await ImapSession.OpenFolderAsync(client, folderKey.Value, FolderAccess.ReadWrite, ct)
				.ConfigureAwait(false);
			UniqueId uid = ParseUid(folder, itemKey.Value);
			// DeletesAsMoves=0 (permanent), or already in Trash, or no Trash folder → expunge;
			// otherwise the default move-to-Trash.
			IMailFolder? trash = permanent
				? null
				: await FindSpecialFolderAsync(client, SpecialFolder.Trash, TrashNames, ct).ConfigureAwait(false);

			if (trash is not null && !folder.FullName.Equals(trash.FullName, StringComparison.Ordinal))
			{
				await folder.MoveToAsync(uid, trash, ct).ConfigureAwait(false);
			}
			else
			{
				await folder.AddFlagsAsync(uid, MessageFlags.Deleted, true, ct).ConfigureAwait(false);
				await ExpungeUidAsync(folder, uid, ct).ConfigureAwait(false);
			}

			return true;
		}, ct);
	}

	/// <summary>
	///   Removes exactly one message. A bare EXPUNGE permanently removes EVERY message in the
	///   folder carrying <c>\Deleted</c> — including ones another client (webmail, a desktop MUA,
	///   a second EAS device mid-operation) marked but has not expunged yet — so an ordinary EAS
	///   delete would silently destroy unrelated mail. MailKit issues <c>UID EXPUNGE</c> when the
	///   server advertises UIDPLUS and otherwise emulates the scoping by unflagging the other
	///   <c>\Deleted</c> messages around the expunge and restoring them afterwards.
	///   <see cref="EmptyFolderAsync" /> is the one path where removing everything is the request.
	/// </summary>
	private static Task ExpungeUidAsync(IMailFolder folder, UniqueId uid, CancellationToken ct)
	{
		return folder.ExpungeAsync([uid], ct);
	}

	public Task<(ItemKey Key, ItemRevision Revision)> MoveItemAsync(
		FolderKey sourceKey, ItemKey itemKey, FolderKey destinationKey, CancellationToken ct)
	{
		return session.RunAsync(async client =>
		{
			IMailFolder source = await ImapSession.OpenFolderAsync(client, sourceKey.Value, FolderAccess.ReadWrite, ct)
				.ConfigureAwait(false);
			IMailFolder destination = await client.GetFolderAsync(
				ImapSession.FromBackendKey(destinationKey.Value), ct).ConfigureAwait(false);
			UniqueId uid = ParseUid(source, itemKey.Value);
			UniqueId? newUid = await source.MoveToAsync(uid, destination, ct).ConfigureAwait(false);
			if (newUid is null)
				throw new BackendException("IMAP server did not report the moved message's new UID (no UIDPLUS).");
			// The COPYUID response carries the destination's UIDVALIDITY, so the new key needs no
			// extra round trip; STATUS covers a server that answers without one.
			uint validity = newUid.Value.Validity;
			if (validity == 0)
			{
				await destination.StatusAsync(StatusItems.UidValidity, ct).ConfigureAwait(false);
				validity = destination.UidValidity;
			}

			// Fetch the moved message's flags at the destination so the caller can store the
			// item's REAL revision, not a placeholder that can never match the next listing. FETCH
			// requires the folder to be open (STATUS above does not), so open it read-only first.
			if (!destination.IsOpen)
				await destination.OpenAsync(FolderAccess.ReadOnly, ct).ConfigureAwait(false);
			IList<IMessageSummary> summaries = await destination
				.FetchAsync([newUid.Value], MessageSummaryItems.UniqueId | MessageSummaryItems.Flags, ct)
				.ConfigureAwait(false);
			string revision = summaries.Count > 0
				? RevisionOf(summaries[0].Flags ?? MessageFlags.None, summaries[0].Keywords)
				: RevisionOf(MessageFlags.None);

			return (new ItemKey($"{validity}:{newUid.Value.Id}"), new ItemRevision(revision));
		}, ct);
	}

	public Task<FolderKey> CreateFolderAsync(FolderKey? parentKey, string displayName, CancellationToken ct)
	{
		return session.RunAsync(async client =>
		{
			IMailFolder parent = parentKey is { } parentFolderKey
				? await client.GetFolderAsync(ImapSession.FromBackendKey(parentFolderKey.Value), ct).ConfigureAwait(false)
				: client.PersonalNamespaces.Count > 0
					? client.GetFolder(client.PersonalNamespaces[0])
					: client.Inbox;
			IMailFolder created = await parent.CreateAsync(displayName, true, ct).ConfigureAwait(false)
			                      ?? throw new BackendException("IMAP server did not return the created folder.");
			return new FolderKey(ImapSession.ToBackendKey(created.FullName));
		}, ct);
	}

	public Task RenameFolderAsync(FolderKey folderKey, string newDisplayName, CancellationToken ct)
	{
		return session.RunAsync(async client =>
		{
			IMailFolder folder =
				await client.GetFolderAsync(ImapSession.FromBackendKey(folderKey.Value), ct).ConfigureAwait(false);
			IMailFolder parent = folder.ParentFolder
			                     ?? throw new BackendException("Cannot rename a namespace root folder.");
			await folder.RenameAsync(parent, newDisplayName, ct).ConfigureAwait(false);
			return true;
		}, ct);
	}

	public Task DeleteFolderAsync(FolderKey folderKey, CancellationToken ct)
	{
		return session.RunAsync(async client =>
		{
			IMailFolder folder =
				await client.GetFolderAsync(ImapSession.FromBackendKey(folderKey.Value), ct).ConfigureAwait(false);
			await folder.DeleteAsync(ct).ConfigureAwait(false);
			return true;
		}, ct);
	}

	// ---------- IMailboxOperations ----------

	public Task SaveToSentAsync(ReadOnlyMemory<byte> rfc822, CancellationToken ct)
	{
		return session.RunAsync(async client =>
		{
			IMailFolder? sent = await FindSpecialFolderAsync(client, SpecialFolder.Sent, SentNames, ct).ConfigureAwait(false);
			if (sent is null)
				return false;
			MimeMessage message = await LoadMessageAsync(rfc822, ct).ConfigureAwait(false);
			await sent.AppendAsync(message, MessageFlags.Seen, ct).ConfigureAwait(false);
			return true;
		}, ct, idempotent: false); // APPEND to Sent: a replay would duplicate the sent copy
	}

	public Task<ReadOnlyMemory<byte>?> GetRawMessageAsync(FolderKey folderKey, ItemKey itemKey, CancellationToken ct)
	{
		return session.RunAsync<ReadOnlyMemory<byte>?>(async client =>
		{
			IMailFolder folder = await ImapSession.OpenFolderAsync(client, folderKey.Value, FolderAccess.ReadOnly, ct)
				.ConfigureAwait(false);
			byte[]? raw = await ReadRawMessageAsync(folder, ParseUid(folder, itemKey.Value), ct).ConfigureAwait(false);
			return raw is null ? null : (ReadOnlyMemory<byte>?)raw;
		}, ct);
	}

	public Task SetAnsweredAsync(FolderKey folderKey, ItemKey itemKey, bool forwarded, CancellationToken ct)
	{
		return session.RunAsync(async client =>
		{
			IMailFolder folder = await ImapSession.OpenFolderAsync(client, folderKey.Value, FolderAccess.ReadWrite, ct)
				.ConfigureAwait(false);
			UniqueId uid = ParseUid(folder, itemKey.Value);
			if (forwarded)
				try
				{
					await folder.AddFlagsAsync(uid, MessageFlags.None, new HashSet<string> { "$Forwarded" }, true, ct)
						.ConfigureAwait(false);
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					logger.LogDebug(ex, "Server rejected $Forwarded keyword");
				}
			else
				await folder.AddFlagsAsync(uid, MessageFlags.Answered, true, ct).ConfigureAwait(false);

			return true;
		}, ct);
	}

	public Task<IReadOnlyList<SearchHit>> SearchAsync(
		FolderKey? folderKey, string freeText, DateTimeOffset? since, int maxResults, CancellationToken ct)
	{
		return session.RunAsync<IReadOnlyList<SearchHit>>(async client =>
		{
			FolderKey searchKey = folderKey ?? new FolderKey(ImapSession.ToBackendKey(client.Inbox.FullName));
			IMailFolder folder = await ImapSession.OpenFolderAsync(client, searchKey.Value, FolderAccess.ReadOnly, ct)
				.ConfigureAwait(false);
			await client.NoOpAsync(ct).ConfigureAwait(false); // refresh the selected-folder view
			SearchQuery query = SearchQuery.SubjectContains(freeText)
				.Or(SearchQuery.FromContains(freeText))
				.Or(SearchQuery.ToContains(freeText))
				.Or(SearchQuery.BodyContains(freeText));
			if (since is { } sinceValue)
				query = query.And(SearchQuery.DeliveredAfter(SearchFloor(sinceValue.UtcDateTime)));
			IList<UniqueId> uids = await folder.SearchAsync(query, ct).ConfigureAwait(false);
			return uids
				.OrderByDescending(u => u.Id)
				.Take(maxResults)
				.Select(u => new SearchHit { Folder = searchKey, Item = new ItemKey(ToItemKey(folder, u)) })
				.ToList();
		}, ct);
	}

	public Task EmptyFolderAsync(FolderKey folderKey, CancellationToken ct)
	{
		return session.RunAsync(async client =>
		{
			IMailFolder folder = await ImapSession.OpenFolderAsync(client, folderKey.Value, FolderAccess.ReadWrite, ct)
				.ConfigureAwait(false);
			// folder.Count is only as fresh as the last EXISTS this connection happened to see,
			// and a folder that stays selected between requests is never told about new mail
			// unprompted — the same reason GetItemRevisionsAsync and SearchAsync NOOP first.
			// Sequence numbers are racy on top of that: a concurrent expunge renumbers them, so
			// the STORE lands on whatever moved into that slot. SEARCH ALL after the NOOP gives
			// stable UIDs for exactly what is in the folder now.
			await client.NoOpAsync(ct).ConfigureAwait(false);
			IList<UniqueId> uids = await folder.SearchAsync(SearchQuery.All, ct).ConfigureAwait(false);
			if (uids.Count > 0)
			{
				await folder.AddFlagsAsync(uids, MessageFlags.Deleted, true, ct).ConfigureAwait(false);
				await folder.ExpungeAsync(uids, ct).ConfigureAwait(false);
			}

			return true;
		}, ct);
	}

	// ---------- helpers ----------

	private static bool IsDraftsFolder(IMailFolder folder)
	{
		return MatchesSpecialFolder(folder, FolderAttributes.Drafts, DraftsNames);
	}

	private static FolderType ClassifyFolder(IMailFolder folder)
	{
		if (folder.Attributes.HasFlag(FolderAttributes.Inbox) ||
		    folder.FullName.Equals("INBOX", StringComparison.OrdinalIgnoreCase))
			return FolderType.Inbox;
		if (IsDraftsFolder(folder))
			return FolderType.Drafts;
		if (MatchesSpecialFolder(folder, FolderAttributes.Sent, SentNames))
			return FolderType.SentItems;
		if (MatchesSpecialFolder(folder, FolderAttributes.Trash, TrashNames))
			return FolderType.DeletedItems;
		return FolderType.UserMail;
	}

	/// <summary>
	///   One predicate per special folder (SPECIAL-USE attribute ∪ FullName ∪ leaf Name), so
	///   FolderSync's classification (<see cref="ClassifyFolder" />) and the Sync write-path gates
	///   (<see cref="IsDraftsFolder" />, and Sent/Trash here) agree. Matching only FullName let a
	///   server without SPECIAL-USE that nests a special folder under a non-INBOX parent (e.g.
	///   "Personal/Drafts") report as an ordinary UserMail folder to the phone while the backend
	///   still treated it as the special folder for creates/edits.
	/// </summary>
	private static bool MatchesSpecialFolder(IMailFolder folder, FolderAttributes attribute, string[] names)
	{
		return folder.Attributes.HasFlag(attribute) ||
		       names.Contains(folder.FullName, StringComparer.OrdinalIgnoreCase) ||
		       names.Contains(folder.Name, StringComparer.OrdinalIgnoreCase);
	}

	/// <summary>
	///   RFC 3501's SEARCH SINCE compares only the calendar date and "disregard[s] ...
	///   timezone" of the server's own INTERNALDATE — which is not necessarily UTC. A UTC
	///   <paramref name="sinceUtc" /> truncated straight to <c>.Date</c> can therefore land one
	///   calendar day later than the server's own idea of that boundary (a message delivered at
	///   23:50 UTC is 01:50 the next day in CEST), silently EXCLUDING a message that should still
	///   be inside the filter window — not a "delete" (the diff engine's aged-out reconciliation
	///   in SyncHandler.Collection.cs only rescues items that were seen before and later fall out
	///   of the window; a message excluded from its very first appearance never gets that
	///   treatment). Backing the floor off by one extra day makes the IMAP-side filter a strict
	///   superset of the caller's intended UTC window regardless of the server's timezone, so
	///   nothing is silently missed — at the cost of occasionally including one extra day near
	///   the edge, which the "roughly last N days" FilterType semantics already tolerate.
	/// </summary>
	internal static DateTime SearchFloor(DateTime sinceUtc)
	{
		return sinceUtc.AddDays(-1).Date;
	}

	/// <summary>The typed contract flags for a message's IMAP flags + keywords.</summary>
	private static MailFlags FlagsOf(MessageFlags flags, IReadOnlyCollection<string>? keywords)
	{
		return new MailFlags
		{
			Seen = (flags & MessageFlags.Seen) != 0,
			Flagged = (flags & MessageFlags.Flagged) != 0,
			Answered = (flags & MessageFlags.Answered) != 0,
			Forwarded = keywords?.Contains("$Forwarded") == true,
			Draft = (flags & MessageFlags.Draft) != 0
		};
	}

	/// <summary>
	///   The exact message bytes as stored on the server (<c>BODY[]</c>), with no parse/serialize
	///   round-trip in between — the contract's currency is the raw RFC822, and re-serializing a
	///   parsed message could silently normalize bytes. Null when the message vanished.
	/// </summary>
	private static async Task<byte[]?> ReadRawMessageAsync(IMailFolder folder, UniqueId uid, CancellationToken ct)
	{
		try
		{
			using Stream stream = await folder.GetStreamAsync(uid, ct).ConfigureAwait(false);
			using MemoryStream buffer = new();
			await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
			return buffer.ToArray();
		}
		catch (MessageNotFoundException)
		{
			return null;
		}
	}

	/// <summary>Parses contract message bytes into MailKit's model for APPEND.</summary>
	private static async Task<MimeMessage> LoadMessageAsync(ReadOnlyMemory<byte> rfc822, CancellationToken ct)
	{
		using MemoryStream stream = new(rfc822.ToArray());
		return await MimeMessage.LoadAsync(stream, ct).ConfigureAwait(false);
	}

	// A mail item's "revision" is a 3-digit string encoding the sync-relevant flags in a
	// fixed order: seen, flagged, answered (e.g. "101" = seen, not flagged, answered),
	// followed by "|kw1,kw2" ONLY when the message carries category-relevant keywords —
	// keyword-less messages keep the historical 3-digit form byte-for-byte, so upgrading
	// only churns messages that already have keywords. The diff engine treats any change
	// to this string as an item change, so the digit order (and the sorted keyword order
	// from CategoryKeywords) must stay stable — a Ping/Sync watcher compares these
	// against the stored snapshot.
	private static string RevisionOf(MessageFlags flags, IEnumerable<string>? keywords = null)
	{
		string digits =
			$"{((flags & MessageFlags.Seen) != 0 ? 1 : 0)}{((flags & MessageFlags.Flagged) != 0 ? 1 : 0)}{((flags & MessageFlags.Answered) != 0 ? 1 : 0)}";
		IReadOnlyList<string> categories = MailKeywords.CategoryKeywords(keywords);
		return categories.Count == 0 ? digits : $"{digits}|{string.Join(',', categories)}";
	}

	/// <summary>
	///   EAS categories are free text while IMAP keywords are RFC 3501 atoms. A category that is
	///   already a valid atom is kept verbatim; one carrying anything an atom cannot hold (spaces,
	///   controls, specials) is DROPPED — the empty string, which the caller filters out. The old
	///   char-by-char '_' substitution collapsed distinct categories ("a b" and "a_b") onto the same
	///   keyword: the server-derived category then never matched the client's original, thrashing the
	///   mail revision string every Sync. Dropping a non-round-trippable category loses it (IMAP
	///   keywords fundamentally cannot carry spaces/specials) but never corrupts a *different*
	///   category, so the diff no longer churns. Server→client needs no inverse — every stored atom is
	///   already a valid category string.
	/// </summary>
	internal static string SanitizeKeyword(string category)
	{
		foreach (char c in category)
			if (c <= ' ' || c >= (char)127 || @"(){%*""\[]".Contains(c))
				return string.Empty;

		return category;
	}

	/// <summary>
	///   Builds an item key as "&lt;uidvalidity&gt;:&lt;uid&gt;". A UID alone is NOT a stable
	///   identifier: RFC 3501 lets a server reset UIDVALIDITY (mailbox recreated, restored from
	///   backup, migrated, index rebuilt), after which the same number names a different message
	///   — so a client's stored "delete 4711" would mutate whatever now holds UID 4711, with no
	///   error. Qualifying the key makes every key from the previous generation unresolvable,
	///   which the snapshot diff turns into Delete+Add and the client into a clean re-sync.
	/// </summary>
	private static string ToItemKey(IMailFolder folder, UniqueId uid)
	{
		return $"{folder.UidValidity}:{uid.Id}";
	}

	/// <summary>
	///   Item keys are client-echoed strings; a malformed one — or one from an earlier
	///   UIDVALIDITY generation of this folder — means the item cannot exist, and is reported the
	///   same way as a vanished item rather than crashing or addressing an unrelated message.
	/// </summary>
	private static UniqueId ParseUid(IMailFolder folder, string itemKey)
	{
		return ParseUid(folder.UidValidity, folder.FullName, itemKey);
	}

	/// <summary>
	///   The qualified "&lt;uidvalidity&gt;:&lt;uid&gt;" form is REQUIRED — an unqualified key
	///   (no ':') used to have the folder's CURRENT UidValidity stamped onto it unconditionally,
	///   which is exactly the hazard <see cref="ToItemKey" /> exists to close: RFC 3501 lets a
	///   server reset UIDVALIDITY (mailbox recreated, restored, migrated, index rebuilt), after
	///   which the same UID number names a different message, so a stale "delete 4711" would
	///   mutate whatever now holds UID 4711 with no error. `GetItemRevisionsAsync` only ever emits
	///   the qualified form, and the pre-upgrade legacy-row path this fallback was written for is
	///   gone (the schema was reinitialized — see AGENTS.md), so the only sources of an unqualified
	///   key left are a stale pre-upgrade device `ServerId` or a buggy/hostile client. Refusing it
	///   turns a stale key into a clean Delete+Add re-sync instead of a silent cross-item mutation.
	/// </summary>
	internal static UniqueId ParseUid(uint currentUidValidity, string folderFullName, string itemKey)
	{
		int separator = itemKey.IndexOf(':');
		if (separator < 0)
			throw new BackendItemNotFoundException(
				$"Mail item key '{itemKey}' has no UIDVALIDITY prefix; it cannot be resolved in \"{folderFullName}\".");

		if (!uint.TryParse(itemKey[..separator], out uint validity) || validity != currentUidValidity)
			throw new BackendItemNotFoundException(
				$"Mail item key '{itemKey}' belongs to an earlier UIDVALIDITY generation of \"{folderFullName}\".");

		string uidPart = itemKey[(separator + 1)..];
		return uint.TryParse(uidPart, out uint value) && value > 0
			? new UniqueId(currentUidValidity, value)
			: throw new BackendItemNotFoundException($"'{itemKey}' is not a valid mail item key.");
	}

	private async Task<IMailFolder?> FindSpecialFolderAsync(
		ImapClient client, SpecialFolder special, string[] fallbackNames, CancellationToken ct)
	{
		// The result is stable for the connection's lifetime (SPECIAL-USE flags/folder names
		// don't change mid-session) — on a server without SPECIAL-USE, resolving it used to cost a
		// full namespace LIST on EVERY delete/save-to-Sent (50 messages == 50 extra LISTs, all under
		// the session gate). Reusing an entry from a PRIOR client instance would be wrong (a
		// reconnect could point at a different mailbox state), so the cache key includes the client.
		if (_specialFolderCache.TryGetValue(special, out (ImapClient Client, IMailFolder? Folder) cached) &&
		    ReferenceEquals(cached.Client, client))
			return cached.Folder;

		IMailFolder? resolved = await ResolveSpecialFolderAsync(client, special, fallbackNames, ct).ConfigureAwait(false);
		_specialFolderCache[special] = (client, resolved);
		return resolved;
	}

	private static async Task<IMailFolder?> ResolveSpecialFolderAsync(
		ImapClient client, SpecialFolder special, string[] fallbackNames, CancellationToken ct)
	{
		try
		{
			IMailFolder? folder = client.GetFolder(special);
			if (folder is not null)
				return folder;
		}
		catch (NotSupportedException)
		{
			// server lacks SPECIAL-USE; fall through to name matching
		}

		IMailFolder? personal = client.PersonalNamespaces.Count > 0
			? client.GetFolder(client.PersonalNamespaces[0])
			: null;
		if (personal is null)
			return null;
		IList<IMailFolder> folders = await personal.GetSubfoldersAsync(false, ct).ConfigureAwait(false);
		return folders.FirstOrDefault(f => fallbackNames.Contains(f.FullName, StringComparer.OrdinalIgnoreCase))
		       ?? folders.FirstOrDefault(f => fallbackNames.Contains(f.Name, StringComparer.OrdinalIgnoreCase));
	}
}

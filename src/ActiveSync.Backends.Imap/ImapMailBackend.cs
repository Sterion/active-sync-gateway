using System.Xml.Linq;
using ActiveSync.Backends.Common.Converters;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace ActiveSync.Backends.Imap;

/// <summary>
///   Email content store + mail-store side-operations over IMAP (submission lives in
///   <c>SmtpSubmitBackend</c>). Item keys are UIDVALIDITY-qualified IMAP UIDs (per folder,
///   see <see cref="ToItemKey" />); revisions encode the sync-relevant flags.
/// </summary>
public sealed partial class ImapMailBackend(
	ImapSession session,
	string? mailAddress,
	Func<string, ImapIdleWatcher?> idleWatcherProvider,
	ILogger logger) : IContentStore, IMailStoreOperations, IItemMoveOperations, IFolderOperations
{
	private static readonly XNamespace Email = EasNamespaces.Email;
	private static readonly XNamespace Email2 = EasNamespaces.Email2;
	private static readonly XNamespace AirSyncBase = EasNamespaces.AirSyncBase;

	private static readonly string[] SentNames = ["Sent", "Sent Items", "Sent Messages", "INBOX.Sent"];
	private static readonly string[] TrashNames = ["Trash", "Deleted Items", "Deleted Messages", "INBOX.Trash"];
	private static readonly string[] DraftsNames = ["Drafts", "INBOX.Drafts"];
	private static readonly string[] JunkNames = ["Junk", "Spam", "Junk E-mail", "INBOX.Junk"];

	public string EasClass => Protocol.EasClass.Email;

	// ---------- IContentStore ----------

	public bool OwnsBackendKey(string backendKey)
	{
		return backendKey.StartsWith(ImapSession.KeyPrefix, StringComparison.Ordinal);
	}

	public Task<IReadOnlyList<BackendFolder>> ListFoldersAsync(CancellationToken ct)
	{
		return session.RunAsync<IReadOnlyList<BackendFolder>>(async client =>
		{
			List<BackendFolder> result = new();
			IMailFolder? personal = client.PersonalNamespaces.Count > 0
				? client.GetFolder(client.PersonalNamespaces[0])
				: null;
			IList<IMailFolder> folders = personal is not null
				? await personal.GetSubfoldersAsync(false, ct).ConfigureAwait(false)
				: [];

			List<IMailFolder> all = new();

			async Task Walk(IMailFolder folder)
			{
				all.Add(folder);
				foreach (IMailFolder child in await folder.GetSubfoldersAsync(false, ct).ConfigureAwait(false))
					await Walk(child).ConfigureAwait(false);
			}

			foreach (IMailFolder f in folders)
				await Walk(f).ConfigureAwait(false);
			if (!all.Any(f => f.FullName.Equals("INBOX", StringComparison.OrdinalIgnoreCase)))
				all.Insert(0, client.Inbox);

			foreach (IMailFolder folder in all)
			{
				if (folder.Attributes.HasFlag(FolderAttributes.NonExistent))
					continue;
				int type = ClassifyFolder(folder);
				string? parentKey = folder.ParentFolder is { } parent && !string.IsNullOrEmpty(parent.FullName)
					? ImapSession.ToBackendKey(parent.FullName)
					: null;
				result.Add(new BackendFolder(
					ImapSession.ToBackendKey(folder.FullName),
					folder.Name,
					parentKey,
					type,
					Protocol.EasClass.Email));
			}

			return result;
		}, ct);
	}

	public Task<IReadOnlyDictionary<string, string>> GetItemRevisionsAsync(
		string folderBackendKey, ContentFilter filter, CancellationToken ct)
	{
		return session.RunAsync<IReadOnlyDictionary<string, string>>(async client =>
		{
			IMailFolder folder = await ImapSession.OpenFolderAsync(client, folderBackendKey, FolderAccess.ReadOnly, ct)
				.ConfigureAwait(false);
			// A folder that stays selected between calls keeps a FROZEN view: servers
			// only announce newly delivered messages (EXISTS) when given the chance
			// (RFC 3501 NOOP/IDLE), and re-opening an already-open folder is a no-op in
			// MailKit. Without this NOOP, a SEARCH on the shared session can miss mail
			// delivered after the original SELECT indefinitely (observed on Stalwart).
			await client.NoOpAsync(ct).ConfigureAwait(false);
			SearchQuery query = filter.SinceUtc is { } since
				? SearchQuery.DeliveredAfter(SearchFloor(since))
				: SearchQuery.All;
			IList<UniqueId> uids = await folder.SearchAsync(query, ct).ConfigureAwait(false);
			if (uids.Count == 0)
				return new Dictionary<string, string>();
			// D15: unlike JMAP's Email/get (bounded by maxObjectsInGet, which forces the H8
			// page-at-500 mitigation), IMAP has no per-command object cap, and this FETCH asks
			// only for UID+Flags — no bodies — so even a large mailbox is cheap. Splitting it
			// into multiple session.RunAsync calls would release ImapSession's per-session gate
			// BETWEEN pages, letting a concurrent Sync/Ping interleave — but a delivery or
			// expunge landing between pages would then stitch the revision map together from two
			// different mailbox states, silently breaking the "the revision map is the whole
			// truth" invariant (AGENTS.md, Sync model) that the diff engine relies on. One atomic
			// FETCH trades a longer single gate hold for a revision map that is always internally
			// consistent — deliberately not paged, unlike the H8 mail *listing* it otherwise
			// mirrors.
			IList<IMessageSummary> summaries = await folder
				.FetchAsync(uids, MessageSummaryItems.UniqueId | MessageSummaryItems.Flags, ct)
				.ConfigureAwait(false);
			Dictionary<string, string> map = new(summaries.Count, StringComparer.Ordinal);
			foreach (IMessageSummary summary in summaries)
				map[ToItemKey(folder, summary.UniqueId)] =
					RevisionOf(summary.Flags ?? MessageFlags.None, summary.Keywords);
			return map;
		}, ct);
	}

	public Task<BackendItem?> GetItemAsync(
		string folderBackendKey, string itemKey, BodyPreference bodyPreference, CancellationToken ct)
	{
		return session.RunAsync<BackendItem?>(async client =>
		{
			IMailFolder folder = await ImapSession.OpenFolderAsync(client, folderBackendKey, FolderAccess.ReadOnly, ct)
				.ConfigureAwait(false);
			UniqueId uid = ParseUid(folder, itemKey);
			IList<IMessageSummary> summaries = await folder
				.FetchAsync([uid], MessageSummaryItems.UniqueId | MessageSummaryItems.Flags, ct)
				.ConfigureAwait(false);
			if (summaries.Count == 0)
				return null;
			MimeMessage message;
			try
			{
				message = await folder.GetMessageAsync(uid, ct).ConfigureAwait(false);
			}
			catch (MessageNotFoundException)
			{
				return null;
			}

			MessageFlags flags = summaries[0].Flags ?? MessageFlags.None;
			MailConverter.MessageFlags converterFlags = new(
				(flags & MessageFlags.Seen) != 0,
				(flags & MessageFlags.Flagged) != 0,
				(flags & MessageFlags.Answered) != 0,
				summaries[0].Keywords?.Contains("$Forwarded") == true,
				summaries[0].Keywords);
			List<XElement> data = MailConverter.ToApplicationData(
				message, converterFlags, bodyPreference,
				idx => MakeFileReference(folderBackendKey, itemKey, idx));
			return new BackendItem(data);
		}, ct);
	}

	public Task<(string ItemKey, string Revision)> CreateItemAsync(
		string folderBackendKey, XElement applicationData, CancellationToken ct)
	{
		// EAS 16.x drafts: the only mail class a client may create via Sync. Anything but
		// the Drafts folder keeps the historical refusal (per-item Status 6 upstream).
		return session.RunAsync(async client =>
		{
			IMailFolder folder = await ImapSession.OpenFolderAsync(client, folderBackendKey, FolderAccess.ReadWrite, ct)
				.ConfigureAwait(false);
			if (!IsDraftsFolder(folder))
				throw new BackendException("Creating mail items via Sync is only supported in the Drafts folder.");

			MimeMessage draft = DraftMessageBuilder.Build(applicationData, null, mailAddress);
			UniqueId? uid = await folder.AppendAsync(draft, MessageFlags.Draft, ct).ConfigureAwait(false);
			if (uid is null)
				throw new BackendException("The IMAP server did not report a UID for the appended draft.");
			return (ToItemKey(folder, uid.Value), RevisionOf(MessageFlags.None));
		}, ct, idempotent: false); // APPEND: a replay would duplicate the draft
	}

	public Task<string> UpdateItemAsync(
		string folderBackendKey, string itemKey, XElement applicationData, CancellationToken ct)
	{
		return session.RunAsync(async client =>
		{
			IMailFolder folder = await ImapSession.OpenFolderAsync(client, folderBackendKey, FolderAccess.ReadWrite, ct)
				.ConfigureAwait(false);
			UniqueId uid = ParseUid(folder, itemKey);

			// EAS 16.x draft edit: content-bearing changes in the Drafts folder rewrite the
			// message (append merged draft, expunge the old one). The old UID vanishes and
			// the new one appears — the snapshot diff turns that into Delete+Add for the
			// client, which is the standard EAS re-identification flow.
			if (IsDraftsFolder(folder) && HasDraftContent(applicationData))
			{
				MimeMessage? original = null;
				try
				{
					original = await folder.GetMessageAsync(uid, ct).ConfigureAwait(false);
				}
				catch (MessageNotFoundException)
				{
					// merge-from-nothing is fine — the payload becomes the whole draft
				}

				MimeMessage merged = DraftMessageBuilder.Build(applicationData, original, mailAddress);
				await folder.AppendAsync(merged, MessageFlags.Draft, ct).ConfigureAwait(false);
				await folder.AddFlagsAsync(uid, MessageFlags.Deleted, true, ct).ConfigureAwait(false);
				await ExpungeUidAsync(folder, uid, ct).ConfigureAwait(false);
				return RevisionOf(MessageFlags.None);
			}

			string? read = applicationData.Element(Email + "Read")?.Value;
			if (read is not null)
			{
				if (read == "1")
					await folder.AddFlagsAsync(uid, MessageFlags.Seen, true, ct).ConfigureAwait(false);
				else
					await folder.RemoveFlagsAsync(uid, MessageFlags.Seen, true, ct).ConfigureAwait(false);
			}

			XElement? flagElement = applicationData.Element(Email + "Flag");
			if (flagElement is not null)
			{
				string? status = flagElement.Element(Email + "Status")?.Value;
				if (status == "2")
					await folder.AddFlagsAsync(uid, MessageFlags.Flagged, true, ct).ConfigureAwait(false);
				else
					await folder.RemoveFlagsAsync(uid, MessageFlags.Flagged, true, ct).ConfigureAwait(false);
			}

			// Presence-guarded like Read/Flag: only an explicit Categories element touches
			// the message's custom keywords — and only the category-relevant subset, so a
			// client clearing its categories can never strip $Forwarded or other system
			// keywords. Servers without custom-keyword support are skipped (same tolerant
			// stance as the $Forwarded write in SetAnsweredAsync).
			XElement? categoriesElement = applicationData.Element(Email + "Categories");
			if (categoriesElement is not null)
			{
				if ((folder.PermanentFlags & MessageFlags.UserDefined) != 0)
				{
					HashSet<string> wanted = categoriesElement.Elements(Email + "Category")
						.Select(c => SanitizeKeyword(c.Value))
						.Where(k => k.Length > 0)
						.ToHashSet(StringComparer.OrdinalIgnoreCase);
					IList<IMessageSummary> current = await folder
						.FetchAsync([uid], MessageSummaryItems.UniqueId | MessageSummaryItems.Flags, ct)
						.ConfigureAwait(false);
					IReadOnlyList<string> existing =
						MailConverter.CategoryKeywords(current.FirstOrDefault()?.Keywords);
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
			return summaries.Count > 0
				? RevisionOf(summaries[0].Flags ?? MessageFlags.None, summaries[0].Keywords)
				: "000";
			// A content-bearing draft edit does append+delete+expunge and is not replayable; a
			// pure flag change (Read/Flag/Categories) is idempotent and retries normally.
		}, ct, idempotent: !HasDraftContent(applicationData));
	}

	private static bool IsDraftsFolder(IMailFolder folder)
	{
		return MatchesSpecialFolder(folder, FolderAttributes.Drafts, DraftsNames);
	}

	/// <summary>Draft-content elements, as opposed to a pure flag change (Read/Flag).</summary>
	private static bool HasDraftContent(XElement applicationData)
	{
		return applicationData.Element(Email + "To") is not null ||
		       applicationData.Element(Email + "Cc") is not null ||
		       applicationData.Element(Email2 + "Bcc") is not null ||
		       applicationData.Element(Email + "Subject") is not null ||
		       applicationData.Element(AirSyncBase + "Body") is not null ||
		       applicationData.Element(AirSyncBase + "Attachments") is not null;
	}

	public Task DeleteItemAsync(string folderBackendKey, string itemKey, bool permanent, CancellationToken ct)
	{
		return session.RunAsync(async client =>
		{
			IMailFolder folder = await ImapSession.OpenFolderAsync(client, folderBackendKey, FolderAccess.ReadWrite, ct)
				.ConfigureAwait(false);
			UniqueId uid = ParseUid(folder, itemKey);
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

	public Task<(string ItemKey, string Revision)> MoveItemAsync(
		string sourceFolderBackendKey, string itemKey, string destinationFolderBackendKey, CancellationToken ct)
	{
		return session.RunAsync(async client =>
		{
			IMailFolder source = await ImapSession.OpenFolderAsync(client, sourceFolderBackendKey, FolderAccess.ReadWrite, ct)
				.ConfigureAwait(false);
			IMailFolder destination = await client.GetFolderAsync(
				ImapSession.FromBackendKey(destinationFolderBackendKey), ct).ConfigureAwait(false);
			UniqueId uid = ParseUid(source, itemKey);
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

			// F5: fetch the moved message's flags at the destination so the caller can store the
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

			return ($"{validity}:{newUid.Value.Id}", revision);
		}, ct);
	}

	public Task<string> CreateFolderAsync(string? parentBackendKey, string displayName, CancellationToken ct)
	{
		return session.RunAsync(async client =>
		{
			IMailFolder parent = parentBackendKey is not null
				? await client.GetFolderAsync(ImapSession.FromBackendKey(parentBackendKey), ct).ConfigureAwait(false)
				: client.PersonalNamespaces.Count > 0
					? client.GetFolder(client.PersonalNamespaces[0])
					: client.Inbox;
			IMailFolder created = await parent.CreateAsync(displayName, true, ct).ConfigureAwait(false)
			                      ?? throw new BackendException("IMAP server did not return the created folder.");
			return ImapSession.ToBackendKey(created.FullName);
		}, ct);
	}

	public Task RenameFolderAsync(string backendKey, string newDisplayName, CancellationToken ct)
	{
		return session.RunAsync(async client =>
		{
			IMailFolder folder =
				await client.GetFolderAsync(ImapSession.FromBackendKey(backendKey), ct).ConfigureAwait(false);
			IMailFolder parent = folder.ParentFolder
			                     ?? throw new BackendException("Cannot rename a namespace root folder.");
			await folder.RenameAsync(parent, newDisplayName, ct).ConfigureAwait(false);
			return true;
		}, ct);
	}

	public Task DeleteFolderAsync(string backendKey, CancellationToken ct)
	{
		return session.RunAsync(async client =>
		{
			IMailFolder folder =
				await client.GetFolderAsync(ImapSession.FromBackendKey(backendKey), ct).ConfigureAwait(false);
			await folder.DeleteAsync(ct).ConfigureAwait(false);
			return true;
		}, ct);
	}

	// ---------- IMailStoreOperations ----------

	public Task SaveToSentAsync(byte[] mime, CancellationToken ct)
	{
		return session.RunAsync(async client =>
		{
			IMailFolder? sent = await FindSpecialFolderAsync(client, SpecialFolder.Sent, SentNames, ct).ConfigureAwait(false);
			if (sent is null)
				return false;
			using MemoryStream stream = new(mime);
			MimeMessage message = await MimeMessage.LoadAsync(stream, ct).ConfigureAwait(false);
			await sent.AppendAsync(message, MessageFlags.Seen, ct).ConfigureAwait(false);
			return true;
		}, ct, idempotent: false); // APPEND to Sent: a replay would duplicate the sent copy
	}

	public Task<byte[]?> GetRawMessageAsync(string folderBackendKey, string itemKey, CancellationToken ct)
	{
		return session.RunAsync<byte[]?>(async client =>
		{
			IMailFolder folder = await ImapSession.OpenFolderAsync(client, folderBackendKey, FolderAccess.ReadOnly, ct)
				.ConfigureAwait(false);
			try
			{
				MimeMessage message = await folder.GetMessageAsync(ParseUid(folder, itemKey), ct).ConfigureAwait(false);
				using MemoryStream ms = new();
				await message.WriteToAsync(ms, ct).ConfigureAwait(false);
				return ms.ToArray();
			}
			catch (MessageNotFoundException)
			{
				return null;
			}
		}, ct);
	}

	public Task<BackendAttachment?> GetAttachmentAsync(string fileReference, CancellationToken ct)
	{
		string folderKey, itemKey;
		int index;
		try
		{
			(folderKey, itemKey, index) = ParseFileReference(fileReference);
		}
		catch (BackendException)
		{
			// Hand-crafted reference: same answer as an attachment that no longer exists.
			return Task.FromResult<BackendAttachment?>(null);
		}

		return session.RunAsync<BackendAttachment?>(async client =>
		{
			IMailFolder folder = await ImapSession.OpenFolderAsync(client, folderKey, FolderAccess.ReadOnly, ct)
				.ConfigureAwait(false);
			MimeMessage message;
			try
			{
				message = await folder.GetMessageAsync(ParseUid(folder, itemKey), ct).ConfigureAwait(false);
			}
			catch (MessageNotFoundException)
			{
				return null;
			}

			MimeEntity? attachment = message.Attachments.Skip(index).FirstOrDefault();
			if (attachment is not MimePart { Content: not null } part)
				return null;
			using MemoryStream ms = new();
			await part.Content.DecodeToAsync(ms, ct).ConfigureAwait(false);
			return new BackendAttachment(part.ContentType.MimeType, ms.ToArray());
		}, ct);
	}

	public Task SetAnsweredAsync(string folderBackendKey, string itemKey, bool forwarded, CancellationToken ct)
	{
		return session.RunAsync(async client =>
		{
			IMailFolder folder = await ImapSession.OpenFolderAsync(client, folderBackendKey, FolderAccess.ReadWrite, ct)
				.ConfigureAwait(false);
			UniqueId uid = ParseUid(folder, itemKey);
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

	public Task<IReadOnlyList<(string FolderBackendKey, string ItemKey)>> SearchAsync(
		string? folderBackendKey, string freeText, DateTime? sinceUtc, int maxResults, CancellationToken ct)
	{
		return session.RunAsync<IReadOnlyList<(string, string)>>(async client =>
		{
			string folderKey = folderBackendKey ?? ImapSession.ToBackendKey(client.Inbox.FullName);
			IMailFolder folder = await ImapSession.OpenFolderAsync(client, folderKey, FolderAccess.ReadOnly, ct)
				.ConfigureAwait(false);
			await client.NoOpAsync(ct).ConfigureAwait(false); // refresh the selected-folder view
			SearchQuery query = SearchQuery.SubjectContains(freeText)
				.Or(SearchQuery.FromContains(freeText))
				.Or(SearchQuery.ToContains(freeText))
				.Or(SearchQuery.BodyContains(freeText));
			if (sinceUtc is { } since)
				query = query.And(SearchQuery.DeliveredAfter(since.Date));
			IList<UniqueId> uids = await folder.SearchAsync(query, ct).ConfigureAwait(false);
			return uids
				.OrderByDescending(u => u.Id)
				.Take(maxResults)
				.Select(u => (folderKey, ToItemKey(folder, u)))
				.ToList();
		}, ct);
	}

	public Task EmptyFolderAsync(string folderBackendKey, CancellationToken ct)
	{
		return session.RunAsync(async client =>
		{
			IMailFolder folder = await ImapSession.OpenFolderAsync(client, folderBackendKey, FolderAccess.ReadWrite, ct)
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

	private static int ClassifyFolder(IMailFolder folder)
	{
		if (folder.Attributes.HasFlag(FolderAttributes.Inbox) ||
		    folder.FullName.Equals("INBOX", StringComparison.OrdinalIgnoreCase))
			return EasFolderType.Inbox;
		if (IsDraftsFolder(folder))
			return EasFolderType.Drafts;
		if (MatchesSpecialFolder(folder, FolderAttributes.Sent, SentNames))
			return EasFolderType.SentItems;
		if (MatchesSpecialFolder(folder, FolderAttributes.Trash, TrashNames))
			return EasFolderType.DeletedItems;
		return EasFolderType.UserMail;
	}

	/// <summary>
	///   G12: one predicate per special folder (SPECIAL-USE attribute ∪ FullName ∪ leaf Name), so
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
	///   D2: RFC 3501's SEARCH SINCE compares only the calendar date and "disregard[s] ...
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
		IReadOnlyList<string> categories = MailConverter.CategoryKeywords(keywords);
		return categories.Count == 0 ? digits : $"{digits}|{string.Join(',', categories)}";
	}

	/// <summary>
	///   EAS categories are free text while IMAP keywords are RFC 3501 atoms. A category that is
	///   already a valid atom is kept verbatim; one carrying anything an atom cannot hold (spaces,
	///   controls, specials) is DROPPED — the empty string, which the caller filters out. The old
	///   char-by-char '_' substitution collapsed distinct categories ("a b" and "a_b") onto the same
	///   keyword: the server-derived category then never matched the client's original, thrashing the
	///   mail revision string every Sync (D6). Dropping a non-round-trippable category loses it (IMAP
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

	public static string MakeFileReference(string folderBackendKey, string itemKey, int attachmentIndex)
	{
		// Per-component escaping so a '|' inside the folder key/name cannot be mis-parsed.
		return DelimitedKey.Encode(folderBackendKey, itemKey, attachmentIndex.ToString());
	}

	public static (string FolderBackendKey, string ItemKey, int AttachmentIndex) ParseFileReference(string fileReference)
	{
		string[]? parts = DelimitedKey.Decode(fileReference, 3);
		if (parts is null || !int.TryParse(parts[2], out int index) || index < 0)
			throw new BackendException("Malformed file reference.");
		return (parts[0], parts[1], index);
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
	///   G3: the qualified "&lt;uidvalidity&gt;:&lt;uid&gt;" form is REQUIRED — an unqualified key
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

	private static async Task<IMailFolder?> FindSpecialFolderAsync(
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

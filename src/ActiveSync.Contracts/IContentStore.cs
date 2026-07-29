// Copyright (c) 2026 Ruben Andersen
// SPDX-License-Identifier: MIT

namespace ActiveSync.Contracts;

/// <summary>
///   The class-agnostic half of a backend content store, used by the generic sync engine. A store
///   implements exactly ONE of the class aliases — <see cref="IMailStore" />,
///   <see cref="ICalendarStore" />, <see cref="ITaskStore" />, <see cref="IContactStore" /> or
///   <see cref="INotesStore" /> — and the host derives the store's content class from which one;
///   connection creation rejects a store implementing more than one (or none).
/// </summary>
public interface IContentStore
{
	/// <summary>
	///   Whether this store owns the given folder key (each store namespaces its keys, e.g. by an
	///   "imap:"/"caldav:" prefix). Key spaces must be disjoint across the stores of one session —
	///   the session dispatches on the first store that claims a key.
	/// </summary>
	/// <param name="key">The folder key to test.</param>
	/// <returns><c>true</c> when this store owns the key.</returns>
	bool OwnsKey(FolderKey key);

	/// <summary>Lists every folder this store currently exposes.</summary>
	/// <param name="ct">Cancellation token for the backend round-trip.</param>
	/// <returns>The store's current folders (empty when it has none).</returns>
	Task<IReadOnlyList<BackendFolder>> ListFoldersAsync(CancellationToken ct);

	/// <summary>
	///   Current revision map of a folder: item key → revision. A changed revision means the item
	///   must be re-sent to the client. The map is the WHOLE truth for the folder (full
	///   enumeration) — a partial map reads as deletions to the diff engine.
	/// </summary>
	/// <param name="folder">The folder to enumerate.</param>
	/// <param name="filter">The host's date filter; items older than its instant may be omitted.</param>
	/// <param name="ct">Cancellation token for the backend round-trip.</param>
	/// <returns>The folder's complete item key → revision map.</returns>
	Task<IReadOnlyDictionary<ItemKey, ItemRevision>> GetItemRevisionsAsync(
		FolderKey folder, ContentFilter filter, CancellationToken ct);

	/// <summary>
	///   Deletes an item. When <paramref name="permanent" /> is true the client asked for a
	///   hard delete (Sync DeletesAsMoves=0); otherwise a store may move it to Trash. Only mail
	///   distinguishes the two — DAV and local stores always delete outright.
	///   The token comes last (convention) and <paramref name="permanent" /> is required —
	///   an interface method must not carry a caller-invisible default.
	/// </summary>
	/// <param name="folder">The folder holding the item.</param>
	/// <param name="item">The item to delete.</param>
	/// <param name="permanent">Whether the client asked for a hard delete.</param>
	/// <param name="ct">Cancellation token for the backend round-trip.</param>
	Task DeleteItemAsync(FolderKey folder, ItemKey item, bool permanent, CancellationToken ct);

	/// <summary>
	///   Waits until something changes in one of the given folders, or the timeout elapses.
	///   Returns the keys of changed folders (empty on timeout).
	/// </summary>
	/// <param name="folders">The folders to watch.</param>
	/// <param name="timeout">How long to wait before giving up.</param>
	/// <param name="ct">Cancellation token aborting the wait.</param>
	/// <returns>The changed folders' keys, or an empty list on timeout.</returns>
	Task<IReadOnlyList<FolderKey>> WaitForChangesAsync(
		IReadOnlyList<FolderKey> folders, TimeSpan timeout, CancellationToken ct);
}

/// <summary>
///   The payload half of a content store for the four payload classes (calendar, tasks, contacts,
///   notes). <typeparamref name="TItem" /> is the class's payload record; the class constraint is
///   what makes <c>TItem?</c> in <see cref="GetItemsAsync" /> a real nullable reference, so
///   "null = not fetched" is expressible in the type system. Mail is deliberately NOT this shape —
///   see <see cref="IMailStore" />.
/// </summary>
/// <typeparam name="TItem">The content class's payload record.</typeparam>
public interface IContentStore<TItem> : IContentStore where TItem : class
{
	/// <summary>Fetches one item's full payload; null when the item no longer exists.</summary>
	/// <param name="folder">The folder holding the item.</param>
	/// <param name="item">The item to fetch.</param>
	/// <param name="ct">Cancellation token for the backend round-trip.</param>
	/// <returns>The item's payload, or <c>null</c> when it vanished or could not be fetched.</returns>
	Task<TItem?> GetItemAsync(FolderKey folder, ItemKey item, CancellationToken ct);

	/// <summary>
	///   Fetches several items of one folder in a single round. The sync engine calls this once
	///   per windowed batch instead of <see cref="GetItemAsync" /> per item, so a store can amortize
	///   the per-fetch overhead. The returned map is keyed by item key; a key mapped to
	///   <c>null</c> (or absent) vanished or could not be fetched and is skipped, exactly as a
	///   <c>null</c> from <see cref="GetItemAsync" /> is. <c>null</c> means "not fetched" — the
	///   caller MUST NOT advance the persisted snapshot for that item (neither recording it as a
	///   delivered Add nor recording a Change's new revision), so it is retried on the next Sync
	///   round instead of being silently and permanently dropped. The DEFAULT implementation
	///   loops <see cref="GetItemAsync" /> — a per-item failure becomes a <c>null</c> entry so one
	///   bad item never fails the batch — so a simple store works unchanged; a store overrides it
	///   to batch at the protocol level.
	/// </summary>
	/// <param name="folder">The folder holding the items.</param>
	/// <param name="items">The items to fetch.</param>
	/// <param name="ct">Cancellation token for the backend round-trip.</param>
	/// <returns>Item key → payload map; <c>null</c> entries were not fetched.</returns>
	async Task<IReadOnlyDictionary<ItemKey, TItem?>> GetItemsAsync(
		FolderKey folder, IReadOnlyList<ItemKey> items, CancellationToken ct)
	{
		Dictionary<ItemKey, TItem?> fetched = new(items.Count);
		foreach (ItemKey item in items)
		{
			try
			{
				fetched[item] = await GetItemAsync(folder, item, ct).ConfigureAwait(false);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				// One item's fetch failure must not sink the whole batch: record it as "not
				// fetched" (null) so the caller skips it and re-tries it on the next Sync round,
				// the same outcome GetItemAsync's per-item failure produces.
				fetched[item] = null;
			}
		}

		return fetched;
	}

	/// <summary>Creates an item from a complete payload; returns its key and revision.</summary>
	/// <param name="folder">The folder to create the item in.</param>
	/// <param name="item">The complete payload to store.</param>
	/// <param name="ct">Cancellation token for the backend round-trip.</param>
	/// <returns>The created item's key and its revision.</returns>
	Task<(ItemKey Key, ItemRevision Revision)> CreateItemAsync(
		FolderKey folder, TItem item, CancellationToken ct);

	/// <summary>
	///   Replaces an item's payload. The host performs any partial-update merge itself and always
	///   hands over a COMPLETE payload — a store never sees a patch.
	///   <para>
	///     <paramref name="expected" /> is an optional precondition (If-Match / ifInState style):
	///     a store that can check it throws <see cref="BackendPreconditionFailedException" /> when
	///     the item's current revision differs, so the host can re-fetch, re-merge and retry once.
	///     A store that CANNOT check it ignores it and applies the write — that is conforming;
	///     the precondition is an upgrade, never an obligation.
	///   </para>
	/// </summary>
	/// <param name="folder">The folder holding the item.</param>
	/// <param name="item">The item to replace.</param>
	/// <param name="value">The complete new payload.</param>
	/// <param name="expected">The revision the write is conditioned on; null = unconditional.</param>
	/// <param name="ct">Cancellation token for the backend round-trip.</param>
	/// <returns>The item's new revision.</returns>
	Task<ItemRevision> UpdateItemAsync(
		FolderKey folder, ItemKey item, TItem value, ItemRevision? expected, CancellationToken ct);
}

/// <summary>A calendar store: <see cref="CalendarItem" /> payloads (iCalendar VEVENT).</summary>
public interface ICalendarStore : IContentStore<CalendarItem>;

/// <summary>A task store: <see cref="TaskItem" /> payloads (iCalendar VTODO).</summary>
public interface ITaskStore : IContentStore<TaskItem>;

/// <summary>A contact store: <see cref="ContactItem" /> payloads (vCard).</summary>
public interface IContactStore : IContentStore<ContactItem>;

/// <summary>A notes store: typed <see cref="NoteItem" /> payloads.</summary>
public interface INotesStore : IContentStore<NoteItem>;

/// <summary>
///   The mail store. Deliberately NOT <c>IContentStore&lt;MailItem&gt;</c>: mail's everyday write
///   is a flags/categories PATCH (the RFC822 is never rewritten), and its only content write — an
///   EAS 16.x draft rewrite — can CHANGE THE ITEM KEY (IMAP: delete + append). A generic
///   <c>UpdateItemAsync</c> taking a full <see cref="MailItem" /> could express neither: it
///   conflates "mark read" with "rewrite the message", forces the host to materialise RFC822
///   bytes to set a flag, and cannot report a moved key.
/// </summary>
public interface IMailStore : IContentStore
{
	/// <summary>Fetches one message; null when it no longer exists.</summary>
	/// <param name="folder">The folder holding the message.</param>
	/// <param name="item">The message to fetch.</param>
	/// <param name="options">Fetch options (always-full today; the future truncation hook).</param>
	/// <param name="ct">Cancellation token for the backend round-trip.</param>
	/// <returns>The message, or <c>null</c> when it vanished or could not be fetched.</returns>
	Task<MailItem?> GetItemAsync(
		FolderKey folder, ItemKey item, MailFetchOptions options, CancellationToken ct);

	/// <summary>
	///   Batch fetch, with the same "null = not fetched, do not advance the snapshot" contract as
	///   <see cref="IContentStore{TItem}.GetItemsAsync" /> — see there for the full rule. The
	///   default implementation loops <see cref="GetItemAsync" />.
	/// </summary>
	/// <param name="folder">The folder holding the messages.</param>
	/// <param name="items">The messages to fetch.</param>
	/// <param name="options">Fetch options (always-full today; the future truncation hook).</param>
	/// <param name="ct">Cancellation token for the backend round-trip.</param>
	/// <returns>Item key → message map; <c>null</c> entries were not fetched.</returns>
	async Task<IReadOnlyDictionary<ItemKey, MailItem?>> GetItemsAsync(
		FolderKey folder, IReadOnlyList<ItemKey> items, MailFetchOptions options, CancellationToken ct)
	{
		Dictionary<ItemKey, MailItem?> fetched = new(items.Count);
		foreach (ItemKey item in items)
		{
			try
			{
				fetched[item] = await GetItemAsync(folder, item, options, ct).ConfigureAwait(false);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				fetched[item] = null;
			}
		}

		return fetched;
	}

	/// <summary>
	///   Creates an EAS 16.x draft — the ONLY mail create a client can Sync; a store refuses any
	///   folder but Drafts.
	/// </summary>
	/// <param name="folder">The Drafts folder to create the draft in.</param>
	/// <param name="item">The complete draft message.</param>
	/// <param name="ct">Cancellation token for the backend round-trip.</param>
	/// <returns>The created draft's key and its revision.</returns>
	Task<(ItemKey Key, ItemRevision Revision)> CreateDraftAsync(
		FolderKey folder, MailItem item, CancellationToken ct);

	/// <summary>
	///   The everyday mail Change: flags and categories, presence-carried (see
	///   <see cref="MailFlagsPatch" />). The RFC822 is never touched. <paramref name="expected" />
	///   follows the same precondition contract as
	///   <see cref="IContentStore{TItem}.UpdateItemAsync" /> — stores that cannot check it (IMAP
	///   has no If-Match for flags) ignore it.
	/// </summary>
	/// <param name="folder">The folder holding the message.</param>
	/// <param name="item">The message to patch.</param>
	/// <param name="patch">The flags/categories to apply; unsupplied fields stay untouched.</param>
	/// <param name="expected">The revision the write is conditioned on; null = unconditional.</param>
	/// <param name="ct">Cancellation token for the backend round-trip.</param>
	/// <returns>The message's new revision.</returns>
	Task<ItemRevision> UpdateFlagsAsync(
		FolderKey folder, ItemKey item, MailFlagsPatch patch, ItemRevision? expected, CancellationToken ct);

	/// <summary>
	///   EAS 16.x draft content rewrite (Drafts folder only). The returned key MAY differ from
	///   the input key — IMAP implements this as delete + append, so the UID moves. CAUTION for
	///   the host: the returned key is informational (logging, the response to this command) and
	///   MUST NOT be echo-suppressed into the snapshot under the NEW key — the client still holds
	///   the OLD ServerId, and the next diff's Delete+Add against the unpatched snapshot IS the
	///   re-identification that teaches it the new one. Patching the snapshot under the new key
	///   would suppress exactly that.
	/// </summary>
	/// <param name="folder">The Drafts folder holding the draft.</param>
	/// <param name="item">The draft to rewrite.</param>
	/// <param name="value">The complete merged replacement message.</param>
	/// <param name="ct">Cancellation token for the backend round-trip.</param>
	/// <returns>The rewritten draft's (possibly moved) key and its revision.</returns>
	Task<(ItemKey Key, ItemRevision Revision)> ReplaceDraftAsync(
		FolderKey folder, ItemKey item, MailItem value, CancellationToken ct);
}

/// <summary>
///   Optional capability: a store that can move an item to another folder of the same class
///   (EAS MoveItems). Mail stores and the JMAP calendar/contact stores implement it; DAV and local
///   stores do not. A store without it makes MoveItems answer Status 5 (move failed) — the same
///   answer the "not supported" throw used to produce.
/// </summary>
public interface IItemMoveOperations
{
	/// <summary>
	///   Moves an item to another folder of the same class; returns the new item key AND the
	///   item's revision at the destination (the same token <see cref="IContentStore.GetItemRevisionsAsync" />
	///   would report for it there). The caller persists this into the destination collection's
	///   snapshot so the next diff does not see a manufactured value that can never match the
	///   backend's real revision and re-send the item as a spurious Change.
	/// </summary>
	/// <param name="source">The folder holding the item.</param>
	/// <param name="item">The item to move.</param>
	/// <param name="destination">The folder to move it to.</param>
	/// <param name="ct">Cancellation token for the backend round-trip.</param>
	/// <returns>The moved item's new key and its revision at the destination.</returns>
	Task<(ItemKey Key, ItemRevision Revision)> MoveItemAsync(
		FolderKey source, ItemKey item, FolderKey destination, CancellationToken ct);
}

/// <summary>
///   Optional capability: a store whose folder set the client can mutate — FolderCreate,
///   FolderUpdate (rename) and FolderDelete. Only mail stores implement it; calendar, contacts,
///   task and local stores expose a fixed folder set. A store without it makes those commands
///   answer EAS Status 3 (folder operation not permitted), the same as an unmodifiable system folder.
/// </summary>
public interface IFolderOperations
{
	/// <summary>Creates a folder. Returns the new folder's key.</summary>
	/// <param name="parent">The parent folder, or <c>null</c> for a root-level folder.</param>
	/// <param name="displayName">The new folder's display name.</param>
	/// <param name="ct">Cancellation token for the backend round-trip.</param>
	/// <returns>The created folder's key.</returns>
	Task<FolderKey> CreateFolderAsync(FolderKey? parent, string displayName, CancellationToken ct);

	/// <summary>Renames a folder in place (its key is stable across the rename).</summary>
	/// <param name="folder">The folder to rename.</param>
	/// <param name="newDisplayName">The new display name.</param>
	/// <param name="ct">Cancellation token for the backend round-trip.</param>
	Task RenameFolderAsync(FolderKey folder, string newDisplayName, CancellationToken ct);

	/// <summary>Deletes a folder and its contents.</summary>
	/// <param name="folder">The folder to delete.</param>
	/// <param name="ct">Cancellation token for the backend round-trip.</param>
	Task DeleteFolderAsync(FolderKey folder, CancellationToken ct);
}

/// <summary>
///   Implemented by calendar stores that serve inline event attachments (EAS 16.x): resolves one
///   attachment of an event by its position.
/// </summary>
public interface ICalendarAttachmentSource
{
	/// <summary>
	///   Resolves one inline attachment of an event by its position.
	///   <paramref name="index" /> is normatively the Nth <c>ATTACH</c> property of the event in
	///   the payload the store itself handed over — so a store can always resolve it from its own
	///   data, with no out-of-band knowledge.
	/// </summary>
	/// <param name="folder">The calendar folder holding the event.</param>
	/// <param name="item">The event holding the attachment.</param>
	/// <param name="index">The attachment's position among the event's ATTACH properties.</param>
	/// <param name="ct">Cancellation token for the backend round-trip.</param>
	/// <returns>The attachment, or <c>null</c> when the event or attachment no longer exists.</returns>
	Task<BackendAttachment?> GetEventAttachmentAsync(
		FolderKey folder, ItemKey item, int index, CancellationToken ct);
}

/// <summary>One period a target is not free, and how it counts against availability.</summary>
public sealed record BusyPeriod
{
	/// <summary>When the period starts.</summary>
	public required DateTimeOffset Start { get; init; }

	/// <summary>When the period ends.</summary>
	public required DateTimeOffset End { get; init; }

	/// <summary>How the period counts against availability (tentative, busy, out-of-office).</summary>
	public required BusyKind Kind { get; init; }
}

/// <summary>
///   Implemented by calendar stores that can answer free/busy queries for the
///   ResolveRecipients Availability option. Null = no data obtainable for that target
///   (per-recipient Availability status 163) — an EMPTY list means "completely free".
/// </summary>
public interface IFreeBusySource
{
	/// <summary>
	///   Fetches the busy periods for one recipient over a time range. <c>null</c> means no data
	///   is obtainable for that target (per-recipient Availability status 163) — this is distinct
	///   from an EMPTY list, which means the target is completely free. A failure resolving one
	///   target must never fail the whole ResolveRecipients response.
	/// </summary>
	/// <param name="targetAddress">The recipient's address to query.</param>
	/// <param name="start">Start of the queried range.</param>
	/// <param name="end">End of the queried range.</param>
	/// <param name="ct">Cancellation token for the backend round-trip.</param>
	/// <returns>The recipient's busy periods, an empty list if free, or <c>null</c> if no data is available.</returns>
	Task<IReadOnlyList<BusyPeriod>?> GetBusyPeriodsAsync(
		string targetAddress, DateTimeOffset start, DateTimeOffset end, CancellationToken ct);
}

/// <summary>
///   Implemented by stores whose folders can be granted read-only (shared calendars):
///   client writes into such a folder are silently reverted by the sync engine, the same
///   convergence semantics as global ReadOnly mode.
/// </summary>
public interface IReadOnlyCollectionSource
{
	/// <summary>Whether the folder maps to a collection granted read-only.</summary>
	/// <param name="folder">The folder to test.</param>
	/// <returns><c>true</c> when the folder is granted read-only.</returns>
	bool IsReadOnlyCollection(FolderKey folder);
}

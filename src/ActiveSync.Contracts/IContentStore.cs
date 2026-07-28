// Copyright (c) 2026 Ruben Andersen
// SPDX-License-Identifier: MIT

using System.Xml.Linq;

namespace ActiveSync.Contracts;

/// <summary>
///   Uniform view over a backend data class (mail, calendar, contacts, tasks) used by the
///   generic Sync engine. Item keys are backend-scoped strings (IMAP UID / DAV href id) that
///   are stable for the lifetime of the item within its folder.
/// </summary>
public interface IContentStore
{
	/// <summary>EAS content class served by this store (Email/Calendar/Contacts/Tasks).</summary>
	string EasClass { get; }

	/// <summary>
	///   Whether this store owns the given folder/item backend key (each store namespaces its
	///   keys, e.g. by a "imap:"/"caldav:" prefix). Key spaces must be disjoint across the
	///   stores of one session — the session dispatches on the first store that claims a key.
	/// </summary>
	bool OwnsBackendKey(string backendKey);

	/// <summary>Lists every folder this store currently exposes for its <see cref="EasClass" />.</summary>
	/// <param name="ct">Cancellation token for the backend round-trip.</param>
	/// <returns>The store's current folders (empty when it has none).</returns>
	Task<IReadOnlyList<BackendFolder>> ListFoldersAsync(CancellationToken ct);

	/// <summary>
	///   Current revision map of a folder: item key → revision token (flags hash / ETag).
	///   A changed revision means the item must be re-sent to the client.
	/// </summary>
	Task<IReadOnlyDictionary<string, string>> GetItemRevisionsAsync(
		string folderBackendKey, ContentFilter filter, CancellationToken ct);

	/// <summary>Fetches an item converted to EAS ApplicationData elements.</summary>
	Task<BackendItem?> GetItemAsync(
		string folderBackendKey, string itemKey, BodyPreference bodyPreference, CancellationToken ct);

	/// <summary>
	///   Fetches several items of one folder in a single round. The Sync engine calls this once
	///   per windowed batch instead of <see cref="GetItemAsync" /> per item, so a store can amortize
	///   the per-fetch overhead — for IMAP, one session lease + one folder open + one FETCH set
	///   rather than N of each. The returned map is keyed by item key; a key mapped to
	///   <c>null</c> (or absent) vanished or could not be fetched and is skipped, exactly as a
	///   <c>null</c> from <see cref="GetItemAsync" /> is. <c>null</c> means "not fetched" — the
	///   caller MUST NOT advance the persisted snapshot for that item (neither recording it as a
	///   delivered Add nor recording a Change's new revision), so it is retried on the next Sync
	///   round instead of being silently and permanently dropped. The DEFAULT implementation
	///   loops <see cref="GetItemAsync" /> — a per-item failure becomes a <c>null</c> entry so one bad
	///   item never fails the batch — so existing stores keep working unchanged; a store overrides
	///   it to batch at the protocol level.
	/// </summary>
	async Task<IReadOnlyDictionary<string, BackendItem?>> GetItemsAsync(
		string folderBackendKey, IReadOnlyList<string> itemKeys, BodyPreference bodyPreference, CancellationToken ct)
	{
		Dictionary<string, BackendItem?> items = new(itemKeys.Count, StringComparer.Ordinal);
		foreach (string itemKey in itemKeys)
		{
			try
			{
				items[itemKey] = await GetItemAsync(folderBackendKey, itemKey, bodyPreference, ct).ConfigureAwait(false);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				// One item's fetch failure must not sink the whole batch: record it as "not
				// fetched" (null) so the caller skips it and re-tries it on the next Sync round,
				// the same outcome GetItemAsync's per-item catch produced.
				items[itemKey] = null;
			}
		}
		return items;
	}

	/// <summary>Creates an item from client ApplicationData; returns (itemKey, revision).</summary>
	Task<(string ItemKey, string Revision)> CreateItemAsync(
		string folderBackendKey, XElement applicationData, CancellationToken ct);

	/// <summary>Applies a client change; returns the new revision.</summary>
	Task<string> UpdateItemAsync(
		string folderBackendKey, string itemKey, XElement applicationData, CancellationToken ct);

	/// <summary>
	///   Deletes an item. When <paramref name="permanent" /> is true the client asked for a
	///   hard delete (Sync DeletesAsMoves=0); otherwise a store may move it to Trash. Only mail
	///   distinguishes the two — DAV and local stores always delete outright.
	///   The token comes last (convention) and <paramref name="permanent" /> is required —
	///   an interface method must not carry a caller-invisible default.
	/// </summary>
	Task DeleteItemAsync(string folderBackendKey, string itemKey, bool permanent, CancellationToken ct);

	// Item move and folder mutation are OPTIONAL CAPABILITIES (IItemMoveOperations /
	// IFolderOperations below), not mandatory members — a third of the in-repo stores threw
	// "not supported" for them (local, DAV, and JMAP calendar/contact folders). A store implements
	// only the capabilities it truly has; the host checks for the interface and answers the EAS
	// command with the unsupported status when it is absent (the same status the throw used to
	// produce), instead of every store carrying a stub.

	/// <summary>
	///   Waits until something changes in one of the given folders, or the timeout elapses.
	///   Returns backend keys of changed folders (empty on timeout).
	/// </summary>
	Task<IReadOnlyList<string>> WaitForChangesAsync(
		IReadOnlyList<string> folderBackendKeys, TimeSpan timeout, CancellationToken ct);
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
	Task<(string ItemKey, string Revision)> MoveItemAsync(
		string sourceFolderBackendKey, string itemKey, string destinationFolderBackendKey, CancellationToken ct);
}

/// <summary>
///   Optional capability: a store whose folder set the client can mutate — FolderCreate,
///   FolderUpdate (rename) and FolderDelete. Only mail stores implement it; calendar, contacts,
///   task and local stores expose a fixed folder set. A store without it makes those commands
///   answer EAS Status 3 (folder operation not permitted), the same as an unmodifiable system folder.
/// </summary>
public interface IFolderOperations
{
	/// <summary>Creates a folder. Returns the new folder's backend key.</summary>
	Task<string> CreateFolderAsync(string? parentBackendKey, string displayName, CancellationToken ct);

	/// <summary>Renames a folder in place (its backend key is stable across the rename).</summary>
	/// <param name="backendKey">The folder's backend key.</param>
	/// <param name="newDisplayName">The new display name.</param>
	/// <param name="ct">Cancellation token for the backend round-trip.</param>
	Task RenameFolderAsync(string backendKey, string newDisplayName, CancellationToken ct);

	/// <summary>Deletes a folder and its contents.</summary>
	/// <param name="backendKey">The folder's backend key.</param>
	/// <param name="ct">Cancellation token for the backend round-trip.</param>
	Task DeleteFolderAsync(string backendKey, CancellationToken ct);
}

/// <summary>
///   Implemented by calendar stores that serve inline event attachments (EAS 16.x):
///   resolves an ItemOperations "calatt::…" FileReference to the attachment bytes.
/// </summary>
public interface ICalendarAttachmentSource
{
	/// <summary>
	///   Resolves one inline attachment of an event by its position, as encoded in a
	///   "calatt::&lt;serverId&gt;::&lt;index&gt;" FileReference (the converter emits the index,
	///   SyncHandler stamps the ServerId).
	/// </summary>
	/// <param name="folderBackendKey">The calendar folder's backend key.</param>
	/// <param name="itemKey">The event's item key.</param>
	/// <param name="index">The attachment's position within the event's attachment list.</param>
	/// <param name="ct">Cancellation token for the backend round-trip.</param>
	/// <returns>The attachment bytes and content type, or <c>null</c> when the event or attachment no longer exists.</returns>
	Task<BackendAttachment?> GetEventAttachmentAsync(
		string folderBackendKey, string itemKey, int index, CancellationToken ct);
}

/// <summary>A busy period with its MergedFreeBusy digit ('1' tentative, '2' busy, '3' OOF).</summary>
public sealed record BusyPeriod(DateTime StartUtc, DateTime EndUtc, char Kind);

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
	/// <param name="startUtc">Start of the queried range, in UTC.</param>
	/// <param name="endUtc">End of the queried range, in UTC.</param>
	/// <param name="ct">Cancellation token for the backend round-trip.</param>
	/// <returns>The recipient's busy periods, an empty list if free, or <c>null</c> if no data is available.</returns>
	Task<IReadOnlyList<BusyPeriod>?> GetBusyPeriodsAsync(
		string targetAddress, DateTime startUtc, DateTime endUtc, CancellationToken ct);
}

/// <summary>
///   Implemented by stores whose folders can be granted read-only (shared calendars):
///   client writes into such a folder are silently reverted by the sync engine, the same
///   convergence semantics as global ReadOnly mode.
/// </summary>
public interface IReadOnlyCollectionSource
{
	/// <summary>Whether the folder maps to a collection granted read-only.</summary>
	bool IsReadOnlyCollection(string folderBackendKey);
}

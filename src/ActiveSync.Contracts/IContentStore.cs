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
	///   K59: the token comes last (convention) and <paramref name="permanent" /> is required —
	///   an interface method must not carry a caller-invisible default.
	/// </summary>
	Task DeleteItemAsync(string folderBackendKey, string itemKey, bool permanent, CancellationToken ct);

	/// <summary>Moves an item to another folder of the same class; returns the new item key.</summary>
	Task<string> MoveItemAsync(
		string sourceFolderBackendKey, string itemKey, string destinationFolderBackendKey, CancellationToken ct);

	/// <summary>Folder manipulation. Returns the new folder's backend key.</summary>
	Task<string> CreateFolderAsync(string? parentBackendKey, string displayName, CancellationToken ct);

	Task RenameFolderAsync(string backendKey, string newDisplayName, CancellationToken ct);
	Task DeleteFolderAsync(string backendKey, CancellationToken ct);

	/// <summary>
	///   Waits until something changes in one of the given folders, or the timeout elapses.
	///   Returns backend keys of changed folders (empty on timeout).
	/// </summary>
	Task<IReadOnlyList<string>> WaitForChangesAsync(
		IReadOnlyList<string> folderBackendKeys, TimeSpan timeout, CancellationToken ct);
}

/// <summary>
///   Implemented by calendar stores that serve inline event attachments (EAS 16.x):
///   resolves an ItemOperations "calatt::…" FileReference to the attachment bytes.
/// </summary>
public interface ICalendarAttachmentSource
{
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

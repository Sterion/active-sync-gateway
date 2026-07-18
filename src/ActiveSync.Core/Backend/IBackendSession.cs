using System.Xml.Linq;

namespace ActiveSync.Core.Backend;

/// <summary>Mail-specific operations that fall outside the generic content-store surface.</summary>
public interface IMailOperations
{
	/// <summary>Submits a raw MIME message via SMTP.</summary>
	Task SendAsync(byte[] mime, CancellationToken ct);

	/// <summary>Appends a raw MIME message to the Sent folder.</summary>
	Task SaveToSentAsync(byte[] mime, CancellationToken ct);

	/// <summary>Fetches the raw MIME of a message (for SmartReply/SmartForward source).</summary>
	Task<byte[]?> GetRawMessageAsync(string folderBackendKey, string itemKey, CancellationToken ct);

	/// <summary>Fetches an attachment by its EAS FileReference.</summary>
	Task<BackendAttachment?> GetAttachmentAsync(string fileReference, CancellationToken ct);

	/// <summary>Marks a message answered/forwarded after SmartReply/SmartForward.</summary>
	Task SetAnsweredAsync(string folderBackendKey, string itemKey, bool forwarded, CancellationToken ct);

	/// <summary>Server-side mailbox search; returns (folderBackendKey, itemKey) hits, newest first.</summary>
	Task<IReadOnlyList<(string FolderBackendKey, string ItemKey)>> SearchAsync(
		string? folderBackendKey, string freeText, DateTime? sinceUtc, int maxResults, CancellationToken ct);

	/// <summary>Empties a folder (ItemOperations EmptyFolderContents).</summary>
	Task EmptyFolderAsync(string folderBackendKey, CancellationToken ct);
}

/// <summary>
///   Contact-photo request accompanying a GAL search (MS-ASCMD Picture element):
///   photos over <see cref="MaxSizeBytes" /> report status 174, photos beyond
///   <see cref="MaxCount" /> across the result set report status 175.
/// </summary>
public sealed record GalPhotoRequest(int? MaxSizeBytes, int? MaxCount);

/// <summary>Contact-specific operations (GAL search for ResolveRecipients / Search).</summary>
public interface IContactOperations
{
	/// <summary>
	///   Searches all address books; returns GAL-style entries as EAS Gal-namespace
	///   properties. When <paramref name="photos" /> is set, each entry carries a
	///   gal:Picture element (status + data per the photo rules).
	/// </summary>
	Task<IReadOnlyList<IReadOnlyList<XElement>>> SearchGalAsync(
		string query, int maxResults, GalPhotoRequest? photos, CancellationToken ct);
}

/// <summary>
///   Out-of-office backend (ManageSieve). The state database is the source of truth for the
///   Oof SETTINGS; this interface only manages the vacation script on the mail server.
/// </summary>
public interface IOofBackend
{
	/// <summary>
	///   Uploads the gateway-owned vacation script and makes it the active script. Returns
	///   the name of the PREVIOUSLY active script ("" when none was active) so it can be
	///   restored on disable — callers must ignore the return when it names our own script.
	/// </summary>
	Task<string> ActivateAsync(string sieveScript, CancellationToken ct);

	/// <summary>
	///   Removes the gateway-owned script and restores the given previously active script
	///   (empty = leave no script active). Missing scripts are tolerated.
	/// </summary>
	Task DeactivateAsync(string previousActiveScript, CancellationToken ct);
}

/// <summary>Calendar-specific operations.</summary>
public interface ICalendarOperations
{
	/// <summary>
	///   Responds to a meeting request: updates the attendee PARTSTAT on the stored event and sends
	///   an iTIP REPLY to the organizer. Returns the calendar item key holding the event, if any.
	/// </summary>
	Task<string?> RespondToMeetingAsync(
		string calendarFolderBackendKey, string eventUid, int userResponse, CancellationToken ct);

	/// <summary>
	///   The stored (merged) iCalendar of one event — the invitation service reads it so
	///   16.x ghosted attendees survive; null when the item vanished.
	/// </summary>
	Task<string?> GetRawEventAsync(string folderBackendKey, string itemKey, CancellationToken ct);

	/// <summary>
	///   Whether the GATEWAY should mail iMIP invitations for organizer changes: the
	///   SendInvitations knob, with Auto probing the server for an RFC 6638 schedule
	///   outbox (server schedules itself → gateway stays silent to avoid double invites).
	/// </summary>
	Task<bool> ShouldSendInvitationsAsync(CancellationToken ct);
}

/// <summary>
///   A per-user backend session bundling the content stores and side operations. Sessions cache
///   live protocol connections (IMAP) and are reused across requests for the same user+device.
/// </summary>
public interface IBackendSession : IAsyncDisposable
{
	/// <summary>The gateway credentials — the user's identity, not any backend login.</summary>
	BackendCredentials Credentials { get; }

	/// <summary>
	///   The user's mail address (explicit in Accounts mode; in PassThrough the login when it
	///   contains '@'). Null when unknown — consumers must degrade, not guess.
	/// </summary>
	string? MailAddress { get; }

	/// <summary>All content stores available for this deployment (mail always; DAV stores if configured).</summary>
	IReadOnlyList<IContentStore> Stores { get; }

	IMailOperations Mail { get; }
	IContactOperations? Contacts { get; }
	ICalendarOperations? Calendar { get; }

	/// <summary>Sieve-backed out-of-office management; null when Sieve is not configured.</summary>
	IOofBackend? Oof { get; }

	IContentStore? GetStoreForClass(string easClass);
	IContentStore? GetStoreForBackendKey(string backendKey);

	/// <summary>
	///   Whether the folder is granted read-only (shared calendars): client writes are then
	///   silently reverted, the same convergence semantics as global ReadOnly mode.
	/// </summary>
	bool IsReadOnlyFolder(string folderBackendKey);
}

public interface IBackendSessionFactory
{
	/// <summary>Validates credentials against the mail backend (used by HTTP Basic auth).</summary>
	Task<bool> AuthenticateAsync(BackendCredentials credentials, CancellationToken ct);

	/// <summary>Gets or creates a cached session for the user/device pair.</summary>
	Task<IBackendSession> GetSessionAsync(BackendCredentials credentials, string deviceId, CancellationToken ct);
}

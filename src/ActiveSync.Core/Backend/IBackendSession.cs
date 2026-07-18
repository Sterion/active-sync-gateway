using System.Xml.Linq;

namespace ActiveSync.Core.Backend;

/// <summary>
///   Mail-STORE operations that fall outside the generic content-store surface. Submission
///   is deliberately separate (<see cref="IMailSubmitOperations" />): store and submit are
///   different roles that may be served by different backends (IMAP + SMTP today; one JMAP
///   session may serve both).
/// </summary>
public interface IMailStoreOperations
{
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

/// <summary>Outbound mail submission (SMTP today; a JMAP backend may submit itself).</summary>
public interface IMailSubmitOperations
{
	/// <summary>Submits a raw MIME message for delivery.</summary>
	Task SendAsync(byte[] mime, CancellationToken ct);
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
///   The out-of-office auto-reply, one body for every audience; null Start/End means
///   "until disabled". Backends that cannot render HTML may send the body as-is.
/// </summary>
public sealed record OofReply(string BodyText, bool BodyIsHtml, DateTime? StartUtc, DateTime? EndUtc);

/// <summary>
///   Out-of-office backend (ManageSieve today). The state database is the source of truth
///   for the Oof SETTINGS; the backend renders and arms its own server-side rule from the
///   semantic reply — callers never see scripts or rules.
/// </summary>
public interface IOofBackend
{
	/// <summary>
	///   Arms the auto-reply. Returns the restore token the caller must persist for
	///   <see cref="DisableAsync" /> (Sieve: the previously active script name, "" when
	///   nothing was active) — or null when the gateway's own rule was already armed, in
	///   which case the caller's stored token remains the one to restore.
	/// </summary>
	Task<string?> EnableAsync(OofReply reply, CancellationToken ct);

	/// <summary>
	///   Disarms the auto-reply and restores the given token ("" = leave nothing active).
	///   Missing or stale tokens are tolerated.
	/// </summary>
	Task DisableAsync(string restoreToken, CancellationToken ct);
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

	IMailStoreOperations MailStore { get; }
	IMailSubmitOperations MailSubmit { get; }
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

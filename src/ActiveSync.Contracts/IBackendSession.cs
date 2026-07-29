// Copyright (c) 2026 Ruben Andersen
// SPDX-License-Identifier: MIT

using System.Xml.Linq;

namespace ActiveSync.Contracts;

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
		string? folderBackendKey, string freeText, DateTimeOffset? since, int maxResults, CancellationToken ct);

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
public sealed record GalPhotoRequest
{
	/// <summary>Largest photo the client accepts, in bytes; <c>null</c> means no size limit.</summary>
	public int? MaxSizeBytes { get; init; }

	/// <summary>How many photos the client accepts across the whole result set; <c>null</c> means no limit.</summary>
	public int? MaxCount { get; init; }
}

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
public sealed record OofReply
{
	/// <summary>The reply body, sent to every audience.</summary>
	public required string BodyText { get; init; }

	/// <summary>Whether <see cref="BodyText" /> is HTML rather than plain text.</summary>
	public bool BodyIsHtml { get; init; }

	/// <summary>When the auto-reply starts; <c>null</c> means "immediately".</summary>
	public DateTimeOffset? Start { get; init; }

	/// <summary>When the auto-reply ends; <c>null</c> means "until disabled".</summary>
	public DateTimeOffset? End { get; init; }
}

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

// IBackendSession / IBackendSessionFactory are the HOST's composite-session aggregation and
// its cache — nothing a plugin implements or receives (a plugin implements IBackendConnection and
// the store/side-op interfaces above). They moved to ActiveSync.Core.Backend so the published
// plugin surface carries only what a plugin actually builds against.

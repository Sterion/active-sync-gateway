// Copyright (c) 2026 Ruben Andersen
// SPDX-License-Identifier: MIT

namespace ActiveSync.Contracts;

/// <summary>One search hit: the folder and item it names.</summary>
/// <remarks>
///   A record rather than a tuple because two same-typed elements carry no meaning on their own —
///   the tuple rule: a tuple is acceptable where the element TYPES disambiguate the elements,
///   a record is required where they do not.
/// </remarks>
public sealed record SearchHit
{
	/// <summary>The folder holding the hit.</summary>
	public required FolderKey Folder { get; init; }

	/// <summary>The matching item.</summary>
	public required ItemKey Item { get; init; }
}

/// <summary>
///   Mailbox-level operations beside the mail store's item surface. Submission is deliberately
///   separate (<see cref="IMailSubmitOperations" />): store and submit are different roles that
///   may be served by different backends (IMAP + SMTP today; one JMAP session may serve both).
/// </summary>
public interface IMailboxOperations
{
	/// <summary>Appends a raw RFC822 message to the Sent folder.</summary>
	/// <param name="rfc822">The raw message bytes (ownership rule: a dedicated, never-mutated buffer).</param>
	/// <param name="ct">Cancellation token for the backend round-trip.</param>
	Task SaveToSentAsync(ReadOnlyMemory<byte> rfc822, CancellationToken ct);

	/// <summary>
	///   Fetches the raw RFC822 of a message (SmartReply/SmartForward source, host-side
	///   attachment extraction). NULLABLE: null = the message vanished, which
	///   SmartReply/SmartForward rely on to answer the right status.
	/// </summary>
	/// <param name="folder">The folder holding the message.</param>
	/// <param name="item">The message to fetch.</param>
	/// <param name="ct">Cancellation token for the backend round-trip.</param>
	/// <returns>The raw message bytes, or <c>null</c> when the message no longer exists.</returns>
	Task<ReadOnlyMemory<byte>?> GetRawMessageAsync(FolderKey folder, ItemKey item, CancellationToken ct);

	/// <summary>Marks a message answered/forwarded after SmartReply/SmartForward.</summary>
	/// <param name="folder">The folder holding the message.</param>
	/// <param name="item">The message to mark.</param>
	/// <param name="forwarded"><c>true</c> marks $Forwarded; <c>false</c> marks answered.</param>
	/// <param name="ct">Cancellation token for the backend round-trip.</param>
	Task SetAnsweredAsync(FolderKey folder, ItemKey item, bool forwarded, CancellationToken ct);

	/// <summary>
	///   Server-side mailbox search. <paramref name="folder" /> is NULLABLE: null = search the
	///   whole mailbox, not one folder. Hits come newest first.
	/// </summary>
	/// <param name="folder">The folder to search, or <c>null</c> for the whole mailbox.</param>
	/// <param name="freeText">The free-text query.</param>
	/// <param name="since">Only messages delivered after this instant; <c>null</c> = no window.</param>
	/// <param name="maxResults">The most hits to return.</param>
	/// <param name="ct">Cancellation token for the backend round-trip.</param>
	/// <returns>The matching hits, newest first.</returns>
	Task<IReadOnlyList<SearchHit>> SearchAsync(
		FolderKey? folder, string freeText, DateTimeOffset? since, int maxResults, CancellationToken ct);

	/// <summary>Empties a folder (ItemOperations EmptyFolderContents).</summary>
	/// <param name="folder">The folder to empty.</param>
	/// <param name="ct">Cancellation token for the backend round-trip.</param>
	Task EmptyFolderAsync(FolderKey folder, CancellationToken ct);
}

/// <summary>Outbound mail submission (SMTP today; a JMAP backend may submit itself).</summary>
public interface IMailSubmitOperations
{
	/// <summary>Submits a raw RFC822 message for delivery.</summary>
	/// <param name="rfc822">The raw message bytes (ownership rule: a dedicated, never-mutated buffer).</param>
	/// <param name="ct">Cancellation token for the backend round-trip.</param>
	Task SendAsync(ReadOnlyMemory<byte> rfc822, CancellationToken ct);
}

/// <summary>
///   Contact-photo request accompanying a GAL search (MS-ASCMD Picture element):
///   photos over <see cref="MaxSizeBytes" /> report <see cref="GalPictureStatus.OverSizeLimit" />,
///   photos beyond <see cref="MaxCount" /> across the result set report
///   <see cref="GalPictureStatus.OverCountLimit" />.
/// </summary>
public sealed record GalPhotoRequest
{
	/// <summary>Largest photo the client accepts, in bytes; <c>null</c> means no size limit.</summary>
	public int? MaxSizeBytes { get; init; }

	/// <summary>How many photos the client accepts across the whole result set; <c>null</c> means no limit.</summary>
	public int? MaxCount { get; init; }
}

/// <summary>
///   Why a GAL entry does or does not carry a photo. The store enforces the request's limits (it
///   can stop fetching photo data once the count is spent, and skip one over the size limit —
///   counting granted photos across the whole result set); the HOST maps this status to the
///   MS-ASCMD wire statuses. A bare nullable photo could not carry the distinction between
///   "has none" and "over a limit", which the wire statuses need.
/// </summary>
public enum GalPictureStatus
{
	/// <summary>The contact has no photo.</summary>
	None,

	/// <summary>The photo is included in <see cref="GalPictureResult.Picture" />.</summary>
	Available,

	/// <summary>The contact has a photo, but it exceeds the request's size limit.</summary>
	OverSizeLimit,

	/// <summary>The contact has a photo, but the request's count limit was already spent.</summary>
	OverCountLimit
}

/// <summary>A GAL entry's photo outcome: the status, plus the photo exactly when available.</summary>
public sealed record GalPictureResult
{
	/// <summary>The photo outcome.</summary>
	public required GalPictureStatus Status { get; init; }

	/// <summary>The photo — set exactly when <see cref="Status" /> is <see cref="GalPictureStatus.Available" />.</summary>
	public GalPicture? Picture { get; init; }
}

/// <summary>One contact photo.</summary>
public sealed record GalPicture
{
	/// <summary>The photo bytes (ownership rule: a dedicated, never-mutated buffer).</summary>
	public required ReadOnlyMemory<byte> Data { get; init; }

	/// <summary>The photo's MIME content type.</summary>
	public required string ContentType { get; init; }
}

/// <summary>
///   One GAL (global address list) search result. GAL entries are not contacts — they are a flat
///   directory projection — so they get their own typed record rather than reusing
///   <see cref="ContactItem" />.
/// </summary>
public sealed record GalEntry
{
	/// <summary>The entry's display name.</summary>
	public required string DisplayName { get; init; }

	/// <summary>The entry's primary mail address.</summary>
	public string? EmailAddress { get; init; }

	/// <summary>The entry's first (given) name.</summary>
	public string? FirstName { get; init; }

	/// <summary>The entry's last (family) name.</summary>
	public string? LastName { get; init; }

	/// <summary>The entry's company.</summary>
	public string? Company { get; init; }

	/// <summary>The entry's job title.</summary>
	public string? Title { get; init; }

	/// <summary>The entry's office location.</summary>
	public string? Office { get; init; }

	/// <summary>The entry's work phone number.</summary>
	public string? Phone { get; init; }

	/// <summary>The entry's mobile phone number.</summary>
	public string? MobilePhone { get; init; }

	/// <summary>The entry's home phone number.</summary>
	public string? HomePhone { get; init; }

	/// <summary>The entry's alias (nickname).</summary>
	public string? Alias { get; init; }

	/// <summary>The entry's photo outcome; <c>null</c> when the client did not request pictures at all.</summary>
	public GalPictureResult? Picture { get; init; }
}

/// <summary>Contact-directory operations (GAL search for ResolveRecipients / Search).</summary>
public interface IDirectoryOperations
{
	/// <summary>
	///   Searches all address books; returns typed GAL entries. When <paramref name="photos" />
	///   is set, each entry carries a <see cref="GalEntry.Picture" /> outcome per the photo rules
	///   (the store enforces the limits; the host maps the statuses to the wire). A null
	///   <paramref name="photos" /> means the client did not request pictures — distinct from a
	///   request with no limits.
	/// </summary>
	/// <param name="query">The free-text query to match entries against.</param>
	/// <param name="maxResults">The most entries to return.</param>
	/// <param name="photos">The photo request, or <c>null</c> when pictures were not requested.</param>
	/// <param name="ct">Cancellation token for the backend round-trip.</param>
	/// <returns>The matching entries.</returns>
	Task<IReadOnlyList<GalEntry>> SearchGalAsync(
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
	///   which case the caller's stored token remains the one to restore. The token is
	///   legitimately opaque: the backend's own datum, stored and handed back verbatim.
	/// </summary>
	/// <param name="reply">The auto-reply to arm.</param>
	/// <param name="ct">Cancellation token for the backend round-trip.</param>
	/// <returns>The restore token to persist, or <c>null</c> to keep the stored one.</returns>
	Task<string?> EnableAsync(OofReply reply, CancellationToken ct);

	/// <summary>
	///   Disarms the auto-reply and restores the given token ("" = leave nothing active).
	///   Missing or stale tokens are tolerated.
	/// </summary>
	/// <param name="restoreToken">The token returned by <see cref="EnableAsync" />.</param>
	/// <param name="ct">Cancellation token for the backend round-trip.</param>
	Task DisableAsync(string restoreToken, CancellationToken ct);
}

/// <summary>A user's answer to a meeting request (MS-ASCMD MeetingResponse UserResponse).</summary>
public enum MeetingResponseKind
{
	/// <summary>The invitation is accepted.</summary>
	Accepted = 1,

	/// <summary>The invitation is tentatively accepted.</summary>
	Tentative = 2,

	/// <summary>The invitation is declined.</summary>
	Declined = 3
}

/// <summary>Meeting/scheduling operations of a calendar store.</summary>
public interface IMeetingOperations
{
	/// <summary>
	///   Responds to a meeting request: updates the attendee PARTSTAT on the stored event and sends
	///   an iTIP REPLY to the organizer. Returns the calendar item key holding the event, if any.
	///   The event UID stays a string: an iCalendar UID is domain data, not a backend key.
	/// </summary>
	/// <param name="calendar">The calendar folder holding the event.</param>
	/// <param name="eventUid">The event's iCalendar UID.</param>
	/// <param name="response">The user's answer.</param>
	/// <param name="ct">Cancellation token for the backend round-trip.</param>
	/// <returns>The calendar item holding the event, or <c>null</c> when none was found.</returns>
	Task<ItemKey?> RespondToMeetingAsync(
		FolderKey calendar, string eventUid, MeetingResponseKind response, CancellationToken ct);

	/// <summary>
	///   Whether the GATEWAY should mail iMIP invitations for organizer changes: the
	///   SendInvitations knob, with Auto probing the server for an RFC 6638 schedule
	///   outbox (server schedules itself → gateway stays silent to avoid double invites).
	/// </summary>
	/// <param name="ct">Cancellation token for the backend round-trip.</param>
	/// <returns><c>true</c> when the gateway should send invitation mail itself.</returns>
	Task<bool> ShouldSendInvitationsAsync(CancellationToken ct);
}

// IBackendSession / IBackendSessionFactory are the HOST's composite-session aggregation and
// its cache — nothing a plugin implements or receives (a plugin implements IBackendConnection and
// the store/side-op interfaces above). They live in ActiveSync.Core.Backend so the published
// plugin surface carries only what a plugin actually builds against.

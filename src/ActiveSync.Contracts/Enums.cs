// Copyright (c) 2026 Ruben Andersen
// SPDX-License-Identifier: MIT

namespace ActiveSync.Contracts;

/// <summary>
///   The kind of folder a store reports, as the FolderSync/FolderCreate Type (MS-ASCMD).
///   The wire values are pinned explicitly because they ARE the protocol's numbering — but a
///   plugin never sees the integer: it names the member and the host encodes it.
/// </summary>
public enum FolderType
{
	/// <summary>Generic user-created folder of no particular class.</summary>
	UserGeneric = 1,

	/// <summary>The mailbox's Inbox.</summary>
	Inbox = 2,

	/// <summary>The mailbox's Drafts folder — the ONE folder a client may Sync-add mail into.</summary>
	Drafts = 3,

	/// <summary>The mailbox's Deleted Items / Trash folder.</summary>
	DeletedItems = 4,

	/// <summary>The mailbox's Sent Items folder.</summary>
	SentItems = 5,

	/// <summary>The mailbox's Outbox.</summary>
	Outbox = 6,

	/// <summary>The default task collection.</summary>
	Tasks = 7,

	/// <summary>The default calendar collection.</summary>
	Calendar = 8,

	/// <summary>The default contact collection.</summary>
	Contacts = 9,

	/// <summary>The default notes collection.</summary>
	Notes = 10,

	/// <summary>The default journal collection (not implemented by this gateway).</summary>
	Journal = 11,

	/// <summary>A user-created mail folder.</summary>
	UserMail = 12,

	/// <summary>A user-created calendar collection.</summary>
	UserCalendar = 13,

	/// <summary>A user-created contact collection.</summary>
	UserContacts = 14,

	/// <summary>A user-created task collection.</summary>
	UserTasks = 15,

	/// <summary>A user-created journal collection.</summary>
	UserJournal = 16,

	/// <summary>A user-created notes collection.</summary>
	UserNotes = 17
}

/// <summary>
///   The shape of a body a client asked for (AirSyncBase BodyPreference Type). Values are the
///   protocol's, pinned so the host can encode them without a translation table.
/// </summary>
public enum BodyType
{
	/// <summary>Plain text.</summary>
	PlainText = 1,

	/// <summary>HTML.</summary>
	Html = 2,

	/// <summary>RTF (legacy; never produced by this gateway).</summary>
	Rtf = 3,

	/// <summary>The raw MIME message.</summary>
	Mime = 4
}

/// <summary>
///   How a free/busy period counts against availability. Deliberately UNPINNED and in an
///   arbitrary order: the MergedFreeBusy digit mapping ('0' free … '3' out-of-office, '4' no
///   data) is HOST-side, applied where the digit string is built, and the wire never sees this
///   enum. Do not "helpfully" pin or reorder these to the digits — that would re-import the wire
///   encoding the typed contract removes.
/// </summary>
public enum BusyKind
{
	/// <summary>Free. Included for completeness; never appears as a busy <em>period</em>.</summary>
	Free,

	/// <summary>Tentatively booked.</summary>
	Tentative,

	/// <summary>Busy.</summary>
	Busy,

	/// <summary>Out of office.</summary>
	OutOfOffice
}

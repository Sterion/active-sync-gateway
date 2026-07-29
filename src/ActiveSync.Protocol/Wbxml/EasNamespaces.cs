// Copyright (c) 2026 Ruben Andersen
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Xml.Linq;

namespace ActiveSync.Protocol.Wbxml;

/// <summary>
///   XML namespaces for the EAS code pages, named exactly as in MS-ASWBXML so the
///   decoded XML matches the plain-text examples in the MS-AS* specifications.
/// </summary>
public static class EasNamespaces
{
	/// <summary>Code page 0. Elements of the Sync command: collections, application data and sync state (SyncKey, Conflict, DeletesAsMoves, ...).</summary>
	public static readonly XNamespace AirSync = "AirSync";

	/// <summary>Code page 1. Fields of the Contact item type (Email1Address, FileAs, CompanyName, ...).</summary>
	public static readonly XNamespace Contacts = "Contacts";

	/// <summary>Code page 2. Fields of the Email item type (message headers and body-related elements).</summary>
	public static readonly XNamespace Email = "Email";

	/// <summary>Code page 3. Obsolete AirNotify code page; carries no tokens from protocol version 12.1 onward.</summary>
	public static readonly XNamespace AirNotify = "AirNotify";

	/// <summary>Code page 4. Fields of the Calendar item type (recurrence, organizer, attendees, ...).</summary>
	public static readonly XNamespace Calendar = "Calendar";

	/// <summary>Code page 5. Elements of the MoveItems command, used to move items between folders.</summary>
	public static readonly XNamespace Move = "Move";

	/// <summary>Code page 6. Elements of the GetItemEstimate command, used to estimate the number of items a Sync would return.</summary>
	public static readonly XNamespace GetItemEstimate = "GetItemEstimate";

	/// <summary>Code page 7. Elements of the FolderSync/FolderCreate/FolderUpdate/FolderDelete commands (folder hierarchy management).</summary>
	public static readonly XNamespace FolderHierarchy = "FolderHierarchy";

	/// <summary>Code page 8. Elements of the MeetingResponse command, used to accept, tentatively accept or decline a meeting request.</summary>
	public static readonly XNamespace MeetingResponse = "MeetingResponse";

	/// <summary>Code page 9. Fields of the Task item type.</summary>
	public static readonly XNamespace Tasks = "Tasks";

	/// <summary>Code page 10. Elements of the ResolveRecipients command, used to resolve free/busy and certificate data for recipients.</summary>
	public static readonly XNamespace ResolveRecipients = "ResolveRecipients";

	/// <summary>Code page 11. Elements of the ValidateCert command, used to validate an S/MIME certificate chain.</summary>
	public static readonly XNamespace ValidateCert = "ValidateCert";

	/// <summary>Code page 12. Extension fields for the Contact item type introduced after the original Contacts code page (page 1).</summary>
	public static readonly XNamespace Contacts2 = "Contacts2";

	/// <summary>Code page 13. Elements of the Ping command, used for push-notification long-polling.</summary>
	public static readonly XNamespace Ping = "Ping";

	/// <summary>Code page 14. Elements of the Provision command, including device security policy settings.</summary>
	public static readonly XNamespace Provision = "Provision";

	/// <summary>Code page 15. Elements of the Search command (mailbox and GAL search, plus the related Store request/response).</summary>
	public static readonly XNamespace Search = "Search";

	/// <summary>Code page 16. Global Address List entry fields returned by the Search and ResolveRecipients commands.</summary>
	public static readonly XNamespace Gal = "Gal";

	/// <summary>Code page 17. Elements shared across multiple commands: BodyPreference/Body, Attachments, and structured Location.</summary>
	public static readonly XNamespace AirSyncBase = "AirSyncBase";

	/// <summary>Code page 18. Elements of the Settings command (out-of-office, device information, user information, accounts).</summary>
	public static readonly XNamespace Settings = "Settings";

	/// <summary>Code page 19. Elements describing linked document library items (LinkId, IsFolder, ContentLength, ...).</summary>
	public static readonly XNamespace DocumentLibrary = "DocumentLibrary";

	/// <summary>Code page 20. Elements of the ItemOperations command (Fetch, Move, EmptyFolderContents, ...).</summary>
	public static readonly XNamespace ItemOperations = "ItemOperations";

	/// <summary>Code page 21. Elements of the SendMail/SmartForward/SmartReply commands used to compose and send mail.</summary>
	public static readonly XNamespace ComposeMail = "ComposeMail";

	/// <summary>Code page 22. Extension fields for the Email item type introduced after the original Email code page (page 2), including unified messaging (UM) fields.</summary>
	public static readonly XNamespace Email2 = "Email2";

	/// <summary>Code page 23. Fields of the Notes item type.</summary>
	public static readonly XNamespace Notes = "Notes";

	/// <summary>Code page 24. Information Rights Management (IRM) elements: templates, licenses and distribution rights.</summary>
	public static readonly XNamespace RightsManagement = "RightsManagement";

	/// <summary>Code page 25. Elements of the Find command (protocol version 16.1 and later), a lighter-weight alternative to Search.</summary>
	public static readonly XNamespace Find = "Find";

	/// <summary>Internal marker namespace used to flag OPAQUE-encoded element content (base64 in the XML).</summary>
	public static readonly XNamespace WbxmlInternal = "urn:activesync:wbxml";

	/// <summary>Attribute name (in the <see cref="WbxmlInternal"/> namespace) set on elements whose content is OPAQUE-encoded binary data represented as base64 text.</summary>
	public static readonly XName OpaqueAttribute = WbxmlInternal + "opaque";
}

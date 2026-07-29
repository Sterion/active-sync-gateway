// Copyright (c) 2026 Ruben Andersen
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

namespace ActiveSync.Protocol;

/// <summary>EAS content classes.</summary>
public static class EasClass
{
	/// <summary>The MS-ASCMD "Email" content class: mail folders and messages (FolderSync/Sync/ItemOperations Class element).</summary>
	public const string Email = "Email";

	/// <summary>The MS-ASCMD "Calendar" content class: calendar folders and events, including meeting invites/responses.</summary>
	public const string Calendar = "Calendar";

	/// <summary>The MS-ASCMD "Contacts" content class: contact folders and address book entries.</summary>
	public const string Contacts = "Contacts";

	/// <summary>The MS-ASCMD "Tasks" content class: task folders and to-do items.</summary>
	public const string Tasks = "Tasks";

	/// <summary>The MS-ASCMD "Notes" content class: notes folders and free-form note items (backed by this gateway's local-only <c>LocalNotesStore</c>).</summary>
	public const string Notes = "Notes";
}

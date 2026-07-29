// Copyright (c) 2026 Ruben Andersen
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

namespace ActiveSync.Protocol;

/// <summary>EAS folder types (MS-ASCMD FolderSync Type element).</summary>
public static class EasFolderType
{
	/// <summary>
	///   Wire value <c>1</c>: a user-created folder with no more specific content class
	///   (MS-ASCMD "User created generic folder"). Clients sync it but do not assume any
	///   particular item schema for it.
	/// </summary>
	public const int UserGeneric = 1;

	/// <summary>Wire value <c>2</c>: the account's default Inbox mail folder.</summary>
	public const int Inbox = 2;

	/// <summary>Wire value <c>3</c>: the account's default Drafts mail folder.</summary>
	public const int Drafts = 3;

	/// <summary>Wire value <c>4</c>: the account's default Deleted Items (trash) mail folder.</summary>
	public const int DeletedItems = 4;

	/// <summary>Wire value <c>5</c>: the account's default Sent Items mail folder.</summary>
	public const int SentItems = 5;

	/// <summary>Wire value <c>6</c>: the account's default Outbox mail folder.</summary>
	public const int Outbox = 6;

	/// <summary>Wire value <c>7</c>: the account's default Tasks folder.</summary>
	public const int Tasks = 7;

	/// <summary>Wire value <c>8</c>: the account's default Calendar folder.</summary>
	public const int Calendar = 8;

	/// <summary>Wire value <c>9</c>: the account's default Contacts folder.</summary>
	public const int Contacts = 9;

	/// <summary>Wire value <c>10</c>: the account's default Notes folder.</summary>
	public const int Notes = 10;

	/// <summary>Wire value <c>11</c>: the account's default Journal folder.</summary>
	public const int Journal = 11;

	/// <summary>
	///   Wire value <c>12</c>: a user-created (non-default) folder whose content class is
	///   Email — e.g. a secondary/shared mailbox folder that is not the Inbox/Drafts/Sent/
	///   Deleted/Outbox default.
	/// </summary>
	public const int UserMail = 12;

	/// <summary>Wire value <c>13</c>: a user-created (non-default) Calendar folder.</summary>
	public const int UserCalendar = 13;

	/// <summary>Wire value <c>14</c>: a user-created (non-default) Contacts folder.</summary>
	public const int UserContacts = 14;

	/// <summary>Wire value <c>15</c>: a user-created (non-default) Tasks folder.</summary>
	public const int UserTasks = 15;

	/// <summary>Wire value <c>16</c>: a user-created (non-default) Journal folder.</summary>
	public const int UserJournal = 16;

	/// <summary>Wire value <c>17</c>: a user-created (non-default) Notes folder.</summary>
	public const int UserNotes = 17;
}

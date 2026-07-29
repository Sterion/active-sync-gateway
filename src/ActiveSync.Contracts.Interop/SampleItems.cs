// Copyright (c) 2026 Ruben Andersen
// SPDX-License-Identifier: MIT

using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;

using MimeKit;

namespace ActiveSync.Contracts.Interop;

/// <summary>
///   Minimal, valid payloads for every content class — the answer to "payload records are awkward
///   to construct in a test". A plugin's own test suite (and the conformance kit's caller) needs
///   *some* item to create, update and read back; hand-rolling an iCalendar or an RFC822 blob in
///   each test is where fixtures go wrong.
/// </summary>
/// <remarks>
///   These are deliberately the smallest documents that satisfy their formats, not templates of
///   what a real backend produces. Contacts are emitted as vCard TEXT rather than through a
///   builder API, mirroring the split the gateway's own contact handling uses (write by hand, read
///   through the library) — the simple format is easier to get right than the object model.
/// </remarks>
public static class SampleItems
{
	private const string ProductId = "-//ActiveSync Gateway//Sample//EN";

	/// <summary>Builds a one-hour event.</summary>
	/// <param name="uid">The event's iCalendar UID.</param>
	/// <param name="summary">The event's summary (title).</param>
	/// <param name="start">When the event starts; stored as UTC.</param>
	/// <returns>The event payload.</returns>
	public static CalendarItem Event(string uid, string summary, DateTimeOffset start)
	{
		ArgumentException.ThrowIfNullOrEmpty(uid);

		CalendarEvent evt = new()
		{
			Uid = uid,
			Summary = summary,
			DtStamp = new CalDateTime(DateTime.UtcNow, "UTC"),
			Start = new CalDateTime(start.UtcDateTime, "UTC"),
			End = new CalDateTime(start.UtcDateTime.AddHours(1), "UTC")
		};

		Calendar calendar = new() { ProductId = ProductId };
		calendar.Events.Add(evt);
		return calendar.ToCalendarItem();
	}

	/// <summary>Builds a task.</summary>
	/// <param name="uid">The task's iCalendar UID.</param>
	/// <param name="summary">The task's summary (title).</param>
	/// <param name="due">When the task is due, or null for an undated task.</param>
	/// <returns>The task payload.</returns>
	public static TaskItem Task(string uid, string summary, DateTimeOffset? due = null)
	{
		ArgumentException.ThrowIfNullOrEmpty(uid);

		Todo todo = new()
		{
			Uid = uid,
			Summary = summary,
			DtStamp = new CalDateTime(DateTime.UtcNow, "UTC")
		};
		if (due is { } deadline)
			todo.Due = new CalDateTime(deadline.UtcDateTime, "UTC");

		Calendar calendar = new() { ProductId = ProductId };
		calendar.Todos.Add(todo);
		return calendar.ToTaskItem();
	}

	/// <summary>Builds a vCard 3.0 contact.</summary>
	/// <param name="uid">The contact's UID.</param>
	/// <param name="lastName">The contact's family name.</param>
	/// <param name="firstName">The contact's given name.</param>
	/// <param name="email">The contact's e-mail address, or null for none.</param>
	/// <returns>The contact payload.</returns>
	public static ContactItem Contact(string uid, string lastName, string firstName, string? email = null)
	{
		ArgumentException.ThrowIfNullOrEmpty(uid);

		string vcard =
			"BEGIN:VCARD\r\n" +
			"VERSION:3.0\r\n" +
			$"PRODID:{ProductId}\r\n" +
			$"UID:{Escape(uid)}\r\n" +
			$"N:{Escape(lastName)};{Escape(firstName)};;;\r\n" +
			$"FN:{Escape($"{firstName} {lastName}".Trim())}\r\n" +
			(email is { Length: > 0 } ? $"EMAIL;TYPE=INTERNET:{Escape(email)}\r\n" : "") +
			"END:VCARD\r\n";
		return new ContactItem { VCard = vcard };
	}

	/// <summary>Builds a plain-text note.</summary>
	/// <param name="subject">The note's subject.</param>
	/// <param name="body">The note's body text.</param>
	/// <returns>The note payload.</returns>
	public static NoteItem Note(string subject, string body) =>
		new()
		{
			Subject = subject,
			Body = new TextBody { Type = BodyType.PlainText, Content = body }
		};

	/// <summary>Builds a plain-text message.</summary>
	/// <param name="from">The sender's address.</param>
	/// <param name="to">The recipient's address.</param>
	/// <param name="subject">The message's subject.</param>
	/// <param name="body">The message's plain-text body.</param>
	/// <param name="flags">The message's flags; unread and unflagged when omitted.</param>
	/// <returns>The message payload.</returns>
	public static MailItem Mail(
		string from, string to, string subject, string body, MailFlags? flags = null)
	{
		MimeMessage message = new()
		{
			Subject = subject,
			Body = new TextPart("plain") { Text = body }
		};
		message.From.Add(MailboxAddress.Parse(from));
		message.To.Add(MailboxAddress.Parse(to));

		return message.ToMailItem(flags ?? new MailFlags());
	}

	/// <summary>
	///   RFC 6350 §3.4 text escaping for the hand-written vCard above: a comma, semicolon or
	///   backslash inside a value would otherwise read as structure.
	/// </summary>
	private static string Escape(string value) => value
		.Replace("\\", "\\\\", StringComparison.Ordinal)
		.Replace(";", "\\;", StringComparison.Ordinal)
		.Replace(",", "\\,", StringComparison.Ordinal)
		.Replace("\r\n", "\\n", StringComparison.Ordinal)
		.Replace("\n", "\\n", StringComparison.Ordinal);
}

// Copyright (c) 2026 Ruben Andersen
// SPDX-License-Identifier: MIT

namespace ActiveSync.Contracts.Conformance;

/// <summary>
///   The minimal valid documents the lifecycle checks create. Written as text rather than through
///   Ical.Net / FolkerKinzel.VCards on purpose: this package must reference nothing but
///   <c>ActiveSync.Contracts</c>, so running the kit never drags a domain library into a plugin
///   that does not want one. (<c>ActiveSync.Contracts.Interop</c> is where a plugin that DOES want
///   those libraries gets richer builders.)
/// </summary>
internal static class SamplePayloads
{
	private const string ProductId = "-//ActiveSync Gateway//Conformance//EN";

	/// <summary>A one-hour VEVENT carrying the run's uid.</summary>
	internal static string Event(string uid, string summary) =>
		"BEGIN:VCALENDAR\r\n" +
		"VERSION:2.0\r\n" +
		$"PRODID:{ProductId}\r\n" +
		"BEGIN:VEVENT\r\n" +
		$"UID:{uid}\r\n" +
		$"DTSTAMP:{Stamp(DateTimeOffset.UtcNow)}\r\n" +
		$"DTSTART:{Stamp(Anchor)}\r\n" +
		$"DTEND:{Stamp(Anchor.AddHours(1))}\r\n" +
		$"SUMMARY:{summary}\r\n" +
		"END:VEVENT\r\n" +
		"END:VCALENDAR\r\n";

	/// <summary>A VTODO carrying the run's uid.</summary>
	internal static string Task(string uid, string summary) =>
		"BEGIN:VCALENDAR\r\n" +
		"VERSION:2.0\r\n" +
		$"PRODID:{ProductId}\r\n" +
		"BEGIN:VTODO\r\n" +
		$"UID:{uid}\r\n" +
		$"DTSTAMP:{Stamp(DateTimeOffset.UtcNow)}\r\n" +
		$"SUMMARY:{summary}\r\n" +
		"END:VTODO\r\n" +
		"END:VCALENDAR\r\n";

	/// <summary>A vCard 3.0 carrying the run's uid.</summary>
	internal static string Contact(string uid, string displayName) =>
		"BEGIN:VCARD\r\n" +
		"VERSION:3.0\r\n" +
		$"PRODID:{ProductId}\r\n" +
		$"UID:{uid}\r\n" +
		$"N:{displayName};Conformance;;;\r\n" +
		$"FN:{displayName}\r\n" +
		"END:VCARD\r\n";

	/// <summary>A note whose body carries the run's uid (notes have no format to embed it in).</summary>
	internal static NoteItem Note(string uid, string subject) =>
		new()
		{
			Subject = subject,
			Body = new TextBody
			{
				Type = BodyType.PlainText,
				Content = $"Conformance run {uid}."
			}
		};

	/// <summary>
	///   Tomorrow, not a fixed date in the past. The checks always enumerate with
	///   <see cref="ContentFilter.All" />, but a store may apply a date window of its own, and an
	///   event just ahead of "now" is inside every such window.
	/// </summary>
	private static DateTimeOffset Anchor => DateTimeOffset.UtcNow.AddDays(1);

	private static string Stamp(DateTimeOffset instant) =>
		instant.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);
}

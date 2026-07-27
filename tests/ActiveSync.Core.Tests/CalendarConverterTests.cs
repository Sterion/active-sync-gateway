using System.Xml.Linq;
using ActiveSync.Backends.Common.Converters;
using ActiveSync.Contracts;
using ActiveSync.Protocol.Wbxml;

namespace ActiveSync.Core.Tests;

public class CalendarConverterTests
{
	private static readonly XNamespace Cal = EasNamespaces.Calendar;

	private static XElement AppData(params XElement[] elements)
	{
		return new XElement("ApplicationData", elements);
	}

	private static string CreateRecurringPrivateEvent(string uid)
	{
		return CalendarConverter.FromApplicationData(AppData(
			new XElement(Cal + "Subject", "Standup"),
			new XElement(Cal + "StartTime", "20260801T090000Z"),
			new XElement(Cal + "EndTime", "20260801T091500Z"),
			new XElement(Cal + "Sensitivity", "2"),
			new XElement(Cal + "BusyStatus", "0"),
			new XElement(Cal + "Reminder", "15"),
			new XElement(Cal + "Recurrence",
				new XElement(Cal + "Type", "1"),
				new XElement(Cal + "DayOfWeek", "62"))), uid, null);
	}

	[Fact]
	public void Update_OmittedElements_PreserveSensitivityBusyStatusRecurrenceAndReminder()
	{
		string uid = Guid.NewGuid().ToString();
		string existing = CreateRecurringPrivateEvent(uid);
		Assert.Contains("CLASS:PRIVATE", existing);
		Assert.Contains("TRANSP:TRANSPARENT", existing);
		Assert.Contains("RRULE", existing);
		Assert.Contains("ACTION:DISPLAY", existing);

		// A partial/ghosted Change carrying only a new subject must not flip the event to
		// PUBLIC/OPAQUE, strip its recurrence, or delete the reminder.
		string updated = CalendarConverter.FromApplicationData(AppData(
			new XElement(Cal + "Subject", "Standup (moved)")), uid, existing);

		Assert.Contains("SUMMARY:Standup (moved)", updated);
		Assert.Contains("CLASS:PRIVATE", updated);
		Assert.Contains("TRANSP:TRANSPARENT", updated);
		Assert.Contains("RRULE", updated);
		Assert.Contains("ACTION:DISPLAY", updated);
	}

	[Fact]
	public void ReminderUpdate_ReplacesOnlyTheDisplayAlarm()
	{
		string uid = Guid.NewGuid().ToString();
		string existing = CreateRecurringPrivateEvent(uid);
		// A custom EMAIL alarm EAS cannot express must survive a reminder change.
		existing = existing.Replace("END:VEVENT",
			"BEGIN:VALARM\r\nACTION:EMAIL\r\nDESCRIPTION:custom\r\nTRIGGER:-PT2H\r\nEND:VALARM\r\nEND:VEVENT");

		string updated = CalendarConverter.FromApplicationData(AppData(
			new XElement(Cal + "Subject", "Standup"),
			new XElement(Cal + "Reminder", "30")), uid, existing);

		Assert.Contains("ACTION:EMAIL", updated);
		Assert.Contains("ACTION:DISPLAY", updated);
		Assert.Contains("-PT30M", updated);
		Assert.DoesNotContain("-PT15M", updated); // the old display alarm was replaced, not kept
	}

	/// <summary>
	///   D5: DTSTART/DTEND;VALUE=DATE carry no time and no zone — the "date" IS the whole value,
	///   so reading it must never go through zone-conversion arithmetic. COVERAGE, not red-first
	///   proof: on the pinned Ical.Net 5.2.3, CalDateTime.AsUtc already special-cases
	///   HasTime=false internally and returns the correct unshifted midnight for every all-day
	///   encoding tried (bare VALUE=DATE, with/without an explicit TZID, with/without a
	///   surrounding VTIMEZONE, regardless of the process's local timezone) — this test passes
	///   on unmodified code too. The fix makes the nominal-date handling explicit (matching
	///   TasksConverter's established Nominal() pattern) instead of relying on that internal
	///   special-case remaining stable across a library upgrade, which is exactly the kind of
	///   silent, version-dependent breakage the finding warns about.
	/// </summary>
	[Fact]
	public void AllDayEvent_KeepsTheNominalDate_RegardlessOfLocalOrEventZone()
	{
		const string ics = """
		                    BEGIN:VCALENDAR
		                    VERSION:2.0
		                    BEGIN:VEVENT
		                    UID:allday-1
		                    DTSTART;VALUE=DATE:20260401
		                    DTEND;VALUE=DATE:20260403
		                    SUMMARY:All day
		                    END:VEVENT
		                    END:VCALENDAR
		                    """;

		List<XElement>? data = CalendarConverter.ToApplicationData(ics, BodyPreference.PlainText);

		Assert.NotNull(data);
		string? allDay = data!.FirstOrDefault(e => e.Name == Cal + "AllDayEvent")?.Value;
		string? start = data.FirstOrDefault(e => e.Name == Cal + "StartTime")?.Value;
		string? end = data.FirstOrDefault(e => e.Name == Cal + "EndTime")?.Value;

		Assert.Equal("1", allDay);
		Assert.Equal("20260401T000000Z", start);
		Assert.Equal("20260403T000000Z", end); // a genuine multi-day DTEND, not the +1h timed fallback
	}

	[Fact]
	public void AllDayEvent_MissingDtEnd_DefaultsToOneNominalDay_NotOneHour()
	{
		// D5 (adjacent): the DTEND-absent fallback used start + 1 HOUR unconditionally, which is
		// nonsensical for an all-day event (a timed default makes sense only for timed events).
		const string ics = """
		                    BEGIN:VCALENDAR
		                    VERSION:2.0
		                    BEGIN:VEVENT
		                    UID:allday-3
		                    DTSTART;VALUE=DATE:20260401
		                    SUMMARY:All day, no DTEND
		                    END:VEVENT
		                    END:VCALENDAR
		                    """;

		List<XElement>? data = CalendarConverter.ToApplicationData(ics, BodyPreference.PlainText);

		Assert.NotNull(data);
		string? end = data!.FirstOrDefault(e => e.Name == Cal + "EndTime")?.Value;
		Assert.Equal("20260402T000000Z", end);
	}

	// D3 — a client-created all-day event lands one day early for half the year, because
	// TimeZoneBlob.ReadBaseOffset returns only the STANDARD bias (Copenhagen +1h) while the
	// wire value for a DST-season all-day event is anchored on the actual CEST (+2h) local
	// midnight. Verified against a real MS-ASTZ blob for Europe/Copenhagen: on 2026-07-16
	// (daylight time), local midnight is 2026-07-15T22:00:00Z, and reading only the +1h base
	// offset rolls the .Date computation back to 2026-07-15.
	[Fact]
	public void AllDayEvent_CreatedDuringDst_LandsOnTheCorrectNominalDate()
	{
		string tzBlob = TimeZoneBlob.ToBase64(TimeZoneInfo.FindSystemTimeZoneById("Europe/Copenhagen"));

		string ics = CalendarConverter.FromApplicationData(AppData(
			new XElement(Cal + "Subject", "Summer trip"),
			new XElement(Cal + "AllDayEvent", "1"),
			new XElement(Cal + "StartTime", "20260715T220000Z"), // local midnight, CEST = UTC+2
			new XElement(Cal + "EndTime", "20260716T220000Z"),
			new XElement(Cal + "TimeZone", tzBlob)), "allday-dst", null);

		Assert.Contains("DTSTART;VALUE=DATE:20260716", ics);
		Assert.Contains("DTEND;VALUE=DATE:20260717", ics);
	}

	[Fact]
	public void SetPartStat_MatchesExactMailboxOnly()
	{
		const string ics = """
		                   BEGIN:VCALENDAR
		                   VERSION:2.0
		                   BEGIN:VEVENT
		                   UID:meet-1
		                   DTSTART:20260801T090000Z
		                   DTEND:20260801T100000Z
		                   SUMMARY:Planning
		                   ORGANIZER:mailto:boss@example.com
		                   ATTENDEE;PARTSTAT=NEEDS-ACTION:mailto:joann@example.com
		                   ATTENDEE;PARTSTAT=NEEDS-ACTION:mailto:ann@example.com
		                   END:VEVENT
		                   END:VCALENDAR
		                   """;

		// ann accepting must not touch joann (substring matching would hit both).
		string? updated = CalendarConverter.SetPartStat(ics, 1, "ann@example.com");
		Assert.NotNull(updated);
		Assert.Contains("PARTSTAT=NEEDS-ACTION", updated);
		Assert.Contains("PARTSTAT=ACCEPTED", updated);
		int accepted = updated.Split("PARTSTAT=ACCEPTED").Length - 1;
		Assert.Equal(1, accepted);

		// Case-insensitive + mailto:-prefixed identity still matches.
		Assert.NotNull(CalendarConverter.SetPartStat(ics, 2, "mailto:ANN@example.com"));

		// No attendee matches → null (no phantom update).
		Assert.Null(CalendarConverter.SetPartStat(ics, 1, "nobody@example.com"));
	}
}

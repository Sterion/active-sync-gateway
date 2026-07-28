using System.Xml.Linq;
using ActiveSync.Backends.Common.Converters;
using ActiveSync.Contracts;
using ActiveSync.Protocol.Wbxml;
using Ical.Net;
using Ical.Net.CalendarComponents;

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

	// D5 — AllDayEvent is the one calendar field read back without a presence guard: a partial
	// 16.x Change that carries StartTime/EndTime but omits AllDayEvent must keep the stored
	// event's all-day-ness (ghosting), not silently convert it to a timed event.
	[Fact]
	public void Change_OmittingAllDayEvent_PreservesStoredAllDayness()
	{
		const string existingAllDay = """
		                              BEGIN:VCALENDAR
		                              VERSION:2.0
		                              BEGIN:VEVENT
		                              UID:allday-ghost
		                              DTSTART;VALUE=DATE:20260716
		                              DTEND;VALUE=DATE:20260717
		                              SUMMARY:Summer trip
		                              END:VEVENT
		                              END:VCALENDAR
		                              """;

		// A partial Change moving the trip by a day, as a 16.x client would send it: new
		// StartTime/EndTime, no AllDayEvent element at all.
		string updated = CalendarConverter.FromApplicationData(AppData(
			new XElement(Cal + "StartTime", "20260716T220000Z"),
			new XElement(Cal + "EndTime", "20260717T220000Z")), "allday-ghost", existingAllDay);

		Assert.Contains("DTSTART;VALUE=DATE:", updated);
		Assert.DoesNotContain("DTSTART:", updated); // must not become a timed DATE-TIME value
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

	[Fact]
	public void ToApplicationData_IgnoresNonDisplayAlarms_ForReminder()
	{
		// D6 — the read picked the first alarm with ANY trigger duration, but the write only
		// ever touches DISPLAY alarms. An event whose only alarm is ACTION:EMAIL therefore
		// echoed that alarm's minutes as Reminder even though a client changing Reminder can
		// never alter it (the write leaves non-DISPLAY alarms untouched) — read and write
		// disagreeing on which alarm is EAS-managed is what makes the edit "not stick".
		const string ics = """
		                    BEGIN:VCALENDAR
		                    VERSION:2.0
		                    BEGIN:VEVENT
		                    UID:reminder-1
		                    DTSTART:20260801T090000Z
		                    DTEND:20260801T093000Z
		                    SUMMARY:Standup
		                    BEGIN:VALARM
		                    ACTION:EMAIL
		                    DESCRIPTION:custom
		                    TRIGGER:-PT15M
		                    END:VALARM
		                    END:VEVENT
		                    END:VCALENDAR
		                    """;

		List<XElement>? data = CalendarConverter.ToApplicationData(ics, BodyPreference.PlainText);

		Assert.NotNull(data);
		Assert.DoesNotContain(data!, e => e.Name == Cal + "Reminder");
	}

	[Fact]
	public void ToApplicationData_SkipsReminder_ForAlarmScheduledAfterStart()
	{
		// D6 (trigger sign) — a positive TRIGGER duration fires AFTER DTSTART, which the
		// EAS Reminder (a minutes-BEFORE value) cannot express. Math.Abs used to report it as
		// a minutes-before value instead of recognising it cannot be represented that way.
		const string ics = """
		                    BEGIN:VCALENDAR
		                    VERSION:2.0
		                    BEGIN:VEVENT
		                    UID:reminder-2
		                    DTSTART:20260801T090000Z
		                    DTEND:20260801T093000Z
		                    SUMMARY:Standup
		                    BEGIN:VALARM
		                    ACTION:DISPLAY
		                    DESCRIPTION:Reminder
		                    TRIGGER:PT15M
		                    END:VALARM
		                    END:VEVENT
		                    END:VCALENDAR
		                    """;

		List<XElement>? data = CalendarConverter.ToApplicationData(ics, BodyPreference.PlainText);

		Assert.NotNull(data);
		Assert.DoesNotContain(data!, e => e.Name == Cal + "Reminder");
	}

	[Fact]
	public void ToApplicationData_MeetingStatus_ReflectsWhetherTheActingUserIsTheOrganizer()
	{
		// D7 — MeetingStatus 1 means "meeting, and I am the organizer"; 3 means "meeting, and
		// I am not". The converter unconditionally emitted 1 whenever there were attendees, so
		// an invitee syncing someone ELSE's meeting was offered organizer actions
		// (cancel/edit attendees) instead of accept/tentative/decline.
		const string ics = """
		                    BEGIN:VCALENDAR
		                    VERSION:2.0
		                    BEGIN:VEVENT
		                    UID:meet-organizer
		                    DTSTART:20260801T090000Z
		                    DTEND:20260801T100000Z
		                    SUMMARY:Planning
		                    ORGANIZER:mailto:boss@example.com
		                    ATTENDEE;PARTSTAT=NEEDS-ACTION:mailto:invitee@example.com
		                    END:VEVENT
		                    END:VCALENDAR
		                    """;

		List<XElement>? data = CalendarConverter.ToApplicationData(ics, BodyPreference.PlainText, "invitee@example.com");

		Assert.NotNull(data);
		Assert.Equal("3", data!.FirstOrDefault(e => e.Name == Cal + "MeetingStatus")?.Value);
	}

	[Fact]
	public void ToApplicationData_MeetingStatus_IsOrganizer_WhenActingUserIsTheOrganizer()
	{
		// The inverse of the above: the organizer's own copy must still read MeetingStatus 1.
		const string ics = """
		                    BEGIN:VCALENDAR
		                    VERSION:2.0
		                    BEGIN:VEVENT
		                    UID:meet-organizer-2
		                    DTSTART:20260801T090000Z
		                    DTEND:20260801T100000Z
		                    SUMMARY:Planning
		                    ORGANIZER:mailto:boss@example.com
		                    ATTENDEE;PARTSTAT=NEEDS-ACTION:mailto:invitee@example.com
		                    END:VEVENT
		                    END:VCALENDAR
		                    """;

		List<XElement>? data = CalendarConverter.ToApplicationData(ics, BodyPreference.PlainText, "boss@example.com");

		Assert.NotNull(data);
		Assert.Equal("1", data!.FirstOrDefault(e => e.Name == Cal + "MeetingStatus")?.Value);
	}

	[Fact]
	public void BusyStatus_TentativeAndOutOfOffice_RoundTripInsteadOfCollapsingToBusy()
	{
		// D8 — TRANSP only expresses TRANSPARENT/OPAQUE (free/busy), so Tentative (1) and
		// Out-of-Office (3) both wrote OPAQUE and both read back as Busy (2), silently
		// discarding the user's actual choice. X-MICROSOFT-CDO-BUSYSTATUS is the property
		// Outlook and every major CalDAV server use to round-trip the extra states.
		string tentativeIcs = CalendarConverter.FromApplicationData(AppData(
			new XElement(Cal + "Subject", "Maybe"),
			new XElement(Cal + "StartTime", "20260801T090000Z"),
			new XElement(Cal + "EndTime", "20260801T100000Z"),
			new XElement(Cal + "BusyStatus", "1")), "busy-tentative", null);
		Assert.Contains("X-MICROSOFT-CDO-BUSYSTATUS:TENTATIVE", tentativeIcs);

		List<XElement>? tentativeData = CalendarConverter.ToApplicationData(tentativeIcs, BodyPreference.PlainText);
		Assert.Equal("1", tentativeData!.FirstOrDefault(e => e.Name == Cal + "BusyStatus")?.Value);

		string oofIcs = CalendarConverter.FromApplicationData(AppData(
			new XElement(Cal + "Subject", "Away"),
			new XElement(Cal + "StartTime", "20260801T090000Z"),
			new XElement(Cal + "EndTime", "20260801T100000Z"),
			new XElement(Cal + "BusyStatus", "3")), "busy-oof", null);
		Assert.Contains("X-MICROSOFT-CDO-BUSYSTATUS:OOF", oofIcs);

		List<XElement>? oofData = CalendarConverter.ToApplicationData(oofIcs, BodyPreference.PlainText);
		Assert.Equal("3", oofData!.FirstOrDefault(e => e.Name == Cal + "BusyStatus")?.Value);
	}

	[Fact]
	public void DeletedOccurrence_OnAllDayRecurringEvent_EmitsDateValuedExdate()
	{
		// D18 — an all-day DTSTART is DATE-valued (no time, no zone); RFC 5545 §3.8.5.1
		// requires EXDATE's value type to match DTSTART's. The occurrence-delete path
		// unconditionally wrote a UTC DATE-TIME EXDATE, which Ical.Net tolerates on its own
		// read-back but other CalDAV servers/clients are not required to.
		string uid = Guid.NewGuid().ToString();
		string existing = CalendarConverter.FromApplicationData(AppData(
			new XElement(Cal + "Subject", "Daily standup"),
			new XElement(Cal + "AllDayEvent", "1"),
			new XElement(Cal + "StartTime", "20260801T000000Z"),
			new XElement(Cal + "EndTime", "20260802T000000Z"),
			new XElement(Cal + "Recurrence",
				new XElement(Cal + "Type", "0"),
				new XElement(Cal + "Occurrences", "5"))), uid, null);
		Assert.Contains("DTSTART;VALUE=DATE:20260801", existing);

		string updated = CalendarConverter.FromApplicationData(AppData(
			new XElement(Cal + "Exceptions",
				new XElement(Cal + "Exception",
					new XElement(Cal + "Deleted", "1"),
					new XElement(Cal + "ExceptionStartTime", "20260803T000000Z")))), uid, existing);

		Assert.Contains("EXDATE;VALUE=DATE:20260803", updated);
		Assert.DoesNotContain("EXDATE:20260803T000000Z", updated);
	}

	[Fact]
	public void SetPartStat_UpdatesTheMaster_EvenWhenTheIcsListsAnOverrideFirst()
	{
		// D19 — every OTHER entry point in this file deliberately selects the master
		// (RecurrenceId is null) first; SetPartStat just took Events.FirstOrDefault(). For a
		// stored recurring meeting whose ICS happens to list a modified-occurrence override
		// before the master VEVENT, accepting the invitation updated the override's attendee
		// and left the master (what every future Sync round reads) at NEEDS-ACTION — the
		// user's acceptance is lost for the series.
		const string ics = """
		                    BEGIN:VCALENDAR
		                    VERSION:2.0
		                    BEGIN:VEVENT
		                    UID:series-1
		                    RECURRENCE-ID:20260805T090000Z
		                    DTSTART:20260805T100000Z
		                    DTEND:20260805T101500Z
		                    SUMMARY:Standup (moved)
		                    ATTENDEE;PARTSTAT=NEEDS-ACTION:mailto:user@example.com
		                    END:VEVENT
		                    BEGIN:VEVENT
		                    UID:series-1
		                    DTSTART:20260801T090000Z
		                    DTEND:20260801T091500Z
		                    RRULE:FREQ=DAILY;COUNT=10
		                    SUMMARY:Standup
		                    ATTENDEE;PARTSTAT=NEEDS-ACTION:mailto:user@example.com
		                    END:VEVENT
		                    END:VCALENDAR
		                    """;

		string? updated = CalendarConverter.SetPartStat(ics, 1, "user@example.com");

		Assert.NotNull(updated);
		Calendar? calendar = Calendar.Load(updated);
		CalendarEvent master = calendar!.Events.First(e => e.RecurrenceIdentifier is null);
		CalendarEvent overrideEvt = calendar.Events.First(e => e.RecurrenceIdentifier is not null);
		Assert.Equal("ACCEPTED", master.Attendees.First().ParticipationStatus);
		Assert.Equal("NEEDS-ACTION", overrideEvt.Attendees.First().ParticipationStatus);
	}

	[Fact]
	public void WeeklyRecurrence_WithNoExplicitByDay_AnchorsOnTheEventsLocalWeekday_NotTheUtcInstant()
	{
		// D21 — a weekly RRULE with no BYDAY defaults (per RFC 5545) to DTSTART's weekday IN
		// DTSTART'S OWN ZONE. The mapper anchored on the UTC instant instead: a 00:30
		// Copenhagen (CEST, UTC+2) standup on a Monday is 22:30Z the previous day (Sunday), so
		// the emitted DayOfWeek mask was Sunday's, not Monday's.
		const string tzBlock = """
		                        BEGIN:VTIMEZONE
		                        TZID:Europe/Copenhagen
		                        X-LIC-LOCATION:Europe/Copenhagen
		                        BEGIN:STANDARD
		                        DTSTART:19701025T030000
		                        TZNAME:CET
		                        TZOFFSETFROM:+0200
		                        TZOFFSETTO:+0100
		                        RRULE:FREQ=YEARLY;INTERVAL=1;BYDAY=-1SU;BYMONTH=10;WKST=SU
		                        END:STANDARD
		                        BEGIN:DAYLIGHT
		                        DTSTART:19700329T020000
		                        TZNAME:CEST
		                        TZOFFSETFROM:+0100
		                        TZOFFSETTO:+0200
		                        RRULE:FREQ=YEARLY;INTERVAL=1;BYDAY=-1SU;BYMONTH=3;WKST=SU
		                        END:DAYLIGHT
		                        END:VTIMEZONE
		                        """;
		string ics = $"""
		              BEGIN:VCALENDAR
		              VERSION:2.0
		              {tzBlock}
		              BEGIN:VEVENT
		              UID:weekly-local-anchor
		              DTSTART;TZID=Europe/Copenhagen:20260803T003000
		              DTEND;TZID=Europe/Copenhagen:20260803T010000
		              SUMMARY:Standup
		              RRULE:FREQ=WEEKLY
		              END:VEVENT
		              END:VCALENDAR
		              """;

		List<XElement>? data = CalendarConverter.ToApplicationData(ics, BodyPreference.PlainText);

		Assert.NotNull(data);
		XElement? recurrence = data!.FirstOrDefault(e => e.Name == Cal + "Recurrence");
		Assert.NotNull(recurrence);
		string? dayOfWeek = recurrence!.Element(Cal + "DayOfWeek")?.Value;
		// 2026-08-03 is a Monday: mask bit 2 (1 << 1). Sunday (the UTC-instant weekday) is bit 1.
		Assert.Equal("2", dayOfWeek);
	}
}

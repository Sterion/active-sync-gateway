using ActiveSync.Backends.Converters;

namespace ActiveSync.Core.Tests;

/// <summary>
///   D33 COVERAGE (not red-first proof): the finding is that a null (unresolvable floating)
///   occurrence start makes the window TakeWhile predicate false and silently ends enumeration,
///   dropping every later occurrence. The specific null trigger cannot be produced
///   deterministically here — Ical.Net resolves floating times via the system zone, so a plain
///   floating recurring event never yields a null start. This test instead guards that a
///   full window of occurrences is enumerated (no early termination) and is struck on the
///   strength of the null-safe fix, not this test.
/// </summary>
public class BusyPeriodsFloatingTests
{
	[Fact]
	public void Floating_WeeklyEvent_AllInWindowOccurrencesReturned()
	{
		string ics =
			"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//t//EN\r\n" +
			"BEGIN:VEVENT\r\nUID:f-1\r\n" +
			"DTSTART:20260106T090000\r\nDTEND:20260106T100000\r\n" +
			"RRULE:FREQ=WEEKLY;BYDAY=TU\r\nSUMMARY:Floating\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

		DateTime start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
		DateTime end = new(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);

		var periods = CalendarConverter.BusyPeriodsFromEvents([ics], start, end);

		// Four Tuesdays in Jan 2026 (6, 13, 20, 27) — none dropped by early termination.
		Assert.Equal(4, periods.Count);
	}
}

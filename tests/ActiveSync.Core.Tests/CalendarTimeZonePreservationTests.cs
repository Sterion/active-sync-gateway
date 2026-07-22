using System.Xml.Linq;
using ActiveSync.Backends.Converters;
using ActiveSync.Protocol.Wbxml;

namespace ActiveSync.Core.Tests;

/// <summary>
///   D12: a timed event stored with a real (non-UTC) TZID must keep that zone when a client
///   Change comes back, so recurrences honour DST instead of being re-anchored to a fixed UTC
///   offset (a weekly 09:00 Copenhagen meeting must not drift an hour across a DST boundary).
/// </summary>
public class CalendarTimeZonePreservationTests
{
	private static readonly XNamespace Cal = EasNamespaces.Calendar;

	private static XElement AppData(params XElement[] elements)
	{
		return new XElement("ApplicationData", elements);
	}

	private const string CopenhagenWeekly =
		"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//EN\r\n" +
		"BEGIN:VEVENT\r\nUID:tz-1\r\n" +
		"DTSTART;TZID=Europe/Copenhagen:20260601T090000\r\n" +
		"DTEND;TZID=Europe/Copenhagen:20260601T093000\r\n" +
		"RRULE:FREQ=WEEKLY;BYDAY=MO\r\nSUMMARY:Standup\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

	[Fact]
	public void Update_PreservesStoredNonUtcTimezone()
	{
		// The client edits the event; EAS carries StartTime/EndTime in UTC (07:00Z = 09:00 CEST).
		string updated = CalendarConverter.FromApplicationData(AppData(
			new XElement(Cal + "Subject", "Standup v2"),
			new XElement(Cal + "StartTime", "20260601T070000Z"),
			new XElement(Cal + "EndTime", "20260601T073000Z")), "tz-1", CopenhagenWeekly);

		Assert.Contains("TZID=Europe/Copenhagen", updated);
		Assert.Contains("T090000", updated); // wall-clock preserved, not re-anchored to 07:00Z
		Assert.DoesNotContain("DTSTART:20260601T070000Z", updated);
	}

	[Fact]
	public void NewEvent_WithoutStoredZone_StaysUtc()
	{
		// A fresh event has no stored zone to recover — documented UTC limitation.
		string ics = CalendarConverter.FromApplicationData(AppData(
			new XElement(Cal + "Subject", "One-off"),
			new XElement(Cal + "StartTime", "20260601T070000Z"),
			new XElement(Cal + "EndTime", "20260601T073000Z")), "tz-2", null);

		Assert.DoesNotContain("TZID=Europe/Copenhagen", ics);
		Assert.Contains("070000Z", ics);
	}
}

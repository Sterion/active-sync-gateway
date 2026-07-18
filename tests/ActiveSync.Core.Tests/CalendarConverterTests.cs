using System.Xml.Linq;
using ActiveSync.Backends.Converters;
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

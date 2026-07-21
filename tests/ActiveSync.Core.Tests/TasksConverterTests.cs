using System.Xml.Linq;
using ActiveSync.Backends.Converters;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Protocol.Wbxml;

namespace ActiveSync.Core.Tests;

public class TasksConverterTests
{
	/// <summary>Captured verbatim from an Axigen server (test account probe).</summary>
	private const string AxigenTask = """
	                                  BEGIN:VCALENDAR
	                                  CALSCALE:GREGORIAN
	                                  PRODID:AXIGEN
	                                  VERSION:2.0
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
	                                  BEGIN:VTODO
	                                  PERCENT-COMPLETE:50
	                                  PRIORITY:5
	                                  STATUS:IN-PROCESS
	                                  SUMMARY:Test Task
	                                  DUE;TZID="Europe/Copenhagen":20260717T000000
	                                  ORGANIZER;CN="Test Man":mailto:test@example.com
	                                  UID:415849554944-006A56E48B-F5B1F6C007DBFEF8
	                                  DTSTAMP:20260715T013819Z
	                                  LAST-MODIFIED:20260715T013828Z
	                                  SEQUENCE:0
	                                  END:VTODO
	                                  END:VCALENDAR
	                                  """;

	private static readonly XNamespace Tasks = EasNamespaces.Tasks;
	private static readonly XNamespace AirSyncBase = EasNamespaces.AirSyncBase;

	private static XElement AppData(params XElement[] elements)
	{
		return new XElement("ApplicationData", elements);
	}

	[Fact]
	public void AxigenSample_ParsesAsIncompleteNormalPriorityTask()
	{
		Assert.Equal("415849554944-006A56E48B-F5B1F6C007DBFEF8", TasksConverter.ExtractUid(AxigenTask));

		List<XElement>? data = TasksConverter.ToApplicationData(AxigenTask, new BodyPreference(1, null, false));
		Assert.NotNull(data);
		Assert.Equal("Test Task", data.Single(e => e.Name == Tasks + "Subject").Value);
		Assert.Equal("0", data.Single(e => e.Name == Tasks + "Complete").Value); // IN-PROCESS/50%
		Assert.Equal("1", data.Single(e => e.Name == Tasks + "Importance").Value); // PRIORITY:5
		// DUE 2026-07-17T00:00 Europe/Copenhagen (CEST, UTC+2): the instant is
		// 2026-07-16T22:00Z, but the nominal DueDate (what clients display) must keep the
		// wall-clock Friday — otherwise phones show Thursday.
		Assert.Equal("2026-07-16T22:00:00.000Z", data.Single(e => e.Name == Tasks + "UtcDueDate").Value);
		Assert.Equal("2026-07-17T00:00:00.000Z", data.Single(e => e.Name == Tasks + "DueDate").Value);
		Assert.DoesNotContain(data, e => e.Name == Tasks + "DateCompleted");
	}

	[Fact]
	public void RoundTrip_PreservesCoreFields_AndDueDateStaysOnThePickedDay()
	{
		string uid = Guid.NewGuid().ToString();
		// Nominal wall-clock Friday midnight (what a phone in UTC+2 sends as DueDate)
		// alongside the earlier UTC instant — the picked DATE must survive.
		string ics = TasksConverter.FromApplicationData(AppData(
			new XElement(Tasks + "Subject", "Buy milk"),
			new XElement(AirSyncBase + "Body",
				new XElement(AirSyncBase + "Type", "1"),
				new XElement(AirSyncBase + "Data", "two liters")),
			new XElement(Tasks + "Importance", "2"),
			new XElement(Tasks + "Complete", "0"),
			new XElement(Tasks + "DueDate", "2026-08-01T00:00:00.000Z"),
			new XElement(Tasks + "UtcDueDate", "2026-07-31T22:00:00.000Z"),
			new XElement(Tasks + "Categories",
				new XElement(Tasks + "Category", "errands"))), uid, null);

		Assert.Contains("VTODO", ics);
		Assert.Contains("DUE;VALUE=DATE:20260801", ics); // date-only, no timezone drift
		Assert.Equal(uid, TasksConverter.ExtractUid(ics));

		List<XElement>? data = TasksConverter.ToApplicationData(ics, new BodyPreference(1, null, false))!;
		Assert.Equal("Buy milk", data.Single(e => e.Name == Tasks + "Subject").Value);
		Assert.Equal("two liters",
			data.Single(e => e.Name == AirSyncBase + "Body").Element(AirSyncBase + "Data")?.Value);
		Assert.Equal("2", data.Single(e => e.Name == Tasks + "Importance").Value);
		Assert.Equal("0", data.Single(e => e.Name == Tasks + "Complete").Value);
		Assert.StartsWith("2026-08-01T00:00:00",
			data.Single(e => e.Name == Tasks + "DueDate").Value);
		Assert.Equal("errands", data.Single(e => e.Name == Tasks + "Categories")
			.Element(Tasks + "Category")?.Value);
	}

	[Fact]
	public void Complete_SetsCompletedStatus_AndUncompletePreservesNothingStale()
	{
		string uid = Guid.NewGuid().ToString();
		string open = TasksConverter.FromApplicationData(AppData(
			new XElement(Tasks + "Subject", "t"),
			new XElement(Tasks + "Complete", "0")), uid, null);

		string done = TasksConverter.FromApplicationData(AppData(
			new XElement(Tasks + "Subject", "t"),
			new XElement(Tasks + "Complete", "1"),
			new XElement(Tasks + "DateCompleted", "2026-07-20T10:00:00.000Z")), uid, open);
		Assert.Contains("STATUS:COMPLETED", done);
		List<XElement>? doneData = TasksConverter.ToApplicationData(done, new BodyPreference(1, null, false))!;
		Assert.Equal("1", doneData.Single(e => e.Name == Tasks + "Complete").Value);
		Assert.Equal("2026-07-20T10:00:00.000Z",
			doneData.Single(e => e.Name == Tasks + "DateCompleted").Value);

		string reopened = TasksConverter.FromApplicationData(AppData(
			new XElement(Tasks + "Subject", "t"),
			new XElement(Tasks + "Complete", "0")), uid, done);
		List<XElement>? reopenedData = TasksConverter.ToApplicationData(reopened, new BodyPreference(1, null, false))!;
		Assert.Equal("0", reopenedData.Single(e => e.Name == Tasks + "Complete").Value);
	}

	[Fact]
	public void Update_OmittedElements_LeaveCompletionDatesAndReminderUntouched()
	{
		string uid = Guid.NewGuid().ToString();
		string existing = TasksConverter.FromApplicationData(AppData(
			new XElement(Tasks + "Subject", "t"),
			new XElement(Tasks + "Complete", "1"),
			new XElement(Tasks + "DateCompleted", "2026-07-20T10:00:00.000Z"),
			new XElement(Tasks + "DueDate", "2026-08-01T00:00:00.000Z"),
			new XElement(Tasks + "ReminderSet", "1"),
			new XElement(Tasks + "ReminderTime", "2026-07-31T09:00:00.000Z")), uid, null);

		// A partial/ghosted Change carrying only a new subject must not reopen the task,
		// null the due date, or delete the reminder.
		string updated = TasksConverter.FromApplicationData(AppData(
			new XElement(Tasks + "Subject", "t renamed")), uid, existing);

		Assert.Contains("ACTION:DISPLAY", updated); // the reminder VALARM survived
		List<XElement> data = TasksConverter.ToApplicationData(updated, new BodyPreference(1, null, false))!;
		Assert.Equal("1", data.Single(e => e.Name == Tasks + "Complete").Value);
		Assert.StartsWith("2026-08-01", data.Single(e => e.Name == Tasks + "DueDate").Value);
		Assert.Equal("t renamed", data.Single(e => e.Name == Tasks + "Subject").Value);
	}

	[Fact]
	public void ReminderSetZero_RemovesOnlyTheDisplayAlarm()
	{
		string uid = Guid.NewGuid().ToString();
		string existing = TasksConverter.FromApplicationData(AppData(
			new XElement(Tasks + "Subject", "t"),
			new XElement(Tasks + "ReminderSet", "1"),
			new XElement(Tasks + "ReminderTime", "2026-07-31T09:00:00.000Z")), uid, null);
		// A custom EMAIL alarm EAS cannot express must survive the explicit reminder removal.
		existing = existing.Replace("END:VTODO",
			"BEGIN:VALARM\r\nACTION:EMAIL\r\nDESCRIPTION:custom\r\nTRIGGER:-PT30M\r\nEND:VALARM\r\nEND:VTODO");

		string updated = TasksConverter.FromApplicationData(AppData(
			new XElement(Tasks + "Subject", "t"),
			new XElement(Tasks + "ReminderSet", "0")), uid, existing);

		Assert.DoesNotContain("ACTION:DISPLAY", updated);
		Assert.Contains("ACTION:EMAIL", updated);
		List<XElement> data = TasksConverter.ToApplicationData(updated, new BodyPreference(1, null, false))!;
		Assert.DoesNotContain(data, e => e.Name == Tasks + "ReminderSet" && e.Value == "1");
	}

	[Fact]
	public void Reminder_RoundTrips_AsAbsoluteTrigger()
	{
		string uid = Guid.NewGuid().ToString();
		string ics = TasksConverter.FromApplicationData(AppData(
			new XElement(Tasks + "Subject", "t"),
			new XElement(Tasks + "ReminderSet", "1"),
			new XElement(Tasks + "ReminderTime", "2026-07-31T09:00:00.000Z")), uid, null);

		// Regression: without VALUE=DATE-TIME the absolute trigger serializes as an empty
		// "TRIGGER:" line, and the reminder silently vanishes on the next sync back.
		Assert.Contains("TRIGGER;VALUE=DATE-TIME:20260731T090000Z", ics);

		List<XElement> data = TasksConverter.ToApplicationData(ics, new BodyPreference(1, null, false))!;
		Assert.Equal("1", data.Single(e => e.Name == Tasks + "ReminderSet").Value);
		Assert.Equal("2026-07-31T09:00:00.000Z", data.Single(e => e.Name == Tasks + "ReminderTime").Value);
	}

	[Fact]
	public void Update_PreservesBackendProgress_WhenClientLeavesTaskIncomplete()
	{
		// The Axigen task is IN-PROCESS at 50% — an EAS change that keeps Complete=0 must
		// not stomp that progress (EAS cannot express percentages).
		string updated = TasksConverter.FromApplicationData(AppData(
				new XElement(Tasks + "Subject", "Test Task renamed"),
				new XElement(Tasks + "Complete", "0"),
				new XElement(Tasks + "UtcDueDate", "2026-07-16T22:00:00.000Z")),
			"415849554944-006A56E48B-F5B1F6C007DBFEF8", AxigenTask);

		Assert.Contains("PERCENT-COMPLETE:50", updated);
		Assert.Contains("STATUS:IN-PROCESS", updated);
		Assert.Contains("SUMMARY:Test Task renamed", updated);
	}

	// ---------- recurrence ----------

	[Theory]
	[InlineData(0, null, null, null, null, "FREQ=DAILY")] // daily
	[InlineData(1, 20, null, null, null, "FREQ=WEEKLY")] // weekly Tue+Thu (mask 4|16)
	[InlineData(2, null, 15, null, null, "FREQ=MONTHLY")] // monthly on the 15th
	[InlineData(3, 8, null, 2, null, "FREQ=MONTHLY")] // 2nd Wednesday
	[InlineData(5, null, 24, null, 12, "FREQ=YEARLY")] // every Dec 24th
	[InlineData(6, 1, null, 5, 10, "FREQ=YEARLY")] // last Sunday of October
	public void Recurrence_RoundTrips_PerType(
		int type, int? dayOfWeek, int? dayOfMonth, int? weekOfMonth, int? monthOfYear, string rruleFragment)
	{
		XElement recurrence = new(Tasks + "Recurrence",
			new XElement(Tasks + "Type", type.ToString()),
			new XElement(Tasks + "Start", "2026-08-03T00:00:00.000Z"));
		if (dayOfWeek is { } dow)
			recurrence.Add(new XElement(Tasks + "DayOfWeek", dow.ToString()));
		if (dayOfMonth is { } dom)
			recurrence.Add(new XElement(Tasks + "DayOfMonth", dom.ToString()));
		if (weekOfMonth is { } wom)
			recurrence.Add(new XElement(Tasks + "WeekOfMonth", wom.ToString()));
		if (monthOfYear is { } moy)
			recurrence.Add(new XElement(Tasks + "MonthOfYear", moy.ToString()));

		string uid = Guid.NewGuid().ToString();
		string ics = TasksConverter.FromApplicationData(AppData(
			new XElement(Tasks + "Subject", "recurring"),
			new XElement(Tasks + "StartDate", "2026-08-03T00:00:00.000Z"),
			recurrence), uid, null);
		Assert.Contains(rruleFragment, ics);

		List<XElement> data = TasksConverter.ToApplicationData(ics, new BodyPreference(1, null, false))!;
		XElement emitted = data.Single(e => e.Name == Tasks + "Recurrence");
		Assert.Equal(type.ToString(), emitted.Element(Tasks + "Type")?.Value);
		Assert.NotNull(emitted.Element(Tasks + "Start")); // required by MS-ASTASK
		if (dayOfWeek is { } expectedDow)
			Assert.Equal(expectedDow.ToString(), emitted.Element(Tasks + "DayOfWeek")?.Value);
		if (dayOfMonth is { } expectedDom)
			Assert.Equal(expectedDom.ToString(), emitted.Element(Tasks + "DayOfMonth")?.Value);
		if (weekOfMonth is { } expectedWom)
			Assert.Equal(expectedWom.ToString(), emitted.Element(Tasks + "WeekOfMonth")?.Value);
		if (monthOfYear is { } expectedMoy)
			Assert.Equal(expectedMoy.ToString(), emitted.Element(Tasks + "MonthOfYear")?.Value);
	}

	[Fact]
	public void Recurrence_IntervalOccurrencesAndUntil_RoundTrip()
	{
		string uid = Guid.NewGuid().ToString();
		string counted = TasksConverter.FromApplicationData(AppData(
			new XElement(Tasks + "Subject", "counted"),
			new XElement(Tasks + "StartDate", "2026-08-03T00:00:00.000Z"),
			new XElement(Tasks + "Recurrence",
				new XElement(Tasks + "Type", "0"),
				new XElement(Tasks + "Start", "2026-08-03T00:00:00.000Z"),
				new XElement(Tasks + "Interval", "2"),
				new XElement(Tasks + "Occurrences", "5"))), uid, null);
		Assert.Contains("INTERVAL=2", counted);
		Assert.Contains("COUNT=5", counted);
		List<XElement> countedData = TasksConverter.ToApplicationData(counted, new BodyPreference(1, null, false))!;
		XElement countedRecurrence = countedData.Single(e => e.Name == Tasks + "Recurrence");
		Assert.Equal("2", countedRecurrence.Element(Tasks + "Interval")?.Value);
		Assert.Equal("5", countedRecurrence.Element(Tasks + "Occurrences")?.Value);

		string bounded = TasksConverter.FromApplicationData(AppData(
			new XElement(Tasks + "Subject", "bounded"),
			new XElement(Tasks + "StartDate", "2026-08-03T00:00:00.000Z"),
			new XElement(Tasks + "Recurrence",
				new XElement(Tasks + "Type", "0"),
				new XElement(Tasks + "Start", "2026-08-03T00:00:00.000Z"),
				new XElement(Tasks + "Until", "2026-12-31T00:00:00.000Z"))), uid, null);
		Assert.Contains("UNTIL=20261231", bounded);
		List<XElement> boundedData = TasksConverter.ToApplicationData(bounded, new BodyPreference(1, null, false))!;
		// Tasks dates use the long form, like every other date field in this class.
		Assert.Equal("2026-12-31T00:00:00.000Z",
			boundedData.Single(e => e.Name == Tasks + "Recurrence").Element(Tasks + "Until")?.Value);
	}

	[Fact]
	public void Recurrence_Regenerate_IsSkippedEntirely()
	{
		// "N days after completion" has no RRULE equivalent — a fixed RRULE would fire
		// wrong occurrences, so the element must be ignored, not approximated.
		string ics = TasksConverter.FromApplicationData(AppData(
			new XElement(Tasks + "Subject", "regenerating"),
			new XElement(Tasks + "Recurrence",
				new XElement(Tasks + "Type", "0"),
				new XElement(Tasks + "Start", "2026-08-03T00:00:00.000Z"),
				new XElement(Tasks + "Regenerate", "1"),
				new XElement(Tasks + "Interval", "3"))), Guid.NewGuid().ToString(), null);

		Assert.DoesNotContain("RRULE", ics);
	}

	[Fact]
	public void Update_OmittedRecurrence_PreservesTheStoredRule()
	{
		string uid = Guid.NewGuid().ToString();
		string recurring = TasksConverter.FromApplicationData(AppData(
			new XElement(Tasks + "Subject", "weekly"),
			new XElement(Tasks + "StartDate", "2026-08-03T00:00:00.000Z"),
			new XElement(Tasks + "Recurrence",
				new XElement(Tasks + "Type", "1"),
				new XElement(Tasks + "Start", "2026-08-03T00:00:00.000Z"),
				new XElement(Tasks + "DayOfWeek", "2"))), uid, null);
		Assert.Contains("FREQ=WEEKLY", recurring);

		// A partial/ghosted Change without the Recurrence element must not strip the rule.
		string renamed = TasksConverter.FromApplicationData(AppData(
			new XElement(Tasks + "Subject", "weekly renamed")), uid, recurring);
		Assert.Contains("FREQ=WEEKLY", renamed);
	}

	[Fact]
	public void Recurrence_OnDatelessTask_AnchorsDtStartFromRecurrenceStart()
	{
		string ics = TasksConverter.FromApplicationData(AppData(
			new XElement(Tasks + "Subject", "dateless"),
			new XElement(Tasks + "Recurrence",
				new XElement(Tasks + "Type", "0"),
				new XElement(Tasks + "Start", "2026-08-03T00:00:00.000Z"))), Guid.NewGuid().ToString(), null);

		Assert.Contains("RRULE", ics);
		Assert.Contains("DTSTART;VALUE=DATE:20260803", ics); // RRULE without an anchor is ill-defined
	}
}

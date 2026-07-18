using System.Xml.Linq;
using ActiveSync.Backends.Converters;
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
}

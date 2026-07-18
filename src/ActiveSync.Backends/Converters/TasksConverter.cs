using System.Xml.Linq;
using ActiveSync.Core.Backend;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;

namespace ActiveSync.Backends.Converters;

/// <summary>
///   iCalendar VTODO ↔ EAS Tasks-class ApplicationData (MS-ASTASK). Recurring tasks are not
///   mapped yet — the Recurrence element is neither emitted nor honored.
/// </summary>
public static class TasksConverter
{
	private static readonly XNamespace Tasks = EasNamespaces.Tasks;
	private static readonly XNamespace AirSyncBase = EasNamespaces.AirSyncBase;

	public static string? ExtractUid(string ics)
	{
		return Calendar.Load(ics)?.Todos.FirstOrDefault()?.Uid;
	}

	public static List<XElement>? ToApplicationData(string ics, BodyPreference bodyPreference)
	{
		Todo? todo = Calendar.Load(ics)?.Todos.FirstOrDefault();
		if (todo is null)
			return null;

		List<XElement> data = new()
		{
			new XElement(Tasks + "Subject", todo.Summary ?? ""),
			new XElement(Tasks + "Importance", todo.Priority switch
			{
				>= 1 and <= 4 => "2", // iCal high priorities
				>= 6 => "0", // iCal low priorities
				_ => "1" // 0 (undefined) or 5 (normal)
			})
		};

		(string sent, bool truncated, long estimated) =
			BodyText.ForBody(todo.Description ?? "", bodyPreference.TruncationSize);
		data.Add(AirSyncBodyWriter.Build(estimated, truncated, sent));

		bool complete = string.Equals(todo.Status, "COMPLETED", StringComparison.OrdinalIgnoreCase)
		                || todo.PercentComplete >= 100;
		data.Add(new XElement(Tasks + "Complete", complete ? "1" : "0"));
		if (complete)
			data.Add(new XElement(Tasks + "DateCompleted",
				EasDateTime.ToLong(todo.Completed?.AsUtc ?? DateTime.UtcNow)));

		// MS-ASTASK quirk: clients render the DATE PART of StartDate/DueDate verbatim
		// (nominal wall-clock, no timezone conversion) while the Utc* variants carry the
		// instant. A zoned "Friday 00:00 Copenhagen" due must therefore emit
		// DueDate=Friday-00:00 (wall clock) + UtcDueDate=Thursday-22:00Z — sending the
		// instant in both makes phones show Thursday.
		if (todo.DtStart is { } start)
		{
			data.Add(new XElement(Tasks + "UtcStartDate", EasDateTime.ToLong(start.AsUtc)));
			data.Add(new XElement(Tasks + "StartDate", EasDateTime.ToLong(Nominal(start))));
		}

		if (todo.Due is { } due)
		{
			data.Add(new XElement(Tasks + "UtcDueDate", EasDateTime.ToLong(due.AsUtc)));
			data.Add(new XElement(Tasks + "DueDate", EasDateTime.ToLong(Nominal(due))));
		}

		data.Add(new XElement(Tasks + "Sensitivity", todo.Class?.ToUpperInvariant() switch
		{
			"PRIVATE" => "2",
			"CONFIDENTIAL" => "3",
			_ => "0"
		}));

		Alarm? reminder = todo.Alarms?.FirstOrDefault(a => a.Trigger?.DateTime is not null);
		if (reminder?.Trigger?.DateTime is { } reminderTime)
		{
			data.Add(new XElement(Tasks + "ReminderSet", "1"));
			data.Add(new XElement(Tasks + "ReminderTime", EasDateTime.ToLong(reminderTime.AsUtc)));
		}

		if (todo.Categories is { Count: > 0 } categories)
			data.Add(new XElement(Tasks + "Categories",
				categories.Select(c => new XElement(Tasks + "Category", c))));

		return data;
	}

	public static string FromApplicationData(XElement applicationData, string uid, string? existingIcs)
	{
		Calendar calendar;
		Todo todo;
		if (existingIcs is not null)
		{
			calendar = IcalHelpers.Load(existingIcs);
			todo = calendar.Todos.FirstOrDefault() ?? AddNewTodo(calendar);
		}
		else
		{
			calendar = new Calendar { ProductId = "-//ActiveSync Gateway//EN" };
			todo = AddNewTodo(calendar);
		}

		todo.Uid = uid;

		string? V(string name)
		{
			return applicationData.Element(Tasks + name)?.Value;
		}

		if (V("Subject") is { } subject)
			todo.Summary = subject;
		if (applicationData.Element(AirSyncBase + "Body")?.Element(AirSyncBase + "Data")?.Value is { } body)
			todo.Description = body;

		if (V("Importance") is { } importance)
			todo.Priority = importance switch
			{
				"2" => 1, // high
				"0" => 9, // low
				_ => 5 // normal
			};

		// Presence-guarded: only an EXPLICIT Complete element changes completion state — an
		// omitted one (partial/ghosted Change) must not silently reopen a completed task.
		if (V("Complete") is { } complete)
		{
			if (complete == "1")
			{
				todo.Status = "COMPLETED";
				todo.PercentComplete = 100;
				DateTime completedAt = V("DateCompleted") is { } dc ? EasDateTime.Parse(dc) : DateTime.UtcNow;
				todo.Completed = new CalDateTime(completedAt, "UTC");
			}
			else if (string.Equals(todo.Status, "COMPLETED", StringComparison.OrdinalIgnoreCase))
			{
				// Un-complete: reset; otherwise leave whatever progress state the backend has
				// (EAS cannot express IN-PROCESS/percentages, so do not stomp them).
				todo.Status = "NEEDS-ACTION";
				todo.PercentComplete = 0;
				todo.Completed = null;
			}
		}

		// Tasks are date-oriented: store DUE/DTSTART as date-only values taken from the
		// client's nominal (wall-clock) fields, so the picked date survives every
		// timezone round trip. Utc* is only a fallback for clients that omit the nominal.
		// Presence-guarded: an omitted date leaves the stored one untouched.
		string? due = V("DueDate") ?? V("UtcDueDate");
		if (due is not null)
			todo.Due = new CalDateTime(DateOnly.FromDateTime(EasDateTime.Parse(due)));
		string? start = V("StartDate") ?? V("UtcStartDate");
		if (start is not null)
			todo.DtStart = new CalDateTime(DateOnly.FromDateTime(EasDateTime.Parse(start)));

		if (V("Sensitivity") is { } sensitivity)
			todo.Class = sensitivity switch
			{
				"2" => "PRIVATE",
				"3" => "CONFIDENTIAL",
				_ => "PUBLIC"
			};

		if (applicationData.Element(Tasks + "Categories") is { } categories)
			todo.Categories = categories.Elements(Tasks + "Category").Select(c => c.Value).ToList();

		// Only the DISPLAY alarm is EAS-managed. ReminderSet=1 replaces it, ReminderSet=0
		// explicitly removes it, an omitted ReminderSet leaves alarms untouched — and
		// custom alarms (EMAIL action etc.) always survive.
		if (V("ReminderSet") is { } reminderSet)
		{
			foreach (Alarm displayAlarm in todo.Alarms.Where(a =>
				         string.Equals(a.Action, "DISPLAY", StringComparison.OrdinalIgnoreCase)).ToList())
				todo.Alarms.Remove(displayAlarm);
			if (reminderSet == "1" && V("ReminderTime") is { } reminderTime)
			{
				// Ical.Net only serializes an absolute trigger when VALUE=DATE-TIME is declared;
				// without it the (null) duration path wins and TRIGGER comes out empty.
				Trigger trigger = new() { DateTime = new CalDateTime(EasDateTime.Parse(reminderTime), "UTC") };
				trigger.SetValueType("DATE-TIME");
				todo.Alarms.Add(new Alarm
				{
					Action = "DISPLAY",
					Description = todo.Summary ?? "Reminder",
					Trigger = trigger
				});
			}
		}

		todo.LastModified = new CalDateTime(DateTime.UtcNow, "UTC");
		return IcalHelpers.Serialize(calendar);
	}

	private static Todo AddNewTodo(Calendar calendar)
	{
		Todo todo = new();
		calendar.Todos.Add(todo);
		return todo;
	}

	/// <summary>
	///   The wall-clock value of a CalDateTime in its own zone (date-only values are
	///   midnight), marked as UTC only so EasDateTime.ToLong passes it through unconverted.
	/// </summary>
	private static DateTime Nominal(CalDateTime value)
	{
		return DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);
	}
}

using System.Text.Json;
using System.Xml.Linq;
using ActiveSync.Backends.Converters;
using ActiveSync.Backends.Jmap;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Protocol.Wbxml;

namespace ActiveSync.Core.Tests;

/// <summary>
///   JSCalendar ⇄ iCalendar bridge, and the full JSCalendar ⇄ iCal ⇄ EAS path via the shared
///   calendar converter. Built on the JSCalendar shape Stalwart emits.
/// </summary>
public class JsCalendarConverterTests
{
	private static readonly XNamespace Cal = EasNamespaces.Calendar;

	private const string EventJson = """
	{
	  "@type": "Event", "uid": "test-uid-1", "title": "Team Sync", "description": "Weekly",
	  "start": "2026-07-20T10:00:00", "timeZone": "UTC", "duration": "PT1H",
	  "locations": { "l": { "@type": "Location", "name": "Room 1" } },
	  "status": "confirmed", "isDraft": false, "isOrigin": true, "id": "b"
	}
	""";

	private static JsonElement Event => JsonDocument.Parse(EventJson).RootElement;

	[Fact]
	public void JsCalendar_ToICalendar_ThenBack_PreservesCommonFields()
	{
		string ics = JsCalendarConverter.ToICalendar(Event);
		Assert.Contains("BEGIN:VEVENT", ics);

		Dictionary<string, object?> back = JsCalendarConverter.FromICalendar(ics, null);
		JsonElement js = JsonSerializer.SerializeToElement(back);
		Assert.Equal("Team Sync", js.GetProperty("title").GetString());
		Assert.Equal("Weekly", js.GetProperty("description").GetString());
		Assert.Equal("test-uid-1", js.GetProperty("uid").GetString());
		Assert.Equal("Room 1",
			js.GetProperty("locations").EnumerateObject().First().Value.GetProperty("name").GetString());
		Assert.Equal("confirmed", js.GetProperty("status").GetString());
		Assert.StartsWith("2026-07-20T10:00:00", js.GetProperty("start").GetString());
	}

	[Fact]
	public void FullBridge_JsCalendarToEas_ExposesEventFields()
	{
		string ics = JsCalendarConverter.ToICalendar(Event);
		List<XElement>? eas = CalendarConverter.ToApplicationData(ics, BodyPreference.PlainText);
		Assert.NotNull(eas);
		string? V(string local) => eas.FirstOrDefault(e => e.Name.LocalName == local)?.Value;
		Assert.Equal("Team Sync", V("Subject"));
		Assert.Equal("Room 1", V("Location"));
		Assert.Equal("20260720T100000Z", V("StartTime"));
	}

	[Fact]
	public void FullBridge_EasToJsCalendar_RoundTripsSubjectAndTime()
	{
		XElement app = new("ApplicationData",
			new XElement(Cal + "Subject", "Planning"),
			new XElement(Cal + "StartTime", "20260721T140000Z"),
			new XElement(Cal + "EndTime", "20260721T150000Z"),
			new XElement(Cal + "Location", "HQ"),
			new XElement(Cal + "BusyStatus", "2"));

		string ics = CalendarConverter.FromApplicationData(app, "uid-eas-1", null);
		Dictionary<string, object?> js = JsCalendarConverter.FromICalendar(ics, null);
		JsonElement rebuilt = JsonSerializer.SerializeToElement(js);
		Assert.Equal("Planning", rebuilt.GetProperty("title").GetString());
		Assert.Equal("uid-eas-1", rebuilt.GetProperty("uid").GetString());

		// ...and back out to EAS via the bridge yields the same subject/time.
		string ics2 = JsCalendarConverter.ToICalendar(rebuilt);
		List<XElement>? eas = CalendarConverter.ToApplicationData(ics2, BodyPreference.PlainText);
		string? V(string local) => eas!.FirstOrDefault(e => e.Name.LocalName == local)?.Value;
		Assert.Equal("Planning", V("Subject"));
		Assert.Equal("20260721T140000Z", V("StartTime"));
	}

	// H4 — the recurrence member is written in one shape and read in another, so recurrence never
	// survives a round trip. Both spellings are exercised: RFC 8984 names it "recurrenceRules" and
	// types it as an array; Stalwart 0.16 (the live backend) emits and accepts the JSCalendar-draft
	// "recurrenceRule", a single object, and rejects the plural name outright.
	[Theory]
	[InlineData("""{ "recurrenceRules": [ { "@type": "RecurrenceRule", "frequency": "weekly", "count": 5 } ] }""")]
	[InlineData("""{ "recurrenceRule": { "@type": "RecurrenceRule", "frequency": "weekly", "count": 5 } }""")]
	public void ToICalendar_ReadsRecurrence_InEitherServerShape(string recurrenceJson)
	{
		JsonElement rule = JsonDocument.Parse(recurrenceJson).RootElement;
		Dictionary<string, object?> js = new()
		{
			["@type"] = "Event", ["uid"] = "rec-1", ["title"] = "Standup",
			["start"] = "2026-07-20T10:00:00", ["timeZone"] = "UTC", ["duration"] = "PT1H"
		};
		foreach (JsonProperty p in rule.EnumerateObject())
			js[p.Name] = JsonSerializer.Deserialize<object>(p.Value.GetRawText());

		string ics = JsCalendarConverter.ToICalendar(JsonSerializer.SerializeToElement(js));
		Assert.Contains("RRULE:", ics);
		Assert.Contains("FREQ=WEEKLY", ics);
		Assert.Contains("COUNT=5", ics);
	}

	[Fact]
	public void Recurrence_SurvivesTheFullJsCalendarRoundTrip()
	{
		XElement app = new("ApplicationData",
			new XElement(Cal + "Subject", "Standup"),
			new XElement(Cal + "StartTime", "20260720T090000Z"),
			new XElement(Cal + "EndTime", "20260720T091500Z"),
			new XElement(Cal + "BusyStatus", "2"),
			new XElement(Cal + "Recurrence",
				new XElement(Cal + "Type", "1"),
				new XElement(Cal + "Interval", "1"),
				new XElement(Cal + "DayOfWeek", "2"),
				new XElement(Cal + "Occurrences", "5")));

		string ics = CalendarConverter.FromApplicationData(app, "uid-rec-1", null);
		Assert.Contains("RRULE:", ics);

		// EAS → iCal → JSCalendar → iCal → EAS: the recurrence must still be there.
		Dictionary<string, object?> js = JsCalendarConverter.FromICalendar(ics, null);
		string ics2 = JsCalendarConverter.ToICalendar(JsonSerializer.SerializeToElement(js));
		Assert.Contains("RRULE:", ics2);
		List<XElement>? eas = CalendarConverter.ToApplicationData(ics2, BodyPreference.PlainText);
		Assert.NotNull(eas);
		XElement? recurrence = eas.FirstOrDefault(e => e.Name.LocalName == "Recurrence");
		Assert.NotNull(recurrence);
		Assert.Equal("1", recurrence.Elements().First(e => e.Name.LocalName == "Type").Value);
	}

	// H5 — the ordinal on a day ("2nd Tuesday"), byMonthDay, byMonth and bySetPosition were
	// unmapped in both directions, so "2nd Tuesday of the month" degraded to "every Tuesday" and
	// "the 15th" to "the day the event happens to start on".
	[Theory]
	// monthly, 2nd Tuesday: Type 3 + WeekOfMonth 2 + DayOfWeek 4 (Tuesday)
	[InlineData("3", "2", "4", null, null, "WeekOfMonth", "2")]
	[InlineData("3", "2", "4", null, null, "DayOfWeek", "4")]
	// monthly on the 15th: Type 2 + DayOfMonth (the event starts on the 20th, so a dropped
	// byMonthDay silently reappears as 20)
	[InlineData("2", null, null, "15", null, "DayOfMonth", "15")]
	// yearly on 15 March: Type 5 + DayOfMonth + MonthOfYear (event starts in July)
	[InlineData("5", null, null, "15", "3", "MonthOfYear", "3")]
	[InlineData("5", null, null, "15", "3", "DayOfMonth", "15")]
	public void Recurrence_OrdinalsAndByParts_SurviveTheJsCalendarRoundTrip(
		string type, string? weekOfMonth, string? dayOfWeek, string? dayOfMonth, string? monthOfYear,
		string expectedElement, string expectedValue)
	{
		XElement recurrence = new(Cal + "Recurrence",
			new XElement(Cal + "Type", type),
			new XElement(Cal + "Interval", "1"));
		if (weekOfMonth is not null) recurrence.Add(new XElement(Cal + "WeekOfMonth", weekOfMonth));
		if (dayOfWeek is not null) recurrence.Add(new XElement(Cal + "DayOfWeek", dayOfWeek));
		if (dayOfMonth is not null) recurrence.Add(new XElement(Cal + "DayOfMonth", dayOfMonth));
		if (monthOfYear is not null) recurrence.Add(new XElement(Cal + "MonthOfYear", monthOfYear));

		XElement app = new("ApplicationData",
			new XElement(Cal + "Subject", "Recurring"),
			new XElement(Cal + "StartTime", "20260720T090000Z"),
			new XElement(Cal + "EndTime", "20260720T100000Z"),
			new XElement(Cal + "BusyStatus", "2"),
			recurrence);

		string ics = CalendarConverter.FromApplicationData(app, "uid-h5", null);
		Dictionary<string, object?> js = JsCalendarConverter.FromICalendar(ics, null);
		string ics2 = JsCalendarConverter.ToICalendar(JsonSerializer.SerializeToElement(js));
		List<XElement>? eas = CalendarConverter.ToApplicationData(ics2, BodyPreference.PlainText);

		XElement? back = eas!.FirstOrDefault(e => e.Name.LocalName == "Recurrence");
		Assert.NotNull(back);
		Assert.Equal(expectedValue,
			back.Elements().FirstOrDefault(e => e.Name.LocalName == expectedElement)?.Value);
	}

	// H23 — a JSCalendar event with no timeZone is *floating* (RFC 8984 §4.1.2): it happens at
	// that wall-clock time wherever the viewer is. Anchoring it to UTC moves it by the viewer's
	// offset — an 09:00 daily standup becomes 11:00 in Copenhagen.
	[Fact]
	public void FloatingEvent_StaysFloating_AndIsNotAnchoredToUtc()
	{
		const string floating = """
		{ "@type": "Event", "uid": "float-1", "title": "Standup",
		  "start": "2026-07-20T09:00:00", "duration": "PT1H" }
		""";

		string ics = JsCalendarConverter.ToICalendar(JsonDocument.Parse(floating).RootElement);
		Assert.Contains("DTSTART:20260720T090000", ics);
		Assert.DoesNotContain("DTSTART:20260720T090000Z", ics);
		Assert.DoesNotContain("TZID", ics);

		// ...and coming back out it must still carry no timeZone, at the same wall-clock time.
		JsonElement back = JsonSerializer.SerializeToElement(JsCalendarConverter.FromICalendar(ics, null));
		Assert.Equal("2026-07-20T09:00:00", back.GetProperty("start").GetString());
		Assert.False(back.TryGetProperty("timeZone", out _));
	}

	[Fact]
	public void ZonedEvent_KeepsItsZone()
	{
		const string zoned = """
		{ "@type": "Event", "uid": "zoned-1", "title": "Standup",
		  "start": "2026-07-20T09:00:00", "timeZone": "Europe/Copenhagen", "duration": "PT1H" }
		""";

		string ics = JsCalendarConverter.ToICalendar(JsonDocument.Parse(zoned).RootElement);
		Assert.Contains("TZID=Europe/Copenhagen", ics);
		JsonElement back = JsonSerializer.SerializeToElement(JsCalendarConverter.FromICalendar(ics, null));
		Assert.Equal("Europe/Copenhagen", back.GetProperty("timeZone").GetString());
		Assert.Equal("2026-07-20T09:00:00", back.GetProperty("start").GetString());
	}

	[Fact]
	public void FromICalendar_DropsServerManagedMembers()
	{
		// The existing event carries read-only members (isDraft/isOrigin/id); an update built
		// from it must not echo them back.
		string ics = JsCalendarConverter.ToICalendar(Event);
		Dictionary<string, object?> update = JsCalendarConverter.FromICalendar(ics, Event);
		Assert.False(update.ContainsKey("isDraft"));
		Assert.False(update.ContainsKey("isOrigin"));
		Assert.False(update.ContainsKey("id"));
	}
}

using System.Text.Json;
using System.Xml.Linq;
using ActiveSync.Backends.Converters;
using ActiveSync.Backends.Jmap;
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

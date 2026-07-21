using System.Globalization;
using System.Text.Json;
using System.Xml;
using ActiveSync.Contracts;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using IcalCalendar = Ical.Net.Calendar;

namespace ActiveSync.Backends.Jmap;

/// <summary>
///   JSCalendar (RFC 8984 / draft-ietf-jmap-calendars) ⇄ iCalendar bridge. The gateway's
///   mature iCalendar ⇄ EAS converter (<c>CalendarConverter</c>) then handles the EAS side, so
///   this only maps the common event fields between JSCalendar and a VEVENT: uid, title,
///   description, start/timeZone/duration (and all-day), location, status, free/busy, privacy,
///   organizer/attendees, and simple recurrence rules. Unknown JSCalendar members are preserved
///   on write. Advanced recurrence exceptions/overrides are not round-tripped (documented).
/// </summary>
public static class JsCalendarConverter
{
	// JSCalendar members this bridge manages; any other member of an existing event is kept.
	private static readonly string[] Managed =
	[
		"@type", "uid", "title", "description", "start", "timeZone", "duration", "showWithoutTime",
		"locations", "status", "freeBusyStatus", "privacy", "participants",
		"recurrenceRules", "recurrenceRule", "replyTo"
	];

	// JMAP `*/set update` values are PatchObjects (RFC 8620 §5.3) — a member absent from the patch
	// is left untouched, not cleared. Verified against Stalwart 0.16. EAS Change payloads carry the
	// complete managed set, so a field the client cleared arrives as an *absent* element and has to
	// be sent as an explicit null. Also correct under full-replace semantics, where an explicit
	// null and an absent member mean the same thing.
	//
	// "@type"/"uid" are always written; "replyTo" is listed as managed but this bridge never
	// produces it, so nulling it would drop the server's own iMIP reply address on every edit.
	// The recurrence member is absent here on purpose: its name is server-dependent, so it is
	// cleared separately under whichever spelling this event is written in — see RecurrenceOf.
	private static readonly string[] ClearedOnUpdate =
	[
		"title", "description", "start", "timeZone", "duration", "showWithoutTime",
		"locations", "status", "freeBusyStatus", "privacy", "participants"
	];

	// Server-managed / read-only members: never echoed back in an update (invalidProperties).
	private static readonly string[] ReadOnly =
	[
		"id", "calendarIds", "isDraft", "isOrigin", "created", "updated", "sequence",
		"method", "prodId", "blobId"
	];

	public static string ToICalendar(JsonElement jsEvent)
	{
		IcalCalendar calendar = new() { ProductId = "-//ActiveSync Gateway//JMAP//EN" };
		CalendarEvent evt = new();
		calendar.Events.Add(evt);

		evt.Uid = Str(jsEvent, "uid") ?? Guid.NewGuid().ToString();
		if (Str(jsEvent, "title") is { } title) evt.Summary = title;
		if (Str(jsEvent, "description") is { } description) evt.Description = description;

		JsonElement location = Values(jsEvent, "locations").FirstOrDefault();
		if (location.ValueKind == JsonValueKind.Object && Str(location, "name") is { } locName)
			evt.Location = locName;

		bool allDay = jsEvent.TryGetProperty("showWithoutTime", out JsonElement swt) && swt.ValueKind == JsonValueKind.True;
		string? startRaw = Str(jsEvent, "start");
		string? tzId = Str(jsEvent, "timeZone");
		TimeSpan duration = jsEvent.TryGetProperty("duration", out JsonElement dur) && dur.GetString() is { } d
			? ParseDuration(d)
			: allDay ? TimeSpan.FromDays(1) : TimeSpan.FromHours(1);

		if (startRaw is not null &&
		    DateTime.TryParse(startRaw, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime start))
		{
			if (allDay)
			{
				evt.Start = new CalDateTime(DateOnly.FromDateTime(start));
				evt.End = new CalDateTime(DateOnly.FromDateTime(start + duration));
			}
			else
			{
				evt.Start = tzId is not null ? new CalDateTime(start, tzId) : new CalDateTime(start, "UTC");
				evt.End = tzId is not null ? new CalDateTime(start + duration, tzId) : new CalDateTime(start + duration, "UTC");
			}
		}

		evt.Status = Str(jsEvent, "status")?.ToLowerInvariant() switch
		{
			"cancelled" => "CANCELLED",
			"tentative" => "TENTATIVE",
			_ => "CONFIRMED"
		};
		if (Str(jsEvent, "freeBusyStatus") == "free")
			evt.Transparency = "TRANSPARENT";
		evt.Class = Str(jsEvent, "privacy")?.ToLowerInvariant() switch
		{
			"private" => "PRIVATE",
			"secret" => "CONFIDENTIAL",
			_ => "PUBLIC"
		};

		foreach (JsonElement participant in Values(jsEvent, "participants"))
		{
			string? email = ParticipantEmail(participant);
			if (email is null)
				continue;
			bool owner = Bool(participant, "roles", "owner");
			if (owner && evt.Organizer is null)
				evt.Organizer = new Organizer($"mailto:{email}") { CommonName = Str(participant, "name") };
			if (Bool(participant, "roles", "attendee") || !owner)
				evt.Attendees.Add(new Attendee($"mailto:{email}")
				{
					CommonName = Str(participant, "name"),
					ParticipationStatus = Str(participant, "participationStatus")?.ToUpperInvariant() switch
					{
						"accepted" => "ACCEPTED",
						"declined" => "DECLINED",
						"tentative" => "TENTATIVE",
						_ => "NEEDS-ACTION"
					}
				});
		}

		if (RecurrenceOf(jsEvent) is { ValueKind: JsonValueKind.Object } rule)
			evt.RecurrenceRule = ToRecurrenceRule(rule);

		return new CalendarSerializer().SerializeToString(calendar) ?? "";
	}

	public static Dictionary<string, object?> FromICalendar(string ics, JsonElement? existing)
	{
		IcalCalendar calendar = IcalCalendar.Load(ics)
		                        ?? throw new BackendException("iCalendar could not be parsed.");
		CalendarEvent evt = calendar.Events.FirstOrDefault(e => e.RecurrenceIdentifier is null)
		                    ?? calendar.Events.FirstOrDefault()
		                    ?? throw new BackendException("iCalendar carried no event.");

		Dictionary<string, object?> js = new()
		{
			["@type"] = "Event",
			["uid"] = evt.Uid
		};

		// Preserve unknown members from the existing event, but never the server-managed /
		// read-only ones — echoing those back in an update is rejected as invalidProperties.
		if (existing is { ValueKind: JsonValueKind.Object } prior)
			foreach (JsonProperty p in prior.EnumerateObject())
				if (!Managed.Contains(p.Name) && !ReadOnly.Contains(p.Name))
					js[p.Name] = JsonSerializer.Deserialize<object>(p.Value.GetRawText());

		if (!string.IsNullOrEmpty(evt.Summary)) js["title"] = evt.Summary;
		if (!string.IsNullOrEmpty(evt.Description)) js["description"] = evt.Description;
		if (!string.IsNullOrEmpty(evt.Location))
			js["locations"] = new Dictionary<string, object?>
			{
				["l"] = new Dictionary<string, object?> { ["@type"] = "Location", ["name"] = evt.Location }
			};

		if (evt.Start is { } start)
		{
			bool allDay = !evt.Start.HasTime;
			js["showWithoutTime"] = allDay;
			js["start"] = allDay
				? start.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
				: start.Value.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
			if (!allDay && start.TzId is { Length: > 0 } tz)
				js["timeZone"] = tz;
			if (evt.End is { } end)
				js["duration"] = XmlConvert.ToString(end.Value - start.Value);
		}

		js["status"] = evt.Status?.ToUpperInvariant() switch
		{
			"CANCELLED" => "cancelled",
			"TENTATIVE" => "tentative",
			_ => "confirmed"
		};
		if (string.Equals(evt.Transparency, "TRANSPARENT", StringComparison.OrdinalIgnoreCase))
			js["freeBusyStatus"] = "free";
		js["privacy"] = evt.Class?.ToUpperInvariant() switch
		{
			"PRIVATE" => "private",
			"CONFIDENTIAL" => "secret",
			_ => "public"
		};

		Dictionary<string, object?> participants = new();
		if (evt.Organizer?.Value is { } organizer)
			participants["owner"] = Participant(organizer.ToString(), evt.Organizer.CommonName, "owner", null);
		int i = 0;
		foreach (Attendee attendee in evt.Attendees)
			if (attendee.Value is { } who)
				participants[$"a{i++}"] = Participant(who.ToString(), attendee.CommonName, "attendee", attendee.ParticipationStatus);
		if (participants.Count > 0)
			js["participants"] = participants;

		// Write the recurrence back in the shape this server speaks (see RecurrenceOf).
		bool plural = existing is { ValueKind: JsonValueKind.Object } prior2 &&
		              prior2.TryGetProperty(RecurrenceRulesMember, out _);
		string recurrenceMember = plural ? RecurrenceRulesMember : RecurrenceRuleMember;
		if (evt.RecurrenceRule is { } rp)
			js[recurrenceMember] = plural
				? new object[] { FromRecurrenceRule(rp) }
				: FromRecurrenceRule(rp);

		// Update (not create): explicitly null every managed member the event did not produce, so
		// clearing a location / recurrence / attendee list survives PatchObject update semantics.
		if (existing is not null)
		{
			foreach (string member in ClearedOnUpdate)
				if (!js.ContainsKey(member))
					js[member] = null;
			if (!js.ContainsKey(recurrenceMember))
				js[recurrenceMember] = null;
		}

		return js;
	}

	// ---------- recurrence ----------

	/// <summary>RFC 8984 §4.3.2 — an <em>array</em> of rules.</summary>
	private const string RecurrenceRulesMember = "recurrenceRules";

	/// <summary>
	///   The JSCalendar-draft spelling — a <em>single</em> rule object. Stalwart 0.16 emits this
	///   one and rejects the plural name with <c>invalidProperties</c> in every form (verified
	///   against the live backend, including the minimal RFC 8984 rule), so it is what the bridge
	///   writes unless the stored event demonstrates the plural shape.
	/// </summary>
	private const string RecurrenceRuleMember = "recurrenceRule";

	/// <summary>
	///   Reads the first recurrence rule under either spelling. The plural member is an array; the
	///   singular one is a bare object. The old code read the plural name with the id-map helper —
	///   which matches neither — so recurrence was silently dropped on every read.
	/// </summary>
	private static JsonElement? RecurrenceOf(JsonElement jsEvent)
	{
		if (jsEvent.ValueKind != JsonValueKind.Object)
			return null;
		if (jsEvent.TryGetProperty(RecurrenceRulesMember, out JsonElement rules) &&
		    rules.ValueKind == JsonValueKind.Array && rules.GetArrayLength() > 0)
			return rules[0];
		if (jsEvent.TryGetProperty(RecurrenceRuleMember, out JsonElement rule) &&
		    rule.ValueKind == JsonValueKind.Object)
			return rule;
		return null;
	}

	private static RecurrenceRule ToRecurrenceRule(JsonElement rule)
	{
		FrequencyType frequency = Str(rule, "frequency")?.ToLowerInvariant() switch
		{
			"daily" => FrequencyType.Daily,
			"weekly" => FrequencyType.Weekly,
			"monthly" => FrequencyType.Monthly,
			"yearly" => FrequencyType.Yearly,
			_ => FrequencyType.Daily
		};
		RecurrenceRule pattern = new(frequency)
		{
			Interval = rule.TryGetProperty("interval", out JsonElement iv) && iv.TryGetInt32(out int interval) ? interval : 1
		};
		if (rule.TryGetProperty("count", out JsonElement c) && c.TryGetInt32(out int count))
			pattern.Count = count;
		if (Str(rule, "until") is { } until &&
		    DateTime.TryParse(until, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTime untilDt))
			pattern.Until = new CalDateTime(DateTime.SpecifyKind(untilDt, DateTimeKind.Utc), "UTC");
		foreach (JsonElement day in Array(rule, "byDay"))
			if (Str(day, "day") is { } d && DayMap.TryGetValue(d.ToLowerInvariant(), out DayOfWeek dow))
			{
				// RFC 8984 NDay.nthOfPeriod is the ordinal — "2nd Tuesday", "-1 = last". Dropping
				// it turned every nth-weekday rule into a plain "every Tuesday".
				int nth = day.TryGetProperty("nthOfPeriod", out JsonElement n) &&
				          n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out int ordinal)
					? ordinal
					: 0;
				pattern.ByDay.Add(nth == 0 ? new WeekDay(dow) : new WeekDay(dow, nth));
			}

		pattern.ByMonthDay = Ints(rule, "byMonthDay").ToList();
		// byMonth is a String[] in JSCalendar ("1".."12", plus a trailing "L" for a leap month in
		// non-Gregorian calendars, which iCalendar cannot express and which is skipped).
		pattern.ByMonth = Array(rule, "byMonth")
			.Select(m => m.ValueKind == JsonValueKind.String ? m.GetString() : m.ToString())
			.Where(m => m is not null && int.TryParse(m, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
			.Select(m => int.Parse(m!, CultureInfo.InvariantCulture))
			.ToList();
		pattern.BySetPosition = Ints(rule, "bySetPosition").ToList();
		return pattern;
	}

	private static IEnumerable<int> Ints(JsonElement element, string property)
	{
		return Array(element, property)
			.Where(v => v.ValueKind == JsonValueKind.Number)
			.Select(v => v.TryGetInt32(out int i) ? i : 0)
			.Where(i => i != 0);
	}

	private static Dictionary<string, object?> FromRecurrenceRule(RecurrenceRule pattern)
	{
		Dictionary<string, object?> rule = new()
		{
			["@type"] = "RecurrenceRule",
			["frequency"] = pattern.Frequency.ToString().ToLowerInvariant()
		};
		if (pattern.Interval > 1)
			rule["interval"] = pattern.Interval;
		if (pattern.Count > 0)
			rule["count"] = pattern.Count;
		if (pattern.Until is { } until)
			rule["until"] = until.Value.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
		if (pattern.ByDay.Count > 0)
			rule["byDay"] = pattern.ByDay
				.Select(object (w) =>
				{
					Dictionary<string, object?> day = new()
					{
						["@type"] = "NDay",
						["day"] = DayName(w.DayOfWeek)
					};
					// Offset is nullable and 0/null both mean "no ordinal"; anything else is the
					// nth occurrence (-1 = last).
					if (w.Offset is { } offset && offset != 0)
						day["nthOfPeriod"] = offset;
					return day;
				})
				.ToArray();
		if (pattern.ByMonthDay is { Count: > 0 } byMonthDay)
			rule["byMonthDay"] = byMonthDay.ToArray();
		if (pattern.ByMonth is { Count: > 0 } byMonth)
			rule["byMonth"] = byMonth.Select(m => m.ToString(CultureInfo.InvariantCulture)).ToArray();
		if (pattern.BySetPosition is { Count: > 0 } bySetPosition)
			rule["bySetPosition"] = bySetPosition.ToArray();
		return rule;
	}

	private static readonly Dictionary<string, DayOfWeek> DayMap = new()
	{
		["mo"] = DayOfWeek.Monday, ["tu"] = DayOfWeek.Tuesday, ["we"] = DayOfWeek.Wednesday,
		["th"] = DayOfWeek.Thursday, ["fr"] = DayOfWeek.Friday, ["sa"] = DayOfWeek.Saturday, ["su"] = DayOfWeek.Sunday
	};

	private static string DayName(DayOfWeek day) => day switch
	{
		DayOfWeek.Monday => "mo",
		DayOfWeek.Tuesday => "tu",
		DayOfWeek.Wednesday => "we",
		DayOfWeek.Thursday => "th",
		DayOfWeek.Friday => "fr",
		DayOfWeek.Saturday => "sa",
		_ => "su"
	};

	// ---------- helpers ----------

	private static Dictionary<string, object?> Participant(string mailto, string? name, string role, string? status)
	{
		string email = mailto.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? mailto[7..] : mailto;
		Dictionary<string, object?> participant = new()
		{
			["@type"] = "Participant",
			["sendTo"] = new Dictionary<string, object?> { ["imip"] = $"mailto:{email}" },
			["roles"] = new Dictionary<string, object?> { [role] = true }
		};
		if (!string.IsNullOrEmpty(name)) participant["name"] = name;
		if (status is not null)
			participant["participationStatus"] = status.ToUpperInvariant() switch
			{
				"ACCEPTED" => "accepted",
				"DECLINED" => "declined",
				"TENTATIVE" => "tentative",
				_ => "needs-action"
			};
		return participant;
	}

	private static string? ParticipantEmail(JsonElement participant)
	{
		if (participant.TryGetProperty("sendTo", out JsonElement sendTo) && sendTo.ValueKind == JsonValueKind.Object &&
		    sendTo.TryGetProperty("imip", out JsonElement imip) && imip.GetString() is { } uri)
			return uri.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? uri[7..] : uri;
		return Str(participant, "email");
	}

	private static TimeSpan ParseDuration(string iso)
	{
		try
		{
			return XmlConvert.ToTimeSpan(iso);
		}
		catch (FormatException)
		{
			return TimeSpan.FromHours(1);
		}
	}

	private static IEnumerable<JsonElement> Values(JsonElement element, string property)
	{
		return element.TryGetProperty(property, out JsonElement map) && map.ValueKind == JsonValueKind.Object
			? map.EnumerateObject().Select(p => p.Value)
			: [];
	}

	private static IEnumerable<JsonElement> Array(JsonElement element, string property)
	{
		return element.TryGetProperty(property, out JsonElement arr) && arr.ValueKind == JsonValueKind.Array
			? arr.EnumerateArray()
			: [];
	}

	private static string? Str(JsonElement element, string property)
	{
		return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out JsonElement v) &&
		       v.ValueKind == JsonValueKind.String
			? v.GetString()
			: null;
	}

	private static bool Bool(JsonElement element, string mapProperty, string key)
	{
		return element.ValueKind == JsonValueKind.Object &&
		       element.TryGetProperty(mapProperty, out JsonElement map) && map.ValueKind == JsonValueKind.Object &&
		       map.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.True;
	}
}

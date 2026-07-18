using System.Xml.Linq;
using ActiveSync.Core.Backend;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Proxies;
using Ical.Net.Serialization;

// EAS 14.1 expresses at most one recurrence rule per event and identifies exceptions by their
// original start time, so the obsolete single-value RecurrenceId/RecurrenceRules surface of
// Ical.Net matches what the protocol can carry.
#pragma warning disable CS0618

namespace ActiveSync.Backends.Converters;

/// <summary>iCalendar VEVENT ↔ EAS Calendar-class ApplicationData (MS-ASCAL).</summary>
public static class CalendarConverter
{
	private static readonly XNamespace Cal = EasNamespaces.Calendar;
	private static readonly XNamespace AirSyncBase = EasNamespaces.AirSyncBase;

	public static string? ExtractUid(string ics)
	{
		Calendar? calendar = Calendar.Load(ics);
		return calendar?.Events.FirstOrDefault()?.Uid;
	}

	public static List<XElement>? ToApplicationData(string ics, BodyPreference bodyPreference)
	{
		Calendar? calendar = Calendar.Load(ics);
		IUniqueComponentList<CalendarEvent>? events = calendar?.Events;
		if (events is null || events.Count == 0)
			return null;

		CalendarEvent master = events.FirstOrDefault(e => e.RecurrenceId is null) ?? events.First();
		List<XElement> data = new();

		TimeZoneInfo? tz = ResolveTimeZone(master.Start?.TzId);
		data.Add(new XElement(Cal + "TimeZone",
			tz is null ? TimeZoneBlob.UtcBase64 : TimeZoneBlob.ToBase64(tz)));

		bool allDay = master.IsAllDay;
		data.Add(new XElement(Cal + "AllDayEvent", allDay ? "1" : "0"));

		DateTime? start = ToUtc(master.Start);
		DateTime? end = ToUtc(master.End) ?? start?.AddHours(1);
		if (start is null)
			return null;
		data.Add(new XElement(Cal + "StartTime", EasDateTime.ToCompact(start.Value)));
		data.Add(new XElement(Cal + "EndTime", EasDateTime.ToCompact(end ?? start.Value.AddHours(1))));
		data.Add(new XElement(Cal + "DtStamp",
			EasDateTime.ToCompact(ToUtc(master.DtStamp) ?? DateTime.UtcNow)));

		data.Add(new XElement(Cal + "Subject", master.Summary ?? ""));
		data.Add(new XElement(Cal + "UID", master.Uid ?? Guid.NewGuid().ToString()));
		if (!string.IsNullOrEmpty(master.Location))
			data.Add(LocationElement(master.Location, bodyPreference.Eas16));

		data.Add(new XElement(Cal + "Sensitivity", master.Class?.ToUpperInvariant() switch
		{
			"PRIVATE" => "2",
			"CONFIDENTIAL" => "3",
			_ => "0"
		}));
		data.Add(new XElement(Cal + "BusyStatus",
			string.Equals(master.Transparency, "TRANSPARENT", StringComparison.OrdinalIgnoreCase) ? "0" : "2"));

		bool hasAttendees = master.Attendees is { Count: > 0 };
		data.Add(new XElement(Cal + "MeetingStatus", hasAttendees ? "1" : "0"));

		if (master.Organizer is not null)
		{
			string? email = master.Organizer.Value?.ToString().Replace("mailto:", "", StringComparison.OrdinalIgnoreCase);
			if (!string.IsNullOrEmpty(email))
			{
				data.Add(new XElement(Cal + "OrganizerEmail", email));
				if (!string.IsNullOrEmpty(master.Organizer.CommonName))
					data.Add(new XElement(Cal + "OrganizerName", master.Organizer.CommonName));
			}
		}

		if (hasAttendees)
		{
			XElement attendees = new(Cal + "Attendees");
			foreach (Attendee attendee in master.Attendees)
			{
				string? email = attendee.Value?.ToString().Replace("mailto:", "", StringComparison.OrdinalIgnoreCase);
				if (string.IsNullOrEmpty(email))
					continue;
				attendees.Add(new XElement(Cal + "Attendee",
					new XElement(Cal + "Email", email),
					new XElement(Cal + "Name", attendee.CommonName ?? email),
					new XElement(Cal + "AttendeeStatus", attendee.ParticipationStatus?.ToUpperInvariant() switch
					{
						"ACCEPTED" => "3",
						"DECLINED" => "4",
						"TENTATIVE" => "2",
						_ => "0"
					}),
					new XElement(Cal + "AttendeeType",
						string.Equals(attendee.Role, "OPT-PARTICIPANT", StringComparison.OrdinalIgnoreCase)
							? "2"
							: "1")));
			}

			if (attendees.HasElements)
				data.Add(attendees);
		}

		Alarm? alarm = master.Alarms?.FirstOrDefault(a => a.Trigger?.Duration is not null);
		if (alarm?.Trigger?.Duration is { } duration)
		{
			int minutes = (int)Math.Abs(duration.ToTimeSpanUnspecified().TotalMinutes);
			data.Add(new XElement(Cal + "Reminder", minutes.ToString()));
		}

		RecurrencePattern? recurrence = master.RecurrenceRules?.FirstOrDefault();
		if (recurrence is not null)
		{
			XElement? recurrenceElement = BuildRecurrence(recurrence, start.Value);
			if (recurrenceElement is not null)
				data.Add(recurrenceElement);

			XElement exceptions = new(Cal + "Exceptions");
			foreach (CalDateTime exceptionDate in master.ExceptionDates.GetAllDates())
			{
				DateTime? when = ToUtc(exceptionDate);
				if (when is null)
					continue;
				exceptions.Add(new XElement(Cal + "Exception",
					new XElement(Cal + "Deleted", "1"),
					new XElement(Cal + "ExceptionStartTime", EasDateTime.ToCompact(when.Value))));
			}

			foreach (CalendarEvent modified in events.Where(e => e.RecurrenceId is not null))
			{
				DateTime? when = ToUtc(modified.RecurrenceId);
				if (when is null)
					continue;
				XElement exception = new(Cal + "Exception",
					new XElement(Cal + "ExceptionStartTime", EasDateTime.ToCompact(when.Value)));
				if (!string.IsNullOrEmpty(modified.Summary))
					exception.Add(new XElement(Cal + "Subject", modified.Summary));
				if (!string.IsNullOrEmpty(modified.Location))
					exception.Add(LocationElement(modified.Location, bodyPreference.Eas16));
				DateTime? exStart = ToUtc(modified.Start);
				DateTime? exEnd = ToUtc(modified.End);
				if (exStart is not null)
					exception.Add(new XElement(Cal + "StartTime", EasDateTime.ToCompact(exStart.Value)));
				if (exEnd is not null)
					exception.Add(new XElement(Cal + "EndTime", EasDateTime.ToCompact(exEnd.Value)));
				exceptions.Add(exception);
			}

			if (exceptions.HasElements)
				data.Add(exceptions);
		}

		string? description = master.Description;
		if (!string.IsNullOrEmpty(description))
		{
			(string content, bool truncated, long estimated) = BodyText.ForBody(description, bodyPreference.TruncationSize);
			data.Add(AirSyncBodyWriter.Build(estimated, truncated, content));
		}

		data.Add(new XElement(Cal + "ResponseRequested", hasAttendees ? "1" : "0"));
		if (bodyPreference.Eas16)
			AppendAttachments(data, master);
		return data;
	}

	/// <summary>
	///   calendar:Location died with 14.1 — 16.x clients expect the structured
	///   airsyncbase:Location instead (only DisplayName maps onto iCal LOCATION).
	/// </summary>
	private static XElement LocationElement(string location, bool eas16)
	{
		return eas16
			? new XElement(AirSyncBase + "Location", new XElement(AirSyncBase + "DisplayName", location))
			: new XElement(Cal + "Location", location);
	}

	private static XElement? BuildRecurrence(RecurrencePattern pattern, DateTime startUtc)
	{
		XElement recurrence = new(Cal + "Recurrence");
		int? type = null;

		switch (pattern.Frequency)
		{
			case FrequencyType.Daily:
				type = 0;
				break;
			case FrequencyType.Weekly:
				type = 1;
				recurrence.Add(new XElement(Cal + "DayOfWeek",
					(pattern.ByDay is { Count: > 0 }
						? DayOfWeekMask(pattern.ByDay.Select(d => d.DayOfWeek))
						: DayOfWeekMask([startUtc.DayOfWeek])).ToString()));
				break;
			case FrequencyType.Monthly when pattern.ByDay is { Count: > 0 }:
				type = 3;
				recurrence.Add(new XElement(Cal + "WeekOfMonth", WeekOfMonth(pattern).ToString()));
				recurrence.Add(new XElement(Cal + "DayOfWeek",
					DayOfWeekMask(pattern.ByDay.Select(d => d.DayOfWeek)).ToString()));
				break;
			case FrequencyType.Monthly:
				type = 2;
				recurrence.Add(new XElement(Cal + "DayOfMonth",
					(pattern.ByMonthDay?.FirstOrDefault() is int dom and not 0 ? dom : startUtc.Day).ToString()));
				break;
			case FrequencyType.Yearly when pattern.ByDay is { Count: > 0 }:
				type = 6;
				recurrence.Add(new XElement(Cal + "WeekOfMonth", WeekOfMonth(pattern).ToString()));
				recurrence.Add(new XElement(Cal + "DayOfWeek",
					DayOfWeekMask(pattern.ByDay.Select(d => d.DayOfWeek)).ToString()));
				recurrence.Add(new XElement(Cal + "MonthOfYear",
					(pattern.ByMonth?.FirstOrDefault() is int m and not 0 ? m : startUtc.Month).ToString()));
				break;
			case FrequencyType.Yearly:
				type = 5;
				recurrence.Add(new XElement(Cal + "DayOfMonth",
					(pattern.ByMonthDay?.FirstOrDefault() is int ymd and not 0 ? ymd : startUtc.Day).ToString()));
				recurrence.Add(new XElement(Cal + "MonthOfYear",
					(pattern.ByMonth?.FirstOrDefault() is int ym and not 0 ? ym : startUtc.Month).ToString()));
				break;
			default:
				return null; // secondly/minutely/hourly are not expressible in EAS
		}

		recurrence.AddFirst(new XElement(Cal + "Type", type.ToString()));
		if (pattern.Interval > 1)
			recurrence.Add(new XElement(Cal + "Interval", pattern.Interval.ToString()));
		if (pattern.Count is > 0)
			recurrence.Add(new XElement(Cal + "Occurrences", pattern.Count.ToString()));
		if (pattern.Until is not null)
		{
			DateTime? until = ToUtc(pattern.Until);
			if (until is not null)
				recurrence.Add(new XElement(Cal + "Until", EasDateTime.ToCompact(until.Value)));
		}

		return recurrence;
	}

	private static int WeekOfMonth(RecurrencePattern pattern)
	{
		// EAS encodes "which week" as 1-5, where 5 specifically means "last week of the
		// month". iCal expresses the same via a negative offset: -1 = last. Map that -1 to
		// EAS's 5; clamp any other value into 1-5 (0/absent → first week).
		int offset = pattern.ByDay?.FirstOrDefault()?.Offset
		             ?? pattern.BySetPosition?.FirstOrDefault()
		             ?? 1;
		return offset == -1 ? 5 : Math.Clamp(offset == 0 ? 1 : offset, 1, 5);
	}

	private static int DayOfWeekMask(IEnumerable<DayOfWeek> days)
	{
		int mask = 0;
		foreach (DayOfWeek day in days)
			mask |= 1 << (int)day; // Sunday=1, Monday=2, ... Saturday=64
		return mask == 0 ? 1 : mask;
	}

	private static IEnumerable<DayOfWeek> MaskToDays(int mask)
	{
		for (int i = 0; i < 7; i++)
			if ((mask & (1 << i)) != 0)
				yield return (DayOfWeek)i;
	}

	/// <summary>
	///   Builds an iCalendar document from client ApplicationData. When <paramref name="existingIcs" />
	///   is provided, unmanaged properties (attendees, organizer, custom props) are preserved.
	/// </summary>
	public static string FromApplicationData(
		XElement applicationData, string uid, string? existingIcs, long? attachmentCapBytes = null)
	{
		string? V(string localName)
		{
			return applicationData.Element(Cal + localName)?.Value;
		}

		Calendar calendar;
		CalendarEvent evt;
		if (existingIcs is not null)
		{
			calendar = IcalHelpers.Load(existingIcs);
			evt = calendar.Events.FirstOrDefault(e => e.RecurrenceId is null) ?? calendar.Events.FirstOrDefault()
				?? AddNewEvent(calendar);
		}
		else
		{
			calendar = new Calendar { ProductId = "-//ActiveSync Gateway//EN" };
			evt = AddNewEvent(calendar);
		}

		evt.Uid = uid;
		if (V("Subject") is { } subject)
			evt.Summary = subject;
		// ≤14.1 sends calendar:Location; 16.x sends airsyncbase:Location with a DisplayName.
		string? location = V("Location")
			?? applicationData.Element(AirSyncBase + "Location")?
				.Element(AirSyncBase + "DisplayName")?.Value;
		if (location is not null)
			evt.Location = location;

		bool allDay = V("AllDayEvent") == "1";
		string? startRaw = V("StartTime");
		string? endRaw = V("EndTime");
		TimeSpan? tzOffset = TimeZoneBlob.ReadBaseOffset(V("TimeZone"));

		if (startRaw is not null)
		{
			DateTime startUtc = EasDateTime.Parse(startRaw);
			evt.Start = allDay
				? new CalDateTime(DateOnly.FromDateTime(LocalDate(startUtc, tzOffset)))
				: new CalDateTime(startUtc, "UTC");
		}

		if (endRaw is not null)
		{
			DateTime endUtc = EasDateTime.Parse(endRaw);
			evt.End = allDay
				? new CalDateTime(DateOnly.FromDateTime(LocalDate(endUtc, tzOffset)))
				: new CalDateTime(endUtc, "UTC");
		}

		// Presence-guarded: an omitted element means "leave as is", never "reset to default" —
		// a partial/ghosted Change must not silently flip a PRIVATE event to PUBLIC.
		if (V("Sensitivity") is { } sensitivity)
			evt.Class = sensitivity switch
			{
				"2" => "PRIVATE",
				"3" => "CONFIDENTIAL",
				_ => "PUBLIC"
			};
		if (V("BusyStatus") is { } busyStatus)
			evt.Transparency = busyStatus == "0" ? "TRANSPARENT" : "OPAQUE";

		string? bodyData = applicationData.Element(AirSyncBase + "Body")?.Element(AirSyncBase + "Data")?.Value;
		if (bodyData is not null)
			evt.Description = bodyData;

		// Recurrence/exceptions are rebuilt only when the payload carries them; an omitted
		// block leaves the stored rules untouched (partial/ghosted Changes must not strip a
		// recurring event). Consequence: EAS cannot express "remove the recurrence" — data
		// preservation wins over that rare edit.
		XElement? recurrenceElement = applicationData.Element(Cal + "Recurrence");
		if (recurrenceElement is not null)
		{
			evt.RecurrenceRules?.Clear();
			RecurrencePattern? rule = ParseRecurrence(recurrenceElement);
			if (rule is not null)
			{
				evt.RecurrenceRules ??= [];
				evt.RecurrenceRules.Add(rule);
			}
		}

		XElement? exceptions = applicationData.Element(Cal + "Exceptions");
		if (exceptions is not null)
		{
			// MERGE, never clear: 14.1 clients send the full exception list (a superset of
			// what is stored), while 16.x clients send only the newly deleted occurrence.
			// EAS has no wire shape for un-deleting an occurrence, so accumulating EXDATEs
			// is correct for both generations.
			HashSet<DateTime> existing = evt.ExceptionDates.GetAllDates()
				.Select(d => ToUtc(d))
				.Where(d => d is not null)
				.Select(d => d!.Value)
				.ToHashSet();
			foreach (XElement exception in exceptions.Elements(Cal + "Exception"))
			{
				if (exception.Element(Cal + "Deleted")?.Value != "1")
					continue; // modified occurrences from clients are rare; deletions dominate
				string? whenRaw = exception.Element(Cal + "ExceptionStartTime")?.Value;
				if (whenRaw is null)
					continue;
				DateTime when = EasDateTime.Parse(whenRaw);
				if (existing.Add(when))
					evt.ExceptionDates.Add(new CalDateTime(when, "UTC"));
			}
		}

		// Only the DISPLAY alarm is EAS-managed: a present Reminder replaces it, an omitted
		// one leaves alarms untouched, and custom alarms (EMAIL action etc.) always survive.
		if (V("Reminder") is { } reminder && int.TryParse(reminder, out int minutes) && minutes >= 0)
		{
			foreach (Alarm displayAlarm in evt.Alarms.Where(a =>
				         string.Equals(a.Action, "DISPLAY", StringComparison.OrdinalIgnoreCase)).ToList())
				evt.Alarms.Remove(displayAlarm);
			evt.Alarms.Add(new Alarm
			{
				Action = "DISPLAY",
				Description = "Reminder",
				Trigger = new Trigger(Duration.FromMinutes(-minutes))
			});
		}

		ApplyAttachmentChanges(evt, applicationData, attachmentCapBytes);

		evt.DtStamp = new CalDateTime(DateTime.UtcNow, "UTC");
		return IcalHelpers.Serialize(calendar);
	}

	// ---------- event attachments (EAS 16.x, stored inline as base64 ATTACH) ----------

	/// <summary>ItemOperations FileReference prefix; full shape "calatt::&lt;serverId&gt;::&lt;index&gt;".</summary>
	public const string AttachmentReferencePrefix = "calatt::";

	private const string FileNameParameter = "X-FILENAME";

	/// <summary>
	///   Emits the 16.x airsyncbase:Attachments block for inline event attachments. The
	///   FileReference carries only the index — the Sync handler prefixes the item's
	///   ServerId, which the converter cannot know.
	/// </summary>
	private static void AppendAttachments(List<XElement> data, CalendarEvent master)
	{
		List<Attachment> binaries = master.Attachments?
			.Where(a => a?.Data is { Length: > 0 }).ToList() ?? [];
		if (binaries.Count == 0)
			return;

		XElement attachments = new(AirSyncBase + "Attachments");
		for (int i = 0; i < binaries.Count; i++)
			attachments.Add(new XElement(AirSyncBase + "Attachment",
				new XElement(AirSyncBase + "DisplayName",
					binaries[i].Parameters.Get(FileNameParameter) ?? $"attachment-{i + 1}"),
				new XElement(AirSyncBase + "FileReference", AttachmentReferencePrefix + i),
				new XElement(AirSyncBase + "Method", "1"),
				new XElement(AirSyncBase + "EstimatedDataSize", binaries[i].Data!.Length.ToString())));
		data.Add(attachments);
	}

	/// <summary>Applies 16.x attachment Add/Delete commands; null cap = feature disabled.</summary>
	private static void ApplyAttachmentChanges(CalendarEvent evt, XElement applicationData, long? capBytes)
	{
		XElement? attachments = applicationData.Element(AirSyncBase + "Attachments");
		if (attachments is null)
			return;
		if (capBytes is null)
			throw new BackendException("Calendar attachments are disabled (CalendarAttachments=Off).");

		// Deletes first, by descending inline index, so positions stay valid.
		List<Attachment> binaries = evt.Attachments.Where(a => a?.Data is { Length: > 0 }).ToList();
		List<int> deleteIndexes = attachments.Elements(AirSyncBase + "Delete")
			.Select(d => d.Element(AirSyncBase + "FileReference")?.Value ?? "")
			.Select(ParseAttachmentIndex)
			.Where(i => i >= 0 && i < binaries.Count)
			.OrderDescending()
			.ToList();
		foreach (int index in deleteIndexes)
			evt.Attachments.Remove(binaries[index]);

		foreach (XElement add in attachments.Elements(AirSyncBase + "Add"))
		{
			string? content = add.Element(AirSyncBase + "Content")?.Value;
			if (content is null)
				continue;
			byte[] bytes;
			try
			{
				bytes = Convert.FromBase64String(content);
			}
			catch (FormatException)
			{
				throw new BackendException("An event attachment carried invalid base64 content.");
			}

			if (bytes.Length > capBytes)
				throw new BackendException(
					$"Event attachment exceeds the configured limit ({capBytes} bytes; see CalendarAttachments).");

			Attachment attachment = new(bytes);
			string? contentType = add.Element(AirSyncBase + "ContentType")?.Value;
			if (!string.IsNullOrWhiteSpace(contentType))
				attachment.FormatType = contentType;
			string? displayName = add.Element(AirSyncBase + "DisplayName")?.Value;
			if (!string.IsNullOrWhiteSpace(displayName))
				attachment.Parameters.Add(FileNameParameter, displayName);
			evt.Attachments.Add(attachment);
		}
	}

	/// <summary>The inline index from "calatt::…::N" or the emission-time "calatt::N".</summary>
	public static int ParseAttachmentIndex(string fileReference)
	{
		int lastSeparator = fileReference.LastIndexOf("::", StringComparison.Ordinal);
		return lastSeparator >= 0 && int.TryParse(fileReference[(lastSeparator + 2)..], out int index)
			? index
			: -1;
	}

	// ---------- free/busy (ResolveRecipients Availability) ----------

	/// <summary>
	///   Parses free-busy-query output (VFREEBUSY) into EAS busy periods. Hand-parsed at the
	///   line level: Ical.Net 5.x does not deserialize the FREEBUSY property (its value comes
	///   back null), and the format is trivially simple — unfold, take FREEBUSY lines, read
	///   the FBTYPE parameter and the comma-separated "start/end" or "start/duration" periods.
	/// </summary>
	public static IReadOnlyList<BusyPeriod> ParseFreeBusy(string ics)
	{
		List<BusyPeriod> result = new();
		string unfolded = ics.Replace("\r\n ", "").Replace("\r\n\t", "").Replace("\n ", "").Replace("\n\t", "");
		foreach (string rawLine in unfolded.Split('\n'))
		{
			string line = rawLine.TrimEnd('\r');
			if (!line.StartsWith("FREEBUSY", StringComparison.OrdinalIgnoreCase))
				continue;
			int colon = line.IndexOf(':');
			if (colon < 0)
				continue;

			string parameters = line[..colon];
			char kind = '2'; // FBTYPE defaults to BUSY (RFC 5545 §3.2.9)
			if (parameters.Contains("BUSY-TENTATIVE", StringComparison.OrdinalIgnoreCase))
				kind = '1';
			else if (parameters.Contains("BUSY-UNAVAILABLE", StringComparison.OrdinalIgnoreCase))
				kind = '3';
			else if (parameters.Contains("FBTYPE=FREE", StringComparison.OrdinalIgnoreCase))
				continue;

			foreach (string period in line[(colon + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries))
			{
				string[] parts = period.Trim().Split('/');
				if (parts.Length != 2)
					continue;
				DateTime start, end;
				try
				{
					start = EasDateTime.Parse(parts[0]);
					// The second half is either an end time or an ISO 8601 duration.
					end = parts[1].StartsWith('P') || parts[1].StartsWith("+P", StringComparison.Ordinal)
						? start + System.Xml.XmlConvert.ToTimeSpan(parts[1].TrimStart('+'))
						: EasDateTime.Parse(parts[1]);
				}
				catch (FormatException)
				{
					continue; // a malformed period must not sink the whole answer
				}

				if (end > start)
					result.Add(new BusyPeriod(start, end, kind));
			}
		}

		return result;
	}

	/// <summary>
	///   Busy periods from stored events (the local calendar store's free/busy source):
	///   occurrences are expanded within the window; TRANSPARENT events do not block time.
	/// </summary>
	public static IReadOnlyList<BusyPeriod> BusyPeriodsFromEvents(
		IEnumerable<string> icsContents, DateTime startUtc, DateTime endUtc)
	{
		List<BusyPeriod> result = new();
		foreach (string ics in icsContents)
		{
			Calendar? calendar;
			try
			{
				calendar = Calendar.Load(ics);
			}
			catch (Exception)
			{
				continue; // an unparsable stored event must not sink the whole answer
			}

			foreach (CalendarEvent evt in calendar?.Events ?? Enumerable.Empty<CalendarEvent>())
			{
				if (string.Equals(evt.Transparency, "TRANSPARENT", StringComparison.OrdinalIgnoreCase))
					continue;
				// GetOccurrences is lazy and unbounded for open-ended recurrences — the
				// TakeWhile on the window end is what terminates it.
				foreach (Occurrence occurrence in evt
					         .GetOccurrences(new CalDateTime(startUtc, "UTC"))
					         .TakeWhile(o => ToUtc(o.Period.StartTime) < endUtc))
				{
					DateTime? start = ToUtc(occurrence.Period.StartTime);
					DateTime? end = ToUtc(occurrence.Period.EffectiveEndTime ?? occurrence.Period.EndTime);
					if (start is null)
						continue;
					result.Add(new BusyPeriod(start.Value, end ?? start.Value.AddMinutes(30), '2'));
				}
			}
		}

		return result;
	}

	/// <summary>Fetches one inline attachment by index (ItemOperations Fetch of calatt: references).</summary>
	public static BackendAttachment? ExtractAttachment(string ics, int index)
	{
		Calendar? calendar = Calendar.Load(ics);
		CalendarEvent? master = calendar?.Events.FirstOrDefault(e => e.RecurrenceId is null)
		                        ?? calendar?.Events.FirstOrDefault();
		List<Attachment> binaries = master?.Attachments?
			.Where(a => a?.Data is { Length: > 0 }).ToList() ?? [];
		if (index < 0 || index >= binaries.Count)
			return null;
		return new BackendAttachment(
			binaries[index].FormatType ?? "application/octet-stream", binaries[index].Data!);
	}

	private static CalendarEvent AddNewEvent(Calendar calendar)
	{
		CalendarEvent evt = new();
		calendar.Events.Add(evt);
		return evt;
	}

	private static DateTime LocalDate(DateTime utc, TimeSpan? offset)
	{
		return (offset is { } o ? utc + o : utc).Date;
	}

	private static RecurrencePattern? ParseRecurrence(XElement recurrence)
	{
		string? V(string localName)
		{
			return recurrence.Element(Cal + localName)?.Value;
		}

		int type = int.TryParse(V("Type"), out int t) ? t : -1;

		RecurrencePattern pattern = new();
		switch (type)
		{
			case 0:
				pattern.Frequency = FrequencyType.Daily;
				break;
			case 1:
				pattern.Frequency = FrequencyType.Weekly;
				if (int.TryParse(V("DayOfWeek"), out int weeklyMask))
					pattern.ByDay = MaskToDays(weeklyMask).Select(d => new WeekDay(d)).ToList();
				break;
			case 2:
				pattern.Frequency = FrequencyType.Monthly;
				if (int.TryParse(V("DayOfMonth"), out int dom))
					pattern.ByMonthDay = [dom];
				break;
			case 3:
				pattern.Frequency = FrequencyType.Monthly;
				ApplyNthDay(pattern);
				break;
			case 5:
				pattern.Frequency = FrequencyType.Yearly;
				if (int.TryParse(V("DayOfMonth"), out int ydom))
					pattern.ByMonthDay = [ydom];
				if (int.TryParse(V("MonthOfYear"), out int ymonth))
					pattern.ByMonth = [ymonth];
				break;
			case 6:
				pattern.Frequency = FrequencyType.Yearly;
				ApplyNthDay(pattern);
				if (int.TryParse(V("MonthOfYear"), out int nmonth))
					pattern.ByMonth = [nmonth];
				break;
			default:
				return null;
		}

		if (int.TryParse(V("Interval"), out int interval) && interval > 1)
			pattern.Interval = interval;
		if (int.TryParse(V("Occurrences"), out int occurrences) && occurrences > 0)
			pattern.Count = occurrences;
		else if (V("Until") is { } until)
			pattern.Until = new CalDateTime(EasDateTime.Parse(until), "UTC");
		return pattern;

		void ApplyNthDay(RecurrencePattern p)
		{
			int week = int.TryParse(V("WeekOfMonth"), out int w) ? w : 1;
			int offset = week == 5 ? -1 : week;
			if (int.TryParse(V("DayOfWeek"), out int mask))
				p.ByDay = MaskToDays(mask).Select(d => new WeekDay(d, offset)).ToList();
		}
	}

	/// <summary>Updates the user's PARTSTAT for a MeetingResponse (1=accept, 2=tentative, 3=decline).</summary>
	public static string? SetPartStat(string ics, int userResponse, string userEmail)
	{
		Calendar? calendar = Calendar.Load(ics);
		CalendarEvent? evt = calendar?.Events.FirstOrDefault();
		if (calendar is null || evt is null)
			return null;

		string partStat = userResponse switch
		{
			1 => "ACCEPTED",
			2 => "TENTATIVE",
			3 => "DECLINED",
			_ => "NEEDS-ACTION"
		};

		bool updated = false;
		foreach (Attendee attendee in evt.Attendees ?? [])
		{
			string? email = attendee.Value?.ToString();
			// Exact mailbox comparison — substring matching would let ann@example.com
			// update joann@example.com's participation status.
			if (email is not null && MailboxEquals(email, userEmail))
			{
				attendee.ParticipationStatus = partStat;
				updated = true;
			}
		}

		if (!updated)
			return null;
		return new CalendarSerializer().SerializeToString(calendar);
	}

	private static bool MailboxEquals(string a, string b)
	{
		static string Normalize(string value)
		{
			string trimmed = value.Trim();
			return trimmed.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
				? trimmed["mailto:".Length..]
				: trimmed;
		}

		return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
	}

	private static TimeZoneInfo? ResolveTimeZone(string? tzId)
	{
		if (string.IsNullOrEmpty(tzId) || tzId.Equals("UTC", StringComparison.OrdinalIgnoreCase))
			return null;
		try
		{
			return TimeZoneInfo.FindSystemTimeZoneById(tzId);
		}
		catch (TimeZoneNotFoundException)
		{
			return null;
		}
		catch (InvalidTimeZoneException)
		{
			return null;
		}
	}

	private static DateTime? ToUtc(CalDateTime? value)
	{
		return value?.AsUtc;
	}
}

using ActiveSync.Contracts;
using ActiveSync.Contracts.Interop;
using ActiveSync.Protocol;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;

// EAS 14.1 expresses at most one recurrence rule per event and identifies exceptions by their
// original start time, so the obsolete single-value RecurrenceId surface of Ical.Net matches what
// the protocol can carry (scoped CS0618 suppressions below, one per call site, rather than a
// file-wide one).

namespace ActiveSync.Backends.Common.Converters;

/// <summary>
///   What a calendar STORE needs from an iCalendar payload it already owns — no EAS anywhere.
///   Under the typed item currency the payload is the currency, so these are the operations a
///   store performs on its own data to fulfil the contract: name a resource from the document's
///   UID, answer <c>ICalendarAttachmentSource</c> by index, stamp a PARTSTAT for
///   <c>IMeetingOperations</c>, and produce <c>IFreeBusySource</c> periods from either a
///   free-busy REPORT or the stored events. The EAS half of the old calendar converter — the
///   ApplicationData read/write and the ghosting merge — is host-side, in ActiveSync.Eas.Conversion.
/// </summary>
public static class CalendarPayload
{
	/// <summary>The master event's UID, used to name the stored resource after its own document.</summary>
	public static string? ExtractUid(string ics)
	{
		Calendar? calendar = Calendar.Load(ics);
		return calendar?.Events.FirstOrDefault()?.Uid;
	}

	/// <summary>
	///   Updates the user's PARTSTAT for a MeetingResponse (1=accept, 2=tentative, 3=decline);
	///   null when the ICS carries no event or the user is not among its attendees.
	/// </summary>
	public static string? SetPartStat(string ics, int userResponse, string userEmail)
	{
		Calendar? calendar = Calendar.Load(ics);
		// Master-first, matching every other entry point here — an ICS that lists a
		// modified-occurrence override before the master VEVENT must still update the master's
		// attendee, or the acceptance is lost for the series the next time anything reads the
		// stored (master) PARTSTAT.
#pragma warning disable CS0618 // obsolete single-value RecurrenceId (see file header note)
		CalendarEvent? evt = calendar?.Events.FirstOrDefault(e => e.RecurrenceId is null)
			?? calendar?.Events.FirstOrDefault();
#pragma warning restore CS0618
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
		return IcalHelpers.Serialize(calendar);
	}

	/// <summary>
	///   One inline attachment by index, for <c>ICalendarAttachmentSource</c>. The index is
	///   normatively the Nth binary ATTACH property of the payload the store itself handed over,
	///   so a store can always resolve it from its own data.
	/// </summary>
	public static BackendAttachment? ExtractAttachment(string ics, int index)
	{
		Calendar? calendar = Calendar.Load(ics);
#pragma warning disable CS0618 // obsolete single-value RecurrenceId (see file header note)
		CalendarEvent? master = calendar?.Events.FirstOrDefault(e => e.RecurrenceId is null)
		                        ?? calendar?.Events.FirstOrDefault();
#pragma warning restore CS0618
		List<Attachment> binaries = master?.Attachments?
			.Where(a => a?.Data is { Length: > 0 }).ToList() ?? [];
		if (index < 0 || index >= binaries.Count)
			return null;
		return new BackendAttachment
		{
			ContentType = binaries[index].FormatType ?? "application/octet-stream",
			Content = binaries[index].Data!
		};
	}

	/// <summary>
	///   Parses free-busy-query output (VFREEBUSY) into contract busy periods. Hand-parsed at the
	///   line level: Ical.Net 5.x does not deserialize the FREEBUSY property (its value comes
	///   back null), and the format is trivially simple — unfold, take FREEBUSY lines, read
	///   the FBTYPE parameter and the comma-separated "start/end" or "start/duration" periods.
	///   Do not "simplify" this back onto Ical.Net without checking that bug is fixed.
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
			// Find the FBTYPE parameter by NAME (split on ';', match the key before '='),
			// not by substring-scanning the whole segment — an unrelated parameter (or a TZID)
			// whose value merely contains "BUSY-TENTATIVE"/etc. anywhere must not be misread as
			// FBTYPE.
			string? fbtype = null;
			foreach (string segment in parameters.Split(';', StringSplitOptions.RemoveEmptyEntries))
			{
				int eq = segment.IndexOf('=');
				if (eq >= 0 && segment[..eq].Equals("FBTYPE", StringComparison.OrdinalIgnoreCase))
				{
					fbtype = segment[(eq + 1)..];
					break;
				}
			}

			BusyKind kind = BusyKind.Busy; // FBTYPE defaults to BUSY (RFC 5545 §3.2.9)
			if (string.Equals(fbtype, "BUSY-TENTATIVE", StringComparison.OrdinalIgnoreCase))
				kind = BusyKind.Tentative;
			else if (string.Equals(fbtype, "BUSY-UNAVAILABLE", StringComparison.OrdinalIgnoreCase))
				kind = BusyKind.OutOfOffice;
			else if (string.Equals(fbtype, "FREE", StringComparison.OrdinalIgnoreCase))
				continue;

			foreach (string period in line[(colon + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries))
			{
				string[] parts = period.Trim().Split('/');
				if (parts.Length != 2)
					continue;
				// An iCalendar UTC date-time is byte-identical to the EAS compact form, so the
				// one parser serves both — the single remaining Protocol call on this side of
				// the split, kept because swapping in a second parser during a relocation would
				// risk a behaviour change for no gain.
				if (!EasDateTime.TryParse(parts[0], out DateTime start))
					continue; // a malformed period must not sink the whole answer
				DateTime end;
				// The second half is either an end time or an ISO 8601 duration.
				if (parts[1].StartsWith('P') || parts[1].StartsWith("+P", StringComparison.Ordinal))
				{
					try
					{
						end = start + System.Xml.XmlConvert.ToTimeSpan(parts[1].TrimStart('+'));
					}
					catch (FormatException)
					{
						continue;
					}
				}
				else if (!EasDateTime.TryParse(parts[1], out end))
				{
					continue;
				}

				if (end > start)
					result.Add(new BusyPeriod { Start = AsUtcOffset(start), End = AsUtcOffset(end), Kind = kind });
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
					         .TakeWhile(o => ToUtc(o.Period.StartTime) is not { } s || s < endUtc))
				{
					DateTime? start = ToUtc(occurrence.Period.StartTime);
					DateTime? end = ToUtc(occurrence.Period.EffectiveEndTime ?? occurrence.Period.EndTime);
					if (start is null)
						continue;
					result.Add(new BusyPeriod
					{
						Start = AsUtcOffset(start.Value),
						End = AsUtcOffset(end ?? start.Value.AddMinutes(30)),
						Kind = BusyKind.Busy
					});
				}
			}
		}

		return result;
	}

	/// <summary>
	///   Wraps a UTC instant as a <see cref="DateTimeOffset" /> for the contract's busy periods.
	///   The Kind is FORCED to UTC rather than trusted: the sources here (date-time parsing,
	///   Ical.Net occurrence expansion) yield UTC values whose Kind is sometimes Unspecified, and
	///   the DateTimeOffset(DateTime, TimeSpan) constructor throws on a Local-kinded input.
	/// </summary>
	private static DateTimeOffset AsUtcOffset(DateTime utc)
	{
		return new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
	}

	private static DateTime? ToUtc(CalDateTime? value)
	{
		return value?.AsUtc;
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
}

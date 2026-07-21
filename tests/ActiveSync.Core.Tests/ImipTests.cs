using System.Xml.Linq;
using ActiveSync.Backends.Converters;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;
using MimeKit;

namespace ActiveSync.Core.Tests;

/// <summary>
///   iMIP groundwork: attendee parsing with PARTSTAT preservation, organizer injection,
///   SEQUENCE bumping, the scheduling-info reader/differ and the REQUEST/CANCEL mail shapes.
/// </summary>
public class ImipTests
{
	private static readonly XNamespace Cal = EasNamespaces.Calendar;

	private static XElement Meeting(string subject, DateTime startUtc, params string[] attendees)
	{
		XElement data = new("ApplicationData",
			new XElement(Cal + "Subject", subject),
			new XElement(Cal + "StartTime", EasDateTime.ToCompact(startUtc)),
			new XElement(Cal + "EndTime", EasDateTime.ToCompact(startUtc.AddHours(1))),
			new XElement(Cal + "BusyStatus", "2"));
		if (attendees.Length > 0)
			data.Add(new XElement(Cal + "Attendees",
				attendees.Select(a => new XElement(Cal + "Attendee",
					new XElement(Cal + "Email", a),
					new XElement(Cal + "Name", a)))));
		return data;
	}

	[Fact]
	public void Attendees_AreParsed_AndOrganizerInjected()
	{
		string ics = CalendarConverter.FromApplicationData(
			Meeting("Standup", new DateTime(2026, 8, 3, 9, 0, 0, DateTimeKind.Utc), "bob@example.com"),
			Guid.NewGuid().ToString(), null, null, "alice@example.com");

		Assert.Contains("ORGANIZER:mailto:alice@example.com", ics);
		Assert.Contains("mailto:bob@example.com", ics);
		Assert.Contains("NEEDS-ACTION", ics);

		CalendarConverter.SchedulingInfo? info = CalendarConverter.ReadSchedulingInfo(ics);
		Assert.NotNull(info);
		Assert.Equal("alice@example.com", info.Organizer);
		Assert.Equal("bob@example.com", Assert.Single(info.Attendees).Email);
		Assert.Equal(0, info.Sequence);
	}

	[Fact]
	public void AttendeeMerge_PreservesPartstat_AndOmittedElementLeavesAttendees()
	{
		string uid = Guid.NewGuid().ToString();
		DateTime start = new(2026, 8, 3, 9, 0, 0, DateTimeKind.Utc);
		string ics = CalendarConverter.FromApplicationData(
			Meeting("Standup", start, "bob@example.com"), uid, null, null, "alice@example.com");
		// The attendee accepted out-of-band (iTIP REPLY applied by SetPartStat).
		string accepted = CalendarConverter.SetPartStat(ics, 1, "bob@example.com")!;

		// The client re-sends the full attendee list plus a newcomer: Bob's ACCEPTED must
		// survive, Carol starts NEEDS-ACTION.
		string updated = CalendarConverter.FromApplicationData(
			Meeting("Standup", start, "bob@example.com", "carol@example.com"),
			uid, accepted, null, "alice@example.com");
		CalendarConverter.SchedulingInfo info = CalendarConverter.ReadSchedulingInfo(updated)!;
		Assert.Equal(2, info.Attendees.Count);
		Assert.Contains("PARTSTAT=ACCEPTED", updated);
		Assert.Contains("carol@example.com", updated);

		// A ghosted Change without the Attendees element keeps everyone.
		string ghosted = CalendarConverter.FromApplicationData(
			new XElement("ApplicationData", new XElement(Cal + "Subject", "Standup renamed")),
			uid, updated, null, "alice@example.com");
		Assert.Equal(2, CalendarConverter.ReadSchedulingInfo(ghosted)!.Attendees.Count);
	}

	[Fact]
	public void Sequence_BumpsOnTimeChange_NotOnReminderChange()
	{
		string uid = Guid.NewGuid().ToString();
		DateTime start = new(2026, 8, 3, 9, 0, 0, DateTimeKind.Utc);
		string ics = CalendarConverter.FromApplicationData(
			Meeting("Standup", start, "bob@example.com"), uid, null, null, "alice@example.com");
		Assert.Equal(0, CalendarConverter.ReadSchedulingInfo(ics)!.Sequence);

		string reminderOnly = CalendarConverter.FromApplicationData(
			new XElement("ApplicationData", new XElement(Cal + "Reminder", "15")),
			uid, ics, null, "alice@example.com");
		Assert.Equal(0, CalendarConverter.ReadSchedulingInfo(reminderOnly)!.Sequence);
		Assert.False(CalendarConverter.SchedulingSignificantlyDiffers(ics, reminderOnly));

		string moved = CalendarConverter.FromApplicationData(
			Meeting("Standup", start.AddHours(2), "bob@example.com"),
			uid, reminderOnly, null, "alice@example.com");
		Assert.Equal(1, CalendarConverter.ReadSchedulingInfo(moved)!.Sequence);
		Assert.True(CalendarConverter.SchedulingSignificantlyDiffers(reminderOnly, moved));
	}

	[Fact]
	public void BuildRequest_WrapsEvent_WithMethodAndWithoutAlarms()
	{
		string uid = Guid.NewGuid().ToString();
		XElement data = Meeting("Planning", new DateTime(2026, 8, 4, 10, 0, 0, DateTimeKind.Utc),
			"bob@example.com");
		data.Add(new XElement(Cal + "Reminder", "30")); // personal — must not travel
		string ics = CalendarConverter.FromApplicationData(
			data, uid, null, null, "alice@example.com");

		MimeMessage request = ImipMailBuilder.BuildRequest(
			ics, "alice@example.com", [("bob@example.com", "Bob")], "Invitation: Planning");

		Assert.Equal("Invitation: Planning", request.Subject);
		Assert.Equal("bob@example.com", request.To.Mailboxes.Single().Address);
		string body = Assert.IsType<TextPart>(request.Body).Text!;
		Assert.Contains("METHOD:REQUEST", body);
		Assert.Contains($"UID:{uid}", body);
		Assert.Contains("mailto:bob@example.com", body);
		Assert.DoesNotContain("VALARM", body);
		Assert.Equal("REQUEST", request.Body.ContentType.Parameters["method"]);
	}

	[Fact]
	public void BuildCancel_CarriesSequence_AndOptionalRecurrenceId()
	{
		MimeMessage whole = ImipMailBuilder.BuildCancel(
			"uid-1", 3, "alice@example.com", [("bob@example.com", null)], null, "Cancelled: X");
		string wholeBody = Assert.IsType<TextPart>(whole.Body).Text!;
		Assert.Contains("METHOD:CANCEL", wholeBody);
		Assert.Contains("SEQUENCE:3", wholeBody);
		Assert.Contains("STATUS:CANCELLED", wholeBody);
		Assert.DoesNotContain("RECURRENCE-ID", wholeBody);

		MimeMessage occurrence = ImipMailBuilder.BuildCancel(
			"uid-1", 4, "alice@example.com", [("bob@example.com", null)],
			new DateTime(2026, 8, 5, 9, 0, 0, DateTimeKind.Utc), "Cancelled occurrence: X");
		Assert.Contains("RECURRENCE-ID:20260805T090000Z", Assert.IsType<TextPart>(occurrence.Body).Text);
	}

	[Fact]
	public void BuildCancel_UsesCrlf_NotThePlatformLineEnding()
	{
		// D7 — StringBuilder.AppendLine emits Environment.NewLine: bare LF on the Linux
		// containers this ships in, while RFC 5545 mandates CRLF. Strict iTIP consumers
		// reject the result. NOTE: this is COVERAGE, not a reproducer — on Windows
		// Environment.NewLine is already CRLF, so it passes against the unfixed code too. It
		// guards the Ical.Net serializer (which always emits CRLF) against a regression to
		// hand-built AppendLine, which only bites on the Linux containers.
		string body = Assert.IsType<TextPart>(ImipMailBuilder.BuildCancel(
			"uid-2", 1, "alice@example.com", [("bob@example.com", null)], null, "Cancelled: X").Body).Text!;

		Assert.DoesNotContain("\n", body.Replace("\r\n", ""));
	}

	[Fact]
	public void BuildCancel_CannotInjectIcalendarProperties()
	{
		// D7 — uid/organizer/attendee were interpolated unescaped and unfolded. For a
		// DAV-backed event the UID comes from client ApplicationData, so a crafted value
		// injected arbitrary properties (an extra ATTENDEE, a METHOD override) into an
		// outbound iTIP message.
		string body = Assert.IsType<TextPart>(ImipMailBuilder.BuildCancel(
			"uid-3\r\nATTENDEE:mailto:attacker@evil.example\r\nMETHOD:REQUEST\r\nX-INJECTED:pwned",
			1, "alice@example.com", [("bob@example.com", null)], null, "Cancelled: X").Body).Text!;

		// The property is structural, not textual: the crafted text may legitimately appear
		// escaped INSIDE the UID value (Ical.Net writes it as a literal \n), but it must never
		// become a line of its own.
		string[] lines = body.Replace("\r\n ", "").Split("\r\n");
		Assert.Equal("ATTENDEE:mailto:bob@example.com", Assert.Single(lines, l => l.StartsWith("ATTENDEE:")));
		Assert.Equal("METHOD:CANCEL", Assert.Single(lines, l => l.StartsWith("METHOD:")));
		Assert.DoesNotContain(lines, l => l.StartsWith("X-INJECTED"));
	}
}

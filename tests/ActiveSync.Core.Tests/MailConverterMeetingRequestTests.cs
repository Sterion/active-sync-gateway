using System.Xml.Linq;
using ActiveSync.Backends.Common.Converters;
using ActiveSync.Contracts;
using ActiveSync.Protocol.Wbxml;
using MimeKit;

namespace ActiveSync.Core.Tests;

/// <summary>
///   D5: meeting-request (iTIP) times must honour the DTSTART/DTEND TZID parameter and folded
///   iCalendar lines, not treat every non-Z value as UTC or truncate a folded property.
/// </summary>
public class MailConverterMeetingRequestTests
{
	private static readonly XNamespace Email = EasNamespaces.Email;

	private static MimeMessage MeetingMessage(string ics)
	{
		MimeMessage message = new();
		message.From.Add(MailboxAddress.Parse("organizer@example.com"));
		message.To.Add(MailboxAddress.Parse("attendee@example.com"));
		message.Subject = "Invitation";
		message.Date = new DateTimeOffset(2025, 5, 20, 8, 0, 0, TimeSpan.Zero);

		Multipart mixed = new("mixed");
		mixed.Add(new TextPart("plain") { Text = "You are invited." });
		MimePart cal = new("text", "calendar")
		{
			Content = new MimeContent(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(ics)))
		};
		mixed.Add(cal);
		message.Body = mixed;
		return message;
	}

	private static XElement Convert(MimeMessage message)
	{
		List<XElement> data = MailConverter.ToApplicationData(
			message,
			new MailConverter.MessageFlags(true, false, false, false, null),
			new BodyPreference(1, null, false),
			_ => "ref");
		return data.First(e => e.Name == Email + "MeetingRequest");
	}

	[Fact]
	public void MeetingRequest_TzidStart_ConvertedToUtc()
	{
		// 09:00 Europe/Copenhagen on 2025-06-01 is CEST (+02:00) → 07:00 UTC.
		string ics =
			"BEGIN:VCALENDAR\r\nMETHOD:REQUEST\r\nBEGIN:VEVENT\r\nUID:abc\r\n" +
			"DTSTART;TZID=Europe/Copenhagen:20250601T090000\r\n" +
			"DTEND;TZID=Europe/Copenhagen:20250601T100000\r\n" +
			"SUMMARY:Sync\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

		XElement mr = Convert(MeetingMessage(ics));

		Assert.Equal("2025-06-01T07:00:00.000Z", mr.Element(Email + "StartTime")!.Value);
		Assert.Equal("2025-06-01T08:00:00.000Z", mr.Element(Email + "EndTime")!.Value);
		Assert.Equal("0", mr.Element(Email + "AllDayEvent")!.Value);
	}

	[Fact]
	public void MeetingRequest_FoldedLocation_NotTruncated()
	{
		// A LOCATION folded across two lines (RFC 5545 §3.1) must be unfolded, not truncated
		// at the fold. The continuation line starts with a single space.
		string ics =
			"BEGIN:VCALENDAR\r\nMETHOD:REQUEST\r\nBEGIN:VEVENT\r\nUID:abc\r\n" +
			"DTSTART:20250601T090000Z\r\n" +
			"LOCATION:Big Conference Room on the\r\n  Fourth Floor\r\n" +
			"END:VEVENT\r\nEND:VCALENDAR\r\n";

		XElement mr = Convert(MeetingMessage(ics));

		Assert.Equal("Big Conference Room on the Fourth Floor", mr.Element(Email + "Location")!.Value);
	}

	[Fact]
	public void MeetingRequest_BareZStart_StillUtc()
	{
		string ics =
			"BEGIN:VCALENDAR\r\nMETHOD:REQUEST\r\nBEGIN:VEVENT\r\nUID:abc\r\n" +
			"DTSTART:20250601T090000Z\r\nSUMMARY:Sync\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

		XElement mr = Convert(MeetingMessage(ics));

		Assert.Equal("2025-06-01T09:00:00.000Z", mr.Element(Email + "StartTime")!.Value);
	}

	// D30 — coverage, not red-first proof: MailConverter's ORGANIZER read repeated the same
	// substring-Replace("mailto:", "") pattern as CalendarConverter's organizer/attendee reads
	// (CalendarConverterTests.ToApplicationData_OrganizerEmail_OnlyStripsTheLeadingMailtoPrefix is
	// the red-first proof for the shared defect/fix — CalendarConverter.StripMailto). Both call
	// sites were fixed in the same change (route through the same prefix-strip helper), so there
	// is no way to observe THIS site fail independently without reverting the already-landed fix,
	// which the protocol forbids. This pins that the third call site got the same treatment.
	[Fact]
	public void MeetingRequest_OrganizerEmail_OnlyStripsTheLeadingMailtoPrefix()
	{
		string ics =
			"BEGIN:VCALENDAR\r\nMETHOD:REQUEST\r\nBEGIN:VEVENT\r\nUID:abc\r\n" +
			"DTSTART:20250601T090000Z\r\n" +
			"ORGANIZER:mailto:mailto:boss@example.com\r\n" +
			"SUMMARY:Sync\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

		XElement mr = Convert(MeetingMessage(ics));

		Assert.Equal("mailto:boss@example.com", mr.Element(Email + "Organizer")!.Value);
	}

	// D1 — Outlook/Google/Exchange all emit BEGIN:VTIMEZONE before BEGIN:VEVENT, and its
	// STANDARD/DAYLIGHT subcomponents each carry a bare (no-Z, no-TZID) DTSTART for the 1970
	// DST transition. Prop() scanned the whole ICS from the top and returned that line instead
	// of the real VEVENT DTSTART, showing the phone a meeting starting in 1970.
	[Fact]
	public void MeetingRequest_LeadingVTimezone_DoesNotShadowVeventDtstart()
	{
		string ics =
			"BEGIN:VCALENDAR\r\nMETHOD:REQUEST\r\n" +
			"BEGIN:VTIMEZONE\r\nTZID:Europe/Copenhagen\r\n" +
			"BEGIN:DAYLIGHT\r\nDTSTART:19700329T020000\r\nTZOFFSETFROM:+0100\r\nTZOFFSETTO:+0200\r\nEND:DAYLIGHT\r\n" +
			"BEGIN:STANDARD\r\nDTSTART:19701025T030000\r\nTZOFFSETFROM:+0200\r\nTZOFFSETTO:+0100\r\nEND:STANDARD\r\n" +
			"END:VTIMEZONE\r\n" +
			"BEGIN:VEVENT\r\nUID:abc\r\n" +
			"DTSTART;TZID=Europe/Copenhagen:20250601T090000\r\n" +
			"DTEND;TZID=Europe/Copenhagen:20250601T100000\r\n" +
			"SUMMARY:Sync\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

		XElement mr = Convert(MeetingMessage(ics));

		Assert.Equal("2025-06-01T07:00:00.000Z", mr.Element(Email + "StartTime")!.Value);
		Assert.Equal("2025-06-01T08:00:00.000Z", mr.Element(Email + "EndTime")!.Value);
	}
}

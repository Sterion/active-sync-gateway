using System.Xml.Linq;
using ActiveSync.Backends.Converters;
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
}

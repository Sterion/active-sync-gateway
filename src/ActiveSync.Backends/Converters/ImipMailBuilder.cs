using System.Text;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.Serialization;
using MimeKit;

namespace ActiveSync.Backends.Converters;

/// <summary>
///   iMIP (RFC 6047) mail assembly: METHOD:REQUEST wraps the stored event verbatim (minus
///   personal VALARMs), METHOD:CANCEL and METHOD:REPLY are minimal hand-built payloads.
///   All three ride the same text/calendar MIME shape.
/// </summary>
public static class ImipMailBuilder
{
	/// <summary>An invitation/update: the stored event ICS re-labeled METHOD:REQUEST.</summary>
	public static MimeMessage BuildRequest(
		string eventIcs, string organizer, IReadOnlyList<(string Email, string? Name)> recipients,
		string subject)
	{
		Calendar calendar = Calendar.Load(eventIcs)
			?? throw new InvalidOperationException("Stored event is not parsable iCalendar.");
		calendar.Method = "REQUEST";
		// VALARMs are the organizer's personal reminders, never part of an invitation.
		foreach (CalendarEvent evt in calendar.Events)
			evt.Alarms.Clear();
		string ics = new CalendarSerializer().SerializeToString(calendar);
		return Compose(organizer, recipients, subject, "REQUEST", ics);
	}

	/// <summary>A cancellation — of the whole meeting, or one occurrence via <paramref name="recurrenceIdUtc" />.</summary>
	public static MimeMessage BuildCancel(
		string uid, int sequence, string organizer, IReadOnlyList<(string Email, string? Name)> recipients,
		DateTime? recurrenceIdUtc, string subject)
	{
		StringBuilder ics = new StringBuilder()
			.AppendLine("BEGIN:VCALENDAR")
			.AppendLine("PRODID:-//ActiveSync Gateway//EN")
			.AppendLine("VERSION:2.0")
			.AppendLine("METHOD:CANCEL")
			.AppendLine("BEGIN:VEVENT")
			.AppendLine($"UID:{uid}")
			.AppendLine($"DTSTAMP:{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}")
			.AppendLine($"SEQUENCE:{sequence}")
			.AppendLine("STATUS:CANCELLED")
			.AppendLine($"ORGANIZER:mailto:{organizer}");
		if (recurrenceIdUtc is { } occurrence)
			ics.AppendLine($"RECURRENCE-ID:{occurrence:yyyyMMdd'T'HHmmss'Z'}");
		foreach ((string email, string? _) in recipients)
			ics.AppendLine($"ATTENDEE:mailto:{email}");
		ics.AppendLine("END:VEVENT")
			.AppendLine("END:VCALENDAR");
		return Compose(organizer, recipients, subject, "CANCEL", ics.ToString());
	}

	/// <summary>
	///   The shared text/calendar MIME shape (also used by MeetingResponse's iTIP REPLY):
	///   From = the acting user, method parameter on the content type.
	/// </summary>
	public static MimeMessage Compose(
		string from, IReadOnlyList<(string Email, string? Name)> recipients, string subject,
		string method, string ics)
	{
		MimeMessage message = new();
		message.From.Add(new MailboxAddress(from, from));
		foreach ((string email, string? name) in recipients)
			message.To.Add(new MailboxAddress(name ?? email, email));
		message.Subject = subject;
		message.Body = new TextPart("calendar")
		{
			Text = ics,
			ContentType = { Parameters = { { "method", method } } }
		};
		return message;
	}
}

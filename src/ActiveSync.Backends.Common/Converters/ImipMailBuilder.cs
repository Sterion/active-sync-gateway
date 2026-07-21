using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
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
		string ics = new CalendarSerializer().SerializeToString(calendar)
			?? throw new InvalidOperationException("Serializing the invitation produced no output.");
		return Compose(organizer, recipients, subject, "REQUEST", ics);
	}

	/// <summary>
	///   A cancellation — of the whole meeting, or one occurrence via
	///   <paramref name="recurrenceIdUtc" />. Serialized through Ical.Net exactly as
	///   <see cref="BuildRequest" /> is: the serializer escapes and folds every value, so a
	///   <paramref name="uid" /> carrying a newline (it comes from client ApplicationData for
	///   DAV-backed events) cannot inject properties into an outbound iTIP message, and line
	///   endings are the CRLF RFC 5545 mandates rather than the platform's.
	/// </summary>
	public static MimeMessage BuildCancel(
		string uid, int sequence, string organizer, IReadOnlyList<(string Email, string? Name)> recipients,
		DateTime? recurrenceIdUtc, string subject)
	{
		CalendarEvent evt = new()
		{
			Uid = uid,
			DtStamp = new CalDateTime(DateTime.UtcNow, "UTC"),
			Sequence = sequence,
			Status = "CANCELLED",
			Organizer = new Organizer($"mailto:{organizer}")
		};
		if (recurrenceIdUtc is { } occurrence)
			evt.RecurrenceIdentifier = new RecurrenceIdentifier(new CalDateTime(occurrence, "UTC"));
		foreach ((string email, string? _) in recipients)
			evt.Attendees.Add(new Attendee($"mailto:{email}"));

		Calendar calendar = new() { Method = "CANCEL", ProductId = "-//ActiveSync Gateway//EN" };
		calendar.Events.Add(evt);
		string ics = new CalendarSerializer().SerializeToString(calendar)
			?? throw new InvalidOperationException("Serializing the cancellation produced no output.");
		return Compose(organizer, recipients, subject, "CANCEL", ics);
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

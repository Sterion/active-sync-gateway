using ActiveSync.Contracts;
using Ical.Net;
using Ical.Net.Serialization;

namespace ActiveSync.Backends.Common.Converters;

/// <summary>Ical.Net load/serialize boilerplate shared by the calendar, task and note converters.</summary>
public static class IcalHelpers
{
	/// <summary>
	///   Loads an iCalendar document. Empty/null content produces a fresh empty calendar (the
	///   merge then starts from nothing, same as a create); genuinely unparsable content throws
	///   <see cref="BackendException" /> rather than escaping as a raw Ical.Net exception —
	///   <c>Calendar.Load</c> THROWS (does not return null) on unparsable text, so the historical
	///   `?? new Calendar()` here only ever caught the null-return (truly empty) case.
	/// </summary>
	public static Calendar Load(string ics)
	{
		try
		{
			return Calendar.Load(ics) ?? new Calendar();
		}
		catch (Exception ex)
		{
			throw new BackendException("The stored iCalendar item could not be parsed.", ex);
		}
	}

	/// <summary>
	///   Serializes to iCalendar text with RFC 5545 §3.1 CRLF line endings, throwing if the
	///   library produces none. Ical.Net's serializer emits <see cref="Environment.NewLine" /> —
	///   CRLF on Windows but bare <c>LF</c> on the Linux containers this ships in — so the output is
	///   normalized explicitly rather than trusting the platform. Every iCalendar this assembly
	///   emits — DAV PUTs and iTIP mail alike — goes through here, so the guarantee holds once.
	/// </summary>
	public static string Serialize(Calendar calendar)
	{
		string ics = new CalendarSerializer().SerializeToString(calendar)
		             ?? throw new BackendException("iCalendar serialization produced no output.");
		return ics.ReplaceLineEndings("\r\n");
	}
}

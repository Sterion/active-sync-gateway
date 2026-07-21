using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using Ical.Net;
using Ical.Net.Serialization;

namespace ActiveSync.Backends.Converters;

/// <summary>Ical.Net load/serialize boilerplate shared by the calendar, task and note converters.</summary>
internal static class IcalHelpers
{
	/// <summary>Loads an iCalendar document, falling back to a fresh empty one if unparsable.</summary>
	public static Calendar Load(string ics)
	{
		return Calendar.Load(ics) ?? new Calendar();
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

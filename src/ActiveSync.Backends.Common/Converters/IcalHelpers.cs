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

	/// <summary>Serializes to iCalendar text, throwing if the library produces none.</summary>
	public static string Serialize(Calendar calendar)
	{
		return new CalendarSerializer().SerializeToString(calendar)
		       ?? throw new BackendException("iCalendar serialization produced no output.");
	}
}

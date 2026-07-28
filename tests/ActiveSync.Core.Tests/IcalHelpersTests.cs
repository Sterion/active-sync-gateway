using System.Xml.Linq;
using ActiveSync.Backends.Common.Converters;
using ActiveSync.Contracts;
using ActiveSync.Protocol.Wbxml;

namespace ActiveSync.Core.Tests;

/// <summary>
///   D22 — IcalHelpers.Load's doc comment claims "falls back to a fresh empty one if
///   unparsable", but `Calendar.Load(ics) ?? new Calendar()` only handles the NULL-return case.
///   Verified against Ical.Net 5.2.3: Calendar.Load on genuinely unparsable text THROWS
///   SerializationException, which escapes every caller (NotesConverter/TasksConverter/
///   CalendarConverter) as a raw, non-BackendException failure instead of degrading. Exercised
///   through NotesConverter since IcalHelpers is internal to Backends.Common and all three
///   sibling converters share the same defect.
/// </summary>
public class IcalHelpersTests
{
	private static readonly XNamespace Notes = EasNamespaces.Notes;

	[Fact]
	public void Update_WithAnUnparsableStoredIcs_ThrowsABackendException()
	{
		XElement appData = new("ApplicationData", new XElement(Notes + "Subject", "Renamed"));

		Assert.Throws<BackendException>(() =>
			NotesConverter.FromApplicationData(appData, "n-1", "this is not an ical at all"));
	}
}

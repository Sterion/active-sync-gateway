using System.Xml.Linq;
using ActiveSync.Eas.Conversion;
using ActiveSync.Protocol.Wbxml;

namespace ActiveSync.Core.Tests;

/// <summary>
///   RecurrenceMapper.Parse is internal, so it is exercised through
///   CalendarConverter.FromApplicationData (the one public entry point that calls it) — a
///   Recurrence element that carries both Occurrences and Until must not silently drop Until.
/// </summary>
public class RecurrenceMapperTests
{
	private static readonly XNamespace Cal = EasNamespaces.Calendar;

	private static XElement AppData(params XElement[] elements)
	{
		return new XElement("ApplicationData", elements);
	}

	[Fact]
	public void BothOccurrencesAndUntil_KeepsUntil_NotSilentlyUnbounded()
	{
		// RRULE (and Ical.Net's RecurrencePattern) allows only one of COUNT/UNTIL, so a client
		// sending both forces a choice. Dropping Until in favor of Occurrences risked an
		// effectively unbounded series if Occurrences was large/careless with no date backstop;
		// dropping Occurrences in favor of Until only ever narrows the series, which is the safer
		// failure mode.
		string ics = CalendarConverter.FromApplicationData(AppData(
			new XElement(Cal + "Subject", "Standup"),
			new XElement(Cal + "StartTime", "20260801T090000Z"),
			new XElement(Cal + "EndTime", "20260801T091500Z"),
			new XElement(Cal + "Recurrence",
				new XElement(Cal + "Type", "0"),
				new XElement(Cal + "Occurrences", "10"),
				new XElement(Cal + "Until", "20260901T000000Z"))), Guid.NewGuid().ToString(), null);

		Assert.Contains("UNTIL=20260901T000000Z", ics);
		Assert.DoesNotContain("COUNT=10", ics);
	}

	[Fact]
	public void OccurrencesOnly_StillApplied()
	{
		string ics = CalendarConverter.FromApplicationData(AppData(
			new XElement(Cal + "Subject", "Standup"),
			new XElement(Cal + "StartTime", "20260801T090000Z"),
			new XElement(Cal + "EndTime", "20260801T091500Z"),
			new XElement(Cal + "Recurrence",
				new XElement(Cal + "Type", "0"),
				new XElement(Cal + "Occurrences", "10"))), Guid.NewGuid().ToString(), null);

		Assert.Contains("COUNT=10", ics);
	}

	[Fact]
	public void UntilOnly_StillApplied()
	{
		string ics = CalendarConverter.FromApplicationData(AppData(
			new XElement(Cal + "Subject", "Standup"),
			new XElement(Cal + "StartTime", "20260801T090000Z"),
			new XElement(Cal + "EndTime", "20260801T091500Z"),
			new XElement(Cal + "Recurrence",
				new XElement(Cal + "Type", "0"),
				new XElement(Cal + "Until", "20260901T000000Z"))), Guid.NewGuid().ToString(), null);

		Assert.Contains("UNTIL=20260901T000000Z", ics);
	}
}

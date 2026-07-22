using ActiveSync.Backends.Converters;
using ActiveSync.Contracts;

namespace ActiveSync.Core.Tests;

/// <summary>MS-ASCMD 2.2.3.107 digit-string rules: interval count, overlap marking, digit precedence.</summary>
public sealed class MergedFreeBusyTests
{
	private static readonly DateTime Start = new(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);

	[Fact]
	public void EmptyPeriods_AllFree()
	{
		Assert.Equal("0000", MergedFreeBusy.Build(Start, Start.AddHours(2), []));
	}

	[Fact]
	public void IntervalCount_IsCeilingOfWindow()
	{
		// 61 minutes → 3 intervals (spec: round up).
		Assert.Equal(3, MergedFreeBusy.Build(Start, Start.AddMinutes(61), []).Length);
		// Exactly 30 minutes → 1 digit.
		Assert.Equal(1, MergedFreeBusy.Build(Start, Start.AddMinutes(30), []).Length);
	}

	[Fact]
	public void BusyPeriod_MarksAllOverlappingIntervals()
	{
		// 12:45–13:20 busy in a 12:00–14:00 window: overlaps intervals 1 and 2 of 0..3.
		BusyPeriod busy = new(Start.AddMinutes(45), Start.AddMinutes(80), '2');
		Assert.Equal("0220", MergedFreeBusy.Build(Start, Start.AddHours(2), [busy]));
	}

	[Fact]
	public void ShortPeriodInsideOneInterval_MarksThatInterval()
	{
		// Spec example: 5 busy minutes inside an interval mark the whole interval.
		BusyPeriod busy = new(Start.AddMinutes(10), Start.AddMinutes(15), '2');
		Assert.Equal("20", MergedFreeBusy.Build(Start, Start.AddHours(1), [busy]));
	}

	[Fact]
	public void HigherDigit_WinsOnOverlap()
	{
		BusyPeriod tentative = new(Start, Start.AddHours(1), '1');
		BusyPeriod oof = new(Start.AddMinutes(30), Start.AddMinutes(60), '3');
		Assert.Equal("13", MergedFreeBusy.Build(Start, Start.AddHours(1), [tentative, oof]));
	}

	[Fact]
	public void ParseFreeBusy_ReadsStalwartShapedVFreeBusy()
	{
		// Verbatim shape of a Stalwart free-busy-query answer.
		const string ics = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Test//EN\r\n" +
		                   "BEGIN:VFREEBUSY\r\nDTSTART:20260728T000000Z\r\nDTEND:20260729T000000Z\r\n" +
		                   "DTSTAMP:20260717T051745Z\r\n" +
		                   "FREEBUSY;FBTYPE=BUSY:20260728T140000Z/20260728T153000Z\r\n" +
		                   "END:VFREEBUSY\r\nEND:VCALENDAR\r\n";
		IReadOnlyList<BusyPeriod> periods = CalendarConverter.ParseFreeBusy(ics);
		BusyPeriod period = Assert.Single(periods);
		Assert.Equal('2', period.Kind);
		Assert.Equal(new DateTime(2026, 7, 28, 14, 0, 0, DateTimeKind.Utc), period.StartUtc);
		Assert.Equal(new DateTime(2026, 7, 28, 15, 30, 0, DateTimeKind.Utc), period.EndUtc);
	}

	[Fact]
	public void PeriodsOutsideTheWindow_AreIgnored()
	{
		BusyPeriod before = new(Start.AddHours(-2), Start.AddHours(-1), '2');
		BusyPeriod after = new(Start.AddHours(3), Start.AddHours(4), '2');
		Assert.Equal("00", MergedFreeBusy.Build(Start, Start.AddHours(1), [before, after]));
	}
}

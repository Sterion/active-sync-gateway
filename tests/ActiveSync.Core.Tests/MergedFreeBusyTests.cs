using ActiveSync.Backends.Common.Converters;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;

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
		BusyPeriod busy = new() { Start = Start.AddMinutes(45), End = Start.AddMinutes(80), Kind = BusyKind.Busy };
		Assert.Equal("0220", MergedFreeBusy.Build(Start, Start.AddHours(2), [busy]));
	}

	[Fact]
	public void ShortPeriodInsideOneInterval_MarksThatInterval()
	{
		// Spec example: 5 busy minutes inside an interval mark the whole interval.
		BusyPeriod busy = new() { Start = Start.AddMinutes(10), End = Start.AddMinutes(15), Kind = BusyKind.Busy };
		Assert.Equal("20", MergedFreeBusy.Build(Start, Start.AddHours(1), [busy]));
	}

	[Fact]
	public void HigherDigit_WinsOnOverlap()
	{
		BusyPeriod tentative = new() { Start = Start, End = Start.AddHours(1), Kind = BusyKind.Tentative };
		BusyPeriod oof = new() { Start = Start.AddMinutes(30), End = Start.AddMinutes(60), Kind = BusyKind.OutOfOffice };
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
		Assert.Equal(BusyKind.Busy, period.Kind);
		Assert.Equal(new DateTimeOffset(2026, 7, 28, 14, 0, 0, TimeSpan.Zero), period.Start);
		Assert.Equal(new DateTimeOffset(2026, 7, 28, 15, 30, 0, TimeSpan.Zero), period.End);
	}

	/// <summary>
	///   FBTYPE was classified by substring-scanning the WHOLE parameter segment
	///   (`parameters.Contains("BUSY-TENTATIVE", ...)`), so an unrelated parameter whose value
	///   merely CONTAINS that text anywhere before the colon is misclassified, even though it is
	///   not the FBTYPE parameter at all. Here an X- parameter happens to embed the substring, and
	///   no real FBTYPE is present — the period must default to BUSY, not TENTATIVE.
	/// </summary>
	[Fact]
	public void ParseFreeBusy_DoesNotMisreadAnUnrelatedParameterContainingFbtypeText()
	{
		const string ics = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Test//EN\r\n" +
		                   "BEGIN:VFREEBUSY\r\nDTSTART:20260728T000000Z\r\nDTEND:20260729T000000Z\r\n" +
		                   "DTSTAMP:20260717T051745Z\r\n" +
		                   "FREEBUSY;X-NOTE=NOT-BUSY-TENTATIVE-REALLY:20260728T140000Z/20260728T153000Z\r\n" +
		                   "END:VFREEBUSY\r\nEND:VCALENDAR\r\n";
		IReadOnlyList<BusyPeriod> periods = CalendarConverter.ParseFreeBusy(ics);
		BusyPeriod period = Assert.Single(periods);
		Assert.Equal(BusyKind.Busy, period.Kind); // no real FBTYPE parameter present -> defaults to BUSY
	}

	[Fact]
	public void PeriodsOutsideTheWindow_AreIgnored()
	{
		BusyPeriod before = new() { Start = Start.AddHours(-2), End = Start.AddHours(-1), Kind = BusyKind.Busy };
		BusyPeriod after = new() { Start = Start.AddHours(3), End = Start.AddHours(4), Kind = BusyKind.Busy };
		Assert.Equal("00", MergedFreeBusy.Build(Start, Start.AddHours(1), [before, after]));
	}

	// Precedence ladder over the kinds a STORE can report: OOF > busy > tentative > free.
	// (The spec's "no data" digit '4' sits between free and tentative in MergedFreeBusy's own
	// rank table, but no BusyPeriod can carry it — "no data" is IFreeBusySource returning null,
	// which the handler answers with Availability status 163 instead of a digit string.)
	[Fact]
	public void Precedence_IsFreeThenTentativeThenBusyThenOof()
	{
		Assert.Equal("0", MergedFreeBusy.Build(Start, Start.AddMinutes(30),
			[new BusyPeriod { Start = Start, End = Start.AddMinutes(30), Kind = BusyKind.Free }]));
		Assert.Equal("1", MergedFreeBusy.Build(Start, Start.AddMinutes(30),
		[
			new BusyPeriod { Start = Start, End = Start.AddMinutes(30), Kind = BusyKind.Free },
			new BusyPeriod { Start = Start, End = Start.AddMinutes(30), Kind = BusyKind.Tentative }
		]));
		Assert.Equal("2", MergedFreeBusy.Build(Start, Start.AddMinutes(30),
		[
			new BusyPeriod { Start = Start, End = Start.AddMinutes(30), Kind = BusyKind.Tentative },
			new BusyPeriod { Start = Start, End = Start.AddMinutes(30), Kind = BusyKind.Busy }
		]));
		Assert.Equal("3", MergedFreeBusy.Build(Start, Start.AddMinutes(30),
		[
			new BusyPeriod { Start = Start, End = Start.AddMinutes(30), Kind = BusyKind.Busy },
			new BusyPeriod { Start = Start, End = Start.AddMinutes(30), Kind = BusyKind.OutOfOffice }
		]));
	}

	// An inverted window (end < start) previously clamped to a single all-free digit,
	// silently reporting "completely free" for a nonsense request.
	[Fact]
	public void InvertedWindow_Throws()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			MergedFreeBusy.Build(Start, Start.AddHours(-1), []));
	}

	// A Kind outside the enum (a plugin casting a raw integer onto BusyKind) has no digit and
	// must be dropped rather than emitting something that rides into WBXML.
	[Fact]
	public void KindOutsideTheEnum_IsNotEmitted()
	{
		string result = MergedFreeBusy.Build(Start, Start.AddMinutes(30),
			[new BusyPeriod { Start = Start, End = Start.AddMinutes(30), Kind = (BusyKind)99 }]);
		Assert.Equal("0", result);
	}
}

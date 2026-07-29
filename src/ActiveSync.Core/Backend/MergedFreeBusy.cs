using ActiveSync.Contracts;

namespace ActiveSync.Core.Backend;

/// <summary>
///   Builds the MS-ASCMD MergedFreeBusy digit string (2.2.3.107): one digit per 30-minute
///   interval from StartTime, ceil(window/30min) digits, any overlap marks the interval and
///   the higher digit wins ('0' free, '1' tentative, '2' busy, '3' OOF, '4' no data).
/// </summary>
public static class MergedFreeBusy
{
	private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

	/// <summary>Spec cap: the string may not exceed 32 KB (the client re-queries for more).</summary>
	private const int MaxDigits = 32 * 1024;

	/// <summary>
	///   Renders the digit string for one window. The digits are HOST knowledge: a store reports
	///   <see cref="BusyKind" />, and the mapping to the wire characters lives here (and only
	///   here), which is why <see cref="BusyKind" /> itself pins no values.
	/// </summary>
	public static string Build(DateTimeOffset start, DateTimeOffset end, IReadOnlyList<BusyPeriod> periods)
	{
		// An inverted window (end before start) is a caller bug; clamping it to a single all-free
		// digit silently answers "completely free" for nonsense input.
		ArgumentOutOfRangeException.ThrowIfLessThan(end, start);

		int intervals = (int)Math.Ceiling((end - start).TotalMinutes / Interval.TotalMinutes);
		intervals = Math.Clamp(intervals, 1, MaxDigits);
		char[] digits = new char[intervals];
		Array.Fill(digits, '0');

		foreach (BusyPeriod period in periods)
		{
			if (period.End <= start || period.Start >= end)
				continue;
			// An out-of-range Kind (a plugin casting an int onto the enum) must never be copied
			// into the digit string — it would ride straight into WBXML — so skip the period.
			char digit = Digit(period.Kind);
			int kindRank = Rank(digit);
			if (kindRank < 0)
				continue;
			int first = Math.Max(0, (int)((period.Start - start) / Interval));
			int last = Math.Min(intervals - 1, (int)Math.Ceiling((period.End - start) / Interval) - 1);
			for (int i = first; i <= last; i++)
				// Higher STATUS wins, by rank — not by ASCII value: '4' (no data) is the highest
				// digit but the weakest signal, so a known busy/tentative/OOF must beat it.
				if (kindRank > Rank(digits[i]))
					digits[i] = digit;
		}

		return new string(digits);
	}

	/// <summary>
	///   The wire digit for a reported <see cref="BusyKind" />; '\0' for a value outside the enum,
	///   which <see cref="Rank" /> then rejects.
	/// </summary>
	private static char Digit(BusyKind kind)
	{
		return kind switch
		{
			BusyKind.Free => '0',
			BusyKind.Tentative => '1',
			BusyKind.Busy => '2',
			BusyKind.OutOfOffice => '3',
			_ => '\0'
		};
	}

	/// <summary>
	///   Precedence of the MS-ASCMD free/busy digits, lowest to highest: '0' free, '4' no data,
	///   '1' tentative, '2' busy, '3' OOF. Returns -1 for any character outside the set.
	/// </summary>
	private static int Rank(char kind)
	{
		return kind switch
		{
			'0' => 0, // free
			'4' => 1, // no data
			'1' => 2, // tentative
			'2' => 3, // busy
			'3' => 4, // out of office
			_ => -1
		};
	}
}

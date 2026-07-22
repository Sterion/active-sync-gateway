namespace ActiveSync.Contracts;

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

	public static string Build(DateTime startUtc, DateTime endUtc, IReadOnlyList<BusyPeriod> periods)
	{
		// An inverted window (end before start) is a caller bug; clamping it to a single all-free
		// digit silently answers "completely free" for nonsense input (A16).
		ArgumentOutOfRangeException.ThrowIfLessThan(endUtc, startUtc);

		int intervals = (int)Math.Ceiling((endUtc - startUtc).TotalMinutes / Interval.TotalMinutes);
		intervals = Math.Clamp(intervals, 1, MaxDigits);
		char[] digits = new char[intervals];
		Array.Fill(digits, '0');

		foreach (BusyPeriod period in periods)
		{
			if (period.EndUtc <= startUtc || period.StartUtc >= endUtc)
				continue;
			// A malformed Kind ('\0', 'B', …) must never be copied verbatim into the digit
			// string — it would ride straight into WBXML — so skip the period entirely (A16).
			int kindRank = Rank(period.Kind);
			if (kindRank < 0)
				continue;
			int first = Math.Max(0, (int)((period.StartUtc - startUtc) / Interval));
			int last = Math.Min(intervals - 1, (int)Math.Ceiling((period.EndUtc - startUtc) / Interval) - 1);
			for (int i = first; i <= last; i++)
				// Higher STATUS wins, by rank — not by ASCII value: '4' (no data) is the highest
				// digit but the weakest signal, so a known busy/tentative/OOF must beat it (A15).
				if (kindRank > Rank(digits[i]))
					digits[i] = period.Kind;
		}

		return new string(digits);
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

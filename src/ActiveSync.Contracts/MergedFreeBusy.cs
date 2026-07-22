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
		int intervals = (int)Math.Ceiling((endUtc - startUtc).TotalMinutes / Interval.TotalMinutes);
		intervals = Math.Clamp(intervals, 1, MaxDigits);
		char[] digits = new char[intervals];
		Array.Fill(digits, '0');

		foreach (BusyPeriod period in periods)
		{
			if (period.EndUtc <= startUtc || period.StartUtc >= endUtc)
				continue;
			int first = Math.Max(0, (int)((period.StartUtc - startUtc) / Interval));
			int last = Math.Min(intervals - 1, (int)Math.Ceiling((period.EndUtc - startUtc) / Interval) - 1);
			for (int i = first; i <= last; i++)
				if (period.Kind > digits[i])
					digits[i] = period.Kind;
		}

		return new string(digits);
	}
}

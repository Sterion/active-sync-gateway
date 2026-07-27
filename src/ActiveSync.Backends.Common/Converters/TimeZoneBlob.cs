using System.Text;

namespace ActiveSync.Backends.Common.Converters;

/// <summary>
///   Builds the EAS TIME_ZONE_INFORMATION blob (MS-ASTZ): a 172-byte little-endian structure
///   (Bias, StandardName[32], StandardDate, StandardBias, DaylightName[32], DaylightDate,
///   DaylightBias) transported as base64.
/// </summary>
public static class TimeZoneBlob
{
	public static readonly string UtcBase64 = Convert.ToBase64String(new byte[172]);

	public static string ToBase64(TimeZoneInfo tz)
	{
		byte[] buffer = new byte[172];
		Span<byte> span = buffer.AsSpan();

		// Bias = UTC - local time, in minutes
		int bias = (int)-tz.BaseUtcOffset.TotalMinutes;
		BitConverter.TryWriteBytes(span[..4], bias);
		WriteName(span[4..68], tz.StandardName);

		TimeZoneInfo.AdjustmentRule? rule = tz.GetAdjustmentRules()
			                                    .FirstOrDefault(r =>
				                                    r.DateStart <= DateTime.UtcNow && r.DateEnd >= DateTime.UtcNow)
		                                    ?? tz.GetAdjustmentRules().LastOrDefault();

		if (rule is not null && tz.SupportsDaylightSavingTime)
		{
			WriteSystemTime(span[68..84], rule.DaylightTransitionEnd); // StandardDate
			BitConverter.TryWriteBytes(span[84..88], 0); // StandardBias
			WriteName(span[88..152], tz.DaylightName);
			WriteSystemTime(span[152..168], rule.DaylightTransitionStart); // DaylightDate
			BitConverter.TryWriteBytes(span[168..172], (int)-rule.DaylightDelta.TotalMinutes);
		}

		return Convert.ToBase64String(buffer);
	}

	/// <summary>Reads only the Bias from a client-supplied blob (enough to interpret times).</summary>
	public static TimeSpan? ReadBaseOffset(string? base64)
	{
		if (string.IsNullOrEmpty(base64))
			return null;
		try
		{
			byte[] bytes = Convert.FromBase64String(base64);
			if (bytes.Length < 4)
				return null;
			int bias = BitConverter.ToInt32(bytes, 0);
			return TimeSpan.FromMinutes(-bias);
		}
		catch (FormatException)
		{
			return null;
		}
	}

	/// <summary>
	///   D3: reads the offset that actually applies to <paramref name="utcInstant" /> — the base
	///   <c>Bias</c> plus <c>DaylightBias</c> when the instant falls inside the zone's daylight
	///   window, determined from the two SYSTEMTIME transition records (StandardDate at byte
	///   offset 68, DaylightDate at 152). <see cref="ReadBaseOffset" /> reads only the standard
	///   bias, which is wrong for roughly half the year in any zone that observes DST: a
	///   Copenhagen all-day event created in July (CEST, +2h) was read back with the +1h standard
	///   offset, rolling the computed nominal date back by a day. Falls back to the base offset
	///   when the blob is short (an older/foreign encoding) or carries no DST rule.
	/// </summary>
	public static TimeSpan? ReadEffectiveOffset(string? base64, DateTime utcInstant)
	{
		if (string.IsNullOrEmpty(base64))
			return null;
		byte[] bytes;
		try
		{
			bytes = Convert.FromBase64String(base64);
		}
		catch (FormatException)
		{
			return null;
		}
		if (bytes.Length < 4)
			return null;

		int bias = BitConverter.ToInt32(bytes, 0);
		if (bytes.Length < 172)
			return TimeSpan.FromMinutes(-bias);

		int daylightBias = BitConverter.ToInt32(bytes, 168);
		if (daylightBias == 0)
			return TimeSpan.FromMinutes(-bias);

		SystemTimeRule? standardRule = ReadSystemTime(bytes, 68); // transition INTO standard time
		SystemTimeRule? daylightRule = ReadSystemTime(bytes, 152); // transition INTO daylight time
		if (standardRule is null || daylightRule is null)
			return TimeSpan.FromMinutes(-bias);

		// Approximate the wall-clock instant using the standard offset alone to pick the
		// transition year and test which side of the window it falls on. A residual error of a
		// few hours around the exact transition moment cannot change which side of a
		// month-scale window the instant is on.
		DateTime approxLocal = utcInstant + TimeSpan.FromMinutes(-bias);
		DateTime? daylightStart = ResolveTransition(daylightRule.Value, approxLocal.Year);
		DateTime? standardStart = ResolveTransition(standardRule.Value, approxLocal.Year);
		if (daylightStart is null || standardStart is null)
			return TimeSpan.FromMinutes(-bias);

		bool inDaylight = daylightStart < standardStart
			? approxLocal >= daylightStart && approxLocal < standardStart
			: approxLocal >= daylightStart || approxLocal < standardStart;

		return inDaylight ? TimeSpan.FromMinutes(-bias - daylightBias) : TimeSpan.FromMinutes(-bias);
	}

	private readonly record struct SystemTimeRule(int Month, int DayOfWeek, int Week, int Hour, int Minute, int Second);

	/// <summary>Decodes a floating-form SYSTEMTIME transition record — the inverse of <see cref="WriteSystemTime" />.</summary>
	private static SystemTimeRule? ReadSystemTime(byte[] bytes, int offset)
	{
		int month = BitConverter.ToUInt16(bytes, offset + 2);
		if (month is < 1 or > 12)
			return null; // wMonth = 0 means "no rule"
		int dayOfWeek = BitConverter.ToUInt16(bytes, offset + 4);
		int week = BitConverter.ToUInt16(bytes, offset + 6);
		int hour = BitConverter.ToUInt16(bytes, offset + 8);
		int minute = BitConverter.ToUInt16(bytes, offset + 10);
		int second = BitConverter.ToUInt16(bytes, offset + 12);
		return new SystemTimeRule(month, dayOfWeek, week is < 1 or > 5 ? 1 : week, hour, minute, second);
	}

	/// <summary>Resolves a floating "Nth weekday of month" (or "last weekday", week=5) rule to a concrete date in <paramref name="year" />.</summary>
	private static DateTime? ResolveTransition(SystemTimeRule rule, int year)
	{
		DateTime candidate;
		if (rule.Week >= 5)
		{
			candidate = new DateTime(year, rule.Month, DateTime.DaysInMonth(year, rule.Month));
			while ((int)candidate.DayOfWeek != rule.DayOfWeek)
				candidate = candidate.AddDays(-1);
		}
		else
		{
			candidate = new DateTime(year, rule.Month, 1);
			while ((int)candidate.DayOfWeek != rule.DayOfWeek)
				candidate = candidate.AddDays(1);
			candidate = candidate.AddDays((rule.Week - 1) * 7);
			if (candidate.Month != rule.Month)
				candidate = candidate.AddDays(-7); // short month pushed a week-4 rule past the end
		}

		return candidate.AddHours(rule.Hour).AddMinutes(rule.Minute).AddSeconds(rule.Second);
	}

	private static void WriteName(Span<byte> destination, string name)
	{
		byte[] bytes = Encoding.Unicode.GetBytes(name);
		bytes.AsSpan(0, Math.Min(bytes.Length, destination.Length - 2)).CopyTo(destination);
	}

	private static void WriteSystemTime(Span<byte> destination, TimeZoneInfo.TransitionTime transition)
	{
		// SYSTEMTIME in "relative" form: wYear=0, wMonth, wDayOfWeek, wDay=week-of-month (5=last), time
		static void W(Span<byte> span, int offset, ushort value)
		{
			BitConverter.TryWriteBytes(span[offset..(offset + 2)], value);
		}

		if (transition.IsFixedDateRule)
		{
			// EAS only has the floating ("Nth weekday of the month") form, so a fixed
			// calendar-day rule (e.g. "March 25") has to be approximated: keep the month,
			// leave wDayOfWeek 0, and map the day-of-month into a week-of-month bucket —
			// day 1-7 → week 1, 8-14 → week 2, etc. ((day-1)/7 + 1). Fixed-date zones are
			// rare (most use the floating DST form), so the approximation is acceptable.
			W(destination, 0, 0);
			W(destination, 2, (ushort)transition.Month);
			W(destination, 4, 0);
			W(destination, 6, (ushort)((transition.Day - 1) / 7 + 1));
		}
		else
		{
			W(destination, 0, 0);
			W(destination, 2, (ushort)transition.Month);
			W(destination, 4, (ushort)transition.DayOfWeek);
			W(destination, 6, (ushort)transition.Week); // 1..4, 5 = last
		}

		W(destination, 8, (ushort)transition.TimeOfDay.Hour);
		W(destination, 10, (ushort)transition.TimeOfDay.Minute);
		W(destination, 12, (ushort)transition.TimeOfDay.Second);
		W(destination, 14, 0);
	}
}

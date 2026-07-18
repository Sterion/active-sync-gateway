using System.Text;

namespace ActiveSync.Backends.Converters;

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

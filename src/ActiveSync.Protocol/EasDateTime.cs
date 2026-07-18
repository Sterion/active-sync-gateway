using System.Globalization;

namespace ActiveSync.Protocol;

/// <summary>Date/time formatting helpers for EAS payloads (MS-ASDTYPE).</summary>
public static class EasDateTime
{
	/// <summary>Full format used by Email/compact contexts: 2026-07-13T12:00:00.000Z</summary>
	public static string ToLong(DateTime utc)
	{
		return utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
	}

	/// <summary>Compact format used by Calendar: 20260713T120000Z</summary>
	public static string ToCompact(DateTime utc)
	{
		return utc.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
	}

	public static DateTime Parse(string value)
	{
		string[] formats =
		[
			"yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
			"yyyy-MM-dd'T'HH:mm:ss'Z'",
			"yyyyMMdd'T'HHmmss'Z'",
			"yyyyMMdd'T'HHmmssfff'Z'"
		];
		if (DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture,
			    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime result))
			return result;
		return DateTime.Parse(value, CultureInfo.InvariantCulture,
			DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
	}
}

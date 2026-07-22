using System.Globalization;
using ActiveSync.Protocol.Wbxml;

namespace ActiveSync.Protocol;

/// <summary>Date/time formatting helpers for EAS payloads (MS-ASDTYPE).</summary>
public static class EasDateTime
{
	/// <summary>Full format used by Email/compact contexts: 2026-07-13T12:00:00.000Z</summary>
	public static string ToLong(DateTime utc)
	{
		return AsUtc(utc).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
	}

	/// <summary>Compact format used by Calendar: 20260713T120000Z</summary>
	public static string ToCompact(DateTime utc)
	{
		return AsUtc(utc).ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
	}

	/// <summary>
	///   Interprets a value the caller asserts is UTC (the parameter is named <c>utc</c>).
	///   <see cref="DateTimeKind.Utc" /> passes through; <see cref="DateTimeKind.Local" /> is
	///   converted; <see cref="DateTimeKind.Unspecified" /> is taken at face value as UTC —
	///   never treated as local and shifted by the machine offset. Values round-tripped through
	///   EF Core (SQLite hands back <c>Unspecified</c>) arrive here as Unspecified; the old
	///   <c>ToUniversalTime()</c> subtracted the host offset from them, a silent,
	///   timezone-dependent corruption invisible on a UTC host and wrong in production.
	/// </summary>
	private static DateTime AsUtc(DateTime value)
	{
		return value.Kind switch
		{
			DateTimeKind.Utc => value,
			DateTimeKind.Local => value.ToUniversalTime(),
			_ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
		};
	}

	private static readonly string[] Formats =
	[
		"yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
		"yyyy-MM-dd'T'HH:mm:ss'Z'",
		"yyyyMMdd'T'HHmmss'Z'",
		"yyyyMMdd'T'HHmmssfff'Z'",
		"yyyyMMdd'T'HHmmss" // basic form without the trailing Z (MS-ASDTYPE tolerant)
	];

	/// <summary>
	///   Parses an MS-ASDTYPE date/time using only the exact spec formats — no loose
	///   <see cref="DateTime.Parse(string)" /> fallback (which accepted culture-dependent forms
	///   like "3/4/2026" and made failure locale-dependent). Returns UTC.
	/// </summary>
	public static bool TryParse(string? value, out DateTime result)
	{
		return DateTime.TryParseExact(value, Formats, CultureInfo.InvariantCulture,
			DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out result);
	}

	/// <summary>
	///   Parses client-supplied date text. Throws <see cref="WbxmlException" /> on anything the
	///   exact formats reject — these values come from untrusted phone payloads, so an
	///   uncontrolled <see cref="FormatException" /> (which would surface as HTTP 500) is
	///   converted to the same protocol-error channel the WBXML decoder uses for a 400.
	/// </summary>
	public static DateTime Parse(string value)
	{
		if (TryParse(value, out DateTime result))
			return result;
		throw new WbxmlException($"'{value}' is not a valid MS-ASDTYPE date/time.");
	}
}

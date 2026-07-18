using System.Globalization;

namespace ActiveSync.Backends.Jmap;

/// <summary>
///   JMAP UTCDate formatting (RFC 8620): always ISO-8601 with literal '-'/':'/'Z' under the
///   invariant culture — a custom format's separators are otherwise culture-specific (a
///   Danish locale renders ':' as '.'), which the server would reject.
/// </summary>
internal static class JmapDate
{
	public static string ToUtc(DateTime value)
	{
		return value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH':'mm':'ss'Z'", CultureInfo.InvariantCulture);
	}
}

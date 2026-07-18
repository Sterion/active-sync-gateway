namespace ActiveSync.Core.Backend;

/// <summary>
///   Encodes a composite identifier (e.g. an attachment FileReference or a Search LongId)
///   as pipe-joined parts. Each part is percent-escaped BEFORE joining, so a literal '|' in
///   a component (legal in IMAP mailbox names) can never be confused with the delimiter —
///   unlike escaping the already-joined string, where the delimiter and the data escape
///   identically.
/// </summary>
public static class DelimitedKey
{
	public static string Encode(params string[] parts)
	{
		return string.Join('|', parts.Select(Uri.EscapeDataString));
	}

	/// <summary>Returns the decoded parts, or null when the count does not match.</summary>
	public static string[]? Decode(string value, int expectedParts)
	{
		string[] parts = value.Split('|');
		if (parts.Length != expectedParts)
			return null;
		return parts.Select(Uri.UnescapeDataString).ToArray();
	}
}

// Copyright (c) 2026 Ruben Andersen
// SPDX-License-Identifier: MIT

namespace ActiveSync.Protocol;

/// <summary>
///   Encodes a composite identifier (e.g. an attachment FileReference or a Search LongId)
///   as pipe-joined parts. Each part is percent-escaped BEFORE joining, so a literal '|' in
///   a component (legal in IMAP mailbox names) can never be confused with the delimiter —
///   unlike escaping the already-joined string, where the delimiter and the data escape
///   identically.
///   <para>
///     It lives with the other EAS wire encodings rather than in the plugin contract: FileReference
///     and LongId are values the CLIENT sees, and once no delimited key crosses the store boundary
///     a plugin has no use for the encoder — its presence there would only invite one.
///   </para>
/// </summary>
public static class DelimitedKey
{
	/// <summary>Percent-escapes each part and joins them with '|', so a literal '|' inside a part can never be mistaken for the delimiter.</summary>
	/// <param name="parts">The components to encode, in order.</param>
	/// <returns>The pipe-joined, percent-escaped composite key.</returns>
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

namespace ActiveSync.Server.Eas;

/// <summary>
///   Sanitizes client-supplied strings (usernames, device ids, commands, mail headers)
///   before they are embedded in log output: control characters would let a hostile client
///   forge log lines or smuggle terminal escape sequences into the console.
/// </summary>
public static class LogText
{
	public static string Clean(string? value, int maxLength = 256)
	{
		if (string.IsNullOrEmpty(value))
			return "";
		string text = value.Length > maxLength ? value[..maxLength] : value;
		// Allocation-free scan on the hot path (Clean runs 6+ times per EAS request): an
		// AsSpan pass avoids the CharEnumerator boxing an IEnumerable LINQ scan would incur.
		ReadOnlySpan<char> span = text.AsSpan();
		int bad = -1;
		for (int i = 0; i < span.Length; i++)
		{
			if (IsUnsafe(span[i]))
			{
				bad = i;
				break;
			}
		}
		if (bad < 0)
			return text;
		return string.Create(text.Length, text, static (dest, source) =>
		{
			for (int i = 0; i < source.Length; i++)
				dest[i] = IsUnsafe(source[i]) ? '?' : source[i];
		});
	}

	/// <summary>
	///   A character that must not reach a log line verbatim: control characters (escape
	///   sequences, newline injection) and the Unicode bidirectional overrides/isolates
	///   (U+202A-202E, U+2066-2069). The latter are format characters — NOT
	///   <see cref="char.IsControl(char)" /> — so a hostile username could visually reorder
	///   the rest of a log line without them.
	/// </summary>
	private static bool IsUnsafe(char c)
	{
		return char.IsControl(c) || c is (>= '‪' and <= '‮') or (>= '⁦' and <= '⁩');
	}
}

using ActiveSync.Protocol;

namespace ActiveSync.Server.Eas;

/// <summary>
///   Sanitizes client-supplied strings (usernames, device ids, commands, mail headers)
///   before they are embedded in log output: unsafe characters would let a hostile client
///   forge log lines, smuggle terminal escape sequences, or visually reorder a log line.
///   Single-field values never carry line structure, so every control character (including
///   CR/LF/TAB) is neutralized — unlike <see cref="WireLog.Payload" />'s multi-line dumps,
///   see <see cref="WireLog.IsUnsafe" />, the one shared classifier.
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
			if (WireLog.IsUnsafe(span[i], allowLineStructure: false))
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
				dest[i] = WireLog.IsUnsafe(source[i], allowLineStructure: false) ? '?' : source[i];
		});
	}
}

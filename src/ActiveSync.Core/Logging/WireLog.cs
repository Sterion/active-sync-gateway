namespace ActiveSync.Core.Logging;

/// <summary>
///   Helpers for the Verbose (Trace) wire-logging tier. Payload dumps are size-capped so a
///   large Sync response or MIME body cannot turn one log event into megabytes (the cap is
///   deliberately a constant — wire logging is a debugging tool, not a tunable feature),
///   and control characters other than line structure are neutralized so hostile content
///   cannot smuggle terminal escape sequences into the console.
/// </summary>
public static class WireLog
{
	public const int MaxChars = 16 * 1024;

	/// <summary>Truncates a dump to <see cref="MaxChars" /> with an explicit marker.</summary>
	public static string Truncate(string text, int max = MaxChars)
	{
		return text.Length <= max
			? text
			: $"{text[..max]}… [truncated, {text.Length} chars total]";
	}

	/// <summary>
	///   Prepares payload text for logging: the dump is truncated first, then control characters
	///   except CR/LF/TAB become '?' (multi-line XML/MIME stays readable, escape sequences do not
	///   survive).
	///   <para>
	///     Truncation leads deliberately. Sanitizing first meant scanning and copying the entire
	///     input — a 50 MB MIME part cost a second 50 MB string on the large-object heap — to keep
	///     16 KB of it. The output is identical either way: characters past the cap are discarded,
	///     so whether they were sanitized on the way is unobservable.
	///   </para>
	/// </summary>
	public static string Payload(string text, int max = MaxChars)
	{
		text = Truncate(text, max);
		if (text.Any(static c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
			text = string.Create(text.Length, text, static (span, source) =>
			{
				for (int i = 0; i < source.Length; i++)
					span[i] = char.IsControl(source[i]) && source[i] is not ('\r' or '\n' or '\t')
						? '?'
						: source[i];
			});
		return text;
	}
}

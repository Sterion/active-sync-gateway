// Copyright (c) 2026 Ruben Andersen
// SPDX-License-Identifier: MIT

namespace ActiveSync.Protocol;

/// <summary>
///   Helpers for the Verbose (Trace) wire-logging tier. Payload dumps are size-capped so a
///   large Sync response or MIME body cannot turn one log event into megabytes (the cap is
///   deliberately a constant — wire logging is a debugging tool, not a tunable feature),
///   and unsafe characters are neutralized so hostile content cannot smuggle terminal escape
///   sequences or visually reorder a log line.
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
	///   Prepares payload text for logging: the dump is truncated first, then unsafe characters
	///   (see <see cref="IsUnsafe" />) other than CR/LF/TAB become '?' (multi-line XML/MIME stays
	///   readable, escape sequences and bidi-override smuggling do not survive).
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
		if (text.Any(static c => IsUnsafe(c, allowLineStructure: true)))
			text = string.Create(text.Length, text, static (span, source) =>
			{
				for (int i = 0; i < source.Length; i++)
					span[i] = IsUnsafe(source[i], allowLineStructure: true) ? '?' : source[i];
			});
		return text;
	}

	/// <summary>
	///   The one character-safety classifier shared by every log-sanitizing entry point in the
	///   codebase (this type's <see cref="Payload" /> and <c>ActiveSync.Server.Eas.LogText.Clean</c>)
	///   — S6/K21: the two used to duplicate this core with different rules, and the bidi-override
	///   defense existed on only one of them. A character is unsafe if it is either a control
	///   character (escape sequences, newline injection) or one of the Unicode bidirectional
	///   override/isolate format characters (U+202A-202E, U+2066-2069) — the latter are NOT
	///   <see cref="char.IsControl(char)" />, so a hostile string could visually reorder the rest of
	///   a log line without them. <paramref name="allowLineStructure" /> lets CR/LF/TAB through for
	///   multi-line payload dumps (XML/MIME); single-field callers like usernames/device ids pass
	///   <c>false</c> so an embedded newline cannot forge a fake log line.
	/// </summary>
	public static bool IsUnsafe(char c, bool allowLineStructure)
	{
		if (allowLineStructure && c is '\r' or '\n' or '\t')
			return false;
		// W9: written as escapes, never as raw literals — an unterminated LRE/RLO sitting in this
		// source line would itself be the Trojan Source hazard (CVE-2021-42574) this check exists
		// to defend against: a bidi-aware viewer (GitHub, most editors, a modern terminal's git
		// diff) would render the remainder of the line reordered, showing a reviewer something
		// different from what the compiler sees. U+202A-202E are the bidi override/embedding
		// controls (LRE/RLE/PDF/LRO/RLO); U+2066-2069 are the bidi isolates (LRI/RLI/FSI/PDI).
		return char.IsControl(c) || c is (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069');
	}
}

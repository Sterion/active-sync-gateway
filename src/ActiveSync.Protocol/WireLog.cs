// Copyright (c) 2026 Ruben Andersen
// SPDX-License-Identifier: MIT

namespace ActiveSync.Protocol;

/// <summary>
///   Helpers for the Verbose (Trace) wire-logging tier. Payload dumps are size-capped so a
///   large Sync response or MIME body cannot turn one log event into megabytes (the cap is
///   deliberately fixed, not a config-settable tunable — wire logging is a debugging tool, not
///   a tunable feature; see the W21 note on <see cref="MaxChars" /> for why it is nonetheless
///   <c>static readonly</c> rather than <c>const</c>), and unsafe characters are neutralized so
///   hostile content cannot smuggle terminal escape sequences or visually reorder a log line.
/// </summary>
public static class WireLog
{
	// W21: the DEFAULT value on Truncate/Payload's `max` parameter must itself be a compile-time
	// constant (a C# language rule), so this private const carries the literal; the PUBLIC field
	// below is a genuine `static readonly` built from it, not the const directly. MaxChars is a
	// policy knob on a published MIT package (unlike EasFolderType/EasClass, genuinely frozen
	// protocol constants, which stay `const`) — a `const` here is inlined into a plugin's IL at
	// compile time, so raising it later would leave an already-built plugin still reading its own
	// build-time value while appearing to read the host's current one (the same hazard AGENTS.md
	// documents for ContractVersion.Major/Minor). This does not make the value config-settable —
	// it is still fixed for the process lifetime — only readable live rather than baked in.
	private const int DefaultMaxChars = 16 * 1024;

	public static readonly int MaxChars = DefaultMaxChars;

	/// <summary>Truncates a dump to <see cref="MaxChars" /> with an explicit marker.</summary>
	public static string Truncate(string text, int max = DefaultMaxChars)
	{
		// W11: without this, a negative max still throws — but as an accident of Range/Substring
		// internals, naming ITS OWN "length" parameter rather than the "max" the caller actually
		// passed. An explicit check up front gives the caller (this is a logging helper, called
		// from inside LogTrace sites that do not expect an exception at all) a message that names
		// the parameter it recognizes.
		ArgumentOutOfRangeException.ThrowIfNegative(max);
		if (text.Length <= max)
			return text;

		// W11: max may land between a surrogate pair's high and low half (any emoji or other
		// non-BMP character sitting exactly at the boundary) — back the cut off by one so the
		// retained window never ends with a lone high surrogate, which a downstream console/JSON
		// sink would otherwise render as U+FFFD mojibake instead of the truncation marker reading
		// cleanly.
		int cut = max > 0 && char.IsHighSurrogate(text[max - 1]) ? max - 1 : max;
		return $"{text[..cut]}… [truncated, {text.Length} chars total]";
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
	public static string Payload(string text, int max = DefaultMaxChars)
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
		// W10: U+2028 LINE SEPARATOR / U+2029 PARAGRAPH SEPARATOR (categories Zl/Zp) — written
		// as escapes, same reason as the bidi range above. Neither is char.IsControl, so a
		// hostile string can forge a line break to a JSON/CLEF sink or a line-splitting log
		// viewer even on the allowLineStructure:false, single-field path (LogText.Clean) whose
		// whole purpose is to prevent exactly that.
		return char.IsControl(c)
		       || c is (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069')
		       || c is '\u2028' or '\u2029';
	}
}

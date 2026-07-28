using ActiveSync.Protocol;

namespace ActiveSync.Protocol.Tests;

public sealed class WireLogTests
{
	[Fact]
	public void Truncate_ShortText_Unchanged()
	{
		Assert.Equal("hello", WireLog.Truncate("hello", 10));
	}

	[Fact]
	public void Truncate_LongText_CappedWithMarker()
	{
		string result = WireLog.Truncate(new string('a', 100), 10);
		Assert.StartsWith(new string('a', 10), result);
		Assert.EndsWith("[truncated, 100 chars total]", result);
	}

	[Fact]
	public void Payload_KeepsLineStructure_NeutralizesEscapes()
	{
		string result = WireLog.Payload("line1\r\nline2\tind\u001b[31mented");
		Assert.Equal("line1\r\nline2\tind?[31mented", result);
	}

	[Fact]
	public void Payload_HugeInput_IsTruncatedBeforeItIsCopied()
	{
		// K33: Payload sanitized the WHOLE input and truncated afterwards, so keeping 16 KB of a
		// 10M-char body allocated a second ~20 MB string on the large-object heap on the way past.
		string huge = "[31m" + new string('a', 10_000_000);

		long before = GC.GetAllocatedBytesForCurrentThread();
		string result = WireLog.Payload(huge);
		long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

		Assert.StartsWith("?[31m", result); // still sanitized inside the retained window
		Assert.EndsWith($"[truncated, {huge.Length} chars total]", result);
		Assert.InRange(allocated, 0, 1_000_000);
	}

	[Fact]
	public void Payload_CleanText_ReturnsSameInstance()
	{
		const string text = "<Sync>\n\t<Status>1</Status>\n</Sync>";
		Assert.Same(text, WireLog.Payload(text));
	}

	// S6/K21: bidi-override characters (U+202A-202E, U+2066-2069) reorder a wire-log dump's visible
	// content, the same smuggling risk LogTextTests.BidiOverrideCharacters_AreNeutralized covers for
	// LogText.Clean — but that defense existed on only the Clean path, not here. A hostile SendMail
	// Subject/body or DAV property containing a bidi override would ride straight through Payload
	// (they are Unicode format chars, NOT char.IsControl, so the control-only scan let them through)
	// into the Trace log line.
	[Fact]
	public void Payload_BidiOverrideCharacters_AreNeutralized()
	{
		// Right-to-Left Override (U+202E) and Pop Directional Isolate (U+2069) — written as escapes
		// (W9), never as raw literals: a raw override sitting unterminated in this source line would
		// itself be the Trojan Source hazard the classifier exists to defend against.
		Assert.DoesNotContain('\u202E', WireLog.Payload("Subject: admin\u202Eevil"));
		Assert.DoesNotContain('\u2069', WireLog.Payload("a\u2069b"));
		Assert.DoesNotContain('\u202A', WireLog.Payload("a\u202Ab"));
		Assert.DoesNotContain('\u2066', WireLog.Payload("a\u2066b"));
		Assert.Equal("admin?evil", WireLog.Payload("admin\u202Eevil"));
	}

	// W10: U+2028 (LINE SEPARATOR, category Zl) and U+2029 (PARAGRAPH SEPARATOR, category Zp) are
	// line terminators to StringReader/XmlReader, to JSON consumers reading a CLEF sink, and to log
	// viewers that split on Unicode line boundaries — but neither is char.IsControl (that is why the
	// bidi overrides needed an explicit range too), so IsUnsafe's control-only check lets them
	// through even on the allowLineStructure:false, single-field path (LogText.Clean on usernames
	// and device ids) whose entire purpose is to prevent an embedded line terminator from forging a
	// fake log line.
	[Fact]
	public void IsUnsafe_LineAndParagraphSeparators_AreUnsafeEvenWithoutLineStructure()
	{
		Assert.True(WireLog.IsUnsafe('\u2028', allowLineStructure: false));
		Assert.True(WireLog.IsUnsafe('\u2029', allowLineStructure: false));
		// And they must not slip through as "allowed line structure" either — only CR/LF/TAB do.
		Assert.True(WireLog.IsUnsafe('\u2028', allowLineStructure: true));
		Assert.True(WireLog.IsUnsafe('\u2029', allowLineStructure: true));
	}

	// W9: IsUnsafe's own classifier embedded the bidi-override code points it defends against as RAW
	// literal characters in its source line — exactly the Trojan Source hazard (CVE-2021-42574) the
	// check exists to prevent. An unterminated LRE/RLO sitting in the file reorders how the rest of
	// that line renders in any bidi-aware viewer (GitHub, most editors, a modern terminal's git
	// diff), so a reviewer can be shown text different from what the compiler sees. This scans the
	// UTF-8 bytes of the source file directly (not the compiled behavior, which is identical either
	// way) for the bidi-override code points as raw bytes.
	[Fact]
	public void WireLogSource_DoesNotEmbedRawBidiOverrideCharacters()
	{
		string source = File.ReadAllText(
			Path.Combine(FindRepoRoot(), "src", "ActiveSync.Protocol", "WireLog.cs"));

		// Ordinal is load-bearing here, not cosmetic: xUnit's culture-aware Assert.DoesNotContain
		// treats these very format characters as linguistically ignorable, so it can report a
		// "match" at position 0 of a string that does not contain the character at all — the
		// bidi-override defense hiding a false positive in the test that guards it.
		int[] bidiOverrideCodePoints = [0x202A, 0x202B, 0x202C, 0x202D, 0x202E, 0x2066, 0x2067, 0x2068, 0x2069];
		foreach (int codePoint in bidiOverrideCodePoints)
			Assert.DoesNotContain(char.ConvertFromUtf32(codePoint), source, StringComparison.Ordinal);
	}

	private static string FindRepoRoot()
	{
		DirectoryInfo? dir = new(AppContext.BaseDirectory);
		while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ActiveSync.slnx")))
			dir = dir.Parent;
		return dir?.FullName
			?? throw new InvalidOperationException("Could not locate repo root (ActiveSync.slnx) above the test binary.");
	}
}

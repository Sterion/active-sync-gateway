using ActiveSync.Server.Eas;

namespace ActiveSync.Server.Tests;

public class LogTextTests
{
	[Fact]
	public void ControlCharacters_AreNeutralized()
	{
		Assert.Equal("a??b?c", LogText.Clean("a\r\nb\tc"));
		Assert.DoesNotContain('\n', LogText.Clean("user\nFAKE 200 log line"));
		Assert.DoesNotContain('\x1b', LogText.Clean("user\x1b[31mansi"));
	}

	[Fact]
	public void LongValues_AreTruncated()
	{
		Assert.Equal(16, LogText.Clean(new string('x', 500), 16).Length);
	}

	[Fact]
	public void PlainText_PassesThroughUnchanged()
	{
		Assert.Equal("user@example.com", LogText.Clean("user@example.com"));
		Assert.Equal("", LogText.Clean(null));
	}

	// Bidi-override characters reorder a log line's visible content (a hostile
	// username can make "admin\u202E ...evil" render as if it were something else) but are
	// Unicode format chars (Cf), NOT char.IsControl, so the control-only scan let them
	// through. They must be neutralized too.
	[Fact]
	public void BidiOverrideCharacters_AreNeutralized()
	{
		// Right-to-Left Override (U+202E) and Pop Directional Isolate (U+2069) -- written as escapes
		// (never as raw literals): a raw override sitting unterminated in this source line would
		// itself be the Trojan Source hazard (CVE-2021-42574) this test guards against.
		Assert.DoesNotContain('\u202E', LogText.Clean("admin\u202Eevil"));
		Assert.DoesNotContain('\u2069', LogText.Clean("a\u2069b"));
		Assert.DoesNotContain('\u202A', LogText.Clean("a\u202Ab"));
		Assert.DoesNotContain('\u2066', LogText.Clean("a\u2066b"));
		Assert.Equal("admin?evil", LogText.Clean("admin\u202Eevil"));
	}

	// This test file embedded the very bidi-override code points it exercises as RAW literal
	// characters in its source — the Trojan Source hazard (CVE-2021-42574) the characters
	// themselves demonstrate: an unterminated LRE/RLO sitting in the file reorders how the rest of
	// that line renders in any bidi-aware viewer (GitHub, most editors, a modern terminal's git
	// diff), so a reviewer can be shown text different from what the compiler sees. This scans the
	// source file's own text directly (not the compiled behavior, which is identical either way)
	// for the bidi-override code points as raw characters.
	[Fact]
	public void SourceFile_DoesNotEmbedRawBidiOverrideCharacters()
	{
		string source = File.ReadAllText(
			Path.Combine(FindRepoRoot(), "tests", "ActiveSync.Server.Tests", "LogTextTests.cs"));

		// Ordinal is load-bearing here, not cosmetic: xUnit's culture-aware Assert.DoesNotContain
		// treats these very format characters as linguistically ignorable, so it can report a
		// "match" at position 0 of a string that does not contain the character at all.
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

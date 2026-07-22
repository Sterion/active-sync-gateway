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

	// E31: bidi-override characters reorder a log line's visible content (a hostile
	// username can make "admin‮ ...evil" render as if it were something else) but are
	// Unicode format chars (Cf), NOT char.IsControl, so the control-only scan let them
	// through. They must be neutralized too.
	[Fact]
	public void BidiOverrideCharacters_AreNeutralized()
	{
		// Right-to-Left Override (U+202E) and Pop Directional Isolate (U+2069):
		Assert.DoesNotContain('‮', LogText.Clean("admin‮evil"));
		Assert.DoesNotContain('⁩', LogText.Clean("a⁩b"));
		Assert.DoesNotContain('‪', LogText.Clean("a‪b"));
		Assert.DoesNotContain('⁦', LogText.Clean("a⁦b"));
		Assert.Equal("admin?evil", LogText.Clean("admin‮evil"));
	}
}

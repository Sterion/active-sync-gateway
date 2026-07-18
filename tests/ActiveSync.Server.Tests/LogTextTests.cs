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
}

using ActiveSync.Core.Logging;

namespace ActiveSync.Core.Tests;

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
	public void Payload_CleanText_ReturnsSameInstance()
	{
		const string text = "<Sync>\n\t<Status>1</Status>\n</Sync>";
		Assert.Same(text, WireLog.Payload(text));
	}
}

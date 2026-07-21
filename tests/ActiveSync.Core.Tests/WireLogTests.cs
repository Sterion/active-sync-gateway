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
}

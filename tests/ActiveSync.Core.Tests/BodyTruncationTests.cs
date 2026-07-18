using System.Text;
using ActiveSync.Backends.Converters;

namespace ActiveSync.Core.Tests;

public class BodyTruncationTests
{
	[Fact]
	public void Ascii_UnderLimit_Unchanged()
	{
		(string data, bool truncated, long estimated) = BodyText.ForBody("hello world", 100);
		Assert.Equal("hello world", data);
		Assert.False(truncated);
		Assert.Equal(11, estimated);
	}

	[Fact]
	public void Ascii_OverLimit_TruncatesToExactByteCount()
	{
		// ASCII path must stay byte-identical to the previous char-based behavior.
		(string data, bool truncated, long estimated) = BodyText.ForBody("hello world", 5);
		Assert.Equal("hello", data);
		Assert.True(truncated);
		Assert.Equal(11, estimated); // EstimatedDataSize = full size
		Assert.Equal(5, Encoding.UTF8.GetByteCount(data));
	}

	[Fact]
	public void MultiByte_LimitMidCodePoint_BacksOffToBoundary()
	{
		string text = "æøå"; // æøå = 6 UTF-8 bytes (2 each)
		Assert.Equal(6, Encoding.UTF8.GetByteCount(text));

		(string data, bool truncated, long estimated) = BodyText.ForBody(text, 5); // limit lands mid-code-point
		Assert.True(truncated);
		Assert.Equal(6, estimated);
		Assert.Equal("æø", data); // whole-code-point prefix ("æø")
		Assert.True(Encoding.UTF8.GetByteCount(data) <= 5);
		// Valid UTF-8 (no split code point): re-encoding round-trips.
		Assert.Equal(data, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(data)));
	}

	[Fact]
	public void Emoji_LimitMidCodePoint_DropsThePartialCodePoint()
	{
		string text = "AB\U0001F600"; // "AB😀" — 😀 is 4 UTF-8 bytes
		(string data, bool truncated, _) = BodyText.ForBody(text, 3); // 3 bytes lands inside the emoji
		Assert.True(truncated);
		Assert.Equal("AB", data);
		Assert.True(Encoding.UTF8.GetByteCount(data) <= 3);
	}

	[Fact]
	public void MultiByte_LimitExactlyOnBoundary_KeepsTheWholePrefix()
	{
		// 4 bytes cuts "æøå" exactly between 'ø' and 'å' — the complete "æø" must survive
		// (the old backoff logic dropped the fully valid trailing 'ø').
		(string data, bool truncated, long estimated) = BodyText.ForBody("æøå", 4);
		Assert.True(truncated);
		Assert.Equal(6, estimated);
		Assert.Equal("æø", data);
		Assert.Equal(4, Encoding.UTF8.GetByteCount(data));
	}

	[Fact]
	public void Ascii_FollowedByMultiByte_BoundaryCutKeepsAscii()
	{
		(string data, _, _) = BodyText.ForBody("ABæ", 2); // cut lands exactly before 'æ'
		Assert.Equal("AB", data);
	}

	[Fact]
	public void NoLimit_Unchanged()
	{
		(string data, bool truncated, _) = BodyText.ForBody("æøå", null);
		Assert.Equal("æøå", data);
		Assert.False(truncated);
	}
}

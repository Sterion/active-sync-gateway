using ActiveSync.Protocol.Wbxml;

namespace ActiveSync.Protocol.Tests;

/// <summary>
///   Hostile-input tests: every malformed document must surface as WbxmlException (mapped to
///   HTTP 400), never as an unrelated runtime exception (mapped to 500).
/// </summary>
public class WbxmlDecoderHardeningTests
{
	// version 1.3, public id 1, charset UTF-8, string table length 0
	private static readonly byte[] Header = [0x03, 0x01, 0x6A, 0x00];

	private static byte[] Doc(params byte[][] parts)
	{
		List<byte> all = new(Header);
		foreach (byte[] part in parts)
			all.AddRange(part);
		return [.. all];
	}

	[Fact]
	public void OpaqueLength_OverflowingInt_IsAParseError()
	{
		// airsync:Sync (page 0 tag 0x05) with content, then OPAQUE with a 5-byte
		// multi-byte uint length (≈ 2^35) whose int cast is negative.
		byte[] doc = Doc(
			[0x45], // Sync, with content
			[0xC3], // OPAQUE
			[0xFF, 0xFF, 0xFF, 0xFF, 0x7F]);
		Assert.Throws<WbxmlException>(() => WbxmlDecoder.Decode(doc));
	}

	[Fact]
	public void OpaqueLength_LargerThanBody_IsAParseError()
	{
		byte[] doc = Doc(
			[0x45],
			[0xC3],
			[0x50], // claims 80 bytes
			[0x01, 0x02, 0x03]); // provides 3
		Assert.Throws<WbxmlException>(() => WbxmlDecoder.Decode(doc));
	}

	[Fact]
	public void StringTableLength_OverflowingInt_IsAParseError()
	{
		// Header with a hostile string table length instead of 0.
		byte[] doc = [0x03, 0x01, 0x6A, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F, 0x45];
		Assert.Throws<WbxmlException>(() => WbxmlDecoder.Decode(doc));
	}

	[Fact]
	public void TruncatedDocument_IsAParseError()
	{
		Assert.Throws<WbxmlException>(() => WbxmlDecoder.Decode([0x03, 0x01]));
	}

	[Theory]
	[InlineData(0xF0, 0x90, 0x80, 0x00)] // 0x110000 — above the Unicode max, ConvertFromUtf32 throws
	[InlineData(0x03, 0xD8, 0x00, 0x00)] // 0xD800 — a UTF-16 surrogate, also invalid
	public void EntityWithInvalidCodePoint_IsAParseError(byte b1, byte b2, byte b3, byte b4)
	{
		// airsync:Sync (0x45, with content) then ENTITY (0x02) + a multi-byte uint code point.
		byte[] doc = Doc([0x45], [0x02], [b1, b2, b3, b4]);
		Assert.Throws<WbxmlException>(() => WbxmlDecoder.Decode(doc));
	}

	[Fact]
	public void EntityWithValidCodePoint_Decodes()
	{
		// ENTITY for 'A' (0x41) inside airsync:Sync must decode without throwing.
		byte[] doc = Doc([0x45], [0x02], [0x41], [0x01]); // 0x01 = END (close Sync)
		System.Xml.Linq.XDocument result = WbxmlDecoder.Decode(doc);
		Assert.Contains("A", result.Root?.Value ?? "");
	}
}

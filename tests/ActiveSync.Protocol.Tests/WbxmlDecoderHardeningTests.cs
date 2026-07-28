using System.Buffers;
using System.Reflection;
using System.Text;
using System.Xml.Linq;
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

	[Fact]
	public async Task DecodeAsync_StreamOverTheLimit_IsAParseError()
	{
		// A chunked request has no Content-Length, so the only way to bound the buffer is to
		// stop copying at the ceiling. The stream here is well-formed WBXML — it is refused
		// on size alone, before decoding.
		byte[] oversized = Doc([0x45], new byte[4096], [0x01]);
		using MemoryStream stream = new(oversized);
		await Assert.ThrowsAsync<WbxmlException>(
			() => WbxmlDecoder.DecodeAsync(stream, CancellationToken.None, 1024));
	}

	[Fact]
	public async Task DecodeAsync_StreamUnderTheLimit_Decodes()
	{
		byte[] valid = Doc([0x45], [0x01]);
		using MemoryStream stream = new(valid);
		System.Xml.Linq.XDocument result =
			await WbxmlDecoder.DecodeAsync(stream, CancellationToken.None, 1024);
		Assert.Equal("Sync", result.Root!.Name.LocalName);
	}

	[Fact]
	public void MultiByteInteger_WrappingToASmallValue_IsAParseError()
	{
		// 0x90 0x80 0x80 0x80 0x03 encodes 2^32 + 3, which truncates to exactly 3 in a uint
		// accumulator. That is the dangerous shape: unlike a length that wraps negative, a
		// wrap to a small legal value passes every downstream bounds check silently — here
		// the OPAQUE run consumes 3 of the following bytes and the document decodes clean.
		byte[] doc = Doc(
			[0x45], // Sync, with content
			[0xC3], // OPAQUE
			[0x90, 0x80, 0x80, 0x80, 0x03],
			[0x01, 0x02, 0x03],
			[0x01]); // END
		Assert.Throws<WbxmlException>(() => WbxmlDecoder.Decode(doc));
	}

	[Fact]
	public void DeeplyNestedDocument_IsAParseError()
	{
		// 4000 nested airsync:Sync-with-content, each properly closed — so the document is
		// otherwise well-formed and only the depth cap can reject it. One byte each on the
		// wire, one XElement each in memory: this is the OOM amplifier, and a 64 MB body of
		// them yields ~64M elements and several GB of heap.
		byte[] open = new byte[4000];
		Array.Fill(open, (byte)0x45);
		byte[] close = new byte[4000];
		Array.Fill(close, (byte)0x01);
		Assert.Throws<WbxmlException>(() => WbxmlDecoder.Decode(Doc(open, close)));
	}

	[Fact]
	public void TooManyElements_IsAParseError()
	{
		// Flat rather than nested — 300k empty siblings inside one closed root, so the depth
		// cap never fires and the document is well-formed: only the element count stops it.
		byte[] flat = new byte[300_000];
		Array.Fill(flat, (byte)0x05); // airsync:Sync, no content
		Assert.Throws<WbxmlException>(() => WbxmlDecoder.Decode(Doc([0x45], flat, [0x01])));
	}

	[Fact]
	public void NestingWithinTheDepthLimit_Decodes()
	{
		// 200 nested elements, closed properly — comfortably under the 256 cap, and far
		// deeper than any real EAS document. Guards the cap against being set too low.
		byte[] open = new byte[200];
		Array.Fill(open, (byte)0x45);
		byte[] close = new byte[200];
		Array.Fill(close, (byte)0x01);
		System.Xml.Linq.XDocument result = WbxmlDecoder.Decode(Doc(open, close));
		Assert.NotNull(result.Root);
	}

	[Fact]
	public void RepeatedShallowNesting_DoesNotAccumulateDepth()
	{
		// 1000 siblings that each open and close: the running depth never exceeds 2, so a
		// decoder that increments without decrementing on END would reject this valid document.
		List<byte> body = [0x45]; // outer Sync, with content
		for (int i = 0; i < 1000; i++)
		{
			body.Add(0x45);
			body.Add(0x01);
		}

		body.Add(0x01);
		System.Xml.Linq.XDocument result = WbxmlDecoder.Decode(Doc([.. body]));
		Assert.Equal(1000, result.Root!.Elements().Count());
	}

	[Fact]
	public void TooManyTextRuns_IsAParseError()
	{
		// 200,001 empty STR_I runs (2 bytes each: STR_I token + null terminator) inside one
		// open element. No single run is large and neither MaxElements nor MaxDepth ever
		// fires (one element, depth 1) — only the RUN COUNT is hostile, and each run
		// allocates a fresh XText with no cap.
		const int runCount = 200_001;
		byte[] runs = new byte[runCount * 2];
		for (int i = 0; i < runCount; i++)
		{
			runs[i * 2] = 0x03; // STR_I
			runs[i * 2 + 1] = 0x00; // empty string, null-terminated
		}

		Assert.Throws<WbxmlException>(() => WbxmlDecoder.Decode(Doc([0x45], runs, [0x01])));
	}

	[Fact]
	public void EntityWithIllegalXmlControlCharacter_IsAParseError()
	{
		// ENTITY for U+000B (vertical tab) — a valid Unicode scalar value, so the old guard
		// (out-of-range / surrogate only) let it through into an XText; it is NOT a legal
		// XML 1.0 Char, so XNode.ToString() (wire-trace logging) throws ArgumentException —
		// turning a 400 into an uncontrolled 500 on any Trace-enabled gateway.
		byte[] doc = Doc([0x45], [0x02], [0x0B], [0x01]);
		Assert.Throws<WbxmlException>(() => WbxmlDecoder.Decode(doc));
	}

	[Fact]
	public void StrIWithIllegalXmlControlCharacter_IsAParseError()
	{
		// Same illegal-XML-character gap (U+000B) reached via the inline-string path
		// (ReadNullTerminatedString) instead of ENTITY.
		byte[] doc = Doc([0x45], [0x03], [0x0B, 0x00], [0x01]);
		Assert.Throws<WbxmlException>(() => WbxmlDecoder.Decode(doc));
	}

	[Fact]
	public void UnknownTagToken_BecomesPlaceholderInsteadOfAbortingTheDocument()
	{
		// Token 0x2A on code page 0 (AirSync) is unassigned — every token 0x05-0x29 on that
		// page is taken, so this really is "unknown", not a table gap. A single missing or
		// newly-specified token must not abort the WHOLE document; the sibling stream
		// after it should still decode instead of the whole Sync/FolderSync 400ing.
		byte[] doc = Doc(
			[0x45], // Sync, with content
			[0x2A], // unknown tag token, no content
			[0x0B], // SyncKey, no content — a KNOWN sibling that comes AFTER the unknown one
			[0x01]); // END Sync

		System.Xml.Linq.XDocument result = WbxmlDecoder.Decode(doc);
		Assert.Equal(2, result.Root!.Elements().Count());
		XElement placeholder = result.Root!.Elements().First();
		Assert.Equal("SyncKey", result.Root!.Elements().Last().Name.LocalName);
		// The placeholder is tagged with the internal marker namespace (not the page's own),
		// so a caller (EasContext) can find and log it without WbxmlDecoder needing a logging
		// hook of its own — that would change ActiveSync.Protocol's published public surface.
		Assert.Equal(EasNamespaces.WbxmlInternal, placeholder.Name.Namespace);
		Assert.Equal("unknown-0-2a", placeholder.Name.LocalName);
	}

	[Fact]
	public void UnknownCodePage_StillAbortsTheDocument()
	{
		// Unlike an unknown TAG, an unknown CODE PAGE means the following bytes are genuinely
		// uninterpretable (no table to fall back on) — this stays a hard failure.
		byte[] doc = Doc([0x00], [0x63], [0x45], [0x01]); // SWITCH_PAGE to page 99
		Assert.Throws<WbxmlException>(() => WbxmlDecoder.Decode(doc));
	}

	[Fact]
	public void TooManyTextCharacters_IsAParseError()
	{
		// One STR_I run whose decoded length alone exceeds the 8 MB text-character ceiling —
		// the same amplification doesn't need many runs, a single huge one works too;
		// this proves the character-count ceiling independently of the run-count one above.
		byte[] text = new byte[8 * 1024 * 1024 + 1];
		Array.Fill(text, (byte)'a');
		byte[] doc = Doc([0x45], [0x03], text, [0x00], [0x01]);
		Assert.Throws<WbxmlException>(() => WbxmlDecoder.Decode(doc));
	}

	// DecodeAsync's 80 KB scratch buffer holds one user's raw request bytes — including any
	// plaintext SyncKey/ClientId/etc it carries — and ArrayPool<byte>.Shared is process-global, so
	// an unscrubbed Return leaves that plaintext readable by the next renter of the same size
	// class, potentially a different user's request on the same worker. Best-effort (ArrayPool does
	// not contractually guarantee returning the same array on the next Rent of the same size), but
	// reliable in practice for a single-threaded, uncontended Rent/Return/Rent in one test method —
	// the same technique is the standard way to test pool-scrubbing behavior.
	[Fact]
	public async Task DecodeAsync_ReturnsItsScratchBufferScrubbed()
	{
		const string marker = "W15SCRATCHBUFFERMARKERVALUE";
		byte[] markerBytes = Encoding.ASCII.GetBytes(marker);
		byte[] doc = Doc([0x45], [0x4B], [0x03], markerBytes, [0x00], [0x01], [0x01]);

		using MemoryStream stream = new(doc);
		await WbxmlDecoder.DecodeAsync(stream, CancellationToken.None);

		byte[] rented = ArrayPool<byte>.Shared.Rent(81920); // same size DecodeAsync itself rents
		try
		{
			string asText = Encoding.ASCII.GetString(rented);
			Assert.DoesNotContain(marker, asText, StringComparison.Ordinal);
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(rented);
		}
	}

	// MaxDocumentBytes is a policy knob (unlike EasFolderType/EasClass, which are genuinely
	// frozen protocol constants), but it was declared `public const`, so a plugin built against
	// contract 1.x — ActiveSync.Protocol is a published MIT package — has it INLINED into its own
	// IL. If the host later raises the ceiling, the plugin keeps passing the OLD value wherever it
	// reads WbxmlDecoder.MaxDocumentBytes as a `maxBytes` argument, while appearing (to a reader of
	// the plugin's source) to report the host's current limit. AGENTS.md documents this exact
	// hazard for ContractVersion.Major/Minor; this is the same hazard on a different member.
	[Fact]
	public void MaxDocumentBytes_IsNotAConstField()
	{
		FieldInfo field = typeof(WbxmlDecoder).GetField(nameof(WbxmlDecoder.MaxDocumentBytes))!;
		Assert.False(field.IsLiteral, "MaxDocumentBytes is `const` — its value gets inlined into every external caller's IL.");
		Assert.True(field.IsStatic && field.IsInitOnly, "Expected a `public static readonly` field.");
	}
}

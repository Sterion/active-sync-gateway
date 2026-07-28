using System.Buffers;
using System.Text;
using System.Xml.Linq;
using ActiveSync.Protocol.Wbxml;

namespace ActiveSync.Protocol.Tests;

/// <summary>
///   Encoder-side limits and error mapping. A response document that the encoder cannot handle
///   must fail as WbxmlException — never as a process-killing StackOverflowException, and never
///   as an unrelated runtime exception thrown midway through a partially written body.
/// </summary>
public class WbxmlEncoderHardeningTests
{
	private static readonly XNamespace AirSync = EasNamespaces.AirSync;

	/// <summary>Nests <paramref name="depth" /> airsync:Sync elements, innermost last.</summary>
	private static XDocument Nested(int depth)
	{
		XElement root = new(AirSync + "Sync");
		XElement current = root;
		for (int i = 1; i < depth; i++)
		{
			XElement child = new(AirSync + "Sync");
			current.Add(child);
			current = child;
		}

		return new XDocument(root);
	}

	[Fact]
	public void OverDeepDocument_IsAWbxmlException()
	{
		// WriteElement recurses once per level. Unbounded, a deep enough document kills the
		// process with an uncatchable StackOverflowException; bounded, it is a clean 400.
		Assert.Throws<WbxmlException>(() => WbxmlEncoder.Encode(Nested(300)));
	}

	[Theory]
	[InlineData("not base64!!")]
	[InlineData("YWJj=")] // valid alphabet, invalid padding
	public void OpaqueElementWithMalformedBase64_IsAWbxmlException(string text)
	{
		// Convert.FromBase64String throws FormatException, which escapes as an uncontrolled
		// 500 — and it throws partway through encoding, with the response half written.
		XElement mime = new(EasNamespaces.ComposeMail + "Mime", text);
		mime.SetAttributeValue(EasNamespaces.OpaqueAttribute, "1");
		XDocument doc = new(new XElement(EasNamespaces.ComposeMail + "SendMail", mime));

		Assert.Throws<WbxmlException>(() => WbxmlEncoder.Encode(doc));
	}

	[Fact]
	public void OpaqueElementWithLargePayload_RoundTrips()
	{
		// Guards the pooled-buffer decode path against sizing bugs: the rented buffer is
		// larger than the payload, so only the written count may reach the wire.
		byte[] payload = new byte[200_000];
		Random.Shared.NextBytes(payload);
		XElement mime = new(EasNamespaces.ComposeMail + "Mime", Convert.ToBase64String(payload));
		mime.SetAttributeValue(EasNamespaces.OpaqueAttribute, "1");
		XDocument doc = new(new XElement(EasNamespaces.ComposeMail + "SendMail", mime));

		XDocument result = WbxmlDecoder.Decode(WbxmlEncoder.Encode(doc));

		Assert.Equal(payload, Convert.FromBase64String(
			result.Root!.Element(EasNamespaces.ComposeMail + "Mime")!.Value));
	}

	// W15: WriteOpaque's rented scratch buffer holds one user's decoded MIME/attachment plaintext,
	// and ArrayPool<byte>.Shared is process-global — unscrubbed, the next renter of the same size
	// class (potentially a different user's request on the same worker) can read the tail of it.
	// Best-effort (ArrayPool does not contractually guarantee returning the same array on the next
	// Rent of the same size), but reliable in practice for a single-threaded, uncontended
	// Rent/Return/Rent in one test method.
	[Fact]
	public void Encode_ReturnsItsOpaqueScratchBufferScrubbed()
	{
		const string marker = "W15OPAQUESCRATCHMARKERVALUEXYZ";
		byte[] payload = Encoding.ASCII.GetBytes(marker);
		string base64 = Convert.ToBase64String(payload);
		XElement mime = new(EasNamespaces.ComposeMail + "Mime", base64);
		mime.SetAttributeValue(EasNamespaces.OpaqueAttribute, "1");
		XDocument doc = new(new XElement(EasNamespaces.ComposeMail + "SendMail", mime));

		_ = WbxmlEncoder.Encode(doc);

		// Same sizing formula WriteOpaque itself rents with.
		int rentSize = (base64.Length / 4 + 1) * 3;
		byte[] rented = ArrayPool<byte>.Shared.Rent(rentSize);
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

	[Fact]
	public void ElementWithBothTextAndChildren_KeepsBoth()
	{
		// The old text-or-children rule wrote the children and dropped the text silently.
		XElement sync = new(AirSync + "Sync",
			new XText("lead"),
			new XElement(AirSync + "SyncKey", "1"),
			new XText("trail"));

		XDocument result = WbxmlDecoder.Decode(WbxmlEncoder.Encode(new XDocument(sync)));

		Assert.Equal("1", result.Root!.Element(AirSync + "SyncKey")!.Value);
		string text = string.Concat(result.Root.Nodes().OfType<XText>().Select(t => t.Value));
		Assert.Equal("leadtrail", text);
	}

	[Fact]
	public void OpaqueElementWithChildren_IsAWbxmlException()
	{
		// Opaque payload and container are mutually exclusive; the old code wrote the payload
		// and dropped the children, emitting a document that did not match what was built.
		XElement mime = new(EasNamespaces.ComposeMail + "Mime",
			Convert.ToBase64String("hi"u8.ToArray()),
			new XElement(EasNamespaces.ComposeMail + "ClientId", "c1"));
		mime.SetAttributeValue(EasNamespaces.OpaqueAttribute, "1");

		Assert.Throws<WbxmlException>(() =>
			WbxmlEncoder.Encode(new XDocument(new XElement(EasNamespaces.ComposeMail + "SendMail", mime))));
	}

	[Fact]
	public void WhitespaceBetweenChildElements_IsNotEmittedAsContent()
	{
		// Indentation from a parsed document is formatting, not content — walking nodes in
		// order must not start injecting it into values the client reads back.
		XElement sync = new(AirSync + "Sync",
			new XText("\n  "),
			new XElement(AirSync + "SyncKey", "1"),
			new XText("\n"));

		XDocument result = WbxmlDecoder.Decode(WbxmlEncoder.Encode(new XDocument(sync)));

		Assert.Empty(result.Root!.Nodes().OfType<XText>());
	}

	[Fact]
	public void TextWithEmbeddedNul_DoesNotScrambleTheDocument()
	{
		// STR_I is NUL-terminated: an embedded NUL used to end the string early, and every
		// byte after it was read as tokens — so the following sibling was lost or the whole
		// document failed to decode.
		XElement sync = new(AirSync + "Sync",
			new XElement(AirSync + "SyncKey", "ab\0cd"),
			new XElement(AirSync + "CollectionId", "5"));

		XDocument result = WbxmlDecoder.Decode(WbxmlEncoder.Encode(new XDocument(sync)));

		Assert.Equal("abcd", result.Root!.Element(AirSync + "SyncKey")!.Value);
		Assert.Equal("5", result.Root.Element(AirSync + "CollectionId")!.Value);
	}

	[Fact]
	public void DocumentAtTheDepthLimit_Encodes()
	{
		// Guards the cap against being set too low — 256 levels is the limit, and the
		// decoder accepts exactly the same depth, so a round trip cannot fail one-sided.
		byte[] bytes = WbxmlEncoder.Encode(Nested(256));
		XDocument decoded = WbxmlDecoder.Decode(bytes);
		Assert.NotNull(decoded.Root);
	}

	[Fact]
	public async Task EncodeAsync_LargePayload_RoundTrips()
	{
		// Guards EncodeAsync specifically (not just Encode, which OpaqueElementWithLargePayload_
		// RoundTrips above already covers) now that it builds and writes its own MemoryStream
		// rather than delegating to Encode() (W14) — a sizing bug in that path would still show up
		// as a broken round trip even though the allocation improvement itself is not observable
		// from outside the type.
		byte[] payload = new byte[200_000];
		Random.Shared.NextBytes(payload);
		XElement mime = new(EasNamespaces.ComposeMail + "Mime", Convert.ToBase64String(payload));
		mime.SetAttributeValue(EasNamespaces.OpaqueAttribute, "1");
		XDocument doc = new(new XElement(EasNamespaces.ComposeMail + "SendMail", mime));

		using MemoryStream destination = new();
		await WbxmlEncoder.EncodeAsync(doc, destination, CancellationToken.None);

		XDocument result = WbxmlDecoder.Decode(destination.ToArray());
		Assert.Equal(payload, Convert.FromBase64String(
			result.Root!.Element(EasNamespaces.ComposeMail + "Mime")!.Value));
	}

	// W14: EncodeAsync used to call the synchronous Encode() (which finishes with output.ToArray(),
	// a full extra copy of an already-doubled MemoryStream buffer) and then write that array to the
	// destination — a large ItemOperations attachment response paid roughly an extra payload-sized
	// allocation for a copy the stream write never needed. The fix is a structural one (which method
	// EncodeAsync's body calls), not something that changes EncodeAsync_LargePayload_RoundTrips'
	// observable output — a benchmark-style allocation comparison between the two shapes was tried
	// first and discarded: on an 8 MB payload the "removed" copy (one payload's worth) was smaller
	// than the run-to-run GC/allocator noise between two successive large encodes, so it could not
	// tell red from green reliably. Reading the source directly is the same technique
	// DependencyRuleTests already uses for "the compiled behavior is identical either way" checks
	// (e.g. JmapMailStore_IsSplitIntoPartialFilesByConcern, Cs0618Suppressions_AreScopedNarrowly).
	[Fact]
	public void EncodeAsync_DoesNotDelegateToEncode_AndWritesFromTheStreamBufferDirectly()
	{
		string source = File.ReadAllText(
			Path.Combine(FindRepoRoot(), "src", "ActiveSync.Protocol", "Wbxml", "WbxmlEncoder.cs"));

		int encodeAsyncStart = source.IndexOf("public static async Task EncodeAsync(", StringComparison.Ordinal);
		Assert.True(encodeAsyncStart >= 0, "Could not locate the EncodeAsync method body.");
		// WriteElement is the next member in the file on both sides of the W14 fix, so it is a
		// stable end-of-body marker regardless of whether BuildStream exists yet.
		int bodyEnd = source.IndexOf("private static void WriteElement", encodeAsyncStart, StringComparison.Ordinal);
		Assert.True(bodyEnd > encodeAsyncStart, "Could not locate the end of the EncodeAsync method body.");
		string body = source[encodeAsyncStart..bodyEnd];

		Assert.DoesNotContain("Encode(document)", body, StringComparison.Ordinal);
		Assert.Contains("GetBuffer()", body, StringComparison.Ordinal);
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

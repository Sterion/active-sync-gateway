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
}

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

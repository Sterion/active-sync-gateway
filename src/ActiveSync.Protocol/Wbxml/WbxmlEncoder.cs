using System.Buffers;
using System.Text;
using System.Xml.Linq;

namespace ActiveSync.Protocol.Wbxml;

/// <summary>
///   Encodes an <see cref="XDocument" /> (element namespaces = EAS code page names) to WBXML.
///   Elements marked with <see cref="EasNamespaces.OpaqueAttribute" />="1" have their text content
///   treated as base64 and emitted as OPAQUE data.
/// </summary>
public static class WbxmlEncoder
{
	private const byte SwitchPage = 0x00;
	private const byte End = 0x01;
	private const byte StrI = 0x03;
	private const byte Opaque = 0xC3;

	// WriteElement recurses once per level, and a StackOverflowException cannot be caught —
	// it kills the process rather than failing the one response. Mirror the decoder's ceiling
	// so an over-deep document is a WbxmlException instead. Nothing legal comes close: a
	// document decoded from the wire is already capped at this depth.
	private const int MaxDepth = 256;

	public static byte[] Encode(XDocument document)
	{
		if (document.Root is null)
			throw new WbxmlException("Cannot encode an empty document.");

		MemoryStream output = new();
		output.WriteByte(0x03); // WBXML 1.3
		output.WriteByte(0x01); // public id: unknown
		output.WriteByte(0x6A); // charset: UTF-8
		output.WriteByte(0x00); // string table length: 0

		int currentPage = 0;
		WriteElement(output, document.Root, ref currentPage, 1);
		return output.ToArray();
	}

	public static async Task EncodeAsync(XDocument document, Stream destination, CancellationToken ct)
	{
		byte[] bytes = Encode(document);
		await destination.WriteAsync(bytes, ct).ConfigureAwait(false);
	}

	private static void WriteElement(MemoryStream output, XElement element, ref int currentPage, int depth)
	{
		if (depth > MaxDepth)
			throw new WbxmlException($"Document nests deeper than {MaxDepth} elements.");

		WbxmlCodePages.CodePage page = WbxmlCodePages.ForNamespace(element.Name.Namespace)
		                               ?? throw new WbxmlException(
			                               $"No WBXML code page for namespace '{element.Name.Namespace}'.");
		if (!page.Reverse.TryGetValue(element.Name.LocalName, out byte token))
			throw new WbxmlException(
				$"Tag '{element.Name.LocalName}' is not defined on code page {page.Index} ({page.Namespace}).");

		if (page.Index != currentPage)
		{
			output.WriteByte(SwitchPage);
			output.WriteByte((byte)page.Index);
			currentPage = page.Index;
		}

		bool isOpaque = (string?)element.Attribute(EasNamespaces.OpaqueAttribute) == "1";
		List<XElement> childElements = element.Elements().ToList();
		string? text = element.Nodes().OfType<XText>().Any()
			? string.Concat(element.Nodes().OfType<XText>().Select(t => t.Value))
			: null;
		bool hasContent = childElements.Count > 0 || !string.IsNullOrEmpty(text) || isOpaque;

		output.WriteByte(hasContent ? (byte)(token | 0x40) : token);
		if (!hasContent)
			return;

		if (isOpaque)
		{
			WriteOpaque(output, text);
		}
		else if (childElements.Count > 0)
		{
			foreach (XElement child in childElements)
				WriteElement(output, child, ref currentPage, depth + 1);
		}
		else
		{
			output.WriteByte(StrI);
			byte[] bytes = Encoding.UTF8.GetBytes(text!);
			output.Write(bytes);
			output.WriteByte(0x00);
		}

		output.WriteByte(End);
	}

	/// <summary>Emits <paramref name="base64" /> as an OPAQUE run.</summary>
	private static void WriteOpaque(MemoryStream output, string? base64)
	{
		if (string.IsNullOrEmpty(base64))
		{
			output.WriteByte(Opaque);
			WriteMultiByteUInt(output, 0);
			return;
		}

		// Decode into a pooled buffer rather than via Convert.FromBase64String: opaque runs
		// carry attachment and MIME payloads of megabytes, and every intermediate byte[] that
		// size is a large-object-heap allocation, per attachment, per request.
		byte[] scratch = ArrayPool<byte>.Shared.Rent((base64.Length / 4 + 1) * 3);
		try
		{
			// Malformed base64 has to surface as WbxmlException. Convert throws FormatException,
			// which escapes as an uncontrolled 500 — and it does so from the middle of encoding,
			// with part of the response already written.
			if (!Convert.TryFromBase64Chars(base64, scratch, out int written))
				throw new WbxmlException("Element marked opaque does not hold valid base64.");

			output.WriteByte(Opaque);
			WriteMultiByteUInt(output, (uint)written);
			output.Write(scratch, 0, written);
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(scratch);
		}
	}

	private static void WriteMultiByteUInt(MemoryStream output, uint value)
	{
		Span<byte> scratch = stackalloc byte[5];
		int count = 0;
		do
		{
			scratch[count++] = (byte)(value & 0x7F);
			value >>= 7;
		} while (value > 0);

		for (int i = count - 1; i >= 0; i--)
			output.WriteByte(i == 0 ? scratch[i] : (byte)(scratch[i] | 0x80));
	}
}

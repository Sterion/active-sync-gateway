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
			// An element is either a container or an opaque payload — it cannot be both, and
			// silently dropping one half of it puts a document on the wire that does not say
			// what the caller built. Refuse instead.
			if (childElements.Count > 0)
				throw new WbxmlException(
					$"Element '{element.Name.LocalName}' is marked opaque but has child elements.");
			WriteOpaque(output, text);
		}
		else
		{
			// Walk the nodes in document order rather than text-or-children: WBXML content is
			// a sequence of inline strings and elements, and an element carrying both used to
			// lose its text entirely.
			foreach (XNode node in element.Nodes())
			{
				switch (node)
				{
					case XElement child:
						WriteElement(output, child, ref currentPage, depth + 1);
						break;
					// Whitespace between child elements is XML formatting, not content — the
					// previous text-or-children rule discarded it, and emitting it now would
					// inject indentation into every value the client reads back.
					case XText t when t.Value.Length > 0
					                  && (childElements.Count == 0 || !string.IsNullOrWhiteSpace(t.Value)):
						WriteInlineString(output, t.Value);
						break;
				}
			}
		}

		output.WriteByte(End);
	}

	/// <summary>Emits <paramref name="text" /> as a NUL-terminated STR_I run.</summary>
	private static void WriteInlineString(MemoryStream output, string text)
	{
		output.WriteByte(StrI);
		// STR_I is NUL-terminated, so an embedded NUL ends the string early and every byte
		// after it is read as tokens — the rest of the document decodes as garbage rather
		// than failing. NUL is not legal in XML content either, so drop it. Throwing instead
		// would fail a whole sync response over one bad byte in one backend-supplied value.
		byte[] bytes = Encoding.UTF8.GetBytes(text.Contains('\0') ? text.Replace("\0", "") : text);
		output.Write(bytes);
		output.WriteByte(0x00);
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

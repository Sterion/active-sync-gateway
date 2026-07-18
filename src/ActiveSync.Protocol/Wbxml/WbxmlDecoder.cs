using System.Text;
using System.Xml.Linq;

namespace ActiveSync.Protocol.Wbxml;

/// <summary>
///   Decodes an EAS WBXML document (MS-ASWBXML) into an <see cref="XDocument" /> whose element
///   namespaces are the EAS code page names. OPAQUE data is stored as base64 text with the
///   <see cref="EasNamespaces.OpaqueAttribute" /> marker set to "1".
/// </summary>
public static class WbxmlDecoder
{
	private const byte SwitchPage = 0x00;
	private const byte End = 0x01;
	private const byte Entity = 0x02;
	private const byte StrI = 0x03;
	private const byte Literal = 0x04;
	private const byte ExtI0 = 0x40, ExtI1 = 0x41, ExtI2 = 0x42;
	private const byte Pi = 0x43;
	private const byte LiteralC = 0x44;
	private const byte ExtT0 = 0x80, ExtT1 = 0x81, ExtT2 = 0x82;
	private const byte StrT = 0x83;
	private const byte LiteralA = 0x84;
	private const byte Ext0 = 0xC0, Ext1 = 0xC1, Ext2 = 0xC2;
	private const byte Opaque = 0xC3;
	private const byte LiteralAc = 0xC4;

	public static async Task<XDocument> DecodeAsync(Stream stream, CancellationToken ct)
	{
		using MemoryStream buffer = new();
		await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
		return Decode(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
	}

	public static XDocument Decode(ReadOnlySpan<byte> data)
	{
		SpanReader reader = new(data);

		byte version = reader.ReadByte();
		if (version != 0x03 && version != 0x02 && version != 0x01)
			throw new WbxmlException($"Unsupported WBXML version 0x{version:X2}.");
		uint publicId = reader.ReadMultiByteUInt();
		if (publicId == 0)
			_ = reader.ReadMultiByteUInt(); // string table index of public id — not used by EAS
		uint charset = reader.ReadMultiByteUInt();
		if (charset != 0x6A && charset != 0x03) // UTF-8 or US-ASCII
			throw new WbxmlException($"Unsupported WBXML charset 0x{charset:X2}.");
		uint stringTableLength = reader.ReadMultiByteUInt();
		reader.Skip((int)stringTableLength); // EAS does not use the string table

		XDocument doc = new();
		WbxmlCodePages.CodePage page = WbxmlCodePages.ForIndex(0)!;
		XElement? current = null;

		while (!reader.AtEnd)
		{
			byte token = reader.ReadByte();
			switch (token)
			{
				case SwitchPage:
					byte pageIndex = reader.ReadByte();
					page = WbxmlCodePages.ForIndex(pageIndex)
					       ?? throw new WbxmlException($"Unknown WBXML code page {pageIndex}.");
					break;

				case End:
					if (current is null)
						throw new WbxmlException("Unbalanced END token.");
					current = current.Parent;
					break;

				case StrI:
					AppendText(current, reader.ReadNullTerminatedString());
					break;

				case Opaque:
					uint length = reader.ReadMultiByteUInt();
					ReadOnlySpan<byte> bytes = reader.ReadBytes((int)length);
					if (current is null)
						throw new WbxmlException("OPAQUE data outside of an element.");
					current.SetAttributeValue(EasNamespaces.OpaqueAttribute, "1");
					AppendText(current, Convert.ToBase64String(bytes));
					break;

				case Entity:
					uint code = reader.ReadMultiByteUInt();
					// Guard before ConvertFromUtf32, which throws ArgumentOutOfRangeException
					// (→ uncontrolled 500) for out-of-range or surrogate code points.
					if (code > 0x10FFFF || code is >= 0xD800 and <= 0xDFFF)
						throw new WbxmlException($"Invalid ENTITY code point U+{code:X}.");
					AppendText(current, char.ConvertFromUtf32((int)code));
					break;

				case StrT:
					_ = reader.ReadMultiByteUInt();
					throw new WbxmlException("String table references are not used by EAS.");

				case Literal:
				case LiteralA:
				case LiteralC:
				case LiteralAc:
					throw new WbxmlException("LITERAL tokens are not used by EAS.");

				case Pi:
					throw new WbxmlException("Processing instructions are not used by EAS.");

				case ExtI0:
				case ExtI1:
				case ExtI2:
					_ = reader.ReadNullTerminatedString();
					break;
				case ExtT0:
				case ExtT1:
				case ExtT2:
					_ = reader.ReadMultiByteUInt();
					break;
				case Ext0:
				case Ext1:
				case Ext2:
					break;

				default:
					bool hasContent = (token & 0x40) != 0;
					bool hasAttributes = (token & 0x80) != 0;
					byte tagToken = (byte)(token & 0x3F);
					if (!page.Tokens.TryGetValue(tagToken, out string? name))
						throw new WbxmlException(
							$"Unknown tag token 0x{tagToken:X2} on code page {page.Index} ({page.Namespace}).");
					if (hasAttributes)
						throw new WbxmlException("Attributes are not used by EAS.");

					XElement element = new(page.Namespace + name);
					if (current is null)
					{
						if (doc.Root is not null)
							throw new WbxmlException("Multiple root elements.");
						doc.Add(element);
					}
					else
					{
						current.Add(element);
					}

					if (hasContent)
						current = element;
					break;
			}
		}

		if (current is not null)
			throw new WbxmlException("Unexpected end of WBXML: unclosed elements remain.");
		if (doc.Root is null)
			throw new WbxmlException("Empty WBXML document.");
		return doc;
	}

	private static void AppendText(XElement? current, string text)
	{
		if (current is null)
			throw new WbxmlException("Text content outside of an element.");
		current.Add(new XText(text));
	}

	private ref struct SpanReader(ReadOnlySpan<byte> data)
	{
		private readonly ReadOnlySpan<byte> _data = data;
		private int _pos;

		public readonly bool AtEnd => _pos >= _data.Length;

		public byte ReadByte()
		{
			if (_pos >= _data.Length)
				throw new WbxmlException("Unexpected end of WBXML data.");
			return _data[_pos++];
		}

		// Lengths arrive as multi-byte uints (up to 35 bits) and are cast to int by the
		// callers, so a hostile length can be negative or overflow _pos + count — compare
		// against the remaining bytes instead so any oversized value is a clean parse error.
		public void Skip(int count)
		{
			if (count < 0 || count > _data.Length - _pos)
				throw new WbxmlException("Unexpected end of WBXML data.");
			_pos += count;
		}

		public ReadOnlySpan<byte> ReadBytes(int count)
		{
			if (count < 0 || count > _data.Length - _pos)
				throw new WbxmlException("Unexpected end of WBXML data.");
			ReadOnlySpan<byte> span = _data.Slice(_pos, count);
			_pos += count;
			return span;
		}

		public uint ReadMultiByteUInt()
		{
			uint value = 0;
			for (int i = 0; i < 5; i++)
			{
				byte b = ReadByte();
				value = (value << 7) | (uint)(b & 0x7F);
				if ((b & 0x80) == 0)
					return value;
			}

			throw new WbxmlException("Multi-byte integer too long.");
		}

		public string ReadNullTerminatedString()
		{
			int start = _pos;
			while (_pos < _data.Length && _data[_pos] != 0)
				_pos++;
			if (_pos >= _data.Length)
				throw new WbxmlException("Unterminated inline string.");
			string str = Encoding.UTF8.GetString(_data[start.._pos]);
			_pos++; // consume terminator
			return str;
		}
	}
}

using System.Text;
using System.Xml.Linq;
using ActiveSync.Backends.Common.Converters;
using ActiveSync.Contracts;
using ActiveSync.Protocol.Wbxml;
using MimeKit;

namespace ActiveSync.Core.Tests;

/// <summary>
///   A Type-4 (full MIME) body must never be byte-truncated — the content is a serialized
///   message/rfc822 stream, so cutting it at an arbitrary byte offset can land mid-header or
///   mid-part and hand the client unparsable MIME. MIME fetches are all-or-nothing.
/// </summary>
public class MailConverterBodyTests
{
	private static readonly XNamespace AirSyncBase = EasNamespaces.AirSyncBase;

	private static MimeMessage Message(string bodyText)
	{
		MimeMessage message = new();
		message.From.Add(MailboxAddress.Parse("sender@example.com"));
		message.To.Add(MailboxAddress.Parse("recipient@example.com"));
		message.Subject = "full mime body";
		message.Date = DateTimeOffset.UtcNow;
		message.Body = new TextPart("plain") { Text = bodyText };
		return message;
	}

	[Fact]
	public void Type4Body_IsNeverTruncated_EvenBelowTheRequestedLimit()
	{
		// A body long enough that the serialized MIME (headers + this text) comfortably exceeds
		// a deliberately tiny TruncationSize.
		string longText = string.Concat(Enumerable.Repeat("0123456789", 200)); // 2000 chars
		MimeMessage message = Message(longText);

		long fullSize;
		using (MemoryStream ms = new())
		{
			message.WriteTo(ms);
			fullSize = ms.Length;
		}

		BodyPreference preference = new(4, 200, false); // Type 4, 200-byte TruncationSize
		XElement body = MailConverter.BuildBody(message, preference, out int nativeBodyType);

		string truncatedFlag = body.Element(AirSyncBase + "Truncated")!.Value;
		string data = body.Element(AirSyncBase + "Data")!.Value;

		Assert.Equal("0", truncatedFlag); // never truncated for type 4
		Assert.True(Encoding.UTF8.GetByteCount(data) >= fullSize - 16); // full MIME survives (± CRLF normalization)
		Assert.Contains("Subject: full mime body", data);
		// BuildBody now calls Prepare(SevenBit) before writing, so a 2000-char run with no
		// whitespace is quoted-printable soft-wrapped ("=\r\n" every ~76 octets) rather than
		// streamed as one unbroken line -- unfold before checking the text survived intact.
		Assert.Contains(longText, data.Replace("=\r\n", ""));
	}

	[Fact]
	public void Type4Body_PreservesBinaryContent_IncludingNulBytes()
	{
		// The serialized RFC 822 stream was decoded as UTF-8 and then had every NUL byte
		// stripped unconditionally. That is correct for the type 1/2 text branches but a byte
		// stream is not UTF-8 text: a part declared Content-Transfer-Encoding: binary carries its
		// bytes raw, and stripping NULs from the resulting string corrupts it byte-for-byte.
		byte[] binaryContent = [0x41, 0x00, 0x42, 0x00, 0x43]; // "A\0B\0C" -- deliberately has NULs
		MimePart part = new("application", "octet-stream")
		{
			Content = new MimeContent(new MemoryStream(binaryContent)),
			ContentTransferEncoding = ContentEncoding.Binary,
			ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = "raw.bin" }
		};

		MimeMessage message = new();
		message.From.Add(MailboxAddress.Parse("sender@example.com"));
		message.To.Add(MailboxAddress.Parse("recipient@example.com"));
		message.Subject = "binary body";
		message.Date = DateTimeOffset.UtcNow;
		message.Body = part;

		BodyPreference preference = new(4, null, false);
		XElement body = MailConverter.BuildBody(message, preference, out _);
		string data = body.Element(AirSyncBase + "Data")!.Value;

		// Round-trip the returned text back through a MIME parser and compare the attachment's
		// decoded bytes to the original -- proves nothing was lost or mangled in transit.
		MimeMessage roundTripped = MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes(data)));
		MimePart roundTrippedPart = Assert.IsType<MimePart>(roundTripped.Body);
		using MemoryStream decoded = new();
		roundTrippedPart.Content!.DecodeTo(decoded);
		Assert.Equal(binaryContent, decoded.ToArray());
	}
}

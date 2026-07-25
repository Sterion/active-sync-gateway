using System.Text;
using System.Xml.Linq;
using ActiveSync.Backends.Common.Converters;
using ActiveSync.Contracts;
using ActiveSync.Protocol.Wbxml;
using MimeKit;

namespace ActiveSync.Core.Tests;

/// <summary>
///   D4: a Type-4 (full MIME) body must never be byte-truncated — the content is a serialized
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
		Assert.Contains(longText, data); // the full body text is present, not cut mid-content
	}
}

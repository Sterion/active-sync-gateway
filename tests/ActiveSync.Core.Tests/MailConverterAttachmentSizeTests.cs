using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Eas.Conversion;
using ActiveSync.Protocol.Wbxml;
using MimeKit;

namespace ActiveSync.Core.Tests;

/// <summary>
///   Every attachment of every message was fully decoded into a MemoryStream just to
///   report its size (`EstimateSize`), on every windowed Sync batch. A batch of messages
///   carrying tens of MB of attachments therefore materialized that much memory repeatedly.
/// </summary>
public class MailConverterAttachmentSizeTests
{
	private static readonly XNamespace AirSyncBase = EasNamespaces.AirSyncBase;

	[Fact]
	public void EstimateSize_DoesNotMaterializeTheWholeAttachmentInMemory()
	{
		const int size = 4 * 1024 * 1024; // 4 MB
		byte[] contentBytes = new byte[size];
		new Random(42).NextBytes(contentBytes);

		// Binary CTE: DecodeTo is a raw copy, no transfer-decoding overhead, so any allocation
		// proportional to `size` can only come from buffering the whole content to measure it.
		MimePart attachment = new("application", "octet-stream")
		{
			Content = new MimeContent(new MemoryStream(contentBytes)),
			ContentTransferEncoding = ContentEncoding.Binary,
			ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = "big.bin" }
		};

		Multipart multipart = new("mixed") { new TextPart("plain") { Text = "body" }, attachment };

		MimeMessage message = new();
		message.From.Add(MailboxAddress.Parse("sender@example.com"));
		message.To.Add(MailboxAddress.Parse("recipient@example.com"));
		message.Subject = "attachment size";
		message.Date = DateTimeOffset.UtcNow;
		message.Body = multipart;

		MailFlags flags = new();

		GC.Collect();
		long before = GC.GetAllocatedBytesForCurrentThread();
		List<XElement> data = MailConverter.ToApplicationData(message, flags, [], BodyPreference.PlainText, _ => "ref");
		long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

		XElement attachmentsElement = data.Single(e => e.Name == AirSyncBase + "Attachments");
		long reportedSize = long.Parse(attachmentsElement
			.Element(AirSyncBase + "Attachment")!
			.Element(AirSyncBase + "EstimatedDataSize")!.Value);

		Assert.Equal(size, reportedSize);
		Assert.True(allocated < size / 2,
			$"allocated {allocated} bytes estimating the size of a {size}-byte attachment -- the whole content was buffered");
	}
}

using System.Text;
using System.Xml.Linq;
using ActiveSync.Backends.Converters;
using ActiveSync.Contracts;
using ActiveSync.Protocol.Wbxml;
using MimeKit;
using MimeKit.Text;

namespace ActiveSync.Core.Tests;

public class DraftMessageBuilderTests
{
	private static readonly XNamespace ASB = EasNamespaces.AirSyncBase;

	private static XElement AppData(params XElement[] elements)
	{
		return new XElement("ApplicationData", elements);
	}

	private static MimePart Attachment(string mimeType, string? fileName, string text)
	{
		string[] type = mimeType.Split('/');
		MimePart part = new(type[0], type[1])
		{
			Content = new MimeContent(new MemoryStream(Encoding.UTF8.GetBytes(text))),
			ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
			ContentTransferEncoding = ContentEncoding.Base64
		};
		if (fileName is not null)
		{
			part.ContentDisposition.FileName = fileName;
			part.FileName = fileName;
		}

		return part;
	}

	private static MimeMessage Existing(MimeEntity body)
	{
		MimeMessage message = new();
		message.From.Add(MailboxAddress.Parse("me@example.org"));
		message.To.Add(MailboxAddress.Parse("you@example.org"));
		message.Subject = "Draft";
		message.Body = body;
		return message;
	}

	private static XElement Delete(string fileReference)
	{
		return new XElement(ASB + "Delete", new XElement(ASB + "FileReference", fileReference));
	}

	private static MimeMessage DraftWithTwoAttachments()
	{
		Multipart mixed = new("mixed");
		mixed.Add(new TextPart(TextFormat.Plain) { Text = "hello" });
		mixed.Add(Attachment("text/plain", "notes.txt", "named"));
		mixed.Add(Attachment("application/pdf", null, "unnamed"));
		return Existing(mixed);
	}

	// D15 — an attachment with no file name must not be matched by every delete reference.
	// The FileReferences we mint end in the attachment INDEX, so a name-tail match against an
	// empty name is true for any reference: one unrelated <Delete> used to drop every unnamed
	// attachment (inline images, message/rfc822 parts) while leaving the named target alone.
	[Fact]
	public void Change_DeleteByFileReference_RemovesTheIndexedAttachmentOnly()
	{
		// Delete attachment 0 — "notes.txt". The unnamed attachment 1 must survive.
		XElement payload = AppData(
			new XElement(ASB + "Attachments", Delete(DelimitedKey.Encode("INBOX", "42:7", "0"))));

		MimeMessage result = DraftMessageBuilder.Build(payload, DraftWithTwoAttachments(), "me@example.org");

		MimeEntity attachment = Assert.Single(result.Attachments);
		Assert.Equal("application/pdf", attachment.ContentType.MimeType);
	}

	// D15 — deleting the unnamed attachment by its index must keep the named one.
	[Fact]
	public void Change_DeleteUnnamedAttachment_KeepsTheNamedSibling()
	{
		XElement payload = AppData(
			new XElement(ASB + "Attachments", Delete(DelimitedKey.Encode("INBOX", "42:7", "1"))));

		MimeMessage result = DraftMessageBuilder.Build(payload, DraftWithTwoAttachments(), "me@example.org");

		MimeEntity attachment = Assert.Single(result.Attachments);
		Assert.Equal("notes.txt", (attachment as MimePart)?.FileName);
	}

	// D15 — a FileReference we did not mint targets nothing rather than being guessed at.
	[Fact]
	public void Change_DeleteWithForeignFileReference_KeepsEverything()
	{
		XElement payload = AppData(
			new XElement(ASB + "Attachments", Delete("not-our-shape")));

		MimeMessage result = DraftMessageBuilder.Build(payload, DraftWithTwoAttachments(), "me@example.org");

		Assert.Equal(2, result.Attachments.Count());
	}
}

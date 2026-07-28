using System.Text;
using System.Xml.Linq;
using ActiveSync.Backends.Common.Converters;
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

	// An attachment with no file name must not be matched by every delete reference.
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

	// Deleting the unnamed attachment by its index must keep the named one.
	[Fact]
	public void Change_DeleteUnnamedAttachment_KeepsTheNamedSibling()
	{
		XElement payload = AppData(
			new XElement(ASB + "Attachments", Delete(DelimitedKey.Encode("INBOX", "42:7", "1"))));

		MimeMessage result = DraftMessageBuilder.Build(payload, DraftWithTwoAttachments(), "me@example.org");

		MimeEntity attachment = Assert.Single(result.Attachments);
		Assert.Equal("notes.txt", (attachment as MimePart)?.FileName);
	}

	// A FileReference we did not mint targets nothing rather than being guessed at.
	[Fact]
	public void Change_DeleteWithForeignFileReference_KeepsEverything()
	{
		XElement payload = AppData(
			new XElement(ASB + "Attachments", Delete("not-our-shape")));

		MimeMessage result = DraftMessageBuilder.Build(payload, DraftWithTwoAttachments(), "me@example.org");

		Assert.Equal(2, result.Attachments.Count());
	}

	// A client-supplied <ContentType> must reach the MIME part, or a phone photo arrives
	// as application/octet-stream and the recipient cannot open it.
	[Fact]
	public void Add_WithDeclaredContentType_UsesIt()
	{
		XElement payload = AppData(
			new XElement(ASB + "Attachments",
				new XElement(ASB + "Add",
					new XElement(ASB + "DisplayName", "holiday.jpg"),
					new XElement(ASB + "ContentType", "image/jpeg"),
					new XElement(ASB + "Content", Convert.ToBase64String([1, 2, 3])))));

		MimeMessage result = DraftMessageBuilder.Build(payload, null, "me@example.org");

		MimeEntity attachment = Assert.Single(result.Attachments);
		Assert.Equal("image/jpeg", attachment.ContentType.MimeType);
	}

	// No <ContentType> from the client: infer from the display name rather than
	// falling back to octet-stream for everything.
	[Fact]
	public void Add_WithoutContentType_InfersFromDisplayName()
	{
		XElement payload = AppData(
			new XElement(ASB + "Attachments",
				new XElement(ASB + "Add",
					new XElement(ASB + "DisplayName", "report.pdf"),
					new XElement(ASB + "Content", Convert.ToBase64String([1, 2, 3])))));

		MimeMessage result = DraftMessageBuilder.Build(payload, null, "me@example.org");

		MimeEntity attachment = Assert.Single(result.Attachments);
		Assert.Equal("application/pdf", attachment.ContentType.MimeType);
	}

	// An unrecognisable name, and an unparsable declared type, still land on octet-stream.
	[Theory]
	[InlineData("blob.zzz", null)]
	[InlineData("blob.zzz", "this is not a media type")]
	public void Add_WithUnknownType_FallsBackToOctetStream(string displayName, string? contentType)
	{
		XElement add = new(ASB + "Add",
			new XElement(ASB + "DisplayName", displayName),
			new XElement(ASB + "Content", Convert.ToBase64String([1, 2, 3])));
		if (contentType is not null)
			add.Add(new XElement(ASB + "ContentType", contentType));

		MimeMessage result = DraftMessageBuilder.Build(
			AppData(new XElement(ASB + "Attachments", add)), null, "me@example.org");

		MimeEntity attachment = Assert.Single(result.Attachments);
		Assert.Equal("application/octet-stream", attachment.ContentType.MimeType);
	}

	// A flag-only Change carries no <Body>; the stored multipart/alternative must survive
	// whole. Picking the first TextPart out of it silently downgraded a rich draft to plain text.
	[Fact]
	public void Change_WithoutBody_KeepsTheHtmlAlternative()
	{
		MultipartAlternative alternative = new()
		{
			new TextPart(TextFormat.Plain) { Text = "plain body" },
			new TextPart(TextFormat.Html) { Text = "<p>rich body</p>" }
		};

		MimeMessage result = DraftMessageBuilder.Build(
			AppData(new XElement(EasNamespaces.Email + "Subject", "renamed")),
			Existing(alternative), "me@example.org");

		Assert.Equal("renamed", result.Subject);
		Assert.Contains("rich body", result.HtmlBody);
		Assert.Equal("plain body", result.TextBody);
	}

	// The same with an attachment alongside: the alternative survives the rebuild into
	// multipart/mixed, and the carried-over attachment is not duplicated.
	[Fact]
	public void Change_WithoutBody_KeepsTheHtmlAlternativeAlongsideAttachments()
	{
		MultipartAlternative alternative = new()
		{
			new TextPart(TextFormat.Plain) { Text = "plain body" },
			new TextPart(TextFormat.Html) { Text = "<p>rich body</p>" }
		};
		Multipart mixed = new("mixed") { alternative, Attachment("text/plain", "notes.txt", "named") };

		MimeMessage result = DraftMessageBuilder.Build(
			AppData(new XElement(EasNamespaces.Email + "Subject", "renamed")),
			Existing(mixed), "me@example.org");

		Assert.Contains("rich body", result.HtmlBody);
		Assert.Equal("plain body", result.TextBody);
		MimeEntity attachment = Assert.Single(result.Attachments);
		Assert.Equal("notes.txt", (attachment as MimePart)?.FileName);
	}

	// Inline images live in a multipart/related that MimeKit does not report as an
	// attachment, so nothing carries them over; keeping the body entity whole is what saves them.
	[Fact]
	public void Change_WithoutBody_KeepsInlineRelatedParts()
	{
		MultipartRelated related = new()
		{
			new TextPart(TextFormat.Html) { Text = "<img src=\"cid:logo\">" }
		};
		MimePart image = new("image", "png")
		{
			Content = new MimeContent(new MemoryStream([1, 2, 3])),
			ContentId = "logo",
			ContentTransferEncoding = ContentEncoding.Base64
		};
		related.Add(image);
		related.Root = related.OfType<TextPart>().First();

		MimeMessage result = DraftMessageBuilder.Build(
			AppData(new XElement(EasNamespaces.Email + "Subject", "renamed")),
			Existing(related), "me@example.org");

		Assert.Contains("cid:logo", result.HtmlBody);
		Assert.Contains(result.BodyParts, p => p.ContentType.MimeType == "image/png");
	}

	// Email:To/Cc are RFC-5322 comma-separated address lists (this repo's own emitter,
	// MailConverter, produces exactly that shape: `message.To.ToString()`), not the ';'-joined
	// DisplayTo convention. MailboxAddress.TryParse rejects a whole comma-joined string, so the
	// naive per-';' split silently dropped every recipient of a multi-recipient draft.
	[Fact]
	public void Change_MultipleCommaSeparatedRecipients_AreAllKept()
	{
		XElement payload = AppData(
			new XElement(EasNamespaces.Email + "To",
				"\"Alice\" <alice@example.com>, \"Bob\" <bob@example.com>"));

		MimeMessage result = DraftMessageBuilder.Build(payload, null, "me@example.org");

		Assert.Equal(2, result.To.Count);
		Assert.Contains(result.To.Mailboxes, m => m.Address == "alice@example.com");
		Assert.Contains(result.To.Mailboxes, m => m.Address == "bob@example.com");
	}

	// Adjacent case — the historical ';'-separated DisplayTo convention must still work when a
	// client uses it instead.
	[Fact]
	public void Change_SemicolonSeparatedRecipients_StillWork()
	{
		XElement payload = AppData(
			new XElement(EasNamespaces.Email + "To", "alice@example.com; bob@example.com"));

		MimeMessage result = DraftMessageBuilder.Build(payload, null, "me@example.org");

		Assert.Equal(2, result.To.Count);
		Assert.Contains(result.To.Mailboxes, m => m.Address == "alice@example.com");
		Assert.Contains(result.To.Mailboxes, m => m.Address == "bob@example.com");
	}

	// A fresh `MimeMessage message = new()` is constructed and only
	// From/To/Cc/Bcc/Subject/Importance/Body/Date are copied from `existing`. In-Reply-To,
	// References and any custom headers on the stored draft were dropped, so a reply draft
	// started elsewhere and merely touched on the phone (e.g. a subject rename) is sent as a
	// new thread instead of a reply.
	[Fact]
	public void Change_ToAnUnrelatedField_PreservesInReplyToAndReferences()
	{
		MimeMessage existing = Existing(new TextPart(TextFormat.Plain) { Text = "original" });
		existing.Headers.Add(HeaderId.InReplyTo, "<original@example.org>");
		existing.Headers.Add(HeaderId.References, "<thread-root@example.org> <original@example.org>");

		MimeMessage result = DraftMessageBuilder.Build(
			AppData(new XElement(EasNamespaces.Email + "Subject", "renamed")), existing, "me@example.org");

		Assert.Equal("<original@example.org>", result.Headers[HeaderId.InReplyTo]);
		Assert.Equal("<thread-root@example.org> <original@example.org>", result.Headers[HeaderId.References]);
	}

	// A payload <Body> still replaces the stored one outright.
	[Fact]
	public void Change_WithBody_ReplacesTheStoredBody()
	{
		MultipartAlternative alternative = new()
		{
			new TextPart(TextFormat.Plain) { Text = "plain body" },
			new TextPart(TextFormat.Html) { Text = "<p>rich body</p>" }
		};

		MimeMessage result = DraftMessageBuilder.Build(AppData(
			new XElement(ASB + "Body",
				new XElement(ASB + "Type", "1"),
				new XElement(ASB + "Data", "rewritten"))), Existing(alternative), "me@example.org");

		Assert.Equal("rewritten", result.TextBody);
		Assert.Null(result.HtmlBody);
	}
}

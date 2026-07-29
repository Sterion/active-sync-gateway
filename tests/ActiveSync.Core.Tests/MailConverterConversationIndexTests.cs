using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Eas.Conversion;
using ActiveSync.Protocol.Wbxml;
using MimeKit;

namespace ActiveSync.Core.Tests;

/// <summary>
///   The emitted email2:ConversationIndex was a 5-byte stub whose own comment contradicted
///   the bytes it wrote (claimed "high 4 bytes", wrote the low 32 bits) and was 17 bytes short of
///   the MS-OXOMSG 2.2.1.3 22-byte header with no GUID — a shape no real client can parse for
///   threading. The element is dropped; ConversationId (already correct and sufficient for
///   threading per the file's own comment) is the only conversation-grouping element emitted.
/// </summary>
public class MailConverterConversationIndexTests
{
	private static readonly XNamespace Email2 = EasNamespaces.Email2;

	private static MimeMessage MessageWithReferences()
	{
		MimeMessage message = new();
		message.From.Add(MailboxAddress.Parse("sender@example.com"));
		message.To.Add(MailboxAddress.Parse("recipient@example.com"));
		message.Subject = "re: threading";
		message.References.Add("<seed@example.com>");
		message.Body = new TextPart("plain") { Text = "body" };
		return message;
	}

	[Fact]
	public void ConversationIndex_IsNeverEmitted()
	{
		MailFlags flags = new();
		List<XElement> data = MailConverter.ToApplicationData(
			MessageWithReferences(), flags, [], BodyPreference.PlainText, _ => "ref");

		Assert.DoesNotContain(data, e => e.Name == Email2 + "ConversationIndex");
		// ConversationId alone is sufficient for threading and must still be present.
		Assert.Contains(data, e => e.Name == Email2 + "ConversationId");
	}
}

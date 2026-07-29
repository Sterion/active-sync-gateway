using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Eas.Conversion;
using ActiveSync.Protocol.Wbxml;
using MimeKit;

namespace ActiveSync.Core.Tests;

/// <summary>
///   DisplayTo used `m.Name ?? m.Address`, but MimeKit's MailboxAddress.Name returns ""
///   (never null) for a bare address with no display name, so `??` never fires and a message
///   To'd at a bare address renders an empty DisplayTo. Devices use DisplayTo to label rows in
///   Sent/Drafts, so those folders show blank recipients.
/// </summary>
public class MailConverterDisplayToTests
{
	private static readonly XNamespace Email = EasNamespaces.Email;

	private static MimeMessage MessageTo(params string[] addresses)
	{
		MimeMessage message = new();
		message.From.Add(MailboxAddress.Parse("sender@example.com"));
		foreach (string address in addresses)
			message.To.Add(MailboxAddress.Parse(address));
		message.Subject = "displayto test";
		message.Date = DateTimeOffset.UtcNow;
		message.Body = new TextPart("plain") { Text = "body" };
		return message;
	}

	[Fact]
	public void BareAddress_WithNoDisplayName_StillProducesADisplayTo()
	{
		MimeMessage message = MessageTo("bob@example.com");
		MailFlags flags = new();

		List<XElement> data = MailConverter.ToApplicationData(
			message, flags, [], BodyPreference.PlainText, _ => "ref");

		string displayTo = data.Single(e => e.Name == Email + "DisplayTo").Value;
		Assert.Equal("bob@example.com", displayTo);
	}

	[Fact]
	public void MixOfNamedAndBareAddresses_NeverEmitsAnEmptySegment()
	{
		MimeMessage message = new();
		message.From.Add(MailboxAddress.Parse("sender@example.com"));
		message.To.Add(new MailboxAddress("Alice", "alice@example.com"));
		message.To.Add(MailboxAddress.Parse("bob@example.com"));
		message.Subject = "mix";
		message.Date = DateTimeOffset.UtcNow;
		message.Body = new TextPart("plain") { Text = "body" };
		MailFlags flags = new();

		List<XElement> data = MailConverter.ToApplicationData(
			message, flags, [], BodyPreference.PlainText, _ => "ref");

		string displayTo = data.Single(e => e.Name == Email + "DisplayTo").Value;
		Assert.Equal("Alice; bob@example.com", displayTo);
	}
}

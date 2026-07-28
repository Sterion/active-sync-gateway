using System.Text;
using System.Xml.Linq;
using ActiveSync.Backends.Common.Converters;
using ActiveSync.Contracts;
using ActiveSync.Protocol.Wbxml;
using MimeKit;

namespace ActiveSync.Core.Tests;

/// <summary>
///   DateReceived was taken from the sender-supplied Date: header
///   (`EasDateTime.ToLong(message.Date.UtcDateTime)`), which defaults to year 0001 when that
///   header is missing on the wire — MS-ASEMAIL DateReceived is meant to be the *delivery* time,
///   not the (possibly absent, possibly forged) sender-claimed send time.
/// </summary>
public class MailConverterDateReceivedTests
{
	private static readonly XNamespace Email = EasNamespaces.Email;

	private static MimeMessage LoadMessageWithoutDateHeader()
	{
		// No Date: header at all — the historical open-source-mail-relay case this finding
		// describes, verified against MimeKit: a parsed message with no Date header reports
		// message.Date == default(DateTimeOffset) (0001-01-01T00:00:00+00:00).
		const string raw =
			"From: sender@example.com\r\n" +
			"To: recipient@example.com\r\n" +
			"Subject: no date header\r\n" +
			"Content-Type: text/plain\r\n" +
			"\r\n" +
			"body\r\n";
		using MemoryStream stream = new(Encoding.ASCII.GetBytes(raw));
		return MimeMessage.Load(stream);
	}

	[Fact]
	public void MissingDateHeader_DoesNotProduceYearOne()
	{
		MimeMessage message = LoadMessageWithoutDateHeader();
		Assert.Equal(default, message.Date); // confirms the MimeKit precondition this test relies on

		MailConverter.MessageFlags flags = new(false, false, false, false);
		List<XElement> data = MailConverter.ToApplicationData(
			message, flags, BodyPreference.PlainText, _ => "ref");

		string dateReceived = data.Single(e => e.Name == Email + "DateReceived").Value;
		Assert.False(dateReceived.StartsWith("0001-"), $"DateReceived defaulted to year 1: {dateReceived}");
	}

	[Fact]
	public void ReceivedUtc_WhenSupplied_TakesPrecedenceOverTheDateHeader()
	{
		// Coverage, not red-first proof: this exercises the new `receivedUtc` parameter, which
		// cannot be expressed against the pre-fix 4-argument signature — there is no way to
		// observe this specific path fail on unmodified code (it wouldn't compile). The year-1
		// fallback test above is the red-first proof for this fix; this pins the "prefer the backend's
		// own delivery timestamp" half of the fix, e.g. IMAP INTERNALDATE / JMAP receivedAt.
		MimeMessage message = new();
		message.From.Add(MailboxAddress.Parse("sender@example.com"));
		message.To.Add(MailboxAddress.Parse("recipient@example.com"));
		message.Subject = "with date header";
		message.Date = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero); // forged/skewed sender date
		message.Body = new TextPart("plain") { Text = "body" };

		DateTimeOffset delivered = new(2024, 6, 15, 12, 30, 0, TimeSpan.Zero);
		MailConverter.MessageFlags flags = new(false, false, false, false);
		List<XElement> data = MailConverter.ToApplicationData(
			message, flags, BodyPreference.PlainText, _ => "ref", delivered);

		string dateReceived = data.Single(e => e.Name == Email + "DateReceived").Value;
		Assert.StartsWith("2024-06-15T12:30:00", dateReceived);
	}
}

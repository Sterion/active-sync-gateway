using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace ActiveSync.Integration.Tests.Infrastructure;

/// <summary>
///   Seeds mail directly into the backend (SMTP submission or an IMAP Drafts append), out of
///   band from the gateway — used by scenarios that need a message to already exist.
/// </summary>
internal static class MailSeeder
{
	/// <summary>Submits a plain-text message over SMTP; returns the subject for lookups.</summary>
	public static async Task<string> SeedMailAsync(string fromUser, string toUser, string subject)
	{
		MimeMessage message = new();
		message.From.Add(MailboxAddress.Parse(fromUser));
		message.To.Add(MailboxAddress.Parse(toUser));
		message.Subject = subject;
		message.Body = new TextPart("plain") { Text = "seed" };
		using SmtpClient smtp = new();
		await smtp.ConnectAsync(TestBackend.SmtpHost, TestBackend.SmtpPort, SecureSocketOptions.None);
		await smtp.AuthenticateAsync(fromUser, TestBackend.Password);
		await smtp.SendAsync(message);
		await smtp.DisconnectAsync(true);
		return subject;
	}

	/// <summary>Appends a message straight into the user's Drafts folder over IMAP.</summary>
	public static async Task AppendDraftAsync(string user, string subject)
	{
		using ImapClient imap = new();
		await imap.ConnectAsync(TestBackend.ImapHost, TestBackend.ImapPort, SecureSocketOptions.None);
		await imap.AuthenticateAsync(user, TestBackend.Password);
		IMailFolder drafts = imap.GetFolder(SpecialFolder.Drafts) ?? await imap.GetFolderAsync("Drafts");
		MimeMessage message = new();
		message.From.Add(MailboxAddress.Parse(user));
		message.To.Add(MailboxAddress.Parse(user));
		message.Subject = subject;
		message.Body = new TextPart("plain") { Text = "draft body" };
		await drafts.AppendAsync(message);
		await imap.DisconnectAsync(true);
	}
}

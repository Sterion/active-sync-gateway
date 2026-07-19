using ActiveSync.Integration.Tests.Infrastructure;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace ActiveSync.Integration.Tests.Scenarios;

/// <summary>
///   Validates the backend stack itself (no gateway involved): IMAP auth works and SMTP
///   submission really delivers into the other user's inbox. If these fail, every other
///   integration test is meaningless.
/// </summary>
[Trait("Category", "Integration")]
public class BackendSmokeTests
{
	[BackendFact]
	public async Task ImapLogin_Works_ForBothUsers()
	{
		foreach (string user in new[] { TestBackend.User1, TestBackend.User2 })
		{
			using ImapClient client = new();
			await client.ConnectAsync(TestBackend.ImapHost, TestBackend.ImapPort, SecureSocketOptions.None);
			await client.AuthenticateAsync(user, TestBackend.Password);
			await client.Inbox.OpenAsync(FolderAccess.ReadOnly);
			await client.DisconnectAsync(true);
		}
	}

	[SmtpSubmissionFact]
	public async Task SmtpSubmission_DeliversToOtherUsersInbox()
	{
		string subject = $"smoke-{Guid.NewGuid():N}";
		MimeMessage message = new();
		message.From.Add(MailboxAddress.Parse(TestBackend.User1));
		message.To.Add(MailboxAddress.Parse(TestBackend.User2));
		message.Subject = subject;
		message.Body = new TextPart("plain") { Text = "backend smoke test" };

		using (SmtpClient smtp = new())
		{
			await smtp.ConnectAsync(TestBackend.SmtpHost, TestBackend.SmtpPort, SecureSocketOptions.None);
			await smtp.AuthenticateAsync(TestBackend.User1, TestBackend.Password);
			await smtp.SendAsync(message);
			await smtp.DisconnectAsync(true);
		}

		await WaitUntil.TrueAsync(async () =>
			{
				using ImapClient imap = new();
				await imap.ConnectAsync(TestBackend.ImapHost, TestBackend.ImapPort, SecureSocketOptions.None);
				await imap.AuthenticateAsync(TestBackend.User2, TestBackend.Password);
				await imap.Inbox.OpenAsync(FolderAccess.ReadOnly);
				IList<UniqueId> uids = await imap.Inbox.SearchAsync(SearchQuery.SubjectContains(subject));
				await imap.DisconnectAsync(true);
				return uids.Count > 0;
			}, $"delivery of '{subject}' to {TestBackend.User2}", TimeSpan.FromSeconds(60));
	}
}

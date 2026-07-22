using ActiveSync.Backends.Smtp;
using ActiveSync.Contracts;
using ActiveSync.Integration.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;

namespace ActiveSync.Integration.Tests.Scenarios;

/// <summary>
///   D9 (coverage): once the SMTP DATA phase is accepted, the QUIT teardown must never fail the
///   operation — the fix disconnects with <c>CancellationToken.None</c> inside a try/catch, so a
///   cancelled request or a flaky disconnect cannot make an already-sent message look like a send
///   failure (which would drive a client resend and duplicate the mail). The precise cancel-after-
///   send race has no deterministic trigger, so this exercises the fixed send+teardown path end to
///   end against a real submission MSA and asserts it completes cleanly; it is coverage for the
///   fix, not a reproduction of the race.
/// </summary>
[Collection("gateway")]
[Trait("Category", "Integration")]
public class SmtpSubmitTeardownTests
{
	[SmtpSubmissionFact]
	public async Task SendAsync_CompletesThroughTheQuitTeardown()
	{
		MimeMessage message = new();
		message.From.Add(MailboxAddress.Parse(TestBackend.User1));
		message.To.Add(MailboxAddress.Parse(TestBackend.User2));
		message.Subject = $"D9 {Guid.NewGuid():N}";
		message.Body = new TextPart("plain") { Text = "smtp teardown coverage" };

		using MemoryStream buffer = new();
		await message.WriteToAsync(buffer);

		SmtpSubmitBackend backend = new(
			new SmtpOptions { Host = TestBackend.SmtpHost, Port = TestBackend.SmtpPort, Security = "None" },
			new BackendCredentials(TestBackend.User1, TestBackend.Password),
			TestBackend.User1,
			NullLogger.Instance);

		// The assertion is that this returns rather than throwing: the DATA phase is accepted and the
		// QUIT teardown runs to completion without surfacing as a submission failure.
		await backend.SendAsync(buffer.ToArray(), CancellationToken.None);
	}
}

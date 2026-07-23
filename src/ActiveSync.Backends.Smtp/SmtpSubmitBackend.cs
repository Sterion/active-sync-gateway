using System.Net.Sockets;
using ActiveSync.Backends.Common;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using MailKit.Net.Smtp;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace ActiveSync.Backends.Smtp;

/// <summary>
///   Outbound mail submission over SMTP. One connection per send — submission is rare
///   compared to the IMAP traffic, and a held-open SMTP session would only age out.
/// </summary>
public sealed class SmtpSubmitBackend(
	SmtpOptions options,
	BackendCredentials credentials,
	string? mailAddress,
	ILogger logger,
	ILogger? wireLogger = null) : IMailSubmitOperations
{
	public async Task SendAsync(byte[] mime, CancellationToken ct)
	{
		using MemoryStream stream = new(mime);
		MimeMessage message = await MimeMessage.LoadAsync(stream, ct).ConfigureAwait(false);

		if (options.ForceFrom && mailAddress is not null)
		{
			string? displayName = message.From.Mailboxes.FirstOrDefault()?.Name;
			message.From.Clear();
			message.From.Add(new MailboxAddress(displayName, mailAddress));
			message.Sender = null;
		}

		// Verbose wire logging (category ActiveSync.Backends.Smtp) — attached only while
		// Trace is enabled; MailKit's secret detector masks the AUTH exchange.
		using SmtpClient smtp = wireLogger?.IsEnabled(LogLevel.Trace) == true
			? new SmtpClient(new MailKitWireLogger(wireLogger))
			: new SmtpClient();
		// Tighter than MailKit's 120 s default so a hung connect/auth fails fast enough to retry.
		smtp.Timeout = 30_000;
		MailTransportSecurity.Apply(smtp, options.AllowInvalidCertificates, options.CaCertificatePath);

		// Connect + authenticate are side-effect-free (nothing is submitted yet), so the whole setup
		// is retried on a transient blip. The DATA phase below runs EXACTLY ONCE — replaying a
		// completed submission would duplicate the mail — so it is deliberately outside the retry.
		await TransientRetry.RunAsync(async () =>
		{
			// A retry may find the client mid-connected (auth threw after connect); reset first so
			// ConnectAsync never fails with "already connected".
			if (smtp.IsConnected)
				await smtp.DisconnectAsync(true, ct).ConfigureAwait(false);
			await smtp.ConnectAsync(options.Host, options.Port, MailTransportSecurity.ForSmtp(options), ct)
				.ConfigureAwait(false);
			await smtp.AuthenticateAsync(credentials.UserName, credentials.Password, ct).ConfigureAwait(false);
		}, ex => IsTransientSmtp(ex, ct), ct, onRetry: (ex, attempt) =>
		{
			Core.Observability.GatewayMetrics.RecordBackendRetry("smtp");
			logger.LogWarning(ex, "SMTP connect/auth transient failure for {User}; retry {Attempt}/{Max}",
				credentials.UserName, attempt, TransientRetry.DelaysMs.Length);
		}).ConfigureAwait(false);

		// RFC 1870 SIZE preflight (D1): if the server advertised a maximum message size and this
		// message exceeds it, the send is doomed — but SendAsync would stream the entire DATA body
		// first, and the resulting 552 is indistinguishable from a transient blip. Fail fast with a
		// distinct, non-retryable BackendException carrying the size hint; at the EAS layer this maps
		// to ComposeMail Status 120 (permanent), which is correct — a too-big message never succeeds
		// on retry, and the DATA transfer is spared.
		EnsureWithinMaxSize(mime.LongLength, smtp.Capabilities, smtp.MaxSize);

		await smtp.SendAsync(message, ct).ConfigureAwait(false); // NOT retried — submission is not idempotent

		// The mail is accepted at this point. The QUIT teardown must NOT be able to fail the
		// operation: pass CancellationToken.None (a cancelled request must not make an already-sent
		// message look like a send failure) and swallow any disconnect error — otherwise the client
		// retries and the recipient gets it twice (D9).
		try
		{
			await smtp.DisconnectAsync(true, CancellationToken.None).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "SMTP disconnect after a successful send failed for {User}", credentials.UserName);
		}

		logger.LogInformation("Sent message {MessageId} for {User}", message.MessageId, credentials.UserName);
	}

	/// <summary>
	///   Enforces the SMTP server's advertised maximum message size (RFC 1870 SIZE) before the DATA
	///   phase. Throws a non-retryable <see cref="BackendException" /> when the server advertised the
	///   SIZE capability with a positive limit and the message exceeds it; otherwise a no-op (no
	///   advertised SIZE, or an advertised-but-unlimited <c>MaxSize == 0</c>).
	/// </summary>
	internal static void EnsureWithinMaxSize(long mimeLength, SmtpCapabilities capabilities, uint maxSize)
	{
		if ((capabilities & SmtpCapabilities.Size) != 0 && maxSize > 0 && mimeLength > maxSize)
			throw new BackendException(
				$"Message size {mimeLength} bytes exceeds the SMTP server's maximum of {maxSize} bytes (RFC 1870 SIZE).");
	}

	private static bool IsTransientSmtp(Exception ex, CancellationToken ct)
	{
		if (ct.IsCancellationRequested)
			return false;
		// SmtpCommandException (a permanent 5xx reject) and AuthenticationException (bad creds) are
		// deterministic — never retried. A transport/protocol blip or a connect timeout is.
		return ex is IOException or SocketException or SmtpProtocolException or OperationCanceledException;
	}
}

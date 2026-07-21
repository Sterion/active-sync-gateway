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

		await smtp.SendAsync(message, ct).ConfigureAwait(false); // NOT retried — submission is not idempotent
		await smtp.DisconnectAsync(true, ct).ConfigureAwait(false);
		logger.LogInformation("Sent message {MessageId} for {User}", message.MessageId, credentials.UserName);
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

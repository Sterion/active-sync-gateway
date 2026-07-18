using ActiveSync.Backends.Common;
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
		MailTransportSecurity.Apply(smtp, options.AllowInvalidCertificates, options.CaCertificatePath);
		await smtp.ConnectAsync(options.Host, options.Port, MailTransportSecurity.ForSmtp(options), ct)
			.ConfigureAwait(false);
		await smtp.AuthenticateAsync(credentials.UserName, credentials.Password, ct).ConfigureAwait(false);
		await smtp.SendAsync(message, ct).ConfigureAwait(false);
		await smtp.DisconnectAsync(true, ct).ConfigureAwait(false);
		logger.LogInformation("Sent message {MessageId} for {User}", message.MessageId, credentials.UserName);
	}
}

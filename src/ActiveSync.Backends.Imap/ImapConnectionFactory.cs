using ActiveSync.Backends.Common;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using MailKit.Net.Imap;
using Microsoft.Extensions.Logging;

namespace ActiveSync.Backends.Imap;

/// <summary>
///   Single place that opens and authenticates an IMAP connection using the configured
///   transport security and certificate policy. The caller owns the returned client (keeps it
///   alive, or disposes it). If connect/authenticate throws, the client is disposed first.
/// </summary>
internal static class ImapConnectionFactory
{
	public static async Task<ImapClient> ConnectAsync(
		ImapOptions options, BackendCredentials credentials, CancellationToken ct,
		ILogger? wireLogger = null)
	{
		// Verbose wire logging: the protocol logger costs nothing to skip, so it is only
		// attached while Trace is actually enabled for the caller's category.
		ImapClient client = wireLogger?.IsEnabled(LogLevel.Trace) == true
			? new ImapClient(new MailKitWireLogger(wireLogger))
			: new ImapClient();
		try
		{
			MailTransportSecurity.Apply(client, options.AllowInvalidCertificates, options.CaCertificatePath);
			await client.ConnectAsync(options.Host, options.Port, MailTransportSecurity.ForImap(options), ct)
				.ConfigureAwait(false);
			await client.AuthenticateAsync(credentials.UserName, credentials.Password, ct).ConfigureAwait(false);
			return client;
		}
		catch
		{
			client.Dispose();
			throw;
		}
	}
}

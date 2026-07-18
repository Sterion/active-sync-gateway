using System.Net.Security;
using MailKit;
using MailKit.Security;

namespace ActiveSync.Backends.Common;

/// <summary>Central mapping of transport-security options for all MailKit connections.</summary>
public static class MailTransportSecurity
{
	public static SecureSocketOptions ForImap(MailConnectionOptions options)
	{
		return Parse(options.Security) ?? (options.UseSsl
			? SecureSocketOptions.SslOnConnect
			: options.Port == 143
				? SecureSocketOptions.StartTlsWhenAvailable
				: SecureSocketOptions.Auto);
	}

	public static SecureSocketOptions ForSmtp(MailConnectionOptions options)
	{
		return Parse(options.Security) ?? (options.UseSsl
			? SecureSocketOptions.SslOnConnect
			: options.Port is 587 or 25
				? SecureSocketOptions.StartTlsWhenAvailable
				: SecureSocketOptions.Auto);
	}

	private static SecureSocketOptions? Parse(string? security)
	{
		return security?.ToLowerInvariant() switch
		{
			null or "" => null,
			"none" => SecureSocketOptions.None,
			"sslonconnect" or "ssl" => SecureSocketOptions.SslOnConnect,
			"starttls" => SecureSocketOptions.StartTls,
			"starttlswhenavailable" => SecureSocketOptions.StartTlsWhenAvailable,
			"auto" => SecureSocketOptions.Auto,
			_ => throw new ArgumentException($"Unknown transport security '{security}'.")
		};
	}

	public static void Apply(IMailService client, bool allowInvalidCertificates, string? caCertificatePath)
	{
		RemoteCertificateValidationCallback? callback =
			ServerCertificateValidator.CreateCallback(allowInvalidCertificates, caCertificatePath);
		if (callback is not null)
			client.ServerCertificateValidationCallback = callback;
	}
}

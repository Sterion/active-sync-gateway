using ActiveSync.Core.Backend;

namespace ActiveSync.Backends.Common;

/// <summary>
///   Schema counterparts of the shared option bases: every provider deriving from
///   <see cref="NetworkBackendOptions" /> / <see cref="MailConnectionOptions" /> composes these
///   into its <see cref="IBackendProvider.DescribeConfiguration" /> and adds only its own
///   fields, so the common shape is described once. Defaults MUST match the option classes —
///   BackendSchemaTests binds an empty section and compares.
/// </summary>
public static class BackendSchemaFields
{
	/// <summary>The two TLS-trust knobs of <see cref="NetworkBackendOptions" />.</summary>
	public static IEnumerable<BackendConfigField> Network()
	{
		yield return new BackendConfigField("AllowInvalidCertificates", "Allow invalid certificates",
			BackendFieldType.Bool, Default: "false",
			Help: "Accept self-signed or otherwise invalid backend TLS certificates. Test and lab use only.");
		yield return new BackendConfigField("CaCertificatePath", "CA certificate path",
			BackendFieldType.String,
			Help: "PEM file of extra trusted CAs (private PKI). Ignored when invalid certificates are allowed.");
	}

	/// <summary>Host/port/TLS of <see cref="MailConnectionOptions" />, with the provider's own default port.</summary>
	public static IEnumerable<BackendConfigField> MailConnection(int defaultPort)
	{
		yield return new BackendConfigField("Host", "Host", BackendFieldType.String, Required: true,
			Help: "Server hostname or address.");
		yield return new BackendConfigField("Port", "Port", BackendFieldType.Int,
			Default: defaultPort.ToString(), Min: 1, Max: 65535);
		yield return new BackendConfigField("UseSsl", "Implicit TLS", BackendFieldType.Bool, Default: "true",
			Help: "TLS from the first byte. Only consulted when Security is left unset.");
		yield return new BackendConfigField("Security", "Transport security", BackendFieldType.Enum,
			EnumValues: ["None", "SslOnConnect", "StartTls", "StartTlsWhenAvailable", "Auto"],
			Help: "Explicit override. Unset derives from implicit TLS and the port. " +
			      "\"None\" also skips opportunistic STARTTLS.");
		foreach (BackendConfigField field in Network())
			yield return field;
	}
}

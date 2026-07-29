using ActiveSync.Contracts;

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
		yield return new BackendConfigField
		{
			Name = "AllowInvalidCertificates", Label = "Allow invalid certificates",
			Type = BackendFieldType.Bool, Default = "false",
			Help = "Accept self-signed or otherwise invalid backend TLS certificates. Test and lab use only."
		};
		yield return new BackendConfigField
		{
			Name = "CaCertificatePath", Label = "CA certificate path",
			Type = BackendFieldType.String,
			Help = "PEM file of extra trusted CAs (private PKI). Ignored when invalid certificates are allowed."
		};
		yield return new BackendConfigField
		{
			Name = "CheckRevocation", Label = "Check certificate revocation",
			Type = BackendFieldType.Bool, Default = "false",
			Help = "Check CRL/OCSP revocation status against CaCertificatePath's trust store. Off by default — " +
			       "only enable this when that private CA actually publishes revocation information."
		};
	}

	/// <summary>Host/port/TLS of <see cref="MailConnectionOptions" />, with the provider's own default port.</summary>
	public static IEnumerable<BackendConfigField> MailConnection(int defaultPort)
	{
		yield return new BackendConfigField
		{
			Name = "Host", Label = "Host", Type = BackendFieldType.String, Required = true,
			Help = "Server hostname or address."
		};
		yield return new BackendConfigField
		{
			Name = "Port", Label = "Port", Type = BackendFieldType.Int,
			Default = defaultPort.ToString(), Min = 1, Max = 65535
		};
		yield return new BackendConfigField
		{
			Name = "UseSsl", Label = "Implicit TLS", Type = BackendFieldType.Bool, Default = "true",
			Help = "TLS from the first byte. Only consulted when Security is left unset."
		};
		yield return new BackendConfigField
		{
			Name = "Security", Label = "Transport security", Type = BackendFieldType.Enum,
			EnumValues = ["None", "SslOnConnect", "StartTls", "StartTlsWhenAvailable", "Auto"],
			// Unset does NOT mean "TLS is required" — the derived default for the standard
			// mail ports is opportunistic STARTTLS (StartTlsWhenAvailable), which downgrades to
			// cleartext SILENTLY if a server's greeting omits the STARTTLS capability (an on-path
			// attacker can strip it). Choose "StartTls" explicitly to make the upgrade mandatory.
			Help = "Explicit override. Unset derives from implicit TLS and the port, defaulting to " +
			       "opportunistic STARTTLS on the standard mail ports — this downgrades to cleartext " +
			       "silently if the server omits the capability. Choose \"StartTls\" to require the " +
			       "upgrade. \"None\" also skips opportunistic STARTTLS."
		};
		foreach (BackendConfigField field in Network())
			yield return field;
	}
}

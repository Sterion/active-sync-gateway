namespace ActiveSync.Backends.Common;

/// <summary>
///   Base for every network-connected backend provider's options: the two TLS-trust knobs
///   shared by all of them. Backend-specific option classes derive from this (or from
///   <see cref="MailConnectionOptions" />) and add only what is specific to that backend, so
///   the common shape — and its validation and startup-banner description — is defined once.
///   Provider-agnostic; lives here (the backend-author library), not in Core.
/// </summary>
public abstract class NetworkBackendOptions
{
	/// <summary>Accept invalid/self-signed backend TLS certificates (test/lab use).</summary>
	public bool AllowInvalidCertificates { get; set; }

	/// <summary>
	///   PEM file with one or more CA certificates trusted in addition to the system store
	///   (private PKI). Ignored when <see cref="AllowInvalidCertificates" /> is true.
	/// </summary>
	public string? CaCertificatePath { get; set; }

	/// <summary>
	///   Check CRL/OCSP revocation status of a backend certificate validated against
	///   <see cref="CaCertificatePath" />'s custom trust store (K13). Off by default — most
	///   private CAs behind a <see cref="CaCertificatePath" /> (lab/self-signed setups) publish no
	///   CRL/OCSP, so turning this on against one fails every connection closed, not open. Ignored
	///   when <see cref="AllowInvalidCertificates" /> is true or <see cref="CaCertificatePath" />
	///   is unset (there is no custom chain to build).
	/// </summary>
	public bool CheckRevocation { get; set; }
}

/// <summary>
///   Base for MailKit-based mail-transport backends (IMAP, SMTP): the connection knobs
///   <see cref="MailTransportSecurity" /> reads. Each provider sets its own default
///   <see cref="Port" /> in its constructor and adds its protocol-specific settings.
/// </summary>
public abstract class MailConnectionOptions : NetworkBackendOptions
{
	/// <summary>Server host. Required.</summary>
	public string Host { get; set; } = "";

	/// <summary>Server port. Each provider sets its own default in its constructor.</summary>
	public int Port { get; set; }

	/// <summary>Implicit TLS on connect (used when <see cref="Security" /> is unset).</summary>
	public bool UseSsl { get; set; } = true;

	/// <summary>
	///   Explicit transport security: None | SslOnConnect | StartTls | StartTlsWhenAvailable | Auto.
	///   When null, derived from <see cref="UseSsl" />/<see cref="Port" />. "None" also skips
	///   opportunistic STARTTLS — required for plaintext test servers that advertise STARTTLS
	///   with self-signed certs.
	/// </summary>
	public string? Security { get; set; }
}

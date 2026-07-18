namespace ActiveSync.Backends.Jmap;

/// <summary>
///   Settings for the "jmap" provider, bound per role section from <c>ProviderSettings</c>.
///   One JMAP HTTP session serves every role the provider is assigned; for a single server
///   the same <see cref="BaseUrl" /> repeats across the role sections.
/// </summary>
public sealed class JmapOptions
{
	/// <summary>
	///   Absolute http(s) base URL of the JMAP server, e.g. https://mail.example.com. The
	///   session resource is fetched from <c>{BaseUrl}/.well-known/jmap</c>; the server's
	///   advertised api/download/upload URLs are re-anchored onto this authority (scheme,
	///   host, port), so a server whose configured hostname differs from the address the
	///   gateway reaches it on (reverse proxy, container network) still works.
	/// </summary>
	public string BaseUrl { get; set; } = "";

	/// <summary>Accept invalid/self-signed backend TLS certificates (test/lab use).</summary>
	public bool AllowInvalidCertificates { get; set; }

	/// <summary>
	///   PEM file with one or more CA certificates trusted in addition to the system store
	///   (private PKI). Ignored when <see cref="AllowInvalidCertificates" /> is true.
	/// </summary>
	public string? CaCertificatePath { get; set; }
}

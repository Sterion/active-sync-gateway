using ActiveSync.Backends.Common;

namespace ActiveSync.Backends.Jmap;

/// <summary>
///   Settings for the "jmap" provider, bound per role section from <c>ProviderSettings</c>.
///   One JMAP HTTP session serves every role the provider is assigned; for a single server
///   the same <see cref="BaseUrl" /> repeats across the role sections. TLS-trust knobs come
///   from <see cref="NetworkBackendOptions" />.
/// </summary>
public sealed class JmapOptions : NetworkBackendOptions
{
	/// <summary>
	///   Absolute http(s) base URL of the JMAP server, e.g. https://mail.example.com. The
	///   session resource is fetched from <c>{BaseUrl}/.well-known/jmap</c>; the server's
	///   advertised api/download/upload URLs are re-anchored onto this authority (scheme,
	///   host, port), so a server whose configured hostname differs from the address the
	///   gateway reaches it on (reverse proxy, container network) still works.
	/// </summary>
	public string BaseUrl { get; set; } = "";
}

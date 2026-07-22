using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Text;
using ActiveSync.Contracts;

namespace ActiveSync.Backends.Common;

/// <summary>
///   The one place every HTTP backend (CalDAV/CardDAV via <c>WebDavClient</c>, JMAP via
///   <c>JmapClient</c>) and their readiness probes build an <see cref="HttpClient" />. Both used
///   to hand-roll the same <see cref="SocketsHttpHandler" /> — no auto-redirect (redirects are
///   followed manually so the Authorization header is never leaked to another origin), a pooled
///   connection lifetime, the operator's TLS trust settings via
///   <see cref="ServerCertificateValidator" />, and a Basic auth header. That duplication is why a
///   TLS or timeout fix could land in one client and not the other; centralizing it removes the
///   divergence.
/// </summary>
public static class BackendHttpClientFactory
{
	/// <summary>
	///   Hard ceiling on a buffered backend response body (H24). Applied as
	///   <see cref="HttpClient.MaxResponseContentBufferSize" /> on the long-lived DAV/JMAP clients so
	///   a malicious or malfunctioning server cannot stream an unbounded body into memory via the
	///   buffering read methods (<c>ReadAsStringAsync</c> etc.). It does NOT apply to a
	///   <c>ResponseHeadersRead</c> stream, so the JMAP EventSource (SSE) watcher is unaffected.
	///   Generous on purpose — a real multistatus listing for a large collection is several MB.
	/// </summary>
	public const long MaxBackendResponseBytes = 128L * 1024 * 1024;

	// Probe handlers are keyed by their TLS shape and reused across calls so a periodic
	// readiness probe does not build and discard a handler (and its connection pool) every
	// time (H26). The HttpClient wrapping them is cheap and created disposable per call.
	private static readonly ConcurrentDictionary<(bool AllowInvalid, string? CaPath), SocketsHttpHandler>
		ProbeHandlers = new();

	/// <summary>
	///   A connection-owning handler for a long-lived backend client: manual redirects, the
	///   operator's TLS trust, and a 10-minute pooled connection lifetime. The caller disposes it.
	/// </summary>
	public static SocketsHttpHandler CreateHandler(bool allowInvalidCertificates, string? caCertificatePath)
	{
		SocketsHttpHandler handler = new()
		{
			AllowAutoRedirect = false,
			PooledConnectionLifetime = TimeSpan.FromMinutes(10)
		};
		RemoteCertificateValidationCallback? certCallback = ServerCertificateValidator.CreateCallback(
			allowInvalidCertificates, caCertificatePath);
		if (certCallback is not null)
			handler.SslOptions.RemoteCertificateValidationCallback = certCallback;
		return handler;
	}

	/// <summary>
	///   A Basic-authenticated client over a fresh <see cref="CreateHandler" />. The caller owns
	///   and disposes the client (which disposes the handler). <paramref name="timeout" /> defaults
	///   to 100 s; pass <see cref="Timeout.InfiniteTimeSpan" /> for a long-lived SSE stream.
	/// </summary>
	public static HttpClient CreateClient(
		BackendCredentials credentials, bool allowInvalidCertificates, string? caCertificatePath,
		TimeSpan? timeout = null)
	{
		HttpClient http = new(CreateHandler(allowInvalidCertificates, caCertificatePath))
		{
			Timeout = timeout ?? TimeSpan.FromSeconds(100),
			MaxResponseContentBufferSize = MaxBackendResponseBytes
		};
		http.DefaultRequestHeaders.Authorization = BasicAuthHeader(credentials);
		return http;
	}

	/// <summary>
	///   An unauthenticated client for a readiness probe. The underlying handler is pooled and
	///   shared per TLS shape, so repeated probes reuse one connection pool rather than churning a
	///   handler each call (H26); the returned client does not dispose it.
	/// </summary>
	public static HttpClient CreateProbeClient(
		bool allowInvalidCertificates, string? caCertificatePath, TimeSpan timeout)
	{
		SocketsHttpHandler handler = ProbeHandlers.GetOrAdd(
			(allowInvalidCertificates, caCertificatePath),
			key =>
			{
				SocketsHttpHandler h = new()
				{
					AllowAutoRedirect = false,
					PooledConnectionLifetime = TimeSpan.FromMinutes(1)
				};
				RemoteCertificateValidationCallback? cb = ServerCertificateValidator.CreateCallback(
					key.AllowInvalid, key.CaPath);
				if (cb is not null)
					h.SslOptions.RemoteCertificateValidationCallback = cb;
				return h;
			});
		return new HttpClient(handler, disposeHandler: false) { Timeout = timeout };
	}

	/// <summary>The Basic Authorization header for the given credentials.</summary>
	public static AuthenticationHeaderValue BasicAuthHeader(BackendCredentials credentials)
	{
		string token = Convert.ToBase64String(
			Encoding.UTF8.GetBytes($"{credentials.UserName}:{credentials.Password}"));
		return new AuthenticationHeaderValue("Basic", token);
	}
}

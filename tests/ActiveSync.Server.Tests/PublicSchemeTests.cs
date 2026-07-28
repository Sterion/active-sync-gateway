using ActiveSync.Core.Options;
using ActiveSync.Server.Setup;
using Microsoft.AspNetCore.Http;

namespace ActiveSync.Server.Tests;

/// <summary>
///   <see cref="WebApplicationExtensions.ResolvePublicScheme" /> decides the request scheme behind a
///   TLS-terminating proxy: a configured PublicUrl wins over the header, so the OIDC redirect_uri is
///   built as https at both the authorize step and the token exchange.
/// </summary>
public sealed class PublicSchemeTests
{
	[Fact]
	public void PublicUrl_Wins_OverForwardedProto()
	{
		Assert.Equal("https", WebApplicationExtensions.ResolvePublicScheme("https://eas.example.com", "http"));
	}

	[Fact]
	public void ForwardedProto_UsedWhenNoPublicUrl()
	{
		Assert.Equal("https", WebApplicationExtensions.ResolvePublicScheme(null, "https"));
	}

	// E16 (behaviour change): X-Forwarded-Proto used to take the LEFTMOST entry of a comma-separated
	// chain, unlike X-Forwarded-For's rightmost-untrusted-hop rule (EndpointAuth.ClientKey) — behind
	// a proxy that APPENDS rather than overwrites the header, the leftmost value is whatever the
	// CLIENT sent, so a direct client could force the scheme downstream by sending its own
	// "http, https" and having the proxy append its own hop. Taking the last entry matches the
	// header's actual semantics under an appending proxy (the trusted proxy's own observed value).
	[Fact]
	public void ForwardedProto_ChainTakesTheLast()
	{
		Assert.Equal("http", WebApplicationExtensions.ResolvePublicScheme(null, "https, http"));
	}

	[Fact]
	public void NothingToForce_ReturnsNull()
	{
		Assert.Null(WebApplicationExtensions.ResolvePublicScheme(null, null));
		Assert.Null(WebApplicationExtensions.ResolvePublicScheme("   ", "  "));
	}

	[Fact]
	public void MalformedPublicUrl_FallsBackToForwardedProto()
	{
		Assert.Equal("https", WebApplicationExtensions.ResolvePublicScheme("not a url", "https"));
	}

	// ---------- E1: X-Forwarded-Proto is only honoured from a trusted proxy ----------

	private static HttpContext Request(string peer, string? forwardedProto = null)
	{
		DefaultHttpContext http = new();
		http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(peer);
		if (forwardedProto is not null)
			http.Request.Headers["X-Forwarded-Proto"] = forwardedProto;
		return http;
	}

	[Fact]
	public void ForwardedProto_FromAnUntrustedPeer_IsIgnored()
	{
		// E1: a direct attacker must not be able to force Request.Scheme=https (which drives the
		// OIDC redirect_uri and every absolute Autodiscover URL). No PublicUrl, no trusted proxies.
		ActiveSyncOptions options = new();
		Assert.Null(WebApplicationExtensions.ResolveRequestScheme(Request("203.0.113.9", "https"), options));
	}

	[Fact]
	public void ForwardedProto_FromATrustedProxy_IsHonoured()
	{
		ActiveSyncOptions options = new();
		options.Auth.TrustedProxies = ["10.0.0.0/8"];
		Assert.Equal("https", WebApplicationExtensions.ResolveRequestScheme(Request("10.1.2.3", "https"), options));
	}

	[Fact]
	public void PublicUrl_Wins_EvenFromAnUntrustedPeer()
	{
		// PublicUrl never depends on client-supplied headers, so it applies regardless of the peer.
		ActiveSyncOptions options = new() { PublicUrl = "https://eas.example.com" };
		Assert.Equal("https", WebApplicationExtensions.ResolveRequestScheme(Request("203.0.113.9", "http"), options));
	}

	[Fact]
	public void NoForwardedHeader_LeavesSchemeUnchanged()
	{
		ActiveSyncOptions options = new();
		options.Auth.TrustedProxies = ["10.0.0.0/8"];
		Assert.Null(WebApplicationExtensions.ResolveRequestScheme(Request("10.1.2.3"), options));
	}
}

using ActiveSync.Core.Options;
using ActiveSync.Server.Eas;
using Microsoft.AspNetCore.Http;

namespace ActiveSync.Server.Tests;

/// <summary>
///   <see cref="AutodiscoverEndpoint.BuildEasUrl" /> advertises the gateway's EAS URL to a phone.
///   E10: when PublicUrl is unset, the client-supplied X-Forwarded-Proto / X-Forwarded-Host must be
///   reflected into that URL ONLY from a configured Auth:TrustedProxies hop — from a direct peer they
///   are ignored, so an authenticated client cannot be handed an attacker-chosen sync host.
/// </summary>
public sealed class AutodiscoverUrlTests
{
	private static HttpContext Request(string peer, string host, string? proto = null, string? fwdHost = null)
	{
		DefaultHttpContext http = new();
		http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(peer);
		http.Request.Scheme = "http";
		http.Request.Host = new HostString(host);
		if (proto is not null)
			http.Request.Headers["X-Forwarded-Proto"] = proto;
		if (fwdHost is not null)
			http.Request.Headers["X-Forwarded-Host"] = fwdHost;
		return http;
	}

	private static string Path => EasEndpoint.Path;

	[Fact]
	public void ForwardedHost_FromAnUntrustedPeer_IsIgnored()
	{
		// E10: the attacker-supplied X-Forwarded-Host must not appear in the advertised URL.
		AuthOptions auth = new();
		string url = AutodiscoverEndpoint.BuildEasUrl(
			Request("203.0.113.9", "gateway.local", proto: "https", fwdHost: "evil.example.com"), auth, null);

		Assert.Equal($"http://gateway.local{Path}", url);
	}

	[Fact]
	public void ForwardedHost_FromATrustedProxy_IsHonoured()
	{
		AuthOptions auth = new() { TrustedProxies = ["10.0.0.0/8"] };
		string url = AutodiscoverEndpoint.BuildEasUrl(
			Request("10.1.2.3", "gateway.local", proto: "https", fwdHost: "eas.example.com"), auth, null);

		Assert.Equal($"https://eas.example.com{Path}", url);
	}

	[Fact]
	public void PublicUrl_Wins_EvenFromAnUntrustedPeer()
	{
		AuthOptions auth = new();
		string url = AutodiscoverEndpoint.BuildEasUrl(
			Request("203.0.113.9", "gateway.local", proto: "https", fwdHost: "evil.example.com"),
			auth, "https://eas.example.com/");

		Assert.Equal($"https://eas.example.com{Path}", url);
	}
}

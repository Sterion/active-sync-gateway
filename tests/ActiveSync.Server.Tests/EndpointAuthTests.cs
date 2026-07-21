using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using ActiveSync.Core.Security;
using ActiveSync.Server.Eas;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ActiveSync.Server.Tests;

public class EndpointAuthTests
{
	private static AuthThrottle Throttle()
	{
		return new AuthThrottle(TestOptionsMonitor.Of(new ActiveSyncOptions()));
	}

	// ---------- E3: the throttle key behind a reverse proxy ----------

	private static DefaultHttpContext Request(string peer, string? forwardedFor = null)
	{
		DefaultHttpContext http = new();
		http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(peer);
		if (forwardedFor is not null)
			http.Request.Headers["X-Forwarded-For"] = forwardedFor;
		return http;
	}

	[Fact]
	public void ClientKey_BehindATrustedProxy_UsesTheForwardedClientAddress()
	{
		// Without this, every request through an ingress shares one throttle key and one
		// user's fumbled password 429s the whole gateway.
		AuthOptions auth = new() { TrustedProxies = ["10.0.0.0/8"] };

		Assert.Equal("203.0.113.7",
			EndpointAuth.ClientKey(Request("10.1.2.3", "203.0.113.7"), auth));
		Assert.Equal("203.0.113.7",
			EndpointAuth.ClientKey(Request("10.1.2.3", "203.0.113.8, 203.0.113.7"), auth));
	}

	[Fact]
	public void ClientKey_TrustedHopsAreSkipped_RightToLeft()
	{
		AuthOptions auth = new() { TrustedProxies = ["10.0.0.0/8", "192.168.1.5"] };

		// The rightmost entry that is not itself a trusted hop is what the outermost trusted
		// proxy actually observed; anything further left is client-supplied.
		Assert.Equal("203.0.113.7",
			EndpointAuth.ClientKey(Request("10.1.2.3", "198.51.100.1, 203.0.113.7, 192.168.1.5"), auth));
	}

	[Fact]
	public void ClientKey_FromAnUntrustedPeer_IgnoresTheForwardedHeader()
	{
		// Otherwise a direct client mints a fresh throttle key per request and never blocks.
		AuthOptions auth = new() { TrustedProxies = ["10.0.0.0/8"] };

		Assert.Equal("203.0.113.9",
			EndpointAuth.ClientKey(Request("203.0.113.9", "1.2.3.4"), auth));
	}

	[Fact]
	public void ClientKey_WithNoTrustedProxiesConfigured_IsThePeerAddress()
	{
		// Default configuration must behave exactly as before.
		AuthOptions auth = new();

		Assert.Equal("10.1.2.3", EndpointAuth.ClientKey(Request("10.1.2.3", "203.0.113.7"), auth));
		Assert.Equal("10.1.2.3", EndpointAuth.ClientKey(Request("10.1.2.3"), auth));
	}

	[Fact]
	public void ClientKey_TrustedProxyWithUnusableForwardedHeader_FallsBackToThePeer()
	{
		AuthOptions auth = new() { TrustedProxies = ["10.0.0.0/8"] };

		Assert.Equal("10.1.2.3", EndpointAuth.ClientKey(Request("10.1.2.3"), auth));
		Assert.Equal("10.1.2.3", EndpointAuth.ClientKey(Request("10.1.2.3", "not-an-address"), auth));
		// Every hop trusted: nothing untrusted was ever observed, so the peer is the best key.
		Assert.Equal("10.1.2.3", EndpointAuth.ClientKey(Request("10.1.2.3", "10.9.9.9"), auth));
	}

	[Fact]
	public void ClientKey_ForwardedEntriesMayCarryPorts()
	{
		AuthOptions auth = new() { TrustedProxies = ["10.0.0.0/8"] };

		Assert.Equal("203.0.113.7",
			EndpointAuth.ClientKey(Request("10.1.2.3", "203.0.113.7:51234"), auth));
		Assert.Equal("2001:db8::1",
			EndpointAuth.ClientKey(Request("10.1.2.3", "[2001:db8::1]:51234"), auth));
	}

	[Fact]
	public void ClientKey_IPv4MappedPeer_MatchesAnIPv4TrustedProxy()
	{
		// Kestrel on a dual-stack socket reports ::ffff:10.1.2.3 for an IPv4 peer.
		AuthOptions auth = new() { TrustedProxies = ["10.0.0.0/8"] };

		Assert.Equal("203.0.113.7",
			EndpointAuth.ClientKey(Request("::ffff:10.1.2.3", "203.0.113.7"), auth));
	}

	[Fact]
	public async Task BackendOutage_Returns503()
	{
		DefaultHttpContext http = new();
		bool ok = await EndpointAuth.AuthenticateAsync(
			http, new ThrowingSessionFactory(), Throttle(), "1.2.3.4",
			new BackendCredentials("u", "p"), NullLogger.Instance, CancellationToken.None);

		Assert.False(ok);
		Assert.Equal(StatusCodes.Status503ServiceUnavailable, http.Response.StatusCode);
	}

	[Fact]
	public async Task RejectedCredentials_Challenge401()
	{
		DefaultHttpContext http = new();
		bool ok = await EndpointAuth.AuthenticateAsync(
			http, new RejectingSessionFactory(), Throttle(), "1.2.3.4",
			new BackendCredentials("u", "bad"), NullLogger.Instance, CancellationToken.None);

		Assert.False(ok);
		Assert.Equal(StatusCodes.Status401Unauthorized, http.Response.StatusCode);
	}

	private sealed class ThrowingSessionFactory : IBackendSessionFactory
	{
		public Task<bool> AuthenticateAsync(BackendCredentials credentials, CancellationToken ct)
		{
			throw new BackendException("mail backend unreachable");
		}

		public Task<IBackendSession> GetSessionAsync(
			BackendCredentials credentials, string deviceId, CancellationToken ct)
		{
			throw new NotSupportedException();
		}
	}

	private sealed class RejectingSessionFactory : IBackendSessionFactory
	{
		public Task<bool> AuthenticateAsync(BackendCredentials credentials, CancellationToken ct)
		{
			return Task.FromResult(false);
		}

		public Task<IBackendSession> GetSessionAsync(
			BackendCredentials credentials, string deviceId, CancellationToken ct)
		{
			throw new NotSupportedException();
		}
	}
}

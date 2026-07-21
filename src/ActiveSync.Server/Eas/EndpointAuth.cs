using System.Net;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using ActiveSync.Core.Security;

namespace ActiveSync.Server.Eas;

/// <summary>
///   Shared Basic-auth prologue for the authenticated endpoints (EAS + Autodiscover): the
///   per-address brute-force throttle and the credential verification against the mail
///   backend. Each helper writes the appropriate response (429 / 401 challenge / 503) and
///   returns a flag telling the caller whether to stop.
/// </summary>
internal static class EndpointAuth
{
	/// <summary>
	///   Per-request throttle key: the client's address. That is the socket's peer address,
	///   EXCEPT when the request arrived from a configured <see cref="AuthOptions.TrustedProxies" />
	///   hop — behind an ingress the peer is the ingress, so every request would share one key
	///   and one user's fumbled password would 429 the whole gateway.
	///   <para>
	///     The trust check is on the peer, not on the header, and that ordering is the whole
	///     security property: <c>X-Forwarded-For</c> is client-supplied, so honouring it from
	///     an untrusted peer would let a direct attacker mint a fresh key per attempt and
	///     never trip the counter — strictly worse than the shared key this fixes.
	///   </para>
	/// </summary>
	public static string ClientKey(HttpContext http, AuthOptions auth)
	{
		if (Normalize(http.Connection.RemoteIpAddress) is not { } peer)
			return "unknown";
		if (auth.TrustedProxies.Count == 0 || !IsTrusted(peer, auth.TrustedProxies))
			return peer.ToString();

		// Rightmost entry that is not itself a trusted hop: the address the outermost
		// trusted proxy actually observed. Everything left of it was appended by something
		// we do not trust, so it proves nothing.
		List<string> hops = ForwardedFor(http);
		for (int i = hops.Count - 1; i >= 0; i--)
			if (ParseForwardedAddress(hops[i]) is { } candidate && !IsTrusted(candidate, auth.TrustedProxies))
				return candidate.ToString();

		// Header absent, unparsable, or every hop trusted — the peer is the best key left.
		return peer.ToString();
	}

	/// <summary>
	///   X-Forwarded-For entries, in order. Hard-capped: the header is unauthenticated input
	///   and the values feed throttle keys, so a long chain must not become work per request.
	/// </summary>
	private static List<string> ForwardedFor(HttpContext http)
	{
		const int maxHops = 16;
		List<string> entries = new();
		foreach (string? header in http.Request.Headers["X-Forwarded-For"])
		{
			if (string.IsNullOrEmpty(header))
				continue;
			foreach (string part in header.Split(','))
			{
				if (entries.Count == maxHops)
					return entries;
				string trimmed = part.Trim();
				if (trimmed.Length > 0)
					entries.Add(trimmed);
			}
		}

		return entries;
	}

	/// <summary>An X-Forwarded-For entry, which may carry a port ("1.2.3.4:5678", "[::1]:5678").</summary>
	private static IPAddress? ParseForwardedAddress(string entry)
	{
		if (IPAddress.TryParse(entry, out IPAddress? bare))
			return Normalize(bare);
		// Only strip a trailing ":port" when it cannot be part of a bare IPv6 address —
		// "::1" has colons but no port, and IPAddress.TryParse above already accepted it.
		int lastColon = entry.LastIndexOf(':');
		if (lastColon > 0 && (entry[0] == '[' || entry.IndexOf(':') == lastColon) &&
		    IPAddress.TryParse(entry[..lastColon].Trim('[', ']'), out IPAddress? withPort))
			return Normalize(withPort);
		return null;
	}

	/// <summary>
	///   Kestrel reports an IPv4 peer as ::ffff:a.b.c.d on a dual-stack socket, so an
	///   operator's "10.0.0.0/8" would never match. Compare in IPv4 terms when we can.
	/// </summary>
	private static IPAddress? Normalize(IPAddress? address)
	{
		return address is not null && address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
	}

	private static bool IsTrusted(IPAddress address, List<string> trustedProxies)
	{
		foreach (string entry in trustedProxies)
		{
			string trimmed = entry.Trim();
			if (trimmed.Length == 0)
				continue;
			if (trimmed.Contains('/'))
			{
				if (IPNetwork.TryParse(trimmed, out IPNetwork network) && network.Contains(address))
					return true;
			}
			else if (IPAddress.TryParse(trimmed, out IPAddress? single) &&
			         (Normalize(single) ?? single).Equals(address))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	///   When the address is over the per-address ceiling (username-agnostic, checked before
	///   the credentials are parsed), writes 429 + Retry-After and returns true; otherwise
	///   returns false. The tighter per-(address, user) block is enforced in
	///   <see cref="AuthenticateAsync" /> once the username is known.
	/// </summary>
	public static bool IsThrottled(HttpContext http, AuthThrottle throttle, string clientKey)
	{
		if (throttle.BlockedForSeconds(clientKey, throttle.IpWideLimit) is not { } retryAfter)
			return false;
		Core.Observability.GatewayMetrics.RecordThrottleRejection();
		http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
		http.Response.Headers.RetryAfter = retryAfter.ToString();
		return true;
	}

	/// <summary>
	///   Verifies the credentials against the mail backend, recording the throttle
	///   success/failure. The failure counter is keyed by (address, username) so one
	///   account's success cannot reset another's; every failure also feeds the per-address
	///   ceiling. On a per-user block writes 429; on rejected credentials writes the
	///   Basic-auth challenge; on a backend outage writes 503. Returns true only when
	///   authenticated.
	/// </summary>
	public static async Task<bool> AuthenticateAsync(
		HttpContext http,
		IBackendSessionFactory sessionFactory,
		AuthThrottle throttle,
		string clientKey,
		BackendCredentials credentials,
		ILogger logger,
		CancellationToken ct)
	{
		string userKey = $"{clientKey}\n{credentials.UserName}";
		if (throttle.BlockedForSeconds(userKey) is { } retryAfter)
		{
			Core.Observability.GatewayMetrics.RecordThrottleRejection();
			http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
			http.Response.Headers.RetryAfter = retryAfter.ToString();
			return false;
		}

		try
		{
			if (!await sessionFactory.AuthenticateAsync(credentials, ct))
			{
				throttle.RecordFailure(userKey);
				throttle.RecordFailure(clientKey); // feeds the per-address ceiling
				HttpBasicAuth.Challenge(http);
				return false;
			}

			// Clear only this account's counter — never another user's on the same address.
			throttle.RecordSuccess(userKey);
			return true;
		}
		catch (BackendException ex)
		{
			logger.LogError(ex, "Backend unavailable during authentication");
			http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
			return false;
		}
	}
}

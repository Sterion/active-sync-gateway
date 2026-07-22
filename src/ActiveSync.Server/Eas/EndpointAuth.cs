using System.Net;
using ActiveSync.Contracts;
using ActiveSync.Core.Accounts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using ActiveSync.Core.Security;
using ActiveSync.Core.State;

namespace ActiveSync.Server.Eas;

/// <summary>Why a login that authenticated is nonetheless refused (checked post-auth).</summary>
internal enum LoginRefusal
{
	None,
	Disabled,
	Blocked
}

/// <summary>
///   Outcome of <see cref="EndpointAuth.TryAuthorizeAsync" />: authorized plus the verified
///   credentials, or a stop (the response has already been written).
/// </summary>
internal readonly record struct AuthOutcome(bool Authorized, BackendCredentials? Credentials);

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
		Core.Observability.GatewayMetrics.RecordThrottleRejection("eas");
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
			Core.Observability.GatewayMetrics.RecordThrottleRejection("eas");
			Core.Observability.GatewayMetrics.RecordAuthOutcome("eas", "throttled", credentials.UserName);
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
				Core.Observability.GatewayMetrics.RecordAuthOutcome("eas", "failure", credentials.UserName);
				HttpBasicAuth.Challenge(http);
				return false;
			}

			// Clear only this account's counter — never another user's on the same address.
			throttle.RecordSuccess(userKey);
			Core.Observability.GatewayMetrics.RecordAuthOutcome("eas", "success", credentials.UserName);
			return true;
		}
		catch (BackendException ex)
		{
			logger.LogError(ex, "Backend unavailable during authentication");
			Core.Observability.GatewayMetrics.RecordAuthOutcome("eas", "error", credentials.UserName);
			http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
			return false;
		}
	}

	/// <summary>
	///   The disabled/blocked decision shared by both authenticated endpoints — the single point the
	///   E14 drift proved must not be copy-pasted. An account disabled via <c>eas user disable</c>
	///   refuses every device; operator blocks (<c>eas block/unblock</c>) are the ad-hoc/device-scoped
	///   variant. A null <paramref name="deviceId" /> matches only user-level blocks (Autodiscover
	///   carries no device id). Decision only, evaluated after authentication so only holders of valid
	///   credentials can observe it — the caller writes the 403 so each endpoint keeps its own body.
	/// </summary>
	public static async Task<LoginRefusal> CheckLoginRefusalAsync(
		AccountResolver resolver, SyncStateService state, string userName, string? deviceId, CancellationToken ct)
	{
		if (resolver.IsLoginDisabled(userName))
			return LoginRefusal.Disabled;
		if (await state.IsLoginBlockedAsync(userName, deviceId, ct))
			return LoginRefusal.Blocked;
		return LoginRefusal.None;
	}

	/// <summary>
	///   The complete Basic-auth prologue for an endpoint with no interleaved per-request work:
	///   unconfigured ⇒ 503, over the per-address ceiling ⇒ 429, missing/invalid credentials ⇒ 401
	///   challenge, backend auth failure ⇒ 429/401/503, and the disabled/blocked gate ⇒ 403 (device
	///   id optional — null for Autodiscover). Returns the verified credentials when authorized;
	///   otherwise the response has been written and the caller returns.
	///   <para>
	///     The EAS endpoint deliberately does NOT use this: its prologue interleaves query-string
	///     parsing, device-id validation, the pre-auth metrics label and the pass-through provisioner
	///     (which must run between auth and the block check), and folding those in would reorder them.
	///     It shares <see cref="CheckLoginRefusalAsync" /> instead, so the 403 gate cannot drift again.
	///   </para>
	/// </summary>
	public static async Task<AuthOutcome> TryAuthorizeAsync(
		HttpContext http,
		BackendRolesProvider rolesProvider,
		AuthOptions authOptions,
		AuthThrottle throttle,
		IBackendSessionFactory sessionFactory,
		AccountResolver resolver,
		SyncStateService state,
		string? blockDeviceId,
		ILogger logger,
		CancellationToken ct)
	{
		// Unconfigured gateway (no mail backend yet): answer 503 until it is configured via
		// `eas config set` (applied within ~1s by the settings change-stamp poll).
		if (!rolesProvider.Current.IsMailConfigured)
		{
			logger.LogWarning("Request refused: the gateway has no mail backend configured (503)");
			http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
			return new AuthOutcome(false, null);
		}

		string clientKey = ClientKey(http, authOptions);
		if (IsThrottled(http, throttle, clientKey))
			return new AuthOutcome(false, null);

		BackendCredentials? credentials = HttpBasicAuth.Parse(http.Request.Headers.Authorization.ToString());
		if (credentials is null)
		{
			HttpBasicAuth.Challenge(http);
			return new AuthOutcome(false, null);
		}

		if (!await AuthenticateAsync(http, sessionFactory, throttle, clientKey, credentials, logger, ct))
			return new AuthOutcome(false, null);

		LoginRefusal refusal = await CheckLoginRefusalAsync(resolver, state, credentials.UserName, blockDeviceId, ct);
		if (refusal != LoginRefusal.None)
		{
			logger.LogWarning("Refused {State} login {User}",
				refusal == LoginRefusal.Disabled ? "disabled" : "blocked", LogText.Clean(credentials.UserName, 128));
			http.Response.StatusCode = StatusCodes.Status403Forbidden;
			return new AuthOutcome(false, null);
		}

		return new AuthOutcome(true, credentials);
	}
}

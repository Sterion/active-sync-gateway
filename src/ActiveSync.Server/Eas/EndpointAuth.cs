using ActiveSync.Core.Backend;

namespace ActiveSync.Server.Eas;

/// <summary>
///   Shared Basic-auth prologue for the authenticated endpoints (EAS + Autodiscover): the
///   per-address brute-force throttle and the credential verification against the mail
///   backend. Each helper writes the appropriate response (429 / 401 challenge / 503) and
///   returns a flag telling the caller whether to stop.
/// </summary>
internal static class EndpointAuth
{
	/// <summary>Per-request throttle key: the client's remote address.</summary>
	public static string ClientKey(HttpContext http)
	{
		return http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
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

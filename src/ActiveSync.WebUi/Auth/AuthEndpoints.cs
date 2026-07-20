using System.Security.Claims;
using ActiveSync.Core.Accounts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using ActiveSync.Core.Security;
using ActiveSync.Core.State;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ActiveSync.WebUi.Auth;

/// <summary>
///   Session endpoints of both portals (login / logout / whoami / mode). Local login runs the
///   exact verdict path the phones get (<see cref="IBackendSessionFactory.AuthenticateAsync" />:
///   local password rule, then backend probe, with the same caches), plus the web-only rules:
///   only DECLARED accounts may log in (a pure pass-through login has nothing to manage), a
///   user-level login block applies here too, and the admin portal additionally requires the
///   account's Admin flag. When OIDC is configured, local login is disabled entirely.
/// </summary>
internal static class AuthEndpoints
{
	internal sealed record LoginRequest(string? Username, string? Password);

	internal static void Map(RouteGroupBuilder api, bool admin)
	{
		api.MapGet("auth/mode", (IOptionsMonitor<ActiveSyncOptions> options) =>
		{
			WebUiOptions webUi = options.CurrentValue.WebUi;
			return Results.Ok(new
			{
				mode = IsOidcConfigured(webUi) ? "oidc" : "local",
				adminEnabled = webUi.Admin.Enabled,
				userPortalEnabled = webUi.UserPortal.Enabled
			});
		}).AllowAnonymous();

		api.MapPost("login", (LoginRequest request, HttpContext http,
				IOptionsMonitor<ActiveSyncOptions> options, AccountResolver resolver,
				IBackendSessionFactory sessionFactory, AuthThrottle throttle,
				SyncStateService state, ILoggerFactory loggerFactory, CancellationToken ct) =>
			LoginAsync(request, http, admin, options, resolver, sessionFactory, throttle, state,
				loggerFactory.CreateLogger("ActiveSync.WebUi.Auth"), ct))
			.AllowAnonymous();

		// Anonymous so sign-out always works — e.g. a non-admin cookie stuck on /admin.
		api.MapPost("logout", async (HttpContext http) =>
		{
			await http.SignOutAsync(WebUiAuth.Scheme);
			return Results.Ok();
		}).AllowAnonymous();

		api.MapGet("session", (ClaimsPrincipal user) => Results.Ok(new
		{
			login = user.Identity?.Name,
			admin = user.HasClaim(WebUiAuth.AdminClaim, "true")
		}));
	}

	internal static bool IsOidcConfigured(WebUiOptions webUi)
	{
		return webUi.Oidc is { Enabled: true } oidc && !string.IsNullOrWhiteSpace(oidc.Authority);
	}

	private static async Task<IResult> LoginAsync(
		LoginRequest request, HttpContext http, bool admin,
		IOptionsMonitor<ActiveSyncOptions> options, AccountResolver resolver,
		IBackendSessionFactory sessionFactory, AuthThrottle throttle,
		SyncStateService state, ILogger logger, CancellationToken ct)
	{
		// OIDC mode: the local login form does not exist — sign in via the identity provider.
		if (IsOidcConfigured(options.CurrentValue.WebUi))
			return Results.NotFound();

		if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrEmpty(request.Password))
			return Results.BadRequest(new { error = "username and password are required" });

		// Same brute-force throttle as EAS, under web-specific keys so a phone retry storm
		// and a web attack never share counters.
		string address = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
		string addressKey = $"webui:{address}";
		string userKey = $"webui:{address}\n{request.Username}";
		if (throttle.BlockedForSeconds(addressKey, throttle.IpWideLimit) is { } addressRetry)
			return TooManyRequests(http, addressRetry);
		if (throttle.BlockedForSeconds(userKey) is { } userRetry)
			return TooManyRequests(http, userRetry);

		await resolver.EnsureFreshAsync(false, ct);
		// Only declared accounts (config or database) may use the web interfaces. The response
		// is indistinguishable from a wrong password so logins cannot be enumerated.
		if (!resolver.MergedUsers.TryGetValue(request.Username, out MergedAccount? account))
		{
			throttle.RecordFailure(userKey);
			throttle.RecordFailure(addressKey);
			return Results.Unauthorized();
		}

		bool authenticated;
		try
		{
			authenticated = await sessionFactory.AuthenticateAsync(
				new BackendCredentials(request.Username, request.Password), ct);
		}
		catch (BackendException ex)
		{
			logger.LogError(ex, "Backend unavailable during web login for {User}", request.Username);
			return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
		}
		catch (KeyNotFoundException)
		{
			// Unconfigured gateway and no local password rule: nothing can verify the
			// password (the bootstrap recipe gives the admin account a pbkdf2$ hash, which
			// verifies locally and never reaches this).
			logger.LogWarning(
				"Web login for {User} rejected: no local password rule and no mail backend to probe",
				request.Username);
			authenticated = false;
		}

		if (!authenticated)
		{
			throttle.RecordFailure(userKey);
			throttle.RecordFailure(addressKey);
			return Results.Unauthorized();
		}

		// A user-level login block disables the web exactly like EAS (403 after valid auth;
		// the CLI remains the un-lockable escape hatch).
		if (await state.IsLoginBlockedAsync(request.Username, null, ct))
			return Results.StatusCode(StatusCodes.Status403Forbidden);

		bool isAdmin = account.Options.Admin == true;
		if (admin && !isAdmin)
			return Results.StatusCode(StatusCodes.Status403Forbidden);

		throttle.RecordSuccess(userKey);
		List<Claim> claims = [new Claim(ClaimTypes.Name, request.Username)];
		if (isAdmin)
			claims.Add(new Claim(WebUiAuth.AdminClaim, "true"));
		await http.SignInAsync(WebUiAuth.Scheme,
			new ClaimsPrincipal(new ClaimsIdentity(claims, WebUiAuth.Scheme)));
		logger.LogInformation("Web {Portal} login for {User}", admin ? "admin" : "portal", request.Username);
		return Results.Ok(new { login = request.Username, admin = isAdmin });
	}

	private static IResult TooManyRequests(HttpContext http, int retryAfterSeconds)
	{
		http.Response.Headers.RetryAfter = retryAfterSeconds.ToString();
		return Results.StatusCode(StatusCodes.Status429TooManyRequests);
	}
}

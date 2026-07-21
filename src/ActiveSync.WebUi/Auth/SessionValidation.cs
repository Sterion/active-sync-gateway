using System.Globalization;
using System.Security.Claims;
using ActiveSync.Core.Accounts;
using ActiveSync.Core.State;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ActiveSync.WebUi.Auth;

/// <summary>
///   Re-resolves a live web session against the account state on every request (rate-limited to
///   once a minute per session). The cookie ticket is self-contained and slides for 12 hours, so
///   without this an account disabled, blocked, deleted or stripped of its Admin flag keeps a
///   fully functional session for up to half a day of continued use — and "sliding" means an
///   active attacker never times out. That undermines exactly the two controls the admin UI
///   presents as the lockout mechanism.
///   <para>
///     The gateway is otherwise built around live re-reads (IOptionsMonitor everywhere,
///     EnsureFreshAsync before nearly every resolver access); this brings web sessions in line.
///   </para>
/// </summary>
internal static class SessionValidation
{
	/// <summary>Unix seconds of the last successful revalidation, carried in the ticket itself.</summary>
	internal const string ValidatedAtClaim = "eas:validated";

	/// <summary>
	///   Marks a session whose admin capability came from the OIDC admin claim rather than the
	///   account flag. Such a session cannot be re-derived from the account (the IdP's claims
	///   are not part of the ticket — deliberately, they never enter the session), so its admin
	///   bit is carried forward; revoking it means revoking the claim at the identity provider,
	///   or clearing the account flag AND signing the user out.
	/// </summary>
	internal const string AdminSourceClaim = "eas:admin-src";

	internal const string OidcAdminSource = "oidc";

	/// <summary>
	///   Unix seconds at which this SESSION began — stamped once at sign-in and carried through
	///   every re-mint. Deliberately not the ticket's IssuedUtc: sliding renewal rewrites that
	///   on every renewal, so it cannot be compared against a revocation cut-off.
	/// </summary>
	internal const string SessionStartClaim = "eas:sid-iat";

	/// <summary>The sign-in stamp both login paths attach to a freshly minted session.</summary>
	internal static Claim SessionStart(DateTimeOffset now)
	{
		return new Claim(SessionStartClaim,
			now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
	}

	/// <summary>How long a validated session may run before it is checked again.</summary>
	internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

	/// <summary>True when this ticket has not been checked within <see cref="Interval" />.</summary>
	internal static bool IsDue(ClaimsPrincipal principal, DateTimeOffset now)
	{
		string? stamped = principal.FindFirst(ValidatedAtClaim)?.Value;
		if (stamped is null ||
		    !long.TryParse(stamped, NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds))
			return true;
		DateTimeOffset validatedAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
		// A stamp in the future means a clock change, not a fresh session — re-check.
		return validatedAt > now || now - validatedAt >= Interval;
	}

	/// <summary>
	///   Rebuilds the session principal from the CURRENT account state, or returns null when the
	///   session must be terminated. Pure — the I/O lives in <see cref="ValidateAsync" />.
	/// </summary>
	internal static ClaimsPrincipal? Rebuild(
		ClaimsPrincipal principal, MergedAccount? account, bool blocked, DateTimeOffset now,
		DateTime? sessionsValidAfterUtc = null)
	{
		string? login = principal.Identity?.Name;
		if (string.IsNullOrEmpty(login) || account is null || account.Options.Enabled == false || blocked)
			return null;

		// Signed out (or password changed) after this session began. A ticket minted before the
		// start stamp existed has no claim at all and is treated as older than any cut-off.
		if (sessionsValidAfterUtc is { } validAfter && SessionStartedAt(principal) < validAfter)
			return null;

		// Admin is re-derived from the account flag; only an OIDC-claim grant is carried over.
		bool oidcAdmin = principal.HasClaim(AdminSourceClaim, OidcAdminSource);
		List<Claim> claims = [new Claim(ClaimTypes.Name, login)];
		if (account.Options.Admin == true || oidcAdmin)
			claims.Add(new Claim(WebUiAuth.AdminClaim, "true"));
		if (oidcAdmin)
			claims.Add(new Claim(AdminSourceClaim, OidcAdminSource));
		if (principal.FindFirst(SessionStartClaim) is { } started)
			claims.Add(new Claim(SessionStartClaim, started.Value));
		claims.Add(new Claim(ValidatedAtClaim,
			now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));
		return new ClaimsPrincipal(new ClaimsIdentity(claims, WebUiAuth.Scheme));
	}

	/// <summary>When this session began; <see cref="DateTime.MinValue" /> when it carries no stamp.</summary>
	internal static DateTime SessionStartedAt(ClaimsPrincipal principal)
	{
		return principal.FindFirst(SessionStartClaim)?.Value is { } value &&
		       long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds)
			? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
			: DateTime.MinValue;
	}

	/// <summary>The cookie handler's OnValidatePrincipal hook.</summary>
	internal static async Task ValidateAsync(CookieValidatePrincipalContext context)
	{
		ClaimsPrincipal? principal = context.Principal;
		if (principal?.Identity?.IsAuthenticated != true)
			return;

		DateTimeOffset now = DateTimeOffset.UtcNow;
		if (!IsDue(principal, now))
			return;

		string login = principal.Identity.Name ?? "";
		IServiceProvider services = context.HttpContext.RequestServices;
		ILogger logger = services.GetRequiredService<ILoggerFactory>()
			.CreateLogger("ActiveSync.WebUi.Session");
		CancellationToken ct = context.HttpContext.RequestAborted;

		MergedAccount? account;
		bool blocked;
		DateTime? validAfter;
		try
		{
			AccountResolver resolver = services.GetRequiredService<AccountResolver>();
			await resolver.EnsureFreshAsync(false, ct);
			resolver.MergedUsers.TryGetValue(login, out account);
			SyncStateService state = services.GetRequiredService<SyncStateService>();
			blocked = !string.IsNullOrEmpty(login) && await state.IsLoginBlockedAsync(login, null, ct);
			validAfter = string.IsNullOrEmpty(login)
				? null
				: await state.GetSessionsValidAfterAsync(login, ct);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			// Deliberately fail OPEN on an infrastructure fault: a database blip must not log
			// every operator out mid-incident. Nothing is stamped, so the very next request
			// retries — the window is one request, not the ticket lifetime.
			logger.LogWarning(ex, "Could not revalidate the web session of {Login}; keeping it for now", login);
			return;
		}

		ClaimsPrincipal? rebuilt = Rebuild(principal, account, blocked, now, validAfter);
		if (rebuilt is null)
		{
			logger.LogInformation(
				"Web session for {Login} terminated: the account is gone, disabled, blocked or signed out",
				login);
			context.RejectPrincipal();
			await context.HttpContext.SignOutAsync(WebUiAuth.Scheme);
			return;
		}

		if (principal.HasClaim(WebUiAuth.AdminClaim, "true") && !rebuilt.HasClaim(WebUiAuth.AdminClaim, "true"))
			logger.LogInformation("Web session for {Login} lost its admin capability", login);
		context.ReplacePrincipal(rebuilt);
		context.ShouldRenew = true;
	}
}

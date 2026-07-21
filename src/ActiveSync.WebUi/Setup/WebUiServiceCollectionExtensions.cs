using System.Security.Claims;
using System.Security.Cryptography;
using ActiveSync.Core.Accounts;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using ActiveSync.Core.Security;
using ActiveSync.Core.State;
using ActiveSync.WebUi.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ActiveSync.WebUi.Setup;

/// <summary>
///   Service registration of the web interfaces. Cookie authentication is passive (it only
///   reads its own cookie and never challenges), so registering it leaves the EAS Basic-auth
///   and metrics endpoints completely untouched. The OIDC handler is registered only when an
///   Authority is configured at startup (restart tier — the handler is DI-time); the
///   admin-claim and AutoProvision knobs stay live (evaluated per sign-in).
/// </summary>
public static class WebUiServiceCollectionExtensions
{
	/// <summary>The OIDC challenge scheme name (registered only when configured).</summary>
	internal const string OidcScheme = "Oidc";

	public static void AddWebUi(this WebApplicationBuilder builder)
	{
		AuthenticationBuilder authentication = builder.Services.AddAuthentication(WebUiAuth.Scheme)
			.AddCookie(WebUiAuth.Scheme, cookie =>
			{
				cookie.Cookie.Name = WebUiAuth.CookieName;
				cookie.Cookie.HttpOnly = true;
				cookie.Cookie.SameSite = SameSiteMode.Strict;
				cookie.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
				cookie.SlidingExpiration = true;
				cookie.ExpireTimeSpan = TimeSpan.FromHours(12);
				// A JSON API: plain status codes instead of login-page redirects — the SPA
				// reacts to 401/403 itself.
				cookie.Events.OnRedirectToLogin = context =>
				{
					context.Response.StatusCode = StatusCodes.Status401Unauthorized;
					return Task.CompletedTask;
				};
				cookie.Events.OnRedirectToAccessDenied = context =>
				{
					context.Response.StatusCode = StatusCodes.Status403Forbidden;
					return Task.CompletedTask;
				};
			});

		// Restart-tier read: the OIDC handler can only be added at DI time, exactly like the
		// other listener-shaping options ProgramServer reads from the raw configuration.
		ActiveSyncOptions startup = builder.Configuration.GetSection("ActiveSync").Get<ActiveSyncOptions>()
			?? new ActiveSyncOptions();
		if (startup.WebUi.Oidc is { Enabled: true } oidc && !string.IsNullOrWhiteSpace(oidc.Authority))
			authentication.AddOpenIdConnect(OidcScheme,
				options => ConfigureOidc(options, oidc, startup.Encryption));

		builder.Services.AddAuthorization(authorization =>
		{
			authorization.AddPolicy(WebUiAuth.AdminPolicy, policy => policy
				.AddAuthenticationSchemes(WebUiAuth.Scheme)
				.RequireAuthenticatedUser()
				.RequireClaim(WebUiAuth.AdminClaim, "true"));
			authorization.AddPolicy(WebUiAuth.UserPolicy, policy => policy
				.AddAuthenticationSchemes(WebUiAuth.Scheme)
				.RequireAuthenticatedUser());
		});

		// Cookie signing keys live in the state database (sealed with the master key), so
		// sessions survive restarts and validate on every replica.
		builder.Services.AddDataProtection().SetApplicationName("ActiveSync.WebUi");
		builder.Services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(provider =>
			new ConfigureOptions<KeyManagementOptions>(management => management.XmlRepository =
				new DbXmlRepository(
					provider.GetRequiredService<ISyncDbContextFactory>(),
					provider.GetRequiredService<IOptions<ActiveSyncOptions>>())));
	}

	private static void ConfigureOidc(
		OpenIdConnectOptions options, WebUiOidcOptions oidc, EncryptionOptions encryption)
	{
		options.Authority = oidc.Authority;
		options.ClientId = oidc.ClientId;
		options.ClientSecret = UnsealClientSecret(oidc.ClientSecret, encryption);
		options.ResponseType = "code";
		options.SignInScheme = WebUiAuth.Scheme;
		// Outside the gated /admin and /user prefixes on purpose: the callback must work in a
		// portal-only deployment where /admin answers 404.
		options.CallbackPath = "/oidc/callback";
		options.RequireHttpsMetadata = oidc.RequireHttpsMetadata;
		// Raw claim names (preferred_username, groups, email) — no legacy SOAP-era renaming.
		options.MapInboundClaims = false;
		// Many IdPs only put profile claims (preferred_username) in the userinfo endpoint.
		options.GetClaimsFromUserInfoEndpoint = true;
		options.Scope.Clear();
		foreach (string scope in oidc.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
			options.Scope.Add(scope);

		options.Events.OnTicketReceived = OnTicketReceivedAsync;
		options.Events.OnRemoteFailure = context =>
		{
			// A cancelled/failed IdP round-trip lands back on the login view, not an error page.
			context.Response.Redirect($"{TargetPortal(context.Properties)}#sso-failed");
			context.HandleResponse();
			return Task.CompletedTask;
		};
	}

	/// <summary>
	///   Maps the IdP ticket onto a gateway account (see <see cref="OidcLogin" />): declared
	///   accounts sign in (admin = flag OR claim), unknown logins are JIT-provisioned when
	///   Oidc:AutoProvision is on (claim-only admin), everything else is turned away on the
	///   login view. The admin-claim/AutoProvision knobs are read LIVE from IOptionsMonitor.
	/// </summary>
	private static async Task OnTicketReceivedAsync(TicketReceivedContext context)
	{
		IServiceProvider services = context.HttpContext.RequestServices;
		ActiveSyncOptions current = services.GetRequiredService<IOptionsMonitor<ActiveSyncOptions>>().CurrentValue;
		WebUiOidcOptions oidc = current.WebUi.Oidc!;
		AccountResolver resolver = services.GetRequiredService<AccountResolver>();
		AccountStore store = services.GetRequiredService<AccountStore>();
		ILogger logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("ActiveSync.WebUi.Oidc");
		CancellationToken ct = context.HttpContext.RequestAborted;
		await resolver.EnsureFreshAsync(false, ct);

		OidcLogin.Verdict verdict = await OidcLogin.EvaluateAsync(
			context.Principal ?? new ClaimsPrincipal(), oidc, resolver.MergedUsers,
			async (login, entry) =>
			{
				List<string> failures = AccountResolver.ValidateEntry(current, resolver.Roles,
					services.GetRequiredService<BackendProviderRegistry>(), login, entry);
				if (failures.Count > 0)
					return failures;
				await store.UpsertAsync(login, entry, ct);
				await resolver.EnsureFreshAsync(true, ct);
				return failures;
			});

		// A user-level login block disables OIDC sign-in exactly like the local form.
		bool blocked = verdict.Allowed && await services.GetRequiredService<SyncStateService>()
			.IsLoginBlockedAsync(verdict.Login!, null, ct);
		if (!verdict.Allowed || blocked)
		{
			logger.LogWarning("OIDC sign-in rejected for {Login}: {Reason}",
				verdict.Login ?? "(no login claim)", blocked ? "login is blocked" : verdict.Reason);
			context.Response.Redirect($"{TargetPortal(context.Properties)}#sso-denied");
			context.HandleResponse();
			return;
		}

		if (verdict.Provisioned)
			logger.LogInformation("OIDC auto-provisioned a database account for {Login}", verdict.Login);
		logger.LogInformation("OIDC sign-in for {Login}{Admin}", verdict.Login, verdict.IsAdmin ? " (admin)" : "");

		// The session principal is OURS (login + admin bit) — IdP claims never leak into it.
		List<Claim> claims = [new Claim(ClaimTypes.Name, verdict.Login!)];
		if (verdict.IsAdmin)
			claims.Add(new Claim(WebUiAuth.AdminClaim, "true"));
		context.Principal = new ClaimsPrincipal(new ClaimsIdentity(claims, WebUiAuth.Scheme));
	}

	/// <summary>The portal the challenge came from ("/admin" or "/user"; RedirectUri carries it).</summary>
	private static string TargetPortal(AuthenticationProperties? properties)
	{
		return properties?.RedirectUri is { } target && target.StartsWith("/user", StringComparison.OrdinalIgnoreCase)
			? "/user"
			: "/admin";
	}

	/// <summary>Sealed (enc:v1:) client secrets — how the web settings editor stores them — unseal here.</summary>
	private static string? UnsealClientSecret(string? configured, EncryptionOptions encryption)
	{
		if (string.IsNullOrWhiteSpace(configured) || !SecretValue.IsSealed(configured))
			return configured;
		byte[]? key = EncryptionKeyLoader.TryLoadKey(encryption, out _);
		if (key is null)
			throw new InvalidOperationException(
				"ActiveSync:WebUi:Oidc:ClientSecret is sealed (enc:v1:) but no ActiveSync:Encryption key is configured.");
		try
		{
			return SecretValue.TryUnseal(configured, key, out string? plaintext, out string? error)
				? plaintext
				: throw new InvalidOperationException(
					$"ActiveSync:WebUi:Oidc:ClientSecret could not be unsealed — {error}.");
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
		}
	}
}

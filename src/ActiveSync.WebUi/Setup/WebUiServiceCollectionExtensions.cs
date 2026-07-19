using ActiveSync.Core.Options;
using ActiveSync.Core.State;
using ActiveSync.WebUi.Auth;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ActiveSync.WebUi.Setup;

/// <summary>
///   Service registration of the web interfaces. Cookie authentication is passive (it only
///   reads its own cookie and never challenges), so registering it leaves the EAS Basic-auth
///   and metrics endpoints completely untouched.
/// </summary>
public static class WebUiServiceCollectionExtensions
{
	public static void AddWebUi(this WebApplicationBuilder builder)
	{
		builder.Services.AddAuthentication(WebUiAuth.Scheme)
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
}

using ActiveSync.Core.Options;
using ActiveSync.WebUi.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace ActiveSync.WebUi.Setup;

/// <summary>
///   Pipeline + endpoint mapping of the web interfaces. Everything is mapped unconditionally
///   and gated at runtime by the LIVE WebUi enable flags (IOptionsMonitor — a database
///   settings change applies within ~1 s, same pattern as the /metrics port filter), so
///   `eas config set ActiveSync:WebUi:Admin:Enabled true` needs no restart.
/// </summary>
public static class WebUiApplicationExtensions
{
	/// <summary>Environment variable pointing at a wwwroot directory on disk (design-iteration loop).</summary>
	private const string AssetsOverrideVariable = "EAS_WEBUI_ASSETS";

	public static void MapWebUi(this WebApplication app)
	{
		app.UseAuthentication();
		app.UseAuthorization();

		IOptionsMonitor<ActiveSyncOptions> monitor =
			app.Services.GetRequiredService<IOptionsMonitor<ActiveSyncOptions>>();

		// Live enable-gate + security headers for the UI path prefixes. /shared holds the
		// theme/helper assets both portals reference, reachable while either portal is on.
		app.Use(async (context, next) =>
		{
			PathString path = context.Request.Path;
			bool inAdmin = path.StartsWithSegments("/admin");
			bool inUser = path.StartsWithSegments("/user");
			bool inShared = path.StartsWithSegments("/shared");
			if (!inAdmin && !inUser && !inShared)
			{
				await next();
				return;
			}

			WebUiOptions webUi = monitor.CurrentValue.WebUi;
			bool enabled = inAdmin ? webUi.Admin.Enabled
				: inUser ? webUi.UserPortal.Enabled
				: webUi.Admin.Enabled || webUi.UserPortal.Enabled;
			if (!enabled)
			{
				context.Response.StatusCode = StatusCodes.Status404NotFound;
				return;
			}

			context.Response.OnStarting(() =>
			{
				// No inline SCRIPTS anywhere in the SPA — scripts stay locked to 'self'.
				// Inline style ATTRIBUTES are allowed: the views lay themselves out with
				// them, and a style-injection is a far smaller risk than script injection.
				context.Response.Headers.ContentSecurityPolicy =
					"default-src 'self'; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'";
				context.Response.Headers.XFrameOptions = "DENY";
				context.Response.Headers["Referrer-Policy"] = "no-referrer";
				return Task.CompletedTask;
			});
			await next();
		});

		// The no-build SPA: embedded wwwroot (admin/, user/, shared/) served from the
		// assembly; EAS_WEBUI_ASSETS switches to a directory on disk for live design editing.
		IFileProvider assets = CreateAssetProvider();
		app.UseStaticFiles(new StaticFileOptions { FileProvider = assets });
		app.MapGet("/admin", () => Index(assets, "admin/index.html"));
		app.MapGet("/user", () => Index(assets, "user/index.html"));

		RouteGroupBuilder adminApi = app.MapGroup("/admin/api")
			.RequireAuthorization(WebUiAuth.AdminPolicy)
			.AddEndpointFilter(RequireCsrfHeaderAsync);
		RouteGroupBuilder userApi = app.MapGroup("/user/api")
			.RequireAuthorization(WebUiAuth.UserPolicy)
			.AddEndpointFilter(RequireCsrfHeaderAsync);

		// SSO challenge entry points (full-page navigations, not API calls). Mapped only when
		// the OIDC handler is registered — same restart-tier read as the registration itself.
		ActiveSyncOptions startup = app.Services.GetRequiredService<IOptions<ActiveSyncOptions>>().Value;
		if (AuthEndpoints.IsOidcConfigured(startup.WebUi))
		{
			app.MapGet("/admin/oidc/login", () => Results.Challenge(
				new AuthenticationProperties { RedirectUri = "/admin" },
				[WebUiServiceCollectionExtensions.OidcScheme]));
			app.MapGet("/user/oidc/login", () => Results.Challenge(
				new AuthenticationProperties { RedirectUri = "/user" },
				[WebUiServiceCollectionExtensions.OidcScheme]));
		}

		AuthEndpoints.Map(adminApi, admin: true);
		AuthEndpoints.Map(userApi, admin: false);
		Api.SettingsEndpoints.Map(adminApi);
		Api.BackendsEndpoints.Map(adminApi);
		Api.UsersEndpoints.Map(adminApi);
		Api.SharesEndpoints.Map(adminApi);
		Api.DevicesEndpoints.Map(adminApi);
		Api.LogsEndpoints.Map(adminApi);
		Api.StateEndpoints.Map(adminApi);
		Api.PortalEndpoints.Map(userApi);
	}

	private static IFileProvider CreateAssetProvider()
	{
		string? overrideDirectory = Environment.GetEnvironmentVariable(AssetsOverrideVariable);
		return !string.IsNullOrWhiteSpace(overrideDirectory) && Directory.Exists(overrideDirectory)
			? new PhysicalFileProvider(Path.GetFullPath(overrideDirectory))
			: new ManifestEmbeddedFileProvider(typeof(WebUiApplicationExtensions).Assembly, "wwwroot");
	}

	private static IResult Index(IFileProvider assets, string path)
	{
		IFileInfo file = assets.GetFileInfo(path);
		return file.Exists
			? Results.Stream(file.CreateReadStream(), "text/html; charset=utf-8")
			: Results.NotFound();
	}

	/// <summary>Rejects non-GET API calls without the CSRF companion header (see WebUiAuth.CsrfHeader).</summary>
	private static async ValueTask<object?> RequireCsrfHeaderAsync(
		EndpointFilterInvocationContext context, EndpointFilterDelegate next)
	{
		HttpRequest request = context.HttpContext.Request;
		if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) &&
		    !request.Headers.ContainsKey(WebUiAuth.CsrfHeader))
			return Results.StatusCode(StatusCodes.Status403Forbidden);
		return await next(context);
	}
}

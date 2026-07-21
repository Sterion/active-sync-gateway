using ActiveSync.Core.Options;
using ActiveSync.Core.State;
using ActiveSync.Server.Eas;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace ActiveSync.Server.Setup;

/// <summary>Pipeline/startup helpers that keep Program.cs a thin composition script.</summary>
public static class WebApplicationExtensions
{
	/// <summary>
	///   Applies any pending EF Core migrations at startup so the schema is created and
	///   upgraded in place (roll-forward, no manual step).
	/// </summary>
	public static async Task ApplyMigrationsAsync(
		this WebApplication app, ILogger logger)
	{
		await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
		SyncDbContext db = scope.ServiceProvider.GetRequiredService<SyncDbContext>();

		// On a fresh database EF probes __EFMigrationsHistory with a try/catch, and the caught
		// failure is still logged as an error-level DbCommand failure — twice (once for the
		// pending check, once inside MigrateAsync). Create the empty history table up front so
		// first boot logs no errors. Unconditionally: the script is CREATE TABLE IF NOT EXISTS
		// (safe against concurrent replicas too), and IHistoryRepository.ExistsAsync cannot be
		// trusted here — the Npgsql provider reports true on a fresh database.
		IHistoryRepository history = db.GetService<IHistoryRepository>();
		await db.Database.ExecuteSqlRawAsync(history.GetCreateIfNotExistsScript());

		List<string> pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
		if (pending.Count > 0)
		{
			logger.LogInformation("Applying {Count} pending database migration(s): {Migrations}",
				pending.Count, string.Join(", ", pending));
			await db.Database.MigrateAsync();
			logger.LogInformation("Database migrations applied.");
		}
		else
		{
			logger.LogInformation("Database schema is up to date; no migrations to apply.");
		}
	}

	/// <summary>
	///   Serilog request-completion logging. EAS requests stash "EAS {Cmd} {User} ({DeviceId})"
	///   in HttpContext.Items; everything else falls back to method + path. Routine completions
	///   log at Debug (meaningful activity has its own Info lines); 4xx warns, 5xx/exceptions error.
	/// </summary>
	public static WebApplication UseEasRequestLogging(this WebApplication app)
	{
		app.UseSerilogRequestLogging(o =>
		{
			o.MessageTemplate = "{RequestSummary} responded {StatusCode} in {Elapsed:0.0000} ms";
			o.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
				diagnosticContext.Set("RequestSummary",
					httpContext.Items[EasEndpoint.RequestSummaryKey] as string
					?? $"{httpContext.Request.Method} {httpContext.Request.Path}");
			o.GetLevel = (httpContext, _, exception) =>
				// /readyz answers 503 BY DESIGN while a component is down or the gateway is
				// unconfigured — a polled probe result, not a server failure worth an Error.
				httpContext.Request.Path.StartsWithSegments("/readyz") && exception is null
					? LogEventLevel.Debug
					// The admin/portal SPA shell probes GET .../api/session on every page load to
					// decide login-form-vs-shell BEFORE the visitor has signed in — a 401 here is
					// the routine "no session yet" answer, not an admin-worth event.
					: IsAnonymousSessionProbe(httpContext) && exception is null
						? LogEventLevel.Verbose
						: exception is not null || httpContext.Response.StatusCode >= 500
							? LogEventLevel.Error
							: httpContext.Response.StatusCode >= 400
								? LogEventLevel.Warning
								: LogEventLevel.Debug;
		});
		return app;
	}

	/// <summary>
	///   True for GET /admin/api/session or GET /user/api/session with a 401 — the SPA shell's
	///   unauthenticated "am I already signed in?" check, which is EXPECTED to answer 401 on
	///   every fresh page load and carries no information an operator needs to see.
	/// </summary>
	private static bool IsAnonymousSessionProbe(HttpContext httpContext)
	{
		return httpContext.Response.StatusCode == StatusCodes.Status401Unauthorized &&
		       HttpMethods.IsGet(httpContext.Request.Method) &&
		       (httpContext.Request.Path.Equals("/admin/api/session", StringComparison.OrdinalIgnoreCase) ||
		        httpContext.Request.Path.Equals("/user/api/session", StringComparison.OrdinalIgnoreCase));
	}

	/// <summary>
	///   Records eas_requests / eas_request_duration_seconds for every request the EAS
	///   endpoint identified (command + user stashed in HttpContext.Items) — including 401,
	///   403 and 449 outcomes. Non-EAS requests (healthz, autodiscover) are not counted.
	///   The stashed labels are attacker-influenced until the request authenticates: the endpoint
	///   stashes "-" as the user until then, and GatewayMetrics clamps the command to the known set.
	/// </summary>
	public static WebApplication UseEasMetrics(this WebApplication app)
	{
		app.Use(async (context, next) =>
		{
			long started = System.Diagnostics.Stopwatch.GetTimestamp();
			try
			{
				await next();
			}
			finally
			{
				if (context.Items[Eas.EasEndpoint.MetricsKey] is (string command, string user))
					Core.Observability.GatewayMetrics.RecordEasRequest(
						command, context.Response.StatusCode, user,
						System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds);
			}
		});
		return app;
	}

	/// <summary>
	///   Adds <c>X-Content-Type-Options: nosniff</c> to every response. Attachments are served
	///   with the Content-Type declared inside the (untrusted) email, so browsers must not
	///   second-guess content types.
	/// </summary>
	public static WebApplication UseNosniffHeader(this WebApplication app)
	{
		app.Use(async (context, next) =>
		{
			context.Response.Headers.XContentTypeOptions = "nosniff";
			await next();
		});
		return app;
	}

	/// <summary>
	///   Corrects <c>Request.Scheme</c> when the gateway sits behind a TLS-terminating proxy (e.g. a
	///   Kubernetes ingress that forwards to the plain-HTTP port). Absolute URLs the app builds from
	///   the request scheme then come out https — most importantly the OIDC <c>redirect_uri</c>, which
	///   the handler derives from the scheme at BOTH the authorize step and the token exchange, so the
	///   identity provider is never handed an http callback. <see cref="ActiveSyncOptions.PublicUrl" />
	///   wins (it never depends on client-supplied headers); otherwise <c>X-Forwarded-Proto</c> is
	///   trusted. Host and <c>RemoteIpAddress</c> are left untouched, so the /cli loopback gate and the
	///   auth throttle are unaffected.
	/// </summary>
	public static WebApplication UsePublicScheme(this WebApplication app)
	{
		app.Use(async (context, next) =>
		{
			string? publicUrl = context.RequestServices
				.GetRequiredService<IOptionsMonitor<ActiveSyncOptions>>().CurrentValue.PublicUrl;
			if (ResolvePublicScheme(publicUrl, context.Request.Headers["X-Forwarded-Proto"].FirstOrDefault()) is { } scheme)
				context.Request.Scheme = scheme;
			await next();
		});
		return app;
	}

	/// <summary>
	///   The scheme to force onto the request, or null to keep its own. A configured
	///   <paramref name="publicUrl" /> beats the <paramref name="forwardedProto" /> header (which may
	///   be a proxy-chain list — the first entry, the original client, wins).
	/// </summary>
	internal static string? ResolvePublicScheme(string? publicUrl, string? forwardedProto)
	{
		if (!string.IsNullOrWhiteSpace(publicUrl) && Uri.TryCreate(publicUrl, UriKind.Absolute, out Uri? uri))
			return uri.Scheme;
		return string.IsNullOrWhiteSpace(forwardedProto) ? null : forwardedProto.Split(',')[0].Trim();
	}
}

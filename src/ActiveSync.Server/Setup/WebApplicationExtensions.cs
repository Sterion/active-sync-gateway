using ActiveSync.Core.State;
using ActiveSync.Server.Eas;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
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
					: exception is not null || httpContext.Response.StatusCode >= 500
						? LogEventLevel.Error
						: httpContext.Response.StatusCode >= 400
							? LogEventLevel.Warning
							: LogEventLevel.Debug;
		});
		return app;
	}

	/// <summary>
	///   Records eas_requests / eas_request_duration_seconds for every request the EAS
	///   endpoint identified (command + user stashed in HttpContext.Items) — including 401,
	///   403 and 449 outcomes. Non-EAS requests (healthz, autodiscover) are not counted.
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
}

using ActiveSync.Core.Accounts;
using ActiveSync.Contracts;
using ActiveSync.Core.Administration;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Observability;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ActiveSync.WebUi.Api;

/// <summary>
///   Live gateway state for the dashboard: cached backend sessions per (user, device) with
///   last-activity, provider push watchers (IMAP IDLE), parked long-poll counts, and cheap
///   DB-derived summary numbers. All in-process reads — no backend round-trips. Readiness
///   components come from the public /readyz JSON (the SPA fetches it directly). Note: EAS
///   is stateless HTTP — a live backend session is a cached connection, not a phone that is
///   "online" right now.
/// </summary>
internal static class StateEndpoints
{
	internal static void Map(RouteGroupBuilder api)
	{
		api.MapGet("state", (BackendSessionFactory sessions, BackendProviderRegistry registry) =>
		{
			List<object> watchers = [];
			foreach (IBackendProvider provider in registry.All)
				if (provider is IWatcherDiagnostics diagnostics)
					foreach (WatcherInfo watcher in diagnostics.SnapshotWatchers())
						watchers.Add(new { provider = provider.Name, watcher.User, watcher.Resource });

			return Results.Ok(new
			{
				sessions = sessions.SnapshotSessions()
					.OrderBy(s => s.User, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.DeviceId)
					.ToList(),
				watchers,
				longPolls = GatewayMetrics.SnapshotLongPolls()
					.Select(pair => new { user = pair.Key, count = pair.Value })
					.OrderBy(p => p.user, StringComparer.OrdinalIgnoreCase)
					.ToList()
			});
		});

		api.MapGet("summary", async (
			DeviceAdminService devices, LogQueryService logs, AccountResolver resolver, CancellationToken ct) =>
		{
			await resolver.EnsureFreshAsync(false, ct);
			DateTime hourAgo = DateTime.UtcNow.AddHours(-1);
			DeviceAdminService.SummaryCounts counts = await devices.SummaryAsync(ct);
			LogQueryService.LogCounts logCounts = await logs.CountsSinceAsync(hourAgo, ct);
			return Results.Ok(new
			{
				declaredUsers = resolver.MergedUsers.Count,
				deviceUsers = counts.DeviceUsers,
				devices = counts.Devices,
				blocks = counts.Blocks,
				pendingWipes = counts.PendingWipes,
				errorsLastHour = logCounts.ErrorsAndFatal,
				warningsLastHour = logCounts.Warnings
			});
		});
	}
}

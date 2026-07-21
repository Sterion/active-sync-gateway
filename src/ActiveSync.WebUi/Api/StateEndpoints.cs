using ActiveSync.Core.Accounts;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Observability;
using ActiveSync.Core.State;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

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

		api.MapGet("summary", async (SyncDbContext db, AccountResolver resolver, CancellationToken ct) =>
		{
			await resolver.EnsureFreshAsync(false, ct);
			DateTime hourAgo = DateTime.UtcNow.AddHours(-1);
			return Results.Ok(new
			{
				declaredUsers = resolver.MergedUsers.Count,
				deviceUsers = await db.Devices.Select(d => d.UserName).Distinct().CountAsync(ct),
				devices = await db.Devices.CountAsync(ct),
				blocks = await db.LoginBlocks.CountAsync(ct),
				pendingWipes = await db.Devices.CountAsync(d => d.PendingAccountWipe, ct),
				errorsLastHour = await db.LogEntries.CountAsync(
					e => e.TimestampUtc >= hourAgo && (e.Level == "Error" || e.Level == "Fatal"), ct),
				warningsLastHour = await db.LogEntries.CountAsync(
					e => e.TimestampUtc >= hourAgo && e.Level == "Warning", ct)
			});
		});
	}
}

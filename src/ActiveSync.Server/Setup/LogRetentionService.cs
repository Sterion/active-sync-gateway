using ActiveSync.Core.Options;
using ActiveSync.Core.State;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ActiveSync.Server.Setup;

/// <summary>
///   Background sweep that deletes database log rows older than Log:RetentionDays (default 7;
///   0 disables). Runs a few times a day; the retention window is read live. Multi-pod safe —
///   the bulk delete is idempotent, so overlapping sweeps from several replicas are harmless.
/// </summary>
public sealed class LogRetentionService(
	ISyncDbContextFactory contextFactory,
	IOptionsMonitor<ActiveSyncOptions> options,
	ILogger<LogRetentionService> logger) : BackgroundService
{
	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		while (!stoppingToken.IsCancellationRequested)
		{
			try
			{
				int days = options.CurrentValue.Log.RetentionDays;
				if (days > 0)
				{
					DateTime cutoff = DateTime.UtcNow.AddDays(-days);
					await using SyncDbContext db = contextFactory.CreateDbContext();
					int deleted = await db.LogEntries.Where(e => e.TimestampUtc < cutoff)
						.ExecuteDeleteAsync(stoppingToken).ConfigureAwait(false);
					if (deleted > 0)
						logger.LogDebug("Log retention removed {Count} row(s) older than {Days} day(s)", deleted, days);
				}
			}
			catch (OperationCanceledException)
			{
				break;
			}
			catch (Exception ex)
			{
				logger.LogDebug(ex, "Log retention sweep failed; will retry");
			}

			try
			{
				await Task.Delay(TimeSpan.FromHours(6), stoppingToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				break;
			}
		}
	}
}

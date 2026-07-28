using ActiveSync.Core.Options;
using ActiveSync.Core.State;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ActiveSync.Server.Setup;

/// <summary>
///   Background sweep that deletes COMPLETED send-dedup claims (<see cref="SentCommandToken" />
///   rows) older than Eas:SendDedupRetentionDays (default 30; 0 disables). <see cref="SendDedupStore" />
///   is pruned per-collection from <c>SyncStateService.CommitCollectionStateAsync</c>, keyed to that
///   collection's own new SyncKey — but ComposeMail (send-by-reference) and MeetingResponse claims are
///   keyed on the fixed collection namespaces "compose"/"meetingresponse", which no Sync round ever
///   commits under, so that per-collection prune never reaches them and they would otherwise persist
///   for the life of the device. An unconfirmed (never-completed) claim is left alone — it may still
///   be genuinely in flight. Runs a few times a day; the retention window is read live. Multi-pod safe
///   — the bulk delete is idempotent, so overlapping sweeps from several replicas are harmless.
/// </summary>
public sealed class SendDedupRetentionService(
	ISyncDbContextFactory contextFactory,
	IOptionsMonitor<ActiveSyncOptions> options,
	ILogger<SendDedupRetentionService> logger) : BackgroundService
{
	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		while (!stoppingToken.IsCancellationRequested)
		{
			try
			{
				int days = options.CurrentValue.Eas.SendDedupRetentionDays;
				if (days > 0)
				{
					DateTime cutoff = DateTime.UtcNow.AddDays(-days);
					await using SyncDbContext db = contextFactory.CreateDbContext();
					int deleted = await ReclaimAsync(db, cutoff, stoppingToken).ConfigureAwait(false);
					if (deleted > 0)
						logger.LogDebug("Send-dedup retention removed {Count} completed claim(s) older than {Cutoff:o}",
							deleted, cutoff);
				}
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
			{
				break; // host shutdown — the only cancellation that should stop the sweep
			}
			catch (Exception ex)
			{
				// Any other fault — including a non-shutdown OperationCanceledException such as an
				// EF command timeout — must NOT stop the sweep, or retention freezes for the process
				// lifetime with no signal. Keep the loop alive; retry on the next tick.
				logger.LogDebug(ex, "Send-dedup retention sweep failed; will retry");
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

	/// <summary>
	///   Deletes every COMPLETED claim created before <paramref name="cutoff" /> and returns how many
	///   were removed. A claim never marked complete is left alone regardless of age.
	/// </summary>
	internal static async Task<int> ReclaimAsync(SyncDbContext db, DateTime cutoff, CancellationToken ct)
	{
		return await db.SentCommandTokens
			.Where(t => t.Completed && t.CreatedUtc < cutoff)
			.ExecuteDeleteAsync(ct).ConfigureAwait(false);
	}
}

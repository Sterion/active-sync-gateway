using ActiveSync.Core.Options;
using ActiveSync.Core.State;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ActiveSync.Server.Setup;

/// <summary>
///   Background sweep that reclaims folders which vanished from the backend and have stayed
///   soft-deleted past Eas:FolderRetentionDays (default 30; 0 disables). A soft-deleted
///   <see cref="UserFolder" /> never had a path that removed the row, its <see cref="DavItem" />
///   href map, or the per-device <see cref="CollectionState" />/<see cref="DeviceFolder" /> keyed
///   by its ServerId, so those tables only grew (A35). Runs a few times a day; the window is read
///   live. Multi-pod safe — the deletes are idempotent, so overlapping sweeps are harmless.
/// </summary>
public sealed class FolderRetentionService(
	ISyncDbContextFactory contextFactory,
	IOptionsMonitor<ActiveSyncOptions> options,
	ILogger<FolderRetentionService> logger) : BackgroundService
{
	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		while (!stoppingToken.IsCancellationRequested)
		{
			try
			{
				int days = options.CurrentValue.Eas.FolderRetentionDays;
				if (days > 0)
				{
					DateTime cutoff = DateTime.UtcNow.AddDays(-days);
					await using SyncDbContext db = contextFactory.CreateDbContext();
					int reclaimed = await ReclaimAsync(db, cutoff, stoppingToken).ConfigureAwait(false);
					if (reclaimed > 0)
						logger.LogDebug("Folder retention reclaimed {Count} folder(s) soft-deleted before {Cutoff:o}",
							reclaimed, cutoff);
				}
			}
			catch (OperationCanceledException)
			{
				break;
			}
			catch (Exception ex)
			{
				logger.LogDebug(ex, "Folder retention sweep failed; will retry");
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
	///   Hard-deletes every folder soft-deleted before <paramref name="cutoff" /> together with its
	///   dependent DAV/collection state, and returns how many folders were reclaimed. Dependents are
	///   deleted explicitly (not via DB FK cascade) because <see cref="CollectionState" /> and
	///   <see cref="DeviceFolder" /> are keyed by the folder's ServerId string, not a foreign key.
	/// </summary>
	internal static async Task<int> ReclaimAsync(SyncDbContext db, DateTime cutoff, CancellationToken ct)
	{
		List<int> reclaim = await db.UserFolders
			.Where(f => f.Deleted && f.DeletedUtc != null && f.DeletedUtc < cutoff)
			.Select(f => f.Id)
			.ToListAsync(ct).ConfigureAwait(false);
		if (reclaim.Count == 0)
			return 0;

		// ServerId == UserFolder.Id as a string; that is the CollectionId / DeviceFolder.ServerId
		// every device stored for this folder.
		List<string> serverIds = reclaim.Select(id => id.ToString()).ToList();

		await db.CollectionStates.Where(c => serverIds.Contains(c.CollectionId))
			.ExecuteDeleteAsync(ct).ConfigureAwait(false);
		await db.DeviceFolders.Where(f => serverIds.Contains(f.ServerId))
			.ExecuteDeleteAsync(ct).ConfigureAwait(false);
		await db.DavItems.Where(i => reclaim.Contains(i.UserFolderKey))
			.ExecuteDeleteAsync(ct).ConfigureAwait(false);
		return await db.UserFolders.Where(f => reclaim.Contains(f.Id))
			.ExecuteDeleteAsync(ct).ConfigureAwait(false);
	}
}

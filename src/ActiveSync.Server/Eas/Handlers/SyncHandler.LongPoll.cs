using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Core.State;

namespace ActiveSync.Server.Eas.Handlers;

// Long-poll (Sync with Wait/HeartbeatInterval): race the backend change watchers against the
// pending-change watchdog so a wait is honoured even when IDLE/STATUS notifications never arrive.
public sealed partial class SyncHandler
{
	/// <summary>
	///   Races the backend watchers against the exact pending-change watchdog. The watchers
	///   give sub-second push when the server cooperates; the watchdog guarantees detection
	///   even when IDLE/STATUS notifications never arrive.
	/// </summary>
	private async Task<bool> WaitWithWatchdogAsync(
		EasContext context,
		List<WaitableCollection> collections,
		TimeSpan timeout, CancellationToken ct)
	{
		int watchdogSeconds = options.Value.Eas.WatchdogSeconds;
		DateTime deadline = DateTime.UtcNow + timeout;
		// Also observe host shutdown: an active long-poll must never delay process exit.
		using CancellationTokenSource cts =
			CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.ApplicationStopping);

		// A single watcher task (WaitForAnyChangeAsync already races the per-store waits and
		// drains its own children) versus the optional watchdog re-check.
		Task<bool> watcherTask = WaitForAnyChangeAsync(collections, timeout, cts.Token);
		Task<bool>? watchdogTask = watchdogSeconds > 0 ? WatchdogAsync() : null;

		LongPollWatchdog.Outcome<bool> outcome = await LongPollWatchdog.RaceAsync(
			[watcherTask], watchdogTask, changed => changed, false, cts, ct);

		if (outcome.Result && outcome.FoundByWatchdog)
			logger.LogWarning(
				"Watchdog: pending changes for {User} found by re-check during Sync wait (missed by the backend watcher)",
				context.Device.UserName);
		return outcome.Result;

		async Task<bool> WatchdogAsync()
		{
			TimeSpan interval = TimeSpan.FromSeconds(watchdogSeconds);
			while (true)
			{
				TimeSpan remaining = deadline - DateTime.UtcNow;
				if (remaining <= TimeSpan.Zero)
					return false;
				await Task.Delay(remaining < interval ? remaining : interval, cts.Token);
				foreach ((XElement element, UserFolder folder, IContentStore store) in collections)
				{
					string collectionId = element.Element(AS + "CollectionId")?.Value ?? "";
					if (await PendingChangeDetector.HasPendingChangesAsync(
						    context, collectionId, folder, store, logger, cts.Token))
						return true;
				}
			}
		}
	}

	private static async Task<bool> WaitForAnyChangeAsync(
		List<WaitableCollection> collections,
		TimeSpan timeout, CancellationToken ct)
	{
		using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
		List<IGrouping<IContentStore, WaitableCollection>> byStore =
			collections.GroupBy(c => c.Store).ToList();
		List<Task<IReadOnlyList<string>>> waits = byStore
			.Select(g => g.Key.WaitForChangesAsync(
				g.Select(c => c.Folder.BackendKey).Distinct().ToList(), timeout, cts.Token))
			.ToList();
		try
		{
			while (waits.Count > 0)
			{
				Task<IReadOnlyList<string>> finished = await Task.WhenAny(waits);
				waits.Remove(finished);
				IReadOnlyList<string> changed = await finished;
				if (changed.Count > 0)
					return true;
			}

			return false;
		}
		finally
		{
			await cts.CancelAsync(); // stop the remaining pollers
		}
	}
}

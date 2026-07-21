using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.State;

namespace ActiveSync.Server.Eas;

/// <summary>
///   Exact pending-change check used by the Ping/Sync watchdog: diffs the backend's current
///   revision map against the device's synced snapshot — the same ground truth Sync itself
///   uses. Unlike the IDLE/STATUS watchers this cannot be fooled by servers that never
///   broadcast flag changes, by coincidentally identical STATUS counters, or by changes that
///   landed before the watch started.
/// </summary>
public static class PendingChangeDetector
{
	public static async Task<bool> HasPendingChangesAsync(
		EasContext context,
		string collectionId,
		UserFolder folder,
		IContentStore store,
		ILogger logger,
		CancellationToken ct)
	{
		CollectionState? state = await context.State.GetCollectionStateAsync(context.Device, collectionId, ct);
		if (state is null || state.SyncKey == 0)
			return false; // never completed a sync — nothing to compare against

		Dictionary<string, string> snapshot = SyncStateService.ReadSnapshot(state);
		ContentFilter filter = ContentFilter.ForClass(store.EasClass, state.FilterType);

		IReadOnlyDictionary<string, string> current;
		try
		{
			current = await store.GetItemRevisionsAsync(folder.BackendKey, filter, ct);
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex)
		{
			// A transient backend blip must never abort a long-running Ping.
			logger.LogDebug(ex, "Watchdog: revision check failed for \"{Folder}\"", folder.DisplayName);
			return false;
		}

		if (current.Count != snapshot.Count)
			return true;
		foreach ((string key, string revision) in current)
			if (!snapshot.TryGetValue(key, out string? known) || known != revision)
				return true;
		return false;
	}
}

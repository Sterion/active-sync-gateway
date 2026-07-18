using System.Text.Json;

namespace ActiveSync.Server.Eas.Handlers;

// Sync request replay: EAS clients may send an empty <Sync/> to mean "repeat my last full
// request". We persist the replayable shape (never the one-shot client Commands) on the
// Device row and rebuild the collection list from it when an empty request arrives.
public sealed partial class SyncHandler
{
	private List<CachedSyncCollection>? BuildReplayedCollections(
		EasContext context, out int? waitSeconds, out int globalWindow)
	{
		waitSeconds = null;
		globalWindow = options.Value.Eas.DefaultWindowSize;
		if (context.Device.LastSyncRequestJson is null)
			return null;
		CachedSyncRequest? cache;
		try
		{
			cache = JsonSerializer.Deserialize<CachedSyncRequest>(context.Device.LastSyncRequestJson);
		}
		catch (JsonException)
		{
			return null;
		}

		if (cache is null || cache.Collections.Count == 0)
			return null;
		logger.LogDebug("Replaying cached Sync request with {Count} collections for {User}",
			cache.Collections.Count, context.Device.UserName);
		waitSeconds = cache.WaitSeconds;
		globalWindow = cache.GlobalWindowSize;
		return cache.Collections;
	}

	/// <summary>Replayable shape of a full Sync request (client Commands are one-shot, never cached).</summary>
	internal sealed record CachedSyncRequest(
		int? WaitSeconds,
		int GlobalWindowSize,
		List<CachedSyncCollection> Collections);

	internal sealed record CachedSyncCollection(string CollectionId, bool GetChanges, int? WindowSize);
}

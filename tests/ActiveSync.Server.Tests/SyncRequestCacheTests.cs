using System.Text.Json;
using ActiveSync.Server.Eas.Handlers;

namespace ActiveSync.Server.Tests;

public class SyncRequestCacheTests
{
	[Fact]
	public void CachedSyncRequest_RoundTripsThroughJson()
	{
		SyncHandler.CachedSyncRequest original = new(
			900,
			100,
			[
				new SyncHandler.CachedSyncCollection("1", true, null),
				new SyncHandler.CachedSyncCollection("7", false, 25)
			]);

		string json = JsonSerializer.Serialize(original);
		SyncHandler.CachedSyncRequest? restored = JsonSerializer.Deserialize<SyncHandler.CachedSyncRequest>(json);

		Assert.NotNull(restored);
		Assert.Equal(original.WaitSeconds, restored.WaitSeconds);
		Assert.Equal(original.GlobalWindowSize, restored.GlobalWindowSize);
		Assert.Equal(2, restored.Collections.Count);
		Assert.Equal(original.Collections[0], restored.Collections[0]);
		Assert.Equal(original.Collections[1], restored.Collections[1]);
	}

	[Fact]
	public void CachedSyncRequest_NoWait_RoundTrips()
	{
		SyncHandler.CachedSyncRequest original = new(null, 50,
			[new SyncHandler.CachedSyncCollection("3", true, null)]);

		SyncHandler.CachedSyncRequest? restored = JsonSerializer.Deserialize<SyncHandler.CachedSyncRequest>(
			JsonSerializer.Serialize(original));

		Assert.NotNull(restored);
		Assert.Null(restored.WaitSeconds);
		Assert.Single(restored.Collections);
	}
}

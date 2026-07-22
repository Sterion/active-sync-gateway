using System.Text.Json;
using ActiveSync.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ActiveSync.Core.State;

/// <summary>
///   Per-collection sync state: the SyncKey lifecycle (with one generation of replay history),
///   the item snapshot, and the applied client-command maps. One of the collaborators composed
///   by <see cref="SyncStateService" />, sharing its request-scoped <see cref="SyncDbContext" />.
///   The snapshot/applied-map readers are static (they operate on a <see cref="CollectionState" />
///   the caller already holds) and are re-exposed from <see cref="SyncStateService" />.
/// </summary>
internal sealed class CollectionStateStore(SyncDbContext db)
{
	private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

	public async Task<(SyncKeyValidation Validation, CollectionState? State)> ValidateSyncKeyAsync(
		Device device, string collectionId, string clientSyncKey, CancellationToken ct)
	{
		CollectionState? state = await db.CollectionStates
			.FirstOrDefaultAsync(c => c.DeviceKey == device.Id && c.CollectionId == collectionId, ct)
			.ConfigureAwait(false);

		if (clientSyncKey == "0")
		{
			if (state is null)
			{
				state = new CollectionState { DeviceKey = device.Id, CollectionId = collectionId };
				// DbSet.Add false positive for VSTHRD103 — see GetOrCreateDeviceAsync.
#pragma warning disable VSTHRD103
				db.CollectionStates.Add(state);
#pragma warning restore VSTHRD103
			}

			state.SyncKey = 0;
			state.SnapshotCompressed = null;
			state.PreviousSnapshotCompressed = null;
			state.LastClientAddsJson = null;
			state.LastClientChangesJson = null;
			state.UpdatedUtc = DateTime.UtcNow;
			await db.SaveChangesAsync(ct).ConfigureAwait(false);
			return (SyncKeyValidation.Initial, state);
		}

		// Invalid returns a null state — never a detached, never-added synthesized entity a
		// caller might mistake for real state (A17).
		if (state is null || !int.TryParse(clientSyncKey, out int key))
			return (SyncKeyValidation.Invalid, null);

		if (key == state.SyncKey)
			return (SyncKeyValidation.Current, state);

		if (key == state.SyncKey - 1 && state.PreviousSnapshotCompressed is not null)
		{
			// Client never saw our last response: roll back one generation.
			// LastClientAddsJson/LastClientChangesJson are deliberately KEPT — they describe
			// the commands of the discarded generation, which are exactly the ones the client
			// is about to re-send.
			state.SyncKey = key;
			state.SnapshotCompressed = state.PreviousSnapshotCompressed;
			state.PreviousSnapshotCompressed = null;
			await db.SaveChangesAsync(ct).ConfigureAwait(false);
			return (SyncKeyValidation.Replay, state);
		}

		return (SyncKeyValidation.Invalid, null);
	}

	/// <summary>
	///   Read-only classification for GetItemEstimate: returns the verdict plus the snapshot
	///   the estimate should diff against, WITHOUT mutating or persisting collection state
	///   (unlike <see cref="ValidateSyncKeyAsync" />, which is the Sync write path). A query
	///   must never reset a client's SyncKey/snapshot.
	/// </summary>
	public async Task<(SyncKeyValidation Validation, Dictionary<string, string> Snapshot, int FilterType)>
		PeekSyncKeyAsync(Device device, string collectionId, string clientSyncKey, CancellationToken ct)
	{
		CollectionState? state = await db.CollectionStates.AsNoTracking()
			.FirstOrDefaultAsync(c => c.DeviceKey == device.Id && c.CollectionId == collectionId, ct)
			.ConfigureAwait(false);

		if (clientSyncKey == "0")
			return (SyncKeyValidation.Initial, [], state?.FilterType ?? 0);
		if (state is null || !int.TryParse(clientSyncKey, out int key))
			return (SyncKeyValidation.Invalid, [], state?.FilterType ?? 0);
		if (key == state.SyncKey)
			return (SyncKeyValidation.Current, ReadSnapshot(state), state.FilterType);
		if (key == state.SyncKey - 1 && state.PreviousSnapshotCompressed is not null)
			return (SyncKeyValidation.Replay, SnapshotCodec.Decompress(state.PreviousSnapshotCompressed), state.FilterType);
		return (SyncKeyValidation.Invalid, [], state.FilterType);
	}

	public static Dictionary<string, string> ReadSnapshot(CollectionState state)
	{
		return SnapshotCodec.Decompress(state.SnapshotCompressed);
	}

	/// <summary>Persists <paramref name="snapshot" /> onto the state's snapshot column (gzipped).</summary>
	public static void WriteSnapshot(CollectionState state, Dictionary<string, string> snapshot)
	{
		state.SnapshotCompressed = SnapshotCodec.Compress(snapshot);
	}

	/// <summary>The applied-Add map of the generation that produced the current SyncKey.</summary>
	public static Dictionary<string, AppliedClientAdd> ReadAppliedAdds(CollectionState state)
	{
		return state.LastClientAddsJson is null
			? []
			: JsonSerializer.Deserialize<Dictionary<string, AppliedClientAdd>>(state.LastClientAddsJson, JsonOpts) ?? [];
	}

	/// <summary>The applied-Change map of the generation that produced the current SyncKey.</summary>
	public static Dictionary<string, AppliedClientChange> ReadAppliedChanges(CollectionState state)
	{
		return state.LastClientChangesJson is null
			? []
			: JsonSerializer.Deserialize<Dictionary<string, AppliedClientChange>>(state.LastClientChangesJson, JsonOpts)
			  ?? [];
	}

	public async Task<int> CommitCollectionStateAsync(
		CollectionState state, Dictionary<string, string> newSnapshot, int filterType, CancellationToken ct,
		Dictionary<string, AppliedClientAdd>? appliedAdds = null,
		Dictionary<string, AppliedClientChange>? appliedChanges = null)
	{
		state.PreviousSnapshotCompressed = state.SnapshotCompressed;
		state.SnapshotCompressed = SnapshotCodec.Compress(newSnapshot);
		state.LastClientAddsJson = appliedAdds is { Count: > 0 }
			? JsonSerializer.Serialize(appliedAdds, JsonOpts)
			: null;
		state.LastClientChangesJson = appliedChanges is { Count: > 0 }
			? JsonSerializer.Serialize(appliedChanges, JsonOpts)
			: null;
		state.SyncKey++;
		state.FilterType = filterType;
		state.UpdatedUtc = DateTime.UtcNow;
		try
		{
			await db.SaveChangesAsync(ct).ConfigureAwait(false);
		}
		catch (DbUpdateConcurrencyException ex)
		{
			// A concurrent Sync for the same (device, collection) already advanced the state.
			// This snapshot was diffed against a now-stale base, so it must not overwrite the
			// winner — fail the request and let the client retry; the SyncKey it holds is now
			// one behind, which ValidateSyncKeyAsync recovers via the Replay path.
			// Reload the entity first: left Modified with its failed values, a later
			// SaveChangesAsync on the same request would retry the doomed UPDATE (A18).
			await db.Entry(state).ReloadAsync(ct).ConfigureAwait(false);
			throw new BackendException("Concurrent sync for this collection — please retry.", ex);
		}

		return state.SyncKey;
	}

	public Task<CollectionState?> GetCollectionStateAsync(Device device, string collectionId, CancellationToken ct)
	{
		return db.CollectionStates
			.FirstOrDefaultAsync(c => c.DeviceKey == device.Id && c.CollectionId == collectionId, ct);
	}
}

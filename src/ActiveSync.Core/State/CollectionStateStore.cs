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
				// A brand-new state is added with its defaults (SyncKey 0, null snapshots) so it
				// exists to be committed; there is no prior generation to lose.
				state = new CollectionState { DeviceKey = device.Id, CollectionId = collectionId };
				// DbSet.Add false positive for VSTHRD103 — see GetOrCreateDeviceAsync.
#pragma warning disable VSTHRD103
				db.CollectionStates.Add(state);
#pragma warning restore VSTHRD103
			}

			// A11/A1/A4: the destructive key-0 reset (wipe both snapshots + ledgers) is DEFERRED to
			// this collection's own commit (CommitCollectionStateAsync, Initial mode). This method
			// used to apply the wipe AND SaveChanges it immediately, which (a) destroyed a live
			// committed generation the instant a spurious/duplicated key 0 arrived, with no undo if
			// the round then never committed, and (b) flushed the shared request-scoped tracker,
			// persisting a sibling collection's pending Replay rollback with no replay generation
			// (the F12 re-delivery bug, reachable across collections). An existing state is returned
			// UNTOUCHED and reset only at commit time.
			return (SyncKeyValidation.Initial, state);
		}

		// Invalid returns a null state — never a detached, never-added synthesized entity a
		// caller might mistake for real state (A17).
		if (state is null || !int.TryParse(clientSyncKey, out int key))
			return (SyncKeyValidation.Invalid, null);

		if (key == state.SyncKey)
			return (SyncKeyValidation.Current, state);

		if (key == state.SyncKey - 1 && state.PreviousSnapshotCompressed is not null)
			// Client never saw our last response — this is a one-generation Replay.
			// A1/A4/F12: the rollback is NOT applied to the tracked entity here. Doing so left the
			// shared request-scoped entity Modified, where a sibling collection's SaveChanges — or a
			// key-0 reset — would flush the rolled-back-but-uncommitted state with no replay
			// generation, so the client's next attempt with the same key validated as Current against
			// the rolled-back snapshot and re-sent already-delivered items. Only the verdict is
			// returned; CommitCollectionStateAsync applies the rollback in Replay mode, atomically
			// with the new generation — or, if the round never commits, the entity stays pristine and
			// the rollback is simply discarded. The snapshot to diff against is the previous
			// generation; callers read it via ReadPreviousSnapshot when the verdict is Replay (this
			// mirrors PeekSyncKeyAsync). The applied-command ledger (LastClientAddsJson/ChangesJson)
			// is likewise untouched: it still describes the generation the client is about to re-send.
			return (SyncKeyValidation.Replay, state);

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

	/// <summary>The one-generation-old snapshot (SyncKey-1), or empty when there is no replay generation.</summary>
	public static Dictionary<string, string> ReadPreviousSnapshot(CollectionState state)
	{
		return SnapshotCodec.Decompress(state.PreviousSnapshotCompressed);
	}

	/// <summary>Persists <paramref name="snapshot" /> onto the state's previous-snapshot column (gzipped).</summary>
	public static void WritePreviousSnapshot(CollectionState state, Dictionary<string, string> snapshot)
	{
		state.PreviousSnapshotCompressed = SnapshotCodec.Compress(snapshot);
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
		CollectionState state, Dictionary<string, string> newSnapshot, int filterType,
		SyncKeyValidation validation, CancellationToken ct,
		Dictionary<string, AppliedClientAdd>? appliedAdds = null,
		Dictionary<string, AppliedClientChange>? appliedChanges = null)
	{
		// The deferred state transition (A1/A11): ValidateSyncKeyAsync no longer mutates the tracked
		// entity for a Replay or a key-0 reset, so a sibling collection's flush cannot persist a
		// half-applied rollback. The mutation lands here, atomically with the new generation.
		switch (validation)
		{
			case SyncKeyValidation.Initial:
				// Deferred key-0 reset: wipe the replay generation and land the fresh one. Net effect
				// matches the historical wipe-then-increment — SyncKey 1, no previous generation.
				state.PreviousSnapshotCompressed = null;
				state.SnapshotCompressed = SnapshotCodec.Compress(newSnapshot);
				state.SyncKey = 1;
				break;
			case SyncKeyValidation.Replay:
				// Deferred replay rollback: regenerate the lost generation N. The stale current
				// snapshot is discarded (NOT shifted into Previous); the N-1 Previous is preserved so a
				// repeated replay still validates; the key stays at N (client sent N-1, gets N back).
				// This reproduces the old rollback-then-increment exactly, but only on commit.
				state.SnapshotCompressed = SnapshotCodec.Compress(newSnapshot);
				break;
			default: // Current — the steady-state advance.
				state.PreviousSnapshotCompressed = state.SnapshotCompressed;
				state.SnapshotCompressed = SnapshotCodec.Compress(newSnapshot);
				state.SyncKey++;
				break;
		}

		state.LastClientAddsJson = appliedAdds is { Count: > 0 }
			? JsonSerializer.Serialize(appliedAdds, JsonOpts)
			: null;
		state.LastClientChangesJson = appliedChanges is { Count: > 0 }
			? JsonSerializer.Serialize(appliedChanges, JsonOpts)
			: null;
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

using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.State;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ActiveSync.Core.Tests;

public sealed class SyncStateServiceTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly SyncDbContext _db;
	private readonly SyncStateService _service;

	public SyncStateServiceTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();
		DbContextOptions<SqliteSyncDbContext> options = new DbContextOptionsBuilder<SqliteSyncDbContext>()
			.UseSqlite(_connection)
			.Options;
		_db = new SqliteSyncDbContext(options);
		// Unit tests build the schema from the model directly; startup uses MigrateAsync.
		_db.Database.EnsureCreated();
		_service = new SyncStateService(_db);
	}

	public void Dispose()
	{
		_db.Dispose();
		_connection.Dispose();
	}

	[Fact]
	public async Task SyncKeyLifecycle_InitialCurrentReplayInvalid()
	{
		Device device = await _service.GetOrCreateDeviceAsync("u@x", "DEV1", "Phone", CancellationToken.None);

		// Initial (key 0)
		(SyncKeyValidation validation, CollectionState? state) =
			await _service.ValidateSyncKeyAsync(device, "5", "0", CancellationToken.None);
		Assert.Equal(SyncKeyValidation.Initial, validation);
		Assert.NotNull(state);
		int key1 = await _service.CommitCollectionStateAsync(
			state, new Dictionary<string, string> { ["a"] = "1" }, 0, CancellationToken.None);
		Assert.Equal(1, key1);

		// Current key accepted
		(validation, state) = await _service.ValidateSyncKeyAsync(device, "5", "1", CancellationToken.None);
		Assert.Equal(SyncKeyValidation.Current, validation);
		Assert.NotNull(state);
		Assert.Equal("1", SyncStateService.ReadSnapshot(state)["a"]);

		int key2 = await _service.CommitCollectionStateAsync(
			state, new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" }, 0, CancellationToken.None);
		Assert.Equal(2, key2);

		// Replay: client resends the previous key → state rolls back one generation
		(validation, state) = await _service.ValidateSyncKeyAsync(device, "5", "1", CancellationToken.None);
		Assert.Equal(SyncKeyValidation.Replay, validation);
		Assert.NotNull(state);
		Assert.False(SyncStateService.ReadSnapshot(state).ContainsKey("b"));

		// Unknown key → invalid
		(validation, _) = await _service.ValidateSyncKeyAsync(device, "5", "42", CancellationToken.None);
		Assert.Equal(SyncKeyValidation.Invalid, validation);
	}

	[Fact]
	public async Task ValidateSyncKey_Invalid_ReturnsNullState()
	{
		// On an unknown key the method used to return a detached, never-added CollectionState
		// whose type gave no hint it was inert; callers could mistake it for real state. Invalid
		// must return a null state (A17).
		Device device = await _service.GetOrCreateDeviceAsync("u@a17", "DEV1", "Phone", CancellationToken.None);
		(_, CollectionState? seed) = await _service.ValidateSyncKeyAsync(device, "5", "0", CancellationToken.None);
		await _service.CommitCollectionStateAsync(seed!, [], 0, CancellationToken.None);

		(SyncKeyValidation validation, CollectionState? state) =
			await _service.ValidateSyncKeyAsync(device, "5", "999", CancellationToken.None);
		Assert.Equal(SyncKeyValidation.Invalid, validation);
		Assert.Null(state);
	}

	[Fact]
	public async Task AppliedAdds_SurviveReplayRollback_AndClearOnNextCommit()
	{
		Device device = await _service.GetOrCreateDeviceAsync("u@x", "DEV1", "Phone", CancellationToken.None);
		(_, CollectionState? state) = await _service.ValidateSyncKeyAsync(device, "7", "0", CancellationToken.None);
		Assert.NotNull(state);
		await _service.CommitCollectionStateAsync(state, [], 0, CancellationToken.None);

		// The request that produces key 2 applied one client Add.
		(_, state) = await _service.ValidateSyncKeyAsync(device, "7", "1", CancellationToken.None);
		Assert.NotNull(state);
		await _service.CommitCollectionStateAsync(state,
			new Dictionary<string, string> { ["item1"] = "r1" }, 0, CancellationToken.None,
			new Dictionary<string, AppliedClientAdd> { ["c1"] = new AppliedClientAdd("item1", "r1") });

		// Lost response: the client retries with key 1 — the rollback keeps the applied-Add
		// map, which is exactly what the retried commands need for dedup.
		(SyncKeyValidation validation, CollectionState? rolledBack) =
			await _service.ValidateSyncKeyAsync(device, "7", "1", CancellationToken.None);
		Assert.Equal(SyncKeyValidation.Replay, validation);
		Assert.NotNull(rolledBack);
		Dictionary<string, AppliedClientAdd> replayed = SyncStateService.ReadAppliedAdds(rolledBack);
		Assert.True(replayed.TryGetValue("c1", out AppliedClientAdd? add));
		Assert.Equal("item1", add!.ItemKey);
		Assert.Equal("r1", add.Revision);

		// A commit without client Adds clears the map — it described the discarded generation.
		await _service.CommitCollectionStateAsync(rolledBack,
			new Dictionary<string, string> { ["item1"] = "r1" }, 0, CancellationToken.None);
		(_, CollectionState? after) = await _service.ValidateSyncKeyAsync(device, "7", "2", CancellationToken.None);
		Assert.NotNull(after);
		Assert.Empty(SyncStateService.ReadAppliedAdds(after));
	}

	[Fact]
	public async Task AppliedChanges_SurviveReplayRollback_AndClearOnNextCommit()
	{
		Device device = await _service.GetOrCreateDeviceAsync("u@x", "DEV1", "Phone", CancellationToken.None);
		(_, CollectionState? state) = await _service.ValidateSyncKeyAsync(device, "9", "0", CancellationToken.None);
		Assert.NotNull(state);
		await _service.CommitCollectionStateAsync(state,
			new Dictionary<string, string> { ["item1"] = "r1" }, 0, CancellationToken.None);

		// The request that produces key 2 applied one client Change.
		(_, state) = await _service.ValidateSyncKeyAsync(device, "9", "1", CancellationToken.None);
		Assert.NotNull(state);
		await _service.CommitCollectionStateAsync(state,
			new Dictionary<string, string> { ["item1"] = "r2" }, 0, CancellationToken.None,
			appliedChanges: new Dictionary<string, AppliedClientChange>
			{
				["41:7"] = new AppliedClientChange("item1", "r2")
			});

		// Lost response: the client retries with key 1 — the rollback keeps the map.
		(SyncKeyValidation validation, CollectionState? rolledBack) =
			await _service.ValidateSyncKeyAsync(device, "9", "1", CancellationToken.None);
		Assert.Equal(SyncKeyValidation.Replay, validation);
		Assert.NotNull(rolledBack);
		Dictionary<string, AppliedClientChange> replayed = SyncStateService.ReadAppliedChanges(rolledBack);
		Assert.True(replayed.TryGetValue("41:7", out AppliedClientChange? change));
		Assert.Equal("item1", change!.ItemKey);
		Assert.Equal("r2", change.Revision);

		// A commit without client Changes clears the map — it described the discarded generation.
		await _service.CommitCollectionStateAsync(rolledBack,
			new Dictionary<string, string> { ["item1"] = "r2" }, 0, CancellationToken.None);
		(_, CollectionState? after) = await _service.ValidateSyncKeyAsync(device, "9", "2", CancellationToken.None);
		Assert.NotNull(after);
		Assert.Empty(SyncStateService.ReadAppliedChanges(after));
	}

	[Fact]
	public async Task SaveChangesAsync_TwoArgOverload_StampsConcurrencyToken()
	{
		// EF's real interception point is SaveChangesAsync(bool, ct) — an execution-strategy retry
		// or any caller using it bypassed stamping when only the 0/1-arg forms were overridden,
		// writing a CollectionState without a fresh token and defeating lost-update detection (A5).
		Device device = await _service.GetOrCreateDeviceAsync("u@a5", "DEV1", "Phone", CancellationToken.None);
		(_, CollectionState? state) = await _service.ValidateSyncKeyAsync(device, "1", "0", CancellationToken.None);
		Assert.NotNull(state);
		Guid before = state.ConcurrencyToken;

		state.SnapshotJson = "{\"x\":\"1\"}";
		await _db.SaveChangesAsync(acceptAllChangesOnSuccess: true, CancellationToken.None);

		Assert.NotEqual(before, state.ConcurrencyToken);
	}

	[Fact]
	public async Task FolderRegistry_AssignsStableServerIds_AndSoftDeletes()
	{
		List<BackendFolder> folders = new()
		{
			new BackendFolder("imap:INBOX", "Inbox", null, 2, "Email"),
			new BackendFolder("imap:Sent", "Sent", null, 5, "Email")
		};
		List<UserFolder> registry = await _service.RefreshFolderRegistryAsync("u@x", folders, CancellationToken.None);
		Assert.Equal(2, registry.Count);
		string inboxId = registry.Single(f => f.BackendKey == "imap:INBOX").ServerId;

		// Second refresh keeps the same ServerId
		registry = await _service.RefreshFolderRegistryAsync("u@x", folders, CancellationToken.None);
		Assert.Equal(inboxId, registry.Single(f => f.BackendKey == "imap:INBOX").ServerId);

		// A folder disappearing from the backend is soft-deleted
		registry = await _service.RefreshFolderRegistryAsync("u@x", [folders[0]], CancellationToken.None);
		Assert.Single(registry);
		Assert.Equal("imap:INBOX", registry[0].BackendKey);
	}

	[Fact]
	public async Task RefreshFolderRegistry_RetryDetach_PreservesUnrelatedTrackedMutations()
	{
		// A concurrent first-sync insert makes the reconcile retry; the retry must discard only
		// the folder rows it re-reads, never an unrelated tracked mutation (here the device's
		// FolderSyncKey bump) that shares the same request-scoped context (A1).
		FaultInjectingInterceptor faults = new();
		await using SqliteSyncDbContext db = StateTestSupport.NewContext(_connection, faults);
		SyncStateService service = new(db);
		Device device = await service.GetOrCreateDeviceAsync("u@a1", "DEV1", "Phone", CancellationToken.None);

		device.FolderSyncKey = 99; // tracked, unsaved — belongs to the same context

		faults.ThrowOnNextSave(new DbUpdateException("dup",
			new SqliteException("UNIQUE constraint failed", 19, 2067)));
		await service.RefreshFolderRegistryAsync("u@a1",
			[new BackendFolder("imap:INBOX", "Inbox", null, 2, "Email")], CancellationToken.None);

		await using SqliteSyncDbContext verify = StateTestSupport.NewContext(_connection);
		Device saved = await verify.Devices.FirstAsync(d => d.DeviceId == "DEV1");
		Assert.Equal(99, saved.FolderSyncKey); // detach-all discarded it, leaving the client acked N+1 over DB N
	}

	[Fact]
	public async Task FolderDiff_ReportsAddsUpdatesDeletes()
	{
		Device device = await _service.GetOrCreateDeviceAsync("u@x", "DEV1", "Phone", CancellationToken.None);
		List<BackendFolder> folders = new() { new BackendFolder("imap:INBOX", "Inbox", null, 2, "Email") };
		List<UserFolder> registry = await _service.RefreshFolderRegistryAsync("u@x", folders, CancellationToken.None);

		FolderHierarchyDiff diff = await _service.ComputeFolderDiffAsync(device, registry, CancellationToken.None);
		Assert.Single(diff.Adds);

		await _service.CommitFolderHierarchyAsync(device, registry, CancellationToken.None);
		diff = await _service.ComputeFolderDiffAsync(device, registry, CancellationToken.None);
		Assert.Empty(diff.Adds);
		Assert.Empty(diff.Updates);
		Assert.Empty(diff.Deletes);

		// Rename → update
		folders = [new BackendFolder("imap:INBOX", "Postboks", null, 2, "Email")];
		registry = await _service.RefreshFolderRegistryAsync("u@x", folders, CancellationToken.None);
		diff = await _service.ComputeFolderDiffAsync(device, registry, CancellationToken.None);
		Assert.Single(diff.Updates);
	}

	[Fact]
	public async Task CommitFolderHierarchy_ConcurrentBump_RejectsStaleWriter()
	{
		// Two pipelined FolderSyncs read the same FolderSyncKey generation. Without a concurrency
		// token on Device both wrote N+1 (a lost update, both clients told N+1); the racing
		// DeviceFolder RemoveRange/Add batches could also 500 on the unique index. The second
		// writer off the stale generation must be rejected, not silently applied (A6).
		Device device = await _service.GetOrCreateDeviceAsync("u@a6", "DEV1", "Phone", CancellationToken.None);
		List<UserFolder> registry = await _service.RefreshFolderRegistryAsync("u@a6",
			[new BackendFolder("imap:INBOX", "Inbox", null, 2, "Email")], CancellationToken.None);

		await using SqliteSyncDbContext db2 = StateTestSupport.NewContext(_connection);
		SyncStateService service2 = new(db2);
		Device device2 = await db2.Devices.FirstAsync(d => d.DeviceId == "DEV1" && d.UserName == "u@a6");
		List<UserFolder> registry2 = await service2.GetFoldersAsync("u@a6", CancellationToken.None);

		int firstKey = await _service.CommitFolderHierarchyAsync(device, registry, CancellationToken.None);
		Assert.Equal(1, firstKey);

		await Assert.ThrowsAsync<BackendException>(() =>
			service2.CommitFolderHierarchyAsync(device2, registry2, CancellationToken.None));
	}

	[Fact]
	public async Task FolderDiff_ReportsTypeChange()
	{
		// A folder whose EAS Type changes (e.g. IMAP folder gaining \Sent, 12 -> 5) but keeps its
		// name and parent was never issued an Update, so the client rendered it in the wrong class
		// forever (A8).
		Device device = await _service.GetOrCreateDeviceAsync("u@a8", "DEV1", "Phone", CancellationToken.None);
		List<UserFolder> registry = await _service.RefreshFolderRegistryAsync("u@a8",
			[new BackendFolder("imap:Archive", "Archive", null, 12, "Email")], CancellationToken.None);
		await _service.CommitFolderHierarchyAsync(device, registry, CancellationToken.None);

		registry = await _service.RefreshFolderRegistryAsync("u@a8",
			[new BackendFolder("imap:Archive", "Archive", null, 5, "Email")], CancellationToken.None);
		FolderHierarchyDiff diff = await _service.ComputeFolderDiffAsync(device, registry, CancellationToken.None);

		Assert.Empty(diff.Adds);
		Assert.Single(diff.Updates);
		Assert.Equal(5, diff.Updates[0].Type);
	}

	[Fact]
	public async Task CommitFolderHierarchy_UnchangedCommit_KeepsRowIdentity()
	{
		// The commit reconciled by deleting the whole hierarchy and reinserting it, so one
		// renamed folder churned every row's primary key. A commit that changes nothing must
		// leave the existing rows untouched (A7).
		Device device = await _service.GetOrCreateDeviceAsync("u@a7", "DEV1", "Phone", CancellationToken.None);
		List<UserFolder> registry = await _service.RefreshFolderRegistryAsync("u@a7",
			[new BackendFolder("imap:INBOX", "Inbox", null, 2, "Email"),
			 new BackendFolder("imap:Sent", "Sent", null, 5, "Email")], CancellationToken.None);
		await _service.CommitFolderHierarchyAsync(device, registry, CancellationToken.None);

		async Task<int[]> DeviceFolderIds()
		{
			await using SqliteSyncDbContext read = StateTestSupport.NewContext(_connection);
			return await read.DeviceFolders.Where(f => f.DeviceKey == device.Id)
				.OrderBy(f => f.ServerId).Select(f => f.Id).ToArrayAsync();
		}

		int[] before = await DeviceFolderIds();
		await _service.CommitFolderHierarchyAsync(device, registry, CancellationToken.None);
		int[] after = await DeviceFolderIds();

		Assert.Equal(before, after);
	}

	[Fact]
	public async Task CommitCollectionState_ConcurrentWrite_LosesNoUpdate_ThrowsOnStale()
	{
		Device device = await _service.GetOrCreateDeviceAsync("u@x", "DEV1", "Phone", CancellationToken.None);
		(_, CollectionState? seed) = await _service.ValidateSyncKeyAsync(device, "c", "0", CancellationToken.None);
		Assert.NotNull(seed);
		await _service.CommitCollectionStateAsync(seed, new Dictionary<string, string> { ["a"] = "1" }, 0,
			CancellationToken.None);

		// A second request (its own context, same DB) loads the same generation...
		DbContextOptions<SqliteSyncDbContext> options = new DbContextOptionsBuilder<SqliteSyncDbContext>()
			.UseSqlite(_connection).Options;
		await using SqliteSyncDbContext db2 = new(options);
		SyncStateService service2 = new(db2);
		Device device2 = await db2.Devices.FirstAsync(d => d.DeviceId == "DEV1");
		(_, CollectionState? stateB) = await service2.ValidateSyncKeyAsync(device2, "c", "1", CancellationToken.None);
		(_, CollectionState? stateA) = await _service.ValidateSyncKeyAsync(device, "c", "1", CancellationToken.None);
		Assert.NotNull(stateA);
		Assert.NotNull(stateB);

		// A commits first and wins.
		int keyA = await _service.CommitCollectionStateAsync(stateA,
			new Dictionary<string, string> { ["a"] = "1", ["fromA"] = "2" }, 0, CancellationToken.None);
		Assert.Equal(2, keyA);

		// B committing off the now-stale generation is rejected, not silently applied.
		BackendException ex = await Assert.ThrowsAsync<BackendException>(() =>
			service2.CommitCollectionStateAsync(stateB,
				new Dictionary<string, string> { ["a"] = "1", ["fromB"] = "3" }, 0, CancellationToken.None));
		Assert.Contains("Concurrent sync", ex.Message);

		// A's write survived intact — no lost update.
		(_, CollectionState? after) = await _service.ValidateSyncKeyAsync(device, "c", "2", CancellationToken.None);
		Assert.NotNull(after);
		Assert.True(SyncStateService.ReadSnapshot(after).ContainsKey("fromA"));
		Assert.False(SyncStateService.ReadSnapshot(after).ContainsKey("fromB"));
	}

	[Fact]
	public async Task CommitCollectionState_ConcurrencyFailure_ReloadsEntityNotLeftDirty()
	{
		// After a concurrency failure the entity kept its failed values and stayed Modified, so a
		// later SaveChangesAsync on the same request retried the doomed UPDATE from an unrelated
		// call site. The catch must reload it back to Unchanged (A18).
		Device device = await _service.GetOrCreateDeviceAsync("u@a18", "DEV1", "Phone", CancellationToken.None);
		(_, CollectionState? seed) = await _service.ValidateSyncKeyAsync(device, "c", "0", CancellationToken.None);
		Assert.NotNull(seed);
		await _service.CommitCollectionStateAsync(seed, new Dictionary<string, string> { ["a"] = "1" }, 0,
			CancellationToken.None);

		await using SqliteSyncDbContext db2 = StateTestSupport.NewContext(_connection);
		SyncStateService service2 = new(db2);
		Device device2 = await db2.Devices.FirstAsync(d => d.DeviceId == "DEV1" && d.UserName == "u@a18");
		(_, CollectionState? stateB) = await service2.ValidateSyncKeyAsync(device2, "c", "1", CancellationToken.None);
		(_, CollectionState? stateA) = await _service.ValidateSyncKeyAsync(device, "c", "1", CancellationToken.None);
		Assert.NotNull(stateA);
		Assert.NotNull(stateB);

		await _service.CommitCollectionStateAsync(stateA,
			new Dictionary<string, string> { ["a"] = "1", ["fromA"] = "2" }, 0, CancellationToken.None);

		await Assert.ThrowsAsync<BackendException>(() =>
			service2.CommitCollectionStateAsync(stateB,
				new Dictionary<string, string> { ["fromB"] = "3" }, 0, CancellationToken.None));

		Assert.Equal(EntityState.Unchanged, db2.Entry(stateB).State);
	}

	[Fact]
	public async Task CompleteAccountWipe_ConcurrentBlockInsert_TreatedAsSuccess()
	{
		// CompleteAccountWipeAsync did AnyAsync then AddAsync against the unique (UserName, DeviceId)
		// LoginBlock index. Two concurrent wipe acks raced the insert → unhandled DbUpdateException
		// → 500. A unique violation must be treated as success, with the wipe-completion flag still
		// persisted (A22).
		FaultInjectingInterceptor faults = new();
		await using SqliteSyncDbContext db = StateTestSupport.NewContext(_connection, faults);
		SyncStateService service = new(db);
		Device device = await service.GetOrCreateDeviceAsync("u@a22", "DEV1", "Phone", CancellationToken.None);
		device.PendingAccountWipe = true;
		await db.SaveChangesAsync(CancellationToken.None);

		// Simulate the concurrent ack inserting the block between our AnyAsync check and our insert.
		faults.ThrowOnNextSave(new DbUpdateException("dup",
			new SqliteException("UNIQUE constraint failed", 19, 2067)));
		await service.CompleteAccountWipeAsync(device, CancellationToken.None); // must not throw

		await using SqliteSyncDbContext verify = StateTestSupport.NewContext(_connection);
		Device saved = await verify.Devices.FirstAsync(d => d.DeviceId == "DEV1" && d.UserName == "u@a22");
		Assert.False(saved.PendingAccountWipe);
	}

	[Fact]
	public async Task GetOrCreateDevice_NonUniqueFailure_PropagatesOriginalError()
	{
		// The insert-race catch assumed every DbUpdateException was "someone inserted first" and
		// re-read with FirstAsync. A non-unique failure (disk full, SQLITE_BUSY, NOT NULL) then
		// finds no row and throws "Sequence contains no elements", destroying the real
		// diagnostic. A non-unique failure must surface unchanged (A9).
		FaultInjectingInterceptor faults = new();
		await using SqliteSyncDbContext db = StateTestSupport.NewContext(_connection, faults);
		SyncStateService service = new(db);

		faults.ThrowOnNextSave(new DbUpdateException("disk full", new Exception("SQLITE_FULL")));
		await Assert.ThrowsAsync<DbUpdateException>(() =>
			service.GetOrCreateDeviceAsync("u@a9", "DEV1", "Phone", CancellationToken.None));
	}

	[Fact]
	public async Task GetOrAddDavItemId_DoesNotFlushUnrelatedTrackedMutations()
	{
		// The DAV id allocation runs its own commit mid-Sync; sharing the request-scoped context
		// meant it flushed everything tracked — including a half-mutated CollectionState, bumping
		// its ConcurrencyToken before the round was known good. With a factory it runs on its own
		// context and leaves the request's unsaved state untouched (A10).
		TestDbContextFactory factory = new(_connection);
		SyncStateService service = new(_db, factory);

		List<UserFolder> registry = await service.RefreshFolderRegistryAsync("u@a10",
			[new BackendFolder("carddav:/ab/", "Contacts", null, 9, "Contacts")], CancellationToken.None);
		UserFolder folder = registry[0];
		Device device = await service.GetOrCreateDeviceAsync("u@a10", "DEV1", "Phone", CancellationToken.None);
		(_, CollectionState? state) =
			await service.ValidateSyncKeyAsync(device, folder.ServerId, "0", CancellationToken.None);
		Assert.NotNull(state);
		await service.CommitCollectionStateAsync(state, [], 0, CancellationToken.None);

		// Mutate the snapshot in memory but do NOT persist it.
		state.SnapshotJson = "{\"dirty\":\"1\"}";
		await service.GetOrAddDavItemIdAsync(folder, "/ab/x.vcf", CancellationToken.None);

		await using SqliteSyncDbContext verify = StateTestSupport.NewContext(_connection);
		CollectionState persisted = await verify.CollectionStates.AsNoTracking()
			.FirstAsync(c => c.DeviceKey == device.Id && c.CollectionId == folder.ServerId);
		Assert.DoesNotContain("dirty", persisted.SnapshotJson);
	}

	[Fact]
	public async Task DavItemMap_RoundTripsHrefs()
	{
		List<UserFolder> registry = await _service.RefreshFolderRegistryAsync("u@x",
			[new BackendFolder("carddav:/ab/", "Contacts", null, 9, "Contacts")], CancellationToken.None);
		UserFolder folder = registry[0];

		string id1 = await _service.GetOrAddDavItemIdAsync(folder, "/ab/x.vcf", CancellationToken.None);
		string id2 = await _service.GetOrAddDavItemIdAsync(folder, "/ab/x.vcf", CancellationToken.None);
		Assert.Equal(id1, id2);

		string? href = await _service.ResolveDavHrefAsync(folder, id1, CancellationToken.None);
		Assert.Equal("/ab/x.vcf", href);
		Assert.Null(await _service.ResolveDavHrefAsync(folder, "99999", CancellationToken.None));
	}
}

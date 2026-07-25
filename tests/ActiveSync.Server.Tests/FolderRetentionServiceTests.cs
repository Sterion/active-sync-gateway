using ActiveSync.Core.Options;
using ActiveSync.Core.State;
using ActiveSync.Server.Setup;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ActiveSync.Server.Tests;

/// <summary>
///   The folder-retention sweep. A folder that vanishes from the backend is only soft-deleted;
///   nothing used to remove the row, its DAV href map, or the per-device collection/device-folder
///   state keyed by its ServerId, so those tables only grew (A35). The sweep reclaims a folder past
///   the retention window together with all of that dependent state, and leaves fresher ones alone.
/// </summary>
public sealed class FolderRetentionServiceTests : IDisposable
{
	private readonly string _dbPath;

	public FolderRetentionServiceTests()
	{
		_dbPath = Path.Combine(Path.GetTempPath(), $"as-folderretention-{Guid.NewGuid():N}.db");
		using SyncDbContext db = NewContext();
		db.Database.EnsureCreated();
	}

	public void Dispose()
	{
		SqliteConnection.ClearAllPools();
		try
		{
			File.Delete(_dbPath);
		}
		catch (IOException)
		{
		}
	}

	private SqliteSyncDbContext NewContext()
	{
		DbContextOptions<SqliteSyncDbContext> options = new DbContextOptionsBuilder<SqliteSyncDbContext>()
			.UseSqlite($"Data Source={_dbPath}")
			.Options;
		return new SqliteSyncDbContext(options);
	}

	[Fact]
	public async Task Reclaim_RemovesStaleFolderAndAllDependentState_KeepsFresh()
	{
		DateTime now = DateTime.UtcNow;
		int staleId, freshId, liveId;

		await using (SyncDbContext seed = NewContext())
		{
			Device device = new() { UserName = "u@a35", DeviceId = "DEV1", DeviceType = "Phone" };
			await seed.Devices.AddAsync(device);

			UserFolder stale = new()
			{
				UserName = "u@a35", BackendKey = "imap:Gone", DisplayName = "Gone", EasClass = "Email",
				Deleted = true, DeletedUtc = now.AddDays(-40)
			};
			UserFolder fresh = new()
			{
				UserName = "u@a35", BackendKey = "imap:RecentlyGone", DisplayName = "RecentlyGone", EasClass = "Email",
				Deleted = true, DeletedUtc = now.AddDays(-5)
			};
			UserFolder live = new()
			{
				UserName = "u@a35", BackendKey = "imap:INBOX", DisplayName = "Inbox", EasClass = "Email"
			};
			await seed.UserFolders.AddRangeAsync(stale, fresh, live);
			await seed.SaveChangesAsync();
			staleId = stale.Id;
			freshId = fresh.Id;
			liveId = live.Id;

			// Dependent state keyed by the folder's ServerId (== Id string) / FK.
			await seed.DavItems.AddAsync(new DavItem { UserFolderKey = staleId, Href = "/g/1.eml" });
			await seed.DavItems.AddAsync(new DavItem { UserFolderKey = freshId, Href = "/r/1.eml" });
			await seed.CollectionStates.AddAsync(new CollectionState { DeviceKey = device.Id, CollectionId = staleId.ToString() });
			await seed.CollectionStates.AddAsync(new CollectionState { DeviceKey = device.Id, CollectionId = freshId.ToString() });
			await seed.DeviceFolders.AddAsync(new DeviceFolder
			{
				DeviceKey = device.Id, ServerId = staleId.ToString(), DisplayName = "Gone"
			});
			await seed.SaveChangesAsync();
		}

		await using (SyncDbContext sweep = NewContext())
		{
			int reclaimed = await FolderRetentionService.ReclaimAsync(sweep, now.AddDays(-30), CancellationToken.None);
			Assert.Equal(1, reclaimed);
		}

		await using SyncDbContext verify = NewContext();
		// The stale folder and every dependent row it owned are gone.
		Assert.Null(await verify.UserFolders.FirstOrDefaultAsync(f => f.Id == staleId));
		Assert.Empty(await verify.DavItems.Where(i => i.UserFolderKey == staleId).ToListAsync());
		Assert.Empty(await verify.CollectionStates.Where(c => c.CollectionId == staleId.ToString()).ToListAsync());
		Assert.Empty(await verify.DeviceFolders.Where(d => d.ServerId == staleId.ToString()).ToListAsync());

		// The recently-deleted and live folders (and their state) are untouched.
		Assert.NotNull(await verify.UserFolders.FirstOrDefaultAsync(f => f.Id == freshId));
		Assert.NotNull(await verify.UserFolders.FirstOrDefaultAsync(f => f.Id == liveId));
		Assert.Single(await verify.DavItems.Where(i => i.UserFolderKey == freshId).ToListAsync());
		Assert.Single(await verify.CollectionStates.Where(c => c.CollectionId == freshId.ToString()).ToListAsync());
	}

	/// <summary>
	///   E2: the retention sweep used to `break` on ANY <see cref="OperationCanceledException" />, so a
	///   non-shutdown cancellation (e.g. an EF command timeout surfacing as OCE) permanently stopped
	///   retention for the process lifetime — not just the current sweep. A fault on one sweep must
	///   not stop the loop while the host is still running; it should fall through to the retry path
	///   and remain parked in the inter-sweep delay, not exit.
	/// </summary>
	[Fact]
	public async Task NonShutdownCancellation_DoesNotStopTheSweepLoop()
	{
		ThrowingFactory factory = new();
		IOptionsMonitor<ActiveSyncOptions> monitor = TestOptionsMonitor.Of(
			new ActiveSyncOptions { Eas = new EasOptions { FolderRetentionDays = 30 } });
		FolderRetentionService service = new(factory, monitor, NullLogger<FolderRetentionService>.Instance);

		await service.StartAsync(CancellationToken.None);
		try
		{
			DateTime deadline = DateTime.UtcNow.AddSeconds(5);
			while (Volatile.Read(ref factory.Calls) < 1 && DateTime.UtcNow < deadline)
				await Task.Delay(20);
			Assert.True(Volatile.Read(ref factory.Calls) >= 1, "expected the sweep to have attempted at least once");

			// Give ExecuteAsync a moment to react to the thrown OCE one way or the other.
			await Task.Delay(300);

			Assert.False(service.ExecuteTask!.IsCompleted,
				"a non-shutdown OperationCanceledException from the sweep must not stop the retention loop " +
				"for the process lifetime — it should fall through to the retry path and stay parked in the " +
				"inter-sweep delay");
		}
		finally
		{
			await service.StopAsync(CancellationToken.None);
		}
	}

	private sealed class ThrowingFactory : ISyncDbContextFactory
	{
		public int Calls;

		public SyncDbContext CreateDbContext()
		{
			Interlocked.Increment(ref Calls);
			throw new OperationCanceledException("simulated EF command timeout");
		}
	}
}

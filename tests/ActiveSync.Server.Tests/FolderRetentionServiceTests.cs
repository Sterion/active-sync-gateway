using ActiveSync.Core.State;
using ActiveSync.Server.Setup;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

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
}

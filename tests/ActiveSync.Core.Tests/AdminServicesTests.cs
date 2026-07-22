using ActiveSync.Core.Administration;
using ActiveSync.Core.State;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ActiveSync.Core.Tests;

/// <summary>
///   Coverage for the shared admin services extracted in item 18 (S3/C18): the single validated
///   read/write path over the device, share and log tables that both the web admin API and the
///   `eas` CLI now consume. These are coverage, not reproducers — S3/C18 is a structural finding
///   (two write paths to one table), so the proof is the refactor plus the unchanged HTTP/CLI
///   behaviour tests; these pin the extracted behaviour so a later drift is caught.
/// </summary>
public sealed class AdminServicesTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly TestContextFactory _factory;

	public AdminServicesTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();
		_factory = new TestContextFactory(_connection);
		using SyncDbContext db = _factory.CreateDbContext();
		db.Database.EnsureCreated();
	}

	public void Dispose()
	{
		_connection.Dispose();
	}

	private async Task SeedAsync(Action<SyncDbContext> seed)
	{
		await using SyncDbContext db = _factory.CreateDbContext();
		seed(db);
		await db.SaveChangesAsync(CancellationToken.None);
	}

	// ---- DeviceAdminService ----

	[Fact]
	public async Task ListDevices_ResolvesUserAndDeviceBlocks_AndPages()
	{
		await SeedAsync(db =>
		{
			for (int i = 0; i < 3; i++)
				db.Devices.Add(new Device
				{
					UserName = "alice", DeviceId = $"d{i}", DeviceType = "T",
					CreatedUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow
				});
			db.Devices.Add(new Device
			{
				UserName = "carol", DeviceId = "d0", DeviceType = "T",
				CreatedUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow
			});
			db.LoginBlocks.Add(new LoginBlock { UserName = "alice", DeviceId = "d1", CreatedUtc = DateTime.UtcNow });
			db.LoginBlocks.Add(new LoginBlock { UserName = "carol", DeviceId = null, CreatedUtc = DateTime.UtcNow });
		});
		DeviceAdminService service = new(_factory);

		DeviceAdminService.DevicePage all = await service.ListAsync(null, 0, null, CancellationToken.None);
		Assert.Equal(4, all.Total);
		Assert.Equal(4, all.Devices.Count);

		DeviceAdminService.DeviceListing aliceD1 = all.Devices.Single(l => l.Device.UserName == "alice" && l.Device.DeviceId == "d1");
		Assert.True(aliceD1.Blocked);
		Assert.False(aliceD1.UserBlocked); // device-scoped block only

		DeviceAdminService.DeviceListing carol = all.Devices.Single(l => l.Device.UserName == "carol");
		Assert.True(carol.Blocked);
		Assert.True(carol.UserBlocked); // user-level block

		// Filter + page: alice has 3, take 2 from offset 1.
		DeviceAdminService.DevicePage page = await service.ListAsync("alice", 1, 2, CancellationToken.None);
		Assert.Equal(3, page.Total);
		Assert.Equal(["d1", "d2"], page.Devices.Select(l => l.Device.DeviceId));
	}

	[Fact]
	public async Task Block_IsIdempotent_AndUnblockReportsRemaining()
	{
		DeviceAdminService service = new(_factory);
		Assert.True(await service.BlockAsync("alice", null, CancellationToken.None));
		Assert.False(await service.BlockAsync("alice", null, CancellationToken.None)); // already blocked
		Assert.True(await service.BlockAsync("alice", "phone", CancellationToken.None));

		DeviceAdminService.UnblockResult removed = await service.UnblockAsync("alice", "phone", CancellationToken.None);
		Assert.True(removed.Removed);
		Assert.Equal(1, removed.RemainingForUser); // the user-level block remains

		DeviceAdminService.UnblockResult missing = await service.UnblockAsync("alice", "ghost", CancellationToken.None);
		Assert.False(missing.Removed);
	}

	[Fact]
	public async Task SetPendingWipe_TogglesTheFlag_AndReportsMissing()
	{
		await SeedAsync(db => db.Devices.Add(new Device
		{
			UserName = "alice", DeviceId = "phone", DeviceType = "T",
			CreatedUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow
		}));
		DeviceAdminService service = new(_factory);

		Assert.Null(await service.SetPendingWipeAsync("alice", "nope", true, CancellationToken.None));
		Device? armed = await service.SetPendingWipeAsync("alice", "phone", true, CancellationToken.None);
		Assert.NotNull(armed);
		Assert.True(armed!.PendingAccountWipe);

		Device? cancelled = await service.SetPendingWipeAsync("alice", "phone", false, CancellationToken.None);
		Assert.False(cancelled!.PendingAccountWipe);
	}

	[Fact]
	public async Task PurgeUser_CountsAndRemoves()
	{
		await SeedAsync(db =>
		{
			db.Devices.Add(new Device
			{
				UserName = "alice", DeviceId = "phone", DeviceType = "T",
				CreatedUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow
			});
			db.LoginBlocks.Add(new LoginBlock { UserName = "alice", DeviceId = null, CreatedUtc = DateTime.UtcNow });
		});
		DeviceAdminService service = new(_factory);

		IReadOnlyList<DeviceAdminService.PurgeCount> counts = await service.PurgeAsync("alice", null, CancellationToken.None);
		Assert.Equal(1, counts.Single(c => c.Table == "Devices").Count);
		Assert.Equal(1, counts.Single(c => c.Table == "LoginBlocks").Count);

		await using SyncDbContext db = _factory.CreateDbContext();
		Assert.Empty(db.Devices);
		Assert.Empty(db.LoginBlocks);
	}

	// ---- ShareAdminService ----

	[Fact]
	public async Task Share_Add_Remode_Unchanged_List_Remove()
	{
		ShareAdminService service = new(_factory);

		ShareAdminService.ShareUpsert created =
			await service.AddOrUpdateAsync("alice", "/cal/family/", false, CancellationToken.None);
		Assert.Equal(ShareAdminService.UpsertKind.Created, created.Kind);

		ShareAdminService.ShareUpsert unchanged =
			await service.AddOrUpdateAsync("alice", "/cal/family/", false, CancellationToken.None);
		Assert.Equal(ShareAdminService.UpsertKind.Unchanged, unchanged.Kind);
		Assert.Equal(created.CreatedUtc, unchanged.CreatedUtc); // same row, not re-stamped

		ShareAdminService.ShareUpsert remoded =
			await service.AddOrUpdateAsync("alice", "/cal/family/", true, CancellationToken.None);
		Assert.Equal(ShareAdminService.UpsertKind.Remoded, remoded.Kind);

		ShareAdminService.SharePage page = await service.ListAsync("alice", 0, null, CancellationToken.None);
		SharedCalendarGrant grant = Assert.Single(page.Grants);
		Assert.True(grant.ReadOnly);

		Assert.True(await service.RemoveAsync("alice", "/cal/family/", CancellationToken.None));
		Assert.False(await service.RemoveAsync("alice", "/cal/family/", CancellationToken.None));
	}

	// ---- LogQueryService ----

	[Fact]
	public async Task Logs_History_FiltersByLevelFloor_TimeWindow_AndLiteralText()
	{
		await SeedAsync(db =>
		{
			db.LogEntries.Add(new LogEntry { TimestampUtc = DateTime.UtcNow.AddMinutes(-1), Level = "Information", Message = "50% off" });
			db.LogEntries.Add(new LogEntry { TimestampUtc = DateTime.UtcNow.AddMinutes(-1), Level = "Error", Message = "boom" });
			db.LogEntries.Add(new LogEntry { TimestampUtc = DateTime.UtcNow.AddDays(-3), Level = "Fatal", Message = "ancient" });
		});
		LogQueryService service = new(_factory);
		DateTime since = DateTime.UtcNow.AddHours(-1);

		// Level floor Error keeps Error+Fatal, and the time window drops the 3-day-old fatal.
		LogQueryService.LogPage errors = await service.QueryAsync(
			new LogQueryService.LogQuery(since, null, LogQueryService.LevelsAtOrAbove("Error"), null, null, null, null, 100),
			CancellationToken.None);
		Assert.Equal(["boom"], errors.Entries.Select(e => e.Message));

		// '%' is matched literally, never as a LIKE wildcard.
		LogQueryService.LogPage percent = await service.QueryAsync(
			new LogQueryService.LogQuery(since, null, null, null, null, null, "%", 100), CancellationToken.None);
		Assert.Equal(["50% off"], percent.Entries.Select(e => e.Message));
	}

	[Fact]
	public async Task Logs_TailMode_UsesCursorAndTimeFloor()
	{
		await SeedAsync(db =>
		{
			db.LogEntries.Add(new LogEntry { TimestampUtc = DateTime.UtcNow.AddDays(-3), Level = "Information", Message = "ancient" });
			db.LogEntries.Add(new LogEntry { TimestampUtc = DateTime.UtcNow.AddMinutes(-1), Level = "Information", Message = "recent" });
		});
		LogQueryService service = new(_factory);

		// after=0 with a 60-minute floor must not walk the whole table from Id 1.
		LogQueryService.LogPage page = await service.QueryAsync(
			new LogQueryService.LogQuery(DateTime.UtcNow.AddMinutes(-60), 0, null, null, null, null, null, 100),
			CancellationToken.None);
		Assert.Equal(["recent"], page.Entries.Select(e => e.Message));
		Assert.True(page.LastId > 0);
	}

	[Theory]
	[InlineData("Information", 4)]
	[InlineData("warn", 3)]
	[InlineData("critical", 1)]
	[InlineData("nonsense", 0)]
	public void LevelsAtOrAbove_AcceptsExactNamesAndCliAliases(string level, int expected)
	{
		Assert.Equal(expected, LogQueryService.LevelsAtOrAbove(level).Length);
	}

	private sealed class TestContextFactory(SqliteConnection connection) : ISyncDbContextFactory
	{
		public SyncDbContext CreateDbContext()
		{
			DbContextOptions<SqliteSyncDbContext> options = new DbContextOptionsBuilder<SqliteSyncDbContext>()
				.UseSqlite(connection)
				.Options;
			return new SqliteSyncDbContext(options);
		}
	}
}

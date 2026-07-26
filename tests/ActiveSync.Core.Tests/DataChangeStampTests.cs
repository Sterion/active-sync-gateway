using ActiveSync.Core.Accounts;
using ActiveSync.Core.Options;
using ActiveSync.Core.Settings;
using ActiveSync.Core.State;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ActiveSync.Core.Tests;

/// <summary>
///   Item 3a of docs/design/db-restructure.md: the two single-row stamp tables merge into one
///   <see cref="DataChange" /> table keyed by watched area. The property that matters is
///   INDEPENDENCE — one row per area, never one row total — because a shared version would make
///   a user write invalidate the settings snapshot (and vice versa), so every consumer would
///   reload on every unrelated change.
/// </summary>
public sealed class DataChangeStampTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly TestContextFactory _factory;
	private readonly UserStore _users;
	private readonly GlobalSettingStore _settings;

	public DataChangeStampTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();
		_factory = new TestContextFactory(_connection);
		using SyncDbContext db = _factory.CreateDbContext();
		db.Database.EnsureCreated();
		_users = new UserStore(_factory);
		_settings = new GlobalSettingStore(_factory);
	}

	public void Dispose() => _connection.Dispose();

	[Fact]
	public async Task TheAreasAreIndependent_AUserWriteDoesNotMoveTheSettingsVersion()
	{
		await _users.UpsertAsync("anna", new UserOptions { MailAddress = "a@x" }, CancellationToken.None);
		await _settings.UpsertAsync("ActiveSync:ReadOnly", "true", CancellationToken.None);
		Guid? usersV1 = await _users.ReadStampAsync(CancellationToken.None);
		Guid? settingsV1 = await _settings.ReadStampAsync(CancellationToken.None);
		Assert.NotNull(usersV1);
		Assert.NotNull(settingsV1);
		Assert.NotEqual(usersV1, settingsV1);

		// A user write moves ONLY "users".
		await _users.UpsertAsync("bob", new UserOptions(), CancellationToken.None);
		Assert.NotEqual(usersV1, await _users.ReadStampAsync(CancellationToken.None));
		Assert.Equal(settingsV1, await _settings.ReadStampAsync(CancellationToken.None));

		// A settings write moves ONLY "settings".
		Guid? usersV2 = await _users.ReadStampAsync(CancellationToken.None);
		await _settings.UpsertAsync("ActiveSync:ReadOnly", "false", CancellationToken.None);
		Assert.NotEqual(settingsV1, await _settings.ReadStampAsync(CancellationToken.None));
		Assert.Equal(usersV2, await _users.ReadStampAsync(CancellationToken.None));
	}

	[Fact]
	public async Task ARoleOverrideWrite_BumpsTheUsersArea_NotAnAreaOfItsOwn()
	{
		// "A stamp belongs to a consumer's aggregate, not to a table": UserBackendRoles is part
		// of the users aggregate, so its writes must move "users" — the resolver rebuilds the
		// whole snapshot on any bump anyway.
		await _users.UpsertAsync("anna", new UserOptions(), CancellationToken.None);
		Guid? before = await _users.ReadStampAsync(CancellationToken.None);

		await _users.UpsertAsync("anna", new UserOptions
		{
			Backends = new Dictionary<string, BackendRoleOverride> { ["MailStore"] = new() { UserName = "imap-anna" } },
		}, CancellationToken.None);

		Assert.NotEqual(before, await _users.ReadStampAsync(CancellationToken.None));
		// ...and it did NOT mint a "userbackendroles" area of its own.
		await using SyncDbContext db = _factory.CreateDbContext();
		Assert.Equal([DataChangeAreas.Users], await db.DataChanges.Select(c => c.Key).ToListAsync());
	}

	[Fact]
	public async Task DeletingADeclaration_AlsoBumps_SoReplicasDropIt()
	{
		await _users.UpsertAsync("anna", new UserOptions { MailAddress = "a@x" }, CancellationToken.None);
		Guid? before = await _users.ReadStampAsync(CancellationToken.None);

		Assert.True(await _users.DeleteAsync("anna", CancellationToken.None));

		Assert.NotEqual(before, await _users.ReadStampAsync(CancellationToken.None));
	}

	[Fact]
	public async Task UnwrittenArea_ReadsNull_AndTheFirstWriteCreatesExactlyOneRow()
	{
		Assert.Null(await _users.ReadStampAsync(CancellationToken.None));
		Assert.Null(await _settings.ReadStampAsync(CancellationToken.None));

		await _users.UpsertAsync("anna", new UserOptions(), CancellationToken.None);
		await _users.UpsertAsync("bob", new UserOptions(), CancellationToken.None);

		await using SyncDbContext db = _factory.CreateDbContext();
		DataChange row = await db.DataChanges.AsNoTracking().SingleAsync();
		Assert.Equal(DataChangeAreas.Users, row.Key);
	}

	[Fact]
	public async Task FirstUseInsertRace_IsToleratedAsAnUpdate()
	{
		// Two replicas can both find no row for an area and both insert; the loser's PK conflict
		// must resolve to an update, not a 500 (the same idiom DeviceStore/DavItemMap use).
		await using SyncDbContext db = _factory.CreateDbContext();
		await using (SyncDbContext racer = _factory.CreateDbContext())
		{
			// The competing replica commits the area's first row while our own insert is staged.
#pragma warning disable VSTHRD103
			racer.DataChanges.Add(new DataChange
			{
				Key = DataChangeAreas.Users, Version = Guid.NewGuid(), UpdatedUtc = DateTime.UtcNow,
			});
#pragma warning restore VSTHRD103
			await racer.SaveChangesAsync();
		}

		await DataChangeStamps.BumpAndSaveAsync(db, DataChangeAreas.Users, CancellationToken.None);

		await using SyncDbContext verify = _factory.CreateDbContext();
		Assert.Single(verify.DataChanges);
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

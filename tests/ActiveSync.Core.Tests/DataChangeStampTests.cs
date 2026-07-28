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

	[Fact]
	public async Task UserStore_ConcurrentFirstBump_IsToleratedInsteadOfARawPkViolation()
	{
		// A8, through the actual PUBLIC call site named in the finding (UserStore.cs — the
		// BumpStampAsync wrapper): UpsertAsync's insert branch stages the new User row, THEN
		// stages the "users" area's first-ever DataChange row (BumpStampAsync's own BumpAsync
		// call finds no row yet), and only then saves. ConcurrentWriteInterceptor injects a
		// genuine competing commit of that SAME first row immediately before our SaveChangesAsync
		// sends its SQL — i.e. strictly AFTER our own read-found-nothing, so this is a real
		// collision (unlike committing the racer BEFORE calling UpsertAsync, which would just let
		// our own read see it and take the trivial update branch, never exercising the conflict
		// at all). Before the fix (BumpStampAsync called the racing BumpAsync + a bare
		// SaveChangesAsync) this surfaced the raw SQLite PK violation as an unhandled exception.
		ConcurrentWriteInterceptor interceptor = new(() =>
		{
			using SyncDbContext racer = StateTestSupport.NewContext(_connection);
#pragma warning disable VSTHRD103
			racer.DataChanges.Add(new DataChange
			{
				Key = DataChangeAreas.Users, Version = Guid.NewGuid(), UpdatedUtc = DateTime.UtcNow,
			});
#pragma warning restore VSTHRD103
			racer.SaveChanges();
		});
		UserStore raced = new(new InterceptedDbContextFactory(_connection, interceptor));

		await raced.UpsertAsync("anna", new UserOptions { MailAddress = "a@x" }, CancellationToken.None);

		Assert.NotNull(await _users.GetAsync("anna", CancellationToken.None));
		await using SyncDbContext verify = _factory.CreateDbContext();
		Assert.Single(await verify.DataChanges.Where(c => c.Key == DataChangeAreas.Users).ToListAsync());
	}

	[Fact]
	public async Task GlobalSettingStore_ConcurrentFirstBump_IsToleratedInsteadOfARawPkViolation()
	{
		// A8, through the second call site the finding names (GlobalSettingStore.cs): same
		// mechanism as UserStore_ConcurrentFirstBump_..., but for the "settings" area and
		// UpsertAsync's own BumpStampAsync wrapper.
		ConcurrentWriteInterceptor interceptor = new(() =>
		{
			using SyncDbContext racer = StateTestSupport.NewContext(_connection);
#pragma warning disable VSTHRD103
			racer.DataChanges.Add(new DataChange
			{
				Key = DataChangeAreas.Settings, Version = Guid.NewGuid(), UpdatedUtc = DateTime.UtcNow,
			});
#pragma warning restore VSTHRD103
			racer.SaveChanges();
		});
		GlobalSettingStore raced = new(new InterceptedDbContextFactory(_connection, interceptor));

		await raced.UpsertAsync("ActiveSync:ReadOnly", "true", CancellationToken.None);

		Assert.Equal("true", await _settings.GetAsync("ActiveSync:ReadOnly", CancellationToken.None));
		await using SyncDbContext verify = _factory.CreateDbContext();
		Assert.Single(await verify.DataChanges.Where(c => c.Key == DataChangeAreas.Settings).ToListAsync());
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

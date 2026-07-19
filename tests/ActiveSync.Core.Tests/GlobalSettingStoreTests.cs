using ActiveSync.Core.Options;
using ActiveSync.Core.Settings;
using ActiveSync.Core.State;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace ActiveSync.Core.Tests;

/// <summary>
///   Database-backed global settings: store CRUD + change-stamp, the config provider / refresher
///   chain that lets a database row override appsettings in the bound <see cref="ActiveSyncOptions" />
///   (what IOptionsMonitor recomputes from), and the build-time loader's tolerance of a missing table.
/// </summary>
public sealed class GlobalSettingStoreTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly TestContextFactory _factory;
	private readonly GlobalSettingStore _store;

	public GlobalSettingStoreTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();
		_factory = new TestContextFactory(_connection);
		using SyncDbContext db = _factory.CreateDbContext();
		db.Database.EnsureCreated();
		_store = new GlobalSettingStore(_factory);
	}

	public void Dispose()
	{
		_connection.Dispose();
	}

	[Fact]
	public async Task Store_CrudAndStamp_RoundTrip()
	{
		Assert.Null(await _store.ReadStampAsync(CancellationToken.None));

		await _store.UpsertAsync("ActiveSync:ReadOnly", "true", CancellationToken.None);
		Guid? stamp1 = await _store.ReadStampAsync(CancellationToken.None);
		Assert.NotNull(stamp1);
		Assert.Equal("true", await _store.GetAsync("ActiveSync:ReadOnly", CancellationToken.None));

		// A second mutation bumps the stamp again.
		await _store.UpsertAsync("ActiveSync:Eas:DefaultWindowSize", "200", CancellationToken.None);
		Assert.NotEqual(stamp1, await _store.ReadStampAsync(CancellationToken.None));

		// Upsert overwrites in place (no duplicate row); the CLI writes canonical key casing.
		await _store.UpsertAsync("ActiveSync:ReadOnly", "false", CancellationToken.None);
		Assert.Equal("false", await _store.GetAsync("ActiveSync:ReadOnly", CancellationToken.None));

		List<(string Key, string Value, DateTime UpdatedUtc)> all = await _store.ListAsync(CancellationToken.None);
		Assert.Equal(["ActiveSync:Eas:DefaultWindowSize", "ActiveSync:ReadOnly"], all.Select(e => e.Key));

		Assert.True(await _store.DeleteAsync("ActiveSync:ReadOnly", CancellationToken.None));
		Assert.False(await _store.DeleteAsync("ActiveSync:ReadOnly", CancellationToken.None));
		Assert.Null(await _store.GetAsync("ActiveSync:ReadOnly", CancellationToken.None));
	}

	[Fact]
	public async Task DbSetting_OverridesConfig_AndFallsBackOnDelete()
	{
		// A file/env value the database will override, plus a POCO-default value (ReadOnly=false).
		DbSettingsConfigurationSource source = new();
		IConfigurationRoot config = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?> { ["ActiveSync:Eas:DefaultWindowSize"] = "50" })
			.Add(source)
			.Build();
		SettingsRefresher refresher = new(_store, source.Provider, ZeroIntervalMonitor());

		// Baseline: file value + POCO default, no database rows yet.
		await refresher.EnsureFreshAsync(true, CancellationToken.None);
		Assert.Equal(50, Bind(config).Eas.DefaultWindowSize);
		Assert.False(Bind(config).ReadOnly);

		// Database rows win: override the file value and set one absent from the file.
		await _store.UpsertAsync("ActiveSync:Eas:DefaultWindowSize", "200", CancellationToken.None);
		await _store.UpsertAsync("ActiveSync:ReadOnly", "true", CancellationToken.None);
		await refresher.EnsureFreshAsync(false, CancellationToken.None);
		Assert.Equal(200, Bind(config).Eas.DefaultWindowSize);
		Assert.True(Bind(config).ReadOnly);

		// Deleting the rows falls back to the file value, then the POCO default.
		await _store.DeleteAsync("ActiveSync:Eas:DefaultWindowSize", CancellationToken.None);
		await _store.DeleteAsync("ActiveSync:ReadOnly", CancellationToken.None);
		await refresher.EnsureFreshAsync(false, CancellationToken.None);
		Assert.Equal(50, Bind(config).Eas.DefaultWindowSize);
		Assert.False(Bind(config).ReadOnly);
	}

	[Fact]
	public async Task Refresher_NoOpOnUnchangedStamp_RaisesChangedOnChange()
	{
		DbSettingsConfigurationSource source = new();
		SettingsRefresher refresher = new(_store, source.Provider, ZeroIntervalMonitor());

		int changed = 0;
		refresher.Changed += () => changed++;

		// First call loads the (empty) snapshot once.
		await refresher.EnsureFreshAsync(true, CancellationToken.None);
		Assert.Equal(1, changed);

		// Unchanged stamp: no reload, no event.
		await refresher.EnsureFreshAsync(false, CancellationToken.None);
		Assert.Equal(1, changed);

		await _store.UpsertAsync("ActiveSync:ReadOnly", "true", CancellationToken.None);
		await refresher.EnsureFreshAsync(false, CancellationToken.None);
		Assert.Equal(2, changed);

		await refresher.EnsureFreshAsync(false, CancellationToken.None);
		Assert.Equal(2, changed);
	}

	[Fact]
	public void Loader_ToleratesMissingTable_ReturnsEmpty()
	{
		// A fresh in-memory database with no schema — the build-time loader must not throw.
		Dictionary<string, string?> loaded = DbSettingsLoader.TryLoad(
			new DatabaseOptions { Provider = "Sqlite", ConnectionString = "Data Source=:memory:" }, null);
		Assert.Empty(loaded);
	}

	private static ActiveSyncOptions Bind(IConfiguration config) =>
		config.GetSection("ActiveSync").Get<ActiveSyncOptions>() ?? new ActiveSyncOptions();

	private static IOptionsMonitor<ActiveSyncOptions> ZeroIntervalMonitor() =>
		new StubMonitor(new ActiveSyncOptions { Auth = new AuthOptions { UsersRefreshSeconds = 0 } });

	/// <summary>Fixed-value monitor — the refresher only reads Auth.UsersRefreshSeconds from it.</summary>
	private sealed class StubMonitor(ActiveSyncOptions value) : IOptionsMonitor<ActiveSyncOptions>
	{
		public ActiveSyncOptions CurrentValue => value;
		public ActiveSyncOptions Get(string? name) => value;
		public IDisposable? OnChange(Action<ActiveSyncOptions, string?> listener) => null;
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

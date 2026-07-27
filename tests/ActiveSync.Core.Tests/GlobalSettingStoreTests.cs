using ActiveSync.Core.Options;
using ActiveSync.Core.Settings;
using ActiveSync.Core.State;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
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

		// Upsert overwrites in place (no duplicate row).
		await _store.UpsertAsync("ActiveSync:ReadOnly", "false", CancellationToken.None);
		Assert.Equal("false", await _store.GetAsync("ActiveSync:ReadOnly", CancellationToken.None));

		// B7 (behaviour change): the stored Key is normalized to lowercase — a sargable equality
		// seek on the primary key needs the row itself normalized, not just the comparison — so a
		// caller that reads rows directly (rather than through Find's canonical casing) sees
		// lowercase. Case-insensitive lookup and single-row-per-key uniqueness are unaffected.
		List<(string Key, string Value, DateTime UpdatedUtc)> all = await _store.ListAsync(CancellationToken.None);
		Assert.Equal(["activesync:eas:defaultwindowsize", "activesync:readonly"], all.Select(e => e.Key));

		Assert.True(await _store.DeleteAsync("ActiveSync:ReadOnly", CancellationToken.None));
		Assert.False(await _store.DeleteAsync("ActiveSync:ReadOnly", CancellationToken.None));
		Assert.Null(await _store.GetAsync("ActiveSync:ReadOnly", CancellationToken.None));
	}

	[Fact]
	public async Task Upsert_IsCaseInsensitive_NoDuplicateRow()
	{
		// B2: the store matched the key case-SENSITIVELY in SQL but case-INsensitively in memory,
		// so an upsert under a different casing inserted a SECOND row; the loaded snapshot then
		// collapsed both with a nondeterministic winner across restarts.
		await _store.UpsertAsync("ActiveSync:ReadOnly", "true", CancellationToken.None);
		await _store.UpsertAsync("activesync:readonly", "false", CancellationToken.None);

		Assert.Single(await _store.ListAsync(CancellationToken.None));
		Assert.Equal("false", await _store.GetAsync("ActiveSync:ReadOnly", CancellationToken.None));

		Assert.True(await _store.DeleteAsync("ACTIVESYNC:READONLY", CancellationToken.None));
		Assert.Empty(await _store.ListAsync(CancellationToken.None));
	}

	// B7 — GlobalSetting.Key IS the primary key, and lookups matched it via
	// `s.Key.ToLower() == key.ToLower()`: a function on the indexed column defeats the PK index
	// (a full scan on every get/upsert/delete), and the PK itself stays case-SENSITIVE, so two rows
	// differing only in case are representable (two replicas racing UpsertAsync, a restored dump).
	// Normalizing the key on write, and matching the normalized value with a plain equality, fixes
	// both: the comparison becomes a sargable index seek, and every write always targets the exact
	// same row regardless of the casing presented.
	[Fact]
	public async Task Upsert_NormalizesKeyCasing_InTheStoredRow()
	{
		await _store.UpsertAsync("ActiveSync:ReadOnly", "true", CancellationToken.None);

		List<(string Key, string Value, DateTime UpdatedUtc)> rows = await _store.ListAsync(CancellationToken.None);
		Assert.Equal("activesync:readonly", Assert.Single(rows).Key);
	}

	[Fact]
	public async Task GetAsync_UsesASargableEqualityComparison_NoFunctionOnTheIndexedColumn()
	{
		List<string> commands = [];
		LoggingTestContextFactory loggingFactory = new(_connection, commands);
		GlobalSettingStore store = new(loggingFactory);
		await store.UpsertAsync("ActiveSync:ReadOnly", "true", CancellationToken.None);

		commands.Clear();
		await store.GetAsync("ActiveSync:ReadOnly", CancellationToken.None);

		string sql = string.Join("\n", commands);
		Assert.DoesNotContain("lower(", sql, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task DeleteAsync_UsesASargableEqualityComparison_NoFunctionOnTheIndexedColumn()
	{
		List<string> commands = [];
		LoggingTestContextFactory loggingFactory = new(_connection, commands);
		GlobalSettingStore store = new(loggingFactory);
		await store.UpsertAsync("ActiveSync:ReadOnly", "true", CancellationToken.None);

		commands.Clear();
		await store.DeleteAsync("ActiveSync:ReadOnly", CancellationToken.None);

		string sql = string.Join("\n", commands);
		Assert.DoesNotContain("lower(", sql, StringComparison.OrdinalIgnoreCase);
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
	public async Task NegativeRefreshInterval_StillPicksUpLaterChanges_NoLockout()
	{
		// B11: a negative cadence used to PERMANENTLY disable live refresh after the first load —
		// including the pickup of an operator setting it back — so recovery needed a restart. It is
		// now clamped to "every request", so a later change is still picked up.
		DbSettingsConfigurationSource source = new();
		SettingsRefresher refresher = new(_store, source.Provider,
			new StubMonitor(new ActiveSyncOptions { Auth = new AuthOptions { UsersRefreshSeconds = -1 } }));

		int changed = 0;
		refresher.Changed += () => changed++;

		await refresher.EnsureFreshAsync(true, CancellationToken.None); // initial load
		Assert.Equal(1, changed);

		await _store.UpsertAsync("ActiveSync:ReadOnly", "true", CancellationToken.None);
		await refresher.EnsureFreshAsync(false, CancellationToken.None); // NOT forced
		Assert.Equal(2, changed);
	}

	[Fact]
	public async Task Refresher_ReloadSubscriberThrows_RecordsStamp_AppliesData_AndKeepsGoing()
	{
		// B6: a downstream reload-token subscriber that throws (e.g. the account-snapshot rebuild)
		// escaped through SetData, so the refresher's own progress markers (_lastStamp/_hasLoaded)
		// were never set and Changed never fired — the same stamp was retried forever and mislogged
		// as a settings failure. The subscriber failure must be isolated: the data is applied, the
		// stamp is recorded, and the next poll is a no-op.
		DbSettingsConfigurationSource source = new();
		SettingsRefresher refresher = new(_store, source.Provider, ZeroIntervalMonitor());
		using IDisposable _ = Microsoft.Extensions.Primitives.ChangeToken.OnChange(
			source.Provider.GetReloadToken, () => throw new InvalidOperationException("boom"));

		await _store.UpsertAsync("ActiveSync:ReadOnly", "true", CancellationToken.None);
		int changed = 0;
		refresher.Changed += () => changed++;

		await refresher.EnsureFreshAsync(true, CancellationToken.None);

		// The data was applied despite the throwing subscriber, and Changed fired.
		Assert.True(source.Provider.TryGet("ActiveSync:ReadOnly", out string? value));
		Assert.Equal("true", value);
		Assert.Equal(1, changed);

		// The stamp was recorded, so the next poll is a no-op — not an endless failing retry.
		await refresher.EnsureFreshAsync(false, CancellationToken.None);
		Assert.Equal(1, changed);
	}

	[Fact]
	public async Task Refresher_ChangedSubscriberThrows_OthersStillRun_AndDoesNotSuppressALaterGenuineFailure()
	{
		// B11: `Changed?.Invoke()` sits inside the outer try/catch, so a throwing subscriber (1) is a
		// multicast Delegate.Invoke — it aborts every subscriber registered AFTER it, (2) is mislogged
		// as "Could not refresh database settings; keeping the current snapshot" even though the data
		// WAS already applied (the throw happens after SetData succeeded), and (3) skips the
		// `_refreshErrorLogged = false` reset that follows the if-block, so it silently suppresses the
		// warning for the NEXT genuinely different failure.
		DbSettingsConfigurationSource source = new();
		ToggleableFactory factory = new(_factory);
		GlobalSettingStore store = new(factory);
		CapturingLogger<SettingsRefresher> logger = new();
		SettingsRefresher refresher = new(store, source.Provider, ZeroIntervalMonitor(), logger);

		int laterSubscriberRuns = 0;
		refresher.Changed += () => throw new InvalidOperationException("boom from an unrelated subscriber");
		refresher.Changed += () => laterSubscriberRuns++;

		await store.UpsertAsync("ActiveSync:ReadOnly", "true", CancellationToken.None);
		await refresher.EnsureFreshAsync(true, CancellationToken.None); // the throwing-subscriber call

		Assert.Equal(1, laterSubscriberRuns); // the second subscriber must still run
		Assert.DoesNotContain(logger.Lines, // the throw must not surface as a refresh failure — data was applied
			l => l.Message.Contains("Could not refresh database settings"));

		// A genuinely different, later failure (a real DB outage) must still be reported — the
		// earlier subscriber throw must not have left _refreshErrorLogged stuck at true.
		factory.FailNext = true;
		await refresher.EnsureFreshAsync(true, CancellationToken.None);
		Assert.Contains(logger.Lines, l => l.Message.Contains("Could not refresh database settings"));
	}

	private sealed class CapturingLogger<T> : ILogger<T>
	{
		public List<(LogLevel Level, string Message)> Lines { get; } = [];
		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
		public bool IsEnabled(LogLevel logLevel) => true;

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
			Exception? exception, Func<TState, Exception?, string> formatter) =>
			Lines.Add((logLevel, formatter(state, exception)));
	}

	/// <summary>Wraps a real factory, throwing on demand to simulate a genuine DB outage.</summary>
	private sealed class ToggleableFactory(ISyncDbContextFactory inner) : ISyncDbContextFactory
	{
		public bool FailNext;

		public SyncDbContext CreateDbContext()
		{
			if (FailNext)
				throw new InvalidOperationException("simulated database outage");
			return inner.CreateDbContext();
		}
	}

	[Theory]
	[InlineData("ActiveSync:Database:ConnectionString")]
	[InlineData("ActiveSync:Encryption:Key")]
	[InlineData("ActiveSync:Plugins:Directory")]
	[InlineData("ActiveSync:UsersFile")]
	public async Task Upsert_RefusesHostControlledKeys_LastChokepoint(string key)
	{
		// B12: even bypassing the write surfaces, the store must never persist a bootstrap /
		// host-controlled key — a stored ConnectionString/Encryption row would be trusted next boot.
		await Assert.ThrowsAsync<InvalidOperationException>(
			() => _store.UpsertAsync(key, "attacker-value", CancellationToken.None));
		Assert.Null(await _store.GetAsync(key, CancellationToken.None));
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

	/// <summary>Captures the SQL EF actually sends, to prove a lookup is (or is not) sargable.</summary>
	private sealed class LoggingTestContextFactory(SqliteConnection connection, List<string> commands)
		: ISyncDbContextFactory
	{
		public SyncDbContext CreateDbContext()
		{
			DbContextOptions<SqliteSyncDbContext> options = new DbContextOptionsBuilder<SqliteSyncDbContext>()
				.UseSqlite(connection)
				.LogTo(commands.Add, [Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.CommandExecuted])
				.EnableSensitiveDataLogging()
				.Options;
			return new SqliteSyncDbContext(options);
		}
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

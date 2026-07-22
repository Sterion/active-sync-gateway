using ActiveSync.Core.Accounts;
using ActiveSync.Contracts;
using ActiveSync.Core.Administration;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using ActiveSync.Core.State;
using ActiveSync.Crypto;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Core.Tests;

/// <summary>
///   Database-declared accounts: store CRUD + stamp, and the resolver's merged snapshot
///   (DB entry replaces the whole config entry; stamp-driven live refresh).
/// </summary>
public sealed class AccountStoreTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly TestContextFactory _factory;
	private readonly AccountStore _store;

	public AccountStoreTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();
		_factory = new TestContextFactory(_connection);
		using SyncDbContext db = _factory.CreateDbContext();
		db.Database.EnsureCreated();
		_store = new AccountStore(_factory);
	}

	public void Dispose()
	{
		_connection.Dispose();
	}

	private static ActiveSyncOptions BaseOptions(double refreshSeconds = 0)
	{
		return new ActiveSyncOptions
		{
			Encryption = new EncryptionOptions { AllowPlaintext = true },
			Auth = new AuthOptions { UsersRefreshSeconds = refreshSeconds },
		};
	}

	private AccountResolver Resolver(ActiveSyncOptions options)
	{
		IConfigurationRoot config = new ConfigurationBuilder().AddInMemoryCollection(
			new Dictionary<string, string?>
			{
				["ActiveSync:Backends:MailStore:Provider"] = "imap",
				["ActiveSync:Backends:MailStore:Host"] = "imap.global",
				["ActiveSync:Backends:MailStore:Port"] = "143",
				["ActiveSync:Backends:MailSubmit:Provider"] = "smtp",
				["ActiveSync:Backends:MailSubmit:Host"] = "smtp.global",
			}).Build();
		BackendRolesProvider rolesProvider = new(config);
		BackendProviderRegistry registry = new(
		[
			new ActiveSync.Backends.Imap.ImapBackendProvider(
				TestOptionsMonitor.Of(new ActiveSyncOptions()), NullLoggerFactory.Instance),
			new ActiveSync.Backends.Smtp.SmtpBackendProvider(NullLoggerFactory.Instance),
			new ActiveSync.Backends.Local.LocalBackendProvider(null!, null!, null!)
		], NullLogger<BackendProviderRegistry>.Instance);
		return new AccountResolver(TestOptionsMonitor.Of(options), rolesProvider, registry, _store);
	}

	[Fact]
	public async Task DbEntry_ReplacesWholeConfigEntry_AndFallsBackOnDelete()
	{
		ActiveSyncOptions options = BaseOptions();
		options.Users = new Dictionary<string, AccountOptions>
		{
			["phone1"] = new()
			{
				MailAddress = "config@x",
				Backends = new Dictionary<string, BackendRoleOverride> { ["MailStore"] = new() { UserName = "config-imap-user" } },
			},
		};
		AccountResolver resolver = Resolver(options);
		await resolver.EnsureFreshAsync(true, CancellationToken.None);
		Assert.Equal("config@x", resolver.Resolve(new BackendCredentials("phone1", "pw")).MailAddress);

		// DB row for the same login: REPLACES the config entry wholesale — the config
		// MailAddress must not leak through the DB entry that doesn't set one.
		await _store.UpsertAsync("phone1",
			new AccountOptions { Backends = new Dictionary<string, BackendRoleOverride> { ["MailStore"] = new() { UserName = "db-imap-user" } } },
			CancellationToken.None);
		await resolver.EnsureFreshAsync(false, CancellationToken.None);

		ResolvedAccount fromDb = resolver.Resolve(new BackendCredentials("phone1", "pw"));
		Assert.Equal("db-imap-user", fromDb.Roles[BackendRole.MailStore].Credentials.UserName);
		Assert.Null(fromDb.MailAddress);
		Assert.True(resolver.MergedUsers["phone1"].FromDatabase);
		Assert.True(resolver.MergedUsers["phone1"].ShadowsConfig);

		// Deleting the row falls back to the config entry.
		Assert.True(await _store.DeleteAsync("phone1", CancellationToken.None));
		await resolver.EnsureFreshAsync(false, CancellationToken.None);
		ResolvedAccount fromConfig = resolver.Resolve(new BackendCredentials("phone1", "pw"));
		Assert.Equal("config-imap-user", fromConfig.Roles[BackendRole.MailStore].Credentials.UserName);
		Assert.Equal("config@x", fromConfig.MailAddress);
		Assert.False(resolver.MergedUsers["phone1"].FromDatabase);
	}

	[Fact]
	public async Task IsLoginDisabled_TracksTheEnabledFlag_CaseInsensitively()
	{
		AccountResolver resolver = Resolver(BaseOptions());
		await _store.UpsertAsync("off", new AccountOptions { Enabled = false }, CancellationToken.None);
		await _store.UpsertAsync("on", new AccountOptions(), CancellationToken.None);
		await resolver.EnsureFreshAsync(true, CancellationToken.None);

		Assert.True(resolver.IsLoginDisabled("off"));
		Assert.True(resolver.IsLoginDisabled("OFF"));
		Assert.False(resolver.IsLoginDisabled("on"));
		Assert.False(resolver.IsLoginDisabled("undeclared"));
	}

	[Fact]
	public async Task StampChange_TriggersRefresh_AndRaisesSnapshotChanged()
	{
		AccountResolver resolver = Resolver(BaseOptions());
		await resolver.EnsureFreshAsync(true, CancellationToken.None);
		int changedEvents = 0;
		resolver.SnapshotChanged += () => changedEvents++;

		// Unchanged stamp: refresh is a no-op.
		await resolver.EnsureFreshAsync(false, CancellationToken.None);
		Assert.Equal(0, changedEvents);

		await _store.UpsertAsync("newuser",
			new AccountOptions { Password = "topsecret" }, CancellationToken.None);
		await resolver.EnsureFreshAsync(false, CancellationToken.None);

		Assert.Equal(1, changedEvents);
		Assert.True(resolver.VerifyLocally("newuser", "topsecret"));
		Assert.False(resolver.VerifyLocally("newuser", "wrong"));

		// Second unchanged check: still one event.
		await resolver.EnsureFreshAsync(false, CancellationToken.None);
		Assert.Equal(1, changedEvents);
	}

	[Fact]
	public async Task MalformedRow_IsSkipped_OthersSurvive()
	{
		await _store.UpsertAsync("good", new AccountOptions { Password = "pw1" }, CancellationToken.None);
		await using (SyncDbContext db = _factory.CreateDbContext())
		{
			// DbSet.Add is synchronous and local (no I/O) — AddAsync exists only to support
			// async value generators (e.g. HiLo/Cosmos), which this project doesn't use.
#pragma warning disable VSTHRD103
			db.AccountEntries.Add(new AccountEntry
			{
				UserName = "broken", Json = "{not json", UpdatedUtc = DateTime.UtcNow,
			});
#pragma warning restore VSTHRD103
			await db.SaveChangesAsync();
		}

		AccountResolver resolver = Resolver(BaseOptions());
		await resolver.EnsureFreshAsync(true, CancellationToken.None);

		Assert.True(resolver.VerifyLocally("good", "pw1"));
		Assert.False(resolver.MergedUsers.ContainsKey("broken"));
	}

	[Fact]
	public async Task RequireDeclaredUsers_DbGrantAdmits_UndeclaredStaysRejected()
	{
		ActiveSyncOptions options = BaseOptions();
		options.RequireDeclaredUsers = true;
		AccountResolver resolver = Resolver(options);
		await resolver.EnsureFreshAsync(true, CancellationToken.None);

		// Nothing declared anywhere yet: everything is rejected locally.
		Assert.False(resolver.VerifyLocally("someone", "pw"));

		// An empty DB entry is a pure allowlist grant — auth still probes IMAP (null).
		await _store.UpsertAsync("someone", new AccountOptions(), CancellationToken.None);
		await resolver.EnsureFreshAsync(false, CancellationToken.None);
		Assert.Null(resolver.VerifyLocally("someone", "pw"));
		Assert.False(resolver.VerifyLocally("otherone", "pw"));
	}

	[Fact]
	public async Task InvalidDbEntry_IsSkipped_ConfigEntrySurvives()
	{
		ActiveSyncOptions options = BaseOptions();
		options.Users = new Dictionary<string, AccountOptions>
		{
			["phone1"] = new() { MailAddress = "config@x" },
		};

		// Out-of-range port makes the DB entry invalid — it must be skipped (lenient), and
		// the shadowed config entry stays active.
		await _store.UpsertAsync("phone1",
			new AccountOptions { Backends = new Dictionary<string, BackendRoleOverride> { ["MailStore"] = new() { Settings = new Dictionary<string, string?> { ["Port"] = "99999" } } } }, CancellationToken.None);

		AccountResolver resolver = Resolver(options);
		await resolver.EnsureFreshAsync(true, CancellationToken.None);

		Assert.Equal("config@x", resolver.Resolve(new BackendCredentials("phone1", "pw")).MailAddress);
		Assert.False(resolver.MergedUsers["phone1"].FromDatabase);
	}

	[Fact]
	public async Task LoadStartingEntry_FindsConfigUser_CaseInsensitively()
	{
		// B8: ActiveSyncOptions.Users is bound with the default ORDINAL comparer, so editing a
		// differently-cased login missed the config entry, started from an empty overlay, and —
		// since a DB row REPLACES the whole config entry — the write discarded every override.
		ActiveSyncOptions options = new()
		{
			// Ordinal comparer, exactly what ConfigurationBinder produces.
			Users = new Dictionary<string, AccountOptions> { ["phone1"] = new() { MailAddress = "config@x" } },
		};

		AccountOptions starting = await AccountEditing.LoadStartingEntryAsync(
			_store, options, "PHONE1", CancellationToken.None);
		Assert.Equal("config@x", starting.MailAddress);
	}

	[Fact]
	public async Task GetAndList_TolerateUnparseableRow_InsteadOfThrowing()
	{
		// B15: LoadAllAsync tolerated a bad row ("one bad row must never take auth down") but
		// GetAsync/ListAsync deserialized bare, so `eas user show`/`eas users`/the admin list
		// hard-failed with JsonException — the very tools for finding the bad row.
		await _store.UpsertAsync("good", new AccountOptions { MailAddress = "g@x" }, CancellationToken.None);
		await using (SyncDbContext db = _factory.CreateDbContext())
		{
#pragma warning disable VSTHRD103
			db.AccountEntries.Add(new AccountEntry
			{
				UserName = "broken", Json = "{not json", UpdatedUtc = DateTime.UtcNow,
			});
#pragma warning restore VSTHRD103
			await db.SaveChangesAsync();
		}

		// GetAsync: a bad row is tolerated (null), a good one still round-trips.
		Assert.Null(await _store.GetAsync("broken", CancellationToken.None));
		Assert.Equal("g@x", (await _store.GetAsync("good", CancellationToken.None))?.MailAddress);

		// ListAsync: the bad row is SURFACED (flagged invalid), never omitted or thrown on.
		var all = await _store.ListAsync(CancellationToken.None);
		Assert.Equal(["broken", "good"], all.Select(e => e.UserName));
		Assert.False(all.Single(e => e.UserName == "broken").Valid);
		Assert.True(all.Single(e => e.UserName == "good").Valid);
	}

	[Fact]
	public async Task Upsert_IsCaseInsensitive_NoDuplicateRow()
	{
		// B2: the store matched the login case-SENSITIVELY in SQL but case-INsensitively in memory,
		// so an upsert under a different casing inserted a SECOND row; LoadAllAsync then collapsed
		// both with a last-row-wins winner that flipped across restarts.
		await _store.UpsertAsync("phone1", new AccountOptions { MailAddress = "first@x" }, CancellationToken.None);
		await _store.UpsertAsync("PHONE1", new AccountOptions { MailAddress = "second@x" }, CancellationToken.None);

		Assert.Single(await _store.ListAsync(CancellationToken.None));
		Assert.Equal("second@x", (await _store.GetAsync("Phone1", CancellationToken.None))?.MailAddress);
		Assert.NotNull(await _store.GetAsync("phone1", CancellationToken.None));

		Assert.True(await _store.DeleteAsync("PHONE1", CancellationToken.None));
		Assert.Empty(await _store.ListAsync(CancellationToken.None));
	}

	[Fact]
	public async Task Store_ListAndGet_RoundTrip()
	{
		Assert.Null(await _store.ReadStampAsync(CancellationToken.None));
		await _store.UpsertAsync("a", new AccountOptions { MailAddress = "a@x" }, CancellationToken.None);
		Guid? stamp1 = await _store.ReadStampAsync(CancellationToken.None);
		Assert.NotNull(stamp1);

		await _store.UpsertAsync("b", new AccountOptions(), CancellationToken.None);
		Assert.NotEqual(stamp1, await _store.ReadStampAsync(CancellationToken.None));

		AccountOptions? a = await _store.GetAsync("a", CancellationToken.None);
		Assert.Equal("a@x", a?.MailAddress);
		Assert.Null(await _store.GetAsync("missing", CancellationToken.None));

		List<(string UserName, AccountOptions Options, DateTime UpdatedUtc, bool Valid)> all =
			await _store.ListAsync(CancellationToken.None);
		Assert.Equal(["a", "b"], all.Select(e => e.UserName));
		Assert.All(all, e => Assert.True(e.Valid));
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

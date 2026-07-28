using ActiveSync.Backends.Sieve;
using ActiveSync.Contracts;
using ActiveSync.Core.Accounts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using ActiveSync.Core.Settings;
using ActiveSync.Core.State;
using ActiveSync.Crypto;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Core.Tests;

/// <summary>
///   Item 4 of docs/design/db-restructure.md — THE RESOLUTION RULE, end to end:
///   <c>user (DB) → user (config) → global (DB) → global (config) → code default</c>, per FIELD.
///   Levels 3–5 are <c>IConfiguration</c>'s own layering (the database settings provider is
///   layered last), so these drive the whole chain through the resolver rather than testing the
///   merge helper in isolation.
/// </summary>
public sealed class UserFieldResolutionTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly TestContextFactory _factory;
	private readonly UserStore _store;

	public UserFieldResolutionTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();
		_factory = new TestContextFactory(_connection);
		using SyncDbContext db = _factory.CreateDbContext();
		db.Database.EnsureCreated();
		_store = new UserStore(_factory);
	}

	public void Dispose() => _connection.Dispose();

	private static ActiveSyncOptions Options() => new()
	{
		Encryption = new EncryptionOptions { AllowPlaintext = true },
		Auth = new AuthOptions { UsersRefreshSeconds = 0 },
	};

	/// <summary>The global role sections — resolution levels 3/4, layered by IConfiguration.</summary>
	private static IConfigurationRoot GlobalConfig(string mailHost = "imap.global") =>
		new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
		{
			["ActiveSync:Backends:MailStore:Provider"] = "imap",
			["ActiveSync:Backends:MailStore:Host"] = mailHost,
			["ActiveSync:Backends:MailStore:Port"] = "143",
			["ActiveSync:Backends:MailSubmit:Provider"] = "smtp",
			["ActiveSync:Backends:MailSubmit:Host"] = "smtp.global",
		}).Build();

	private UserResolver Resolver(ActiveSyncOptions options, IConfigurationRoot? config = null)
	{
		BackendProviderRegistry registry = new(
		[
			new ActiveSync.Backends.Imap.ImapBackendProvider(
				TestOptionsMonitor.Of(options), NullLoggerFactory.Instance),
			new ActiveSync.Backends.Smtp.SmtpBackendProvider(NullLoggerFactory.Instance),
			new ActiveSync.Backends.Local.LocalBackendProvider(null!, null!, null!, NullLoggerFactory.Instance)
		], NullLogger<BackendProviderRegistry>.Instance);
		return new UserResolver(
			TestOptionsMonitor.Of(options), new BackendRolesProvider(config ?? GlobalConfig()),
			registry, _store);
	}

	private static ProviderSettings MailSettings(ResolvedUser user) =>
		user.Roles[BackendRole.MailStore].Settings;

	[Fact]
	public async Task UserDatabase_BeatsUserConfig_PerField()
	{
		ActiveSyncOptions options = Options();
		options.Users = new Dictionary<string, UserOptions>
		{
			["anna"] = new()
			{
				MailAddress = "config@x",
				Admin = true,
				Backends = new Dictionary<string, BackendRoleOverride>
				{
					["MailStore"] = new()
					{
						UserName = "config-user",
						Settings = new Dictionary<string, string?> { ["Host"] = "config.example.com" },
					},
				},
			},
		};
		// The database deviates on SOME fields only.
		await _store.UpsertAsync("anna", new UserOptions
		{
			MailAddress = "db@x",
			Backends = new Dictionary<string, BackendRoleOverride>
			{
				["MailStore"] = new()
				{
					Settings = new Dictionary<string, string?> { ["Host"] = "db.example.com" },
				},
			},
		}, CancellationToken.None);

		UserResolver resolver = Resolver(options);
		await resolver.EnsureFreshAsync(true, CancellationToken.None);
		ResolvedUser resolved = resolver.Resolve(new BackendCredentials("anna", "pw"));

		Assert.Equal("db@x", resolved.MailAddress);                                   // DB wins
		Assert.Equal("db.example.com", MailSettings(resolved).Section["Host"]);       // DB wins
		Assert.Equal("config-user", resolved.Roles[BackendRole.MailStore].Credentials.UserName); // config survives
		Assert.True(resolver.MergedUsers["anna"].Options.Admin);                      // config survives
	}

	[Fact]
	public async Task ClearingAUserValue_RevertsToConfig_ThenToGlobal()
	{
		ActiveSyncOptions options = Options();
		options.Users = new Dictionary<string, UserOptions>
		{
			["anna"] = new()
			{
				Backends = new Dictionary<string, BackendRoleOverride>
				{
					["MailStore"] = new()
					{
						Settings = new Dictionary<string, string?> { ["Host"] = "config.example.com" },
					},
				},
			},
		};
		await _store.UpsertAsync("anna", new UserOptions
		{
			Backends = new Dictionary<string, BackendRoleOverride>
			{
				["MailStore"] = new()
				{
					Settings = new Dictionary<string, string?> { ["Host"] = "db.example.com" },
				},
			},
		}, CancellationToken.None);

		UserResolver resolver = Resolver(options);
		await resolver.EnsureFreshAsync(true, CancellationToken.None);
		Assert.Equal("db.example.com",
			MailSettings(resolver.Resolve(new BackendCredentials("anna", "pw"))).Section["Host"]);

		// Step 1: remove the DATABASE deviation → falls back to the config user value.
		await _store.DeleteAsync("anna", CancellationToken.None);
		await resolver.EnsureFreshAsync(true, CancellationToken.None);
		Assert.Equal("config.example.com",
			MailSettings(resolver.Resolve(new BackendCredentials("anna", "pw"))).Section["Host"]);

		// Step 2: remove the CONFIG deviation → falls back to the global role section.
		options.Users["anna"].Backends = null;
		UserResolver globalOnly = Resolver(options);
		await globalOnly.EnsureFreshAsync(true, CancellationToken.None);
		Assert.Equal("imap.global",
			MailSettings(globalOnly.Resolve(new BackendCredentials("anna", "pw"))).Section["Host"]);
	}

	[Fact]
	public async Task AGlobalChange_ReachesEveryUserThatDoesNotOverrideIt()
	{
		ActiveSyncOptions options = Options();
		options.Users = new Dictionary<string, UserOptions>
		{
			["inherits"] = new(),
			["overrides"] = new()
			{
				Backends = new Dictionary<string, BackendRoleOverride>
				{
					["MailStore"] = new()
					{
						Settings = new Dictionary<string, string?> { ["Host"] = "own.example.com" },
					},
				},
			},
		};

		// The global role section moves (an `eas config set Backends:MailStore:Host`, level 3).
		UserResolver resolver = Resolver(options, GlobalConfig("imap.moved"));
		await resolver.EnsureFreshAsync(true, CancellationToken.None);

		Assert.Equal("imap.moved",
			MailSettings(resolver.Resolve(new BackendCredentials("inherits", "pw"))).Section["Host"]);
		Assert.Equal("own.example.com",
			MailSettings(resolver.Resolve(new BackendCredentials("overrides", "pw"))).Section["Host"]);
		// A user with no declaration at all is pass-through and inherits it too.
		Assert.Equal("imap.moved",
			MailSettings(resolver.Resolve(new BackendCredentials("undeclared", "pw"))).Section["Host"]);
	}

	[Fact]
	public async Task LiveBackendsEditInvalidatingAConfigUser_DoesNotFreezeDatabaseUserPickup()
	{
		// B1: a config-declared user ("u") overrides the Oof role and inherits the global sieve
		// provider — valid at construction. A LIVE `ActiveSync:Backends` edit then removes the
		// global Oof role (an `eas config unset`), which BackendRolesProvider applies (Oof is
		// optional, so the edit is itself shape-valid) and propagates through OnRolesChanged —
		// already guarded (B6) — leaving u's Oof override unable to inherit a provider.
		//
		// The bug: EnsureFreshAsync's OWN BuildSnapshot call has no equivalent guard. The very next
		// completely unrelated database user pickup (an `eas user set bob`) re-runs BuildSnapshot
		// with the now-broken roles, which throws for "u" (config users are strict) — the exception
		// escapes to EnsureFreshAsync's outer catch, which never advances _lastStamp/_snapshot. So
		// "bob" never appears, and neither will any later database change: every subsequent poll
		// re-throws the same way, forever, until restart.
		BackendProviderRegistry registry = new(
		[
			new ActiveSync.Backends.Imap.ImapBackendProvider(
				TestOptionsMonitor.Of(new ActiveSyncOptions()), NullLoggerFactory.Instance),
			new ActiveSync.Backends.Smtp.SmtpBackendProvider(NullLoggerFactory.Instance),
			new ActiveSync.Backends.Local.LocalBackendProvider(null!, null!, null!, NullLoggerFactory.Instance),
			new SieveBackendProvider(NullLoggerFactory.Instance),
		], NullLogger<BackendProviderRegistry>.Instance);

		DbSettingsConfigurationSource dbSource = new();
		dbSource.Provider.SetData(new Dictionary<string, string?>
		{
			["ActiveSync:Backends:Oof:Provider"] = "sieve",
			["ActiveSync:Backends:Oof:Host"] = "sieve.global",
		});
		IConfigurationRoot root = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?>
			{
				["ActiveSync:Backends:MailStore:Provider"] = "imap",
				["ActiveSync:Backends:MailStore:Host"] = "imap.global",
				["ActiveSync:Backends:MailSubmit:Provider"] = "smtp",
				["ActiveSync:Backends:MailSubmit:Host"] = "smtp.global",
			})
			.Add(dbSource)
			.Build();
		BackendRolesProvider rolesProvider = new(root, registry);

		ActiveSyncOptions options = Options();
		options.Users = new Dictionary<string, UserOptions>
		{
			["u"] = new() { Backends = new Dictionary<string, BackendRoleOverride> { ["Oof"] = new() } },
		};
		UserResolver resolver = new(TestOptionsMonitor.Of(options), rolesProvider, registry, _store);
		Assert.Contains("u", resolver.MergedUsers.Keys);

		// Live edit: remove the global Oof role. u's override can no longer inherit a provider.
		dbSource.Provider.SetData(new Dictionary<string, string?>());

		// A wholly unrelated database change — must still reach every replica within
		// Auth:UsersRefreshSeconds (db-restructure.md invariant 4).
		await _store.UpsertAsync("bob", new UserOptions(), CancellationToken.None);
		await resolver.EnsureFreshAsync(true, CancellationToken.None);

		Assert.Contains("bob", resolver.MergedUsers.Keys); // database pickup must not freeze
		Assert.True(resolver.MergedUsers["u"].Invalid);    // the bad config user is refused, not fatal
	}

	[Fact]
	public async Task Settings_ResolvePerKey_NotAsAWholeDictionary()
	{
		ActiveSyncOptions options = Options();
		options.Users = new Dictionary<string, UserOptions>
		{
			["anna"] = new()
			{
				Backends = new Dictionary<string, BackendRoleOverride>
				{
					["MailStore"] = new()
					{
						Settings = new Dictionary<string, string?>
						{
							["Host"] = "config.example.com",
							["Port"] = "1143",
						},
					},
				},
			},
		};
		// The database sets only Port — Host must still come from the config level.
		await _store.UpsertAsync("anna", new UserOptions
		{
			Backends = new Dictionary<string, BackendRoleOverride>
			{
				["MailStore"] = new()
				{
					Settings = new Dictionary<string, string?> { ["Port"] = "2143" },
				},
			},
		}, CancellationToken.None);

		UserResolver resolver = Resolver(options);
		await resolver.EnsureFreshAsync(true, CancellationToken.None);
		ProviderSettings settings = MailSettings(resolver.Resolve(new BackendCredentials("anna", "pw")));

		Assert.Equal("config.example.com", settings.Section["Host"]);
		Assert.Equal("2143", settings.Section["Port"]);
	}

	[Fact]
	public void Settings_ListElementAtOneLevel_ReplacesTheWholeInheritedList()
	{
		// A list is addressed X:0, X:1, …; a level that sets ANY element replaces the whole list,
		// or a shorter list would silently inherit the longer one's trailing elements.
		UserOptions config = new()
		{
			Backends = new Dictionary<string, BackendRoleOverride>
			{
				["Calendar"] = new()
				{
					Settings = new Dictionary<string, string?>
					{
						["SharedCollections:0"] = "/cal/a/",
						["SharedCollections:1"] = "/cal/b/",
						["SharedCollections:2"] = "/cal/c/",
						["BaseUrl"] = "https://dav.example.com",
					},
				},
			},
		};
		UserOptions database = new()
		{
			Backends = new Dictionary<string, BackendRoleOverride>
			{
				["Calendar"] = new()
				{
					Settings = new Dictionary<string, string?> { ["SharedCollections:0"] = "/cal/only/" },
				},
			},
		};

		UserMerge.Merged merged = UserMerge.Merge(config, database);

		Dictionary<string, string?> settings = merged.Options.Backends!["Calendar"].Settings!;
		Assert.Equal("/cal/only/", settings["SharedCollections:0"]);
		Assert.False(settings.ContainsKey("SharedCollections:1")); // the whole list was replaced
		Assert.False(settings.ContainsKey("SharedCollections:2"));
		Assert.Equal("https://dav.example.com", settings["BaseUrl"]); // an unrelated key survives
	}

	[Fact]
	public void Settings_ANullAtTheDatabaseLevel_ClearsRatherThanFallingThrough()
	{
		// The pre-existing explicit-clear semantics must survive the extra level: a null VALUE is
		// a directive ("remove the inherited global key"), not an absence, so it wins over a
		// config value for the same key instead of falling through to it.
		UserOptions config = new()
		{
			Backends = new Dictionary<string, BackendRoleOverride>
			{
				["MailStore"] = new()
				{
					Settings = new Dictionary<string, string?> { ["Host"] = "config.example.com" },
				},
			},
		};
		UserOptions database = new()
		{
			Backends = new Dictionary<string, BackendRoleOverride>
			{
				["MailStore"] = new()
				{
					Settings = new Dictionary<string, string?> { ["Host"] = null },
				},
			},
		};

		UserMerge.Merged merged = UserMerge.Merge(config, database);

		Dictionary<string, string?> settings = merged.Options.Backends!["MailStore"].Settings!;
		Assert.True(settings.ContainsKey("Host"));  // the directive is CARRIED, not dropped
		Assert.Null(settings["Host"]);              // ...and it still means "clear"
		Assert.Equal(UserFieldSource.UserDatabase, merged.Sources["Backends:MailStore:Settings:Host"]);
	}

	[Fact]
	public void Merge_TracksTheSourceOfEveryFieldItResolved()
	{
		UserOptions config = new() { MailAddress = "config@x", Admin = true };
		UserOptions database = new() { MailAddress = "db@x", Enabled = false };

		UserMerge.Merged merged = UserMerge.Merge(config, database);

		Assert.Equal(UserFieldSource.UserDatabase, merged.Sources["MailAddress"]);
		Assert.Equal(UserFieldSource.UserConfig, merged.Sources["Admin"]);
		Assert.Equal(UserFieldSource.UserDatabase, merged.Sources["Enabled"]);
		Assert.False(merged.Sources.ContainsKey("Password")); // nothing set it at either level
	}

	[Fact]
	public void Merge_WithOnlyOneLevelPresent_IsThatLevel()
	{
		UserMerge.Merged configOnly = UserMerge.Merge(new UserOptions { MailAddress = "c@x" }, null);
		Assert.Equal("c@x", configOnly.Options.MailAddress);
		Assert.Equal(UserFieldSource.UserConfig, configOnly.Sources["MailAddress"]);

		UserMerge.Merged dbOnly = UserMerge.Merge(null, new UserOptions { MailAddress = "d@x" });
		Assert.Equal("d@x", dbOnly.Options.MailAddress);
		Assert.Equal(UserFieldSource.UserDatabase, dbOnly.Sources["MailAddress"]);

		UserMerge.Merged neither = UserMerge.Merge(null, null);
		Assert.Null(neither.Options.MailAddress);
		Assert.Empty(neither.Sources);
	}

	[Fact]
	public async Task ARoleDeclaredOnlyInTheDatabase_JoinsTheConfigDeclaredOnes()
	{
		// Roles are unioned, not replaced: declaring Calendar in the database must not drop a
		// MailStore override the config set.
		ActiveSyncOptions options = Options();
		options.Users = new Dictionary<string, UserOptions>
		{
			["anna"] = new()
			{
				Backends = new Dictionary<string, BackendRoleOverride>
				{
					["MailStore"] = new() { UserName = "config-imap" },
				},
			},
		};
		await _store.UpsertAsync("anna", new UserOptions
		{
			Backends = new Dictionary<string, BackendRoleOverride>
			{
				["MailSubmit"] = new() { UserName = "db-smtp" },
			},
		}, CancellationToken.None);

		UserResolver resolver = Resolver(options);
		await resolver.EnsureFreshAsync(true, CancellationToken.None);
		ResolvedUser resolved = resolver.Resolve(new BackendCredentials("anna", "pw"));

		Assert.Equal("config-imap", resolved.Roles[BackendRole.MailStore].Credentials.UserName);
		Assert.Equal("db-smtp", resolved.Roles[BackendRole.MailSubmit].Credentials.UserName);
	}

	[Fact]
	public async Task SnapshotChangedSubscriberThrows_OthersStillRun_AndDoesNotSuppressALaterGenuineFailure()
	{
		// B11: `SnapshotChanged?.Invoke()` sits inside the outer try/catch (UserResolver.cs), same
		// shape as SettingsRefresher.Changed. A throwing subscriber (1) is a multicast Delegate.Invoke
		// — it aborts every subscriber registered after it (e.g. BackendSessionFactory's auth-cache
		// clear), (2) is mislogged as "Could not refresh database accounts; keeping the current
		// snapshot" even though the rebuild WAS already applied, and (3) skips the
		// `_refreshErrorLogged = false` reset, permanently suppressing the warning for the next
		// GENUINE failure.
		ToggleableFactory factory = new(_factory);
		UserStore store = new(factory);
		CapturingLogger<UserResolver> logger = new();
		BackendProviderRegistry registry = new(
		[
			new ActiveSync.Backends.Imap.ImapBackendProvider(
				TestOptionsMonitor.Of(new ActiveSyncOptions()), NullLoggerFactory.Instance),
			new ActiveSync.Backends.Smtp.SmtpBackendProvider(NullLoggerFactory.Instance),
			new ActiveSync.Backends.Local.LocalBackendProvider(null!, null!, null!, NullLoggerFactory.Instance),
		], NullLogger<BackendProviderRegistry>.Instance);
		UserResolver resolver = new(
			TestOptionsMonitor.Of(Options()), new BackendRolesProvider(GlobalConfig()), registry, store, logger);

		int laterSubscriberRuns = 0;
		resolver.SnapshotChanged += () => throw new InvalidOperationException("boom from an unrelated subscriber");
		resolver.SnapshotChanged += () => laterSubscriberRuns++;

		await store.UpsertAsync("anna", new UserOptions(), CancellationToken.None);
		await resolver.EnsureFreshAsync(true, CancellationToken.None); // the throwing-subscriber call

		Assert.Equal(1, laterSubscriberRuns); // the second subscriber must still run
		Assert.DoesNotContain(logger.Lines, // the throw must not surface as a refresh failure — data was applied
			l => l.Message.Contains("Could not refresh database accounts"));

		// A genuinely different, later failure (a real DB outage) must still be reported — the
		// earlier subscriber throw must not have left _refreshErrorLogged stuck at true.
		factory.FailNext = true;
		await resolver.EnsureFreshAsync(true, CancellationToken.None);
		Assert.Contains(logger.Lines, l => l.Message.Contains("Could not refresh database accounts"));
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

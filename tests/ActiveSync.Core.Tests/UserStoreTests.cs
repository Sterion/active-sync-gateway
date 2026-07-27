using ActiveSync.Core.Accounts;
using ActiveSync.Contracts;
using ActiveSync.Core.Administration;
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
///   Database-declared accounts: store CRUD + stamp, and the resolver's merged snapshot
///   (DB entry replaces the whole config entry; stamp-driven live refresh).
/// </summary>
public sealed class UserStoreTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly TestContextFactory _factory;
	private readonly UserStore _store;

	public UserStoreTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();
		_factory = new TestContextFactory(_connection);
		using SyncDbContext db = _factory.CreateDbContext();
		db.Database.EnsureCreated();
		_store = new UserStore(_factory);
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

	private UserResolver Resolver(ActiveSyncOptions options)
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
		return new UserResolver(TestOptionsMonitor.Of(options), rolesProvider, registry, _store);
	}

	[Fact]
	public async Task DbEntry_OverridesConfigPerField_AndClearingRevertsToConfig()
	{
		// Item 4 (behaviour change): a database declaration is a per-field DEVIATION, not a
		// wholesale substitution. Previously setting ONE database field silently discarded every
		// config-set field for that login.
		ActiveSyncOptions options = BaseOptions();
		options.Users = new Dictionary<string, UserOptions>
		{
			["phone1"] = new()
			{
				MailAddress = "config@x",
				Backends = new Dictionary<string, BackendRoleOverride> { ["MailStore"] = new() { UserName = "config-imap-user" } },
			},
		};
		UserResolver resolver = Resolver(options);
		await resolver.EnsureFreshAsync(true, CancellationToken.None);
		Assert.Equal("config@x", resolver.Resolve(new BackendCredentials("phone1", "pw")).MailAddress);

		// The database sets ONLY the backend user name. The config MailAddress must SURVIVE.
		await _store.UpsertAsync("phone1",
			new UserOptions { Backends = new Dictionary<string, BackendRoleOverride> { ["MailStore"] = new() { UserName = "db-imap-user" } } },
			CancellationToken.None);
		await resolver.EnsureFreshAsync(false, CancellationToken.None);

		ResolvedUser fromDb = resolver.Resolve(new BackendCredentials("phone1", "pw"));
		Assert.Equal("db-imap-user", fromDb.Roles[BackendRole.MailStore].Credentials.UserName);
		Assert.Equal("config@x", fromDb.MailAddress);   // config field survives the DB override
		MergedUser merged = resolver.MergedUsers["phone1"];
		Assert.True(merged.FromDatabase);
		Assert.True(merged.ShadowsConfig);
		// ...and each field reports the level it actually came from.
		Assert.Equal(UserFieldSource.UserDatabase, merged.SourceOf("Backends:MailStore:UserName"));
		Assert.Equal(UserFieldSource.UserConfig, merged.SourceOf("MailAddress"));

		// Clearing the database deviation reverts that field to config — not to nothing.
		Assert.True(await _store.DeleteAsync("phone1", CancellationToken.None));
		await resolver.EnsureFreshAsync(false, CancellationToken.None);
		ResolvedUser fromConfig = resolver.Resolve(new BackendCredentials("phone1", "pw"));
		Assert.Equal("config-imap-user", fromConfig.Roles[BackendRole.MailStore].Credentials.UserName);
		Assert.Equal("config@x", fromConfig.MailAddress);
		Assert.False(resolver.MergedUsers["phone1"].FromDatabase);
	}

	[Fact]
	public async Task IsLoginDisabled_TracksTheEnabledFlag_CaseInsensitively()
	{
		UserResolver resolver = Resolver(BaseOptions());
		await _store.UpsertAsync("off", new UserOptions { Enabled = false }, CancellationToken.None);
		await _store.UpsertAsync("on", new UserOptions(), CancellationToken.None);
		await resolver.EnsureFreshAsync(true, CancellationToken.None);

		Assert.True(resolver.IsLoginDisabled("off"));
		Assert.True(resolver.IsLoginDisabled("OFF"));
		Assert.False(resolver.IsLoginDisabled("on"));
		Assert.False(resolver.IsLoginDisabled("undeclared"));
	}

	[Fact]
	public async Task StampChange_TriggersRefresh_AndRaisesSnapshotChanged()
	{
		UserResolver resolver = Resolver(BaseOptions());
		await resolver.EnsureFreshAsync(true, CancellationToken.None);
		int changedEvents = 0;
		resolver.SnapshotChanged += () => changedEvents++;

		// Unchanged stamp: refresh is a no-op.
		await resolver.EnsureFreshAsync(false, CancellationToken.None);
		Assert.Equal(0, changedEvents);

		await _store.UpsertAsync("newuser",
			new UserOptions { Password = "topsecret" }, CancellationToken.None);
		await resolver.EnsureFreshAsync(false, CancellationToken.None);

		Assert.Equal(1, changedEvents);
		Assert.True(resolver.VerifyLocally("newuser", "topsecret"));
		Assert.False(resolver.VerifyLocally("newuser", "wrong"));

		// Second unchanged check: still one event.
		await resolver.EnsureFreshAsync(false, CancellationToken.None);
		Assert.Equal(1, changedEvents);
	}

	[Fact]
	public async Task MalformedSettingsBlob_OnlyDropsThatRolesSettings_UserSurvives()
	{
		// Normalising the shape (item 3) NARROWS the corrupt-row guard: every scalar is now a
		// typed column, so the only thing that can fail to parse is one role's Settings blob.
		// That role's settings are dropped with a warning; the user's typed columns — including
		// the gateway password auth depends on — still apply.
		await _store.UpsertAsync("good", new UserOptions { Password = "pw1" }, CancellationToken.None);
		await _store.UpsertAsync("partly", new UserOptions
		{
			Password = "pw2",
			Backends = new Dictionary<string, BackendRoleOverride> { ["MailStore"] = new() { UserName = "keep-me" } },
		}, CancellationToken.None);
		await using (SyncDbContext db = _factory.CreateDbContext())
		{
			UserBackendRole role = await db.UserBackendRoles.SingleAsync(r => r.Role == "MailStore");
			role.SettingsJson = "{not json";
			await db.SaveChangesAsync();
		}

		UserResolver resolver = Resolver(BaseOptions());
		await resolver.EnsureFreshAsync(true, CancellationToken.None);

		Assert.True(resolver.VerifyLocally("good", "pw1"));
		// The corrupt blob did NOT take the user down: they are still declared, still
		// authenticate locally, and the role's typed columns survived.
		Assert.True(resolver.VerifyLocally("partly", "pw2"));
		UserOptions? reread = await _store.GetAsync("partly", CancellationToken.None);
		Assert.Equal("keep-me", reread!.Backends!["MailStore"].UserName);
		Assert.Null(reread.Backends["MailStore"].Settings);
	}

	[Fact]
	public async Task AutoProvisionOff_DbGrantAdmits_UndeclaredStaysRejected()
	{
		// AutoProvisionUsers=false absorbed the deleted RequireDeclaredUsers allowlist.
		ActiveSyncOptions options = BaseOptions();
		options.AutoProvisionUsers = false;
		UserResolver resolver = Resolver(options);
		await resolver.EnsureFreshAsync(true, CancellationToken.None);

		// Nothing declared anywhere yet: everything is rejected locally.
		Assert.False(resolver.VerifyLocally("someone", "pw"));

		// An empty DB entry is a pure allowlist grant — auth still probes IMAP (null).
		await _store.UpsertAsync("someone", new UserOptions(), CancellationToken.None);
		await resolver.EnsureFreshAsync(false, CancellationToken.None);
		Assert.Null(resolver.VerifyLocally("someone", "pw"));
		Assert.False(resolver.VerifyLocally("otherone", "pw"));
	}

	[Fact]
	public async Task InvalidDbEntry_FailsClosed_DoesNotFallBackToShadowedConfig()
	{
		// B3 (behaviour change): a DB row REPLACES the whole config entry, so an invalid row must NOT
		// silently fall back to the shadowed config identity. Previously it was skipped and the config
		// entry stayed active; now the invalid row wins (replace semantics) and fails closed — the
		// login is refused and surfaced as invalid until the row is corrected or removed.
		ActiveSyncOptions options = BaseOptions();
		options.Users = new Dictionary<string, UserOptions>
		{
			["phone1"] = new() { MailAddress = "config@x" },
		};

		// Out-of-range port makes the DB entry invalid.
		await _store.UpsertAsync("phone1",
			new UserOptions { Backends = new Dictionary<string, BackendRoleOverride> { ["MailStore"] = new() { Settings = new Dictionary<string, string?> { ["Port"] = "99999" } } } }, CancellationToken.None);

		UserResolver resolver = Resolver(options);
		await resolver.EnsureFreshAsync(true, CancellationToken.None);

		MergedUser merged = resolver.MergedUsers["phone1"];
		Assert.True(merged.Invalid);
		Assert.True(merged.FromDatabase);
		Assert.True(merged.ShadowsConfig);
		// Fails closed: no local auth (no pass-through probe) and no resolution — the config
		// MailAddress must NOT leak through the invalid row.
		Assert.False(resolver.VerifyLocally("phone1", "pw"));
		Assert.Throws<InvalidOperationException>(
			() => resolver.Resolve(new BackendCredentials("phone1", "pw")));
	}

	[Fact]
	public async Task InvalidDbEntry_FailsClosed_DoesNotDegradeToPassThrough_OrUnDisable()
	{
		// B3: a DB row that fails validation (e.g. a live backend edit invalidated a previously-good
		// row) used to be SKIPPED — leaving no entry, so Resolve degraded to pass-through (presented
		// credentials forwarded verbatim to every role) and IsLoginDisabled returned false, which
		// UN-disabled a disabled account. It must instead honour Enabled==false and fail closed.
		UserResolver resolver = Resolver(BaseOptions());
		await _store.UpsertAsync("phone1", new UserOptions
		{
			Enabled = false,
			Backends = new Dictionary<string, BackendRoleOverride>
				{ ["MailStore"] = new() { Settings = new Dictionary<string, string?> { ["Port"] = "99999" } } },
		}, CancellationToken.None);
		await resolver.EnsureFreshAsync(true, CancellationToken.None);

		// Disabled stays disabled (honoured before validation) and the row is surfaced as invalid.
		Assert.True(resolver.IsLoginDisabled("phone1"));
		Assert.True(resolver.MergedUsers["phone1"].Invalid);
		Assert.True(resolver.MergedUsers["phone1"].FromDatabase);
		// Never authenticates locally (no pass-through) and never resolves.
		Assert.False(resolver.VerifyLocally("phone1", "pw"));
		Assert.Throws<InvalidOperationException>(
			() => resolver.Resolve(new BackendCredentials("phone1", "pw")));
	}

	[Fact]
	public async Task LoadStartingEntry_StartsFromTheDatabaseAlone_NeverCopyingConfig()
	{
		// Item 6: an edit must record only DEVIATIONS. Starting from a copy of the config entry
		// (which is what a whole-entry-replacement world needed) would freeze every config value
		// as a database override, so a later configuration change would stop reaching the user.
		ActiveSyncOptions options = new()
		{
			// Ordinal comparer, exactly what ConfigurationBinder produces.
			Users = new Dictionary<string, UserOptions> { ["phone1"] = new() { MailAddress = "config@x" } },
		};

		UserOptions fresh = await UserEditing.LoadStartingEntryAsync(
			_store, options, "PHONE1", CancellationToken.None);
		Assert.Null(fresh.MailAddress);

		// An existing database declaration IS the starting point, matched case-insensitively (B8).
		await _store.UpsertAsync("phone1", new UserOptions { Admin = true }, CancellationToken.None);
		UserOptions existing = await UserEditing.LoadStartingEntryAsync(
			_store, options, "PHONE1", CancellationToken.None);
		Assert.True(existing.Admin);
		Assert.Null(existing.MailAddress);   // still config's to supply
	}

	[Fact]
	public async Task GetAndList_TolerateAnUnparseableSettingsBlob_InsteadOfThrowing()
	{
		// B15: every read path must tolerate the one remaining blob — `eas user show`/`eas users`/
		// the admin list are the very tools for finding the bad row, so they must render it
		// FLAGGED rather than hard-failing with JsonException.
		await _store.UpsertAsync("good", new UserOptions { MailAddress = "g@x" }, CancellationToken.None);
		await _store.UpsertAsync("broken", new UserOptions
		{
			MailAddress = "b@x",
			Backends = new Dictionary<string, BackendRoleOverride> { ["Calendar"] = new() { Provider = "caldav" } },
		}, CancellationToken.None);
		await using (SyncDbContext db = _factory.CreateDbContext())
		{
			UserBackendRole role = await db.UserBackendRoles.SingleAsync(r => r.Role == "Calendar");
			role.SettingsJson = "{not json";
			await db.SaveChangesAsync();
		}

		// GetAsync: the typed columns still round-trip; only the unparseable settings are dropped.
		UserOptions? broken = await _store.GetAsync("broken", CancellationToken.None);
		Assert.Equal("b@x", broken!.MailAddress);
		Assert.Equal("caldav", broken.Backends!["Calendar"].Provider);
		Assert.Equal("g@x", (await _store.GetAsync("good", CancellationToken.None))?.MailAddress);

		// ListAsync: the bad row is SURFACED (flagged invalid), never omitted or thrown on.
		var all = await _store.ListAsync(CancellationToken.None);
		Assert.Equal(["broken", "good"], all.Select(e => e.Login));
		Assert.False(all.Single(e => e.Login == "broken").Valid);
		Assert.True(all.Single(e => e.Login == "good").Valid);
	}

	[Fact]
	public async Task Upsert_IsCaseInsensitive_NoDuplicateRow()
	{
		// B2: the store matched the login case-SENSITIVELY in SQL but case-INsensitively in memory,
		// so an upsert under a different casing inserted a SECOND row; LoadAllAsync then collapsed
		// both with a last-row-wins winner that flipped across restarts.
		await _store.UpsertAsync("phone1", new UserOptions { MailAddress = "first@x" }, CancellationToken.None);
		await _store.UpsertAsync("PHONE1", new UserOptions { MailAddress = "second@x" }, CancellationToken.None);

		Assert.Single(await _store.ListAsync(CancellationToken.None));
		Assert.Equal("second@x", (await _store.GetAsync("Phone1", CancellationToken.None))?.MailAddress);
		Assert.NotNull(await _store.GetAsync("phone1", CancellationToken.None));

		Assert.True(await _store.DeleteAsync("PHONE1", CancellationToken.None));
		Assert.Empty(await _store.ListAsync(CancellationToken.None));
	}

	[Fact]
	public async Task Upsert_IsWhitespaceInsensitive_NoDuplicateRow()
	{
		// B13: NormalizeLogin case-folded but did not trim, so an upsert under a whitespace-padded
		// login (Basic auth delivers it verbatim) inserted a SECOND row rather than matching the
		// existing one — mirroring the B2 case-folding bug this normalization already fixed.
		await _store.UpsertAsync("phone1", new UserOptions { MailAddress = "first@x" }, CancellationToken.None);
		await _store.UpsertAsync(" phone1 ", new UserOptions { MailAddress = "second@x" }, CancellationToken.None);

		Assert.Single(await _store.ListAsync(CancellationToken.None));
		Assert.Equal("second@x", (await _store.GetAsync("phone1", CancellationToken.None))?.MailAddress);
		Assert.NotNull(await _store.GetAsync(" phone1", CancellationToken.None));
	}

	[Fact]
	public async Task Store_ListAndGet_RoundTrip()
	{
		Assert.Null(await _store.ReadStampAsync(CancellationToken.None));
		await _store.UpsertAsync("a", new UserOptions { MailAddress = "a@x" }, CancellationToken.None);
		Guid? stamp1 = await _store.ReadStampAsync(CancellationToken.None);
		Assert.NotNull(stamp1);

		await _store.UpsertAsync("b", new UserOptions(), CancellationToken.None);
		Assert.NotEqual(stamp1, await _store.ReadStampAsync(CancellationToken.None));

		UserOptions? a = await _store.GetAsync("a", CancellationToken.None);
		Assert.Equal("a@x", a?.MailAddress);
		Assert.Null(await _store.GetAsync("missing", CancellationToken.None));

		List<(string Login, UserOptions Options, DateTime UpdatedUtc, bool Valid)> all =
			await _store.ListAsync(CancellationToken.None);
		Assert.Equal(["a", "b"], all.Select(e => e.Login));
		Assert.All(all, e => Assert.True(e.Valid));
	}

	[Fact]
	public async Task Upsert_NormalizesStoredCasing_SoIndexAndLookupAgree()
	{
		// B1/B8: the stored Login must be canonical (lowercase) so the raw unique index enforces
		// case-folded uniqueness and lookups are exact (index seek), not a non-sargable LOWER() scan
		// that leaves the case-variant pair reachable. Round 1's fix only added LOWER() to the read
		// predicates; it never normalized the stored value.
		await _store.UpsertAsync("PHONE1", new UserOptions { MailAddress = "x@x" }, CancellationToken.None);

		await using SyncDbContext db = _factory.CreateDbContext();
		User row = await db.Users.AsNoTracking().SingleAsync();
		Assert.Equal("phone1", row.Login);
	}

	[Fact]
	public async Task LoadAll_WarnsOnCaseVariantDuplicate_InsteadOfSilentlyDropping()
	{
		// B1: two case-only-variant rows can coexist under the raw unique index (a pre-fix pair, a
		// restored dump, or a direct DB write). LoadAllAsync collapsed them into an OrdinalIgnoreCase
		// dictionary, silently discarding one user's entire override set. The collapse must be SURFACED.
		await using (SyncDbContext db = _factory.CreateDbContext())
		{
#pragma warning disable VSTHRD103
			db.Users.Add(new User { Login = "phone1", Declared = true, UpdatedUtc = DateTime.UtcNow });
			db.Users.Add(new User { Login = "Phone1", Declared = true, UpdatedUtc = DateTime.UtcNow });
#pragma warning restore VSTHRD103
			await db.SaveChangesAsync();
		}

		CapturingLogger logger = new();
		await _store.LoadAllAsync(logger, CancellationToken.None);

		Assert.Contains(logger.Lines, l => l.Level == LogLevel.Warning);
	}

	// (The NormalizeAccountUserNameCasing data-migration test is gone with the migration chain:
	// the schema reinit replaced the chain with a fresh Initial pair, and the store has always
	// written the login case-folded since B1/B8 — covered by Upsert_NormalizesStoredCasing.)

	[Fact]
	public async Task ConcurrentRoleChange_DuringAccountRefresh_IsNotLostToTheStaleRefreshFinishingLast()
	{
		// B4: EnsureFreshAsync (account refresh — captures _rolesProvider.Current at BuildSnapshot
		// time) and OnRolesChanged (a live "Backends" edit, on the config-reload thread) swap
		// _snapshot with no shared lock. If a role change lands WHILE an account refresh's
		// BuildSnapshot is still running against the OLD roles, the refresh can finish (and swap)
		// LAST, overwriting the role-aware snapshot OnRolesChanged just installed.
		//
		// Reproduced with a provider whose ValidateConfiguration blocks (once) until released,
		// assigned to the Notes role — every declared user validates it, so this opens a real
		// window between "roles captured" and "swap" inside BuildSnapshot, the same shape a slow
		// CA read or a plugin's own validation would create in production.
		GateProvider slow = new();
		BackendProviderRegistry registry = new(
		[
			new ActiveSync.Backends.Imap.ImapBackendProvider(
				TestOptionsMonitor.Of(new ActiveSyncOptions()), NullLoggerFactory.Instance),
			new ActiveSync.Backends.Smtp.SmtpBackendProvider(NullLoggerFactory.Instance),
			// Calendar/Tasks/Contacts auto-fallback to "local" (no explicit assignment below) — must
			// be registered so their validation doesn't add unrelated noise to `failures`.
			new ActiveSync.Backends.Local.LocalBackendProvider(null!, null!, null!),
			slow,
		], NullLogger<BackendProviderRegistry>.Instance);

		DbSettingsConfigurationSource dbSource = new();
		dbSource.Provider.SetData(new Dictionary<string, string?>
		{
			["ActiveSync:Backends:Notes:Provider"] = "slow",
			["ActiveSync:Backends:Notes:Tag"] = "v1",
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
		// No registry here: this BackendRolesProvider's OWN live-edit provider validation (B14) must
		// stay OUT of the gate — only UserResolver's BuildSnapshot should hit the slow provider.
		BackendRolesProvider rolesProvider = new(root);

		// No config Users yet: the CONSTRUCTOR itself calls BuildSnapshot synchronously (with
		// dbUsers null too), and that would hit the slow gate on the calling thread before the test
		// even starts the refresh — declare "u" only AFTER construction (TestOptionsMonitor.Of
		// exposes the live, mutable ActiveSyncOptions instance, so this is picked up by the refresh).
		ActiveSyncOptions options = new() { Encryption = new EncryptionOptions { AllowPlaintext = true } };
		UserResolver resolver = new(TestOptionsMonitor.Of(options), rolesProvider, registry, _store);
		options.Users = new Dictionary<string, UserOptions> { ["u"] = new() };

		// A DB stamp move so EnsureFreshAsync actually rebuilds (rather than no-op on Store is null).
		await _store.UpsertAsync("dbuser", new UserOptions(), CancellationToken.None);

		// Kick off the account refresh; it captures roles v1 and then blocks INSIDE BuildSnapshot
		// (validating the Notes role) until released.
		Task refreshTask = Task.Run(() => resolver.EnsureFreshAsync(true, CancellationToken.None));
		Assert.True(slow.Entered.Wait(TimeSpan.FromSeconds(5)), "refresh did not reach the slow validator");

		// While the refresh is still mid-build against roles v1, land a live role change to v2 — on
		// a background task too, so this test cannot deadlock against a fix that serializes the two
		// swaps under one lock (the role-change build would then block waiting for that lock).
		Task roleChangeTask = Task.Run(() => dbSource.Provider.SetData(new Dictionary<string, string?>
		{
			["ActiveSync:Backends:Notes:Provider"] = "slow",
			["ActiveSync:Backends:Notes:Tag"] = "v2",
		}));

		// Give the role change a real chance to run to completion BEFORE releasing the blocked
		// refresh. It does no I/O and its own ValidateConfiguration call doesn't block (the gate
		// only blocks the FIRST ever call, already consumed by the refresh) — on UNFIXED code this
		// reliably finishes well within the margin. On FIXED code it instead blocks acquiring the
		// same lock the refresh holds, so this wait is expected to time out — that's fine, it isn't
		// what makes the test deterministic; releasing the refresh below is what unblocks it either
		// way, and the ASSERTION is what actually proves which snapshot survived.
		await Task.WhenAny(roleChangeTask, Task.Delay(TimeSpan.FromSeconds(2)));

		// Let the blocked refresh finish; on UNFIXED code it swaps in its stale, roles-v1-based
		// snapshot over whatever the role change already installed above.
		slow.Release.Set();
		await refreshTask;
		await roleChangeTask;

		string? tag = resolver.Resolve(new BackendCredentials("u", "pw"))
			.Roles[BackendRole.Notes].Settings.Section["Tag"];
		Assert.Equal("v2", tag); // the live role change must not be lost to the stale refresh
	}

	/// <summary>Test-only provider whose ValidateConfiguration blocks ONCE, on the first call, until
	/// released — used to open a deterministic window inside BuildSnapshot (B4).</summary>
	private sealed class GateProvider : IBackendProvider
	{
		private int _callCount;

		public ManualResetEventSlim Entered { get; } = new(false);
		public ManualResetEventSlim Release { get; } = new(false);

		public string Name => "slow";
		public IReadOnlySet<BackendRole> SupportedRoles { get; } = new HashSet<BackendRole> { BackendRole.Notes };

		public Task<IBackendConnection> CreateConnectionAsync(
			BackendConnectionContext context, CancellationToken ct) => throw new NotSupportedException();

		public void ValidateConfiguration(BackendRole role, ProviderSettings settings, IList<string> failures)
		{
			if (Interlocked.Increment(ref _callCount) == 1)
			{
				Entered.Set();
				Release.Wait();
			}
		}

		public string DescribeRole(BackendRole role, ProviderSettings settings) => "slow";
	}

	private sealed class CapturingLogger : ILogger
	{
		public List<(LogLevel Level, string Message)> Lines { get; } = [];
		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
		public bool IsEnabled(LogLevel logLevel) => true;

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
			Exception? exception, Func<TState, Exception?, string> formatter) =>
			Lines.Add((logLevel, formatter(state, exception)));
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

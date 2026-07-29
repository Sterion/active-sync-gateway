using System.Collections.Concurrent;
using System.Reflection;
using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Core.Accounts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Observability;
using ActiveSync.Core.Options;
using ActiveSync.Core.Settings;
using ActiveSync.Core.State;
using ActiveSync.Crypto;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ActiveSync.Core.Tests;

/// <summary>
///   Session-lifetime behaviour of <see cref="BackendSessionFactory" />: the refcounted lease that
///   keeps an in-use session alive across an idle-eviction sweep, that the share-grant DB read
///   happens once per build rather than once per request, and that a disposed factory
///   unsubscribes from snapshot/settings events.
/// </summary>
public sealed class BackendSessionFactoryTests : IDisposable
{
	private static readonly BackendCredentials Creds = new() { UserName = "user1@example.com", Password = "pass" };

	private readonly SqliteConnection _connection;
	private readonly TestContextFactory _dbFactory;

	public BackendSessionFactoryTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();
		_dbFactory = new TestContextFactory(_connection);
		using SyncDbContext db = _dbFactory.CreateDbContext();
		db.Database.EnsureCreated();
	}

	public void Dispose() => _connection.Dispose();

	[Fact]
	public async Task IdleEviction_DoesNotTearDownASessionAnActiveRequestHolds()
	{
		// A Ping holds its session for the whole heartbeat (up to ~29.5 min), far longer than
		// SessionIdleMinutes, so the idle sweep fires while the request is mid-flight. It must evict
		// the session from the cache WITHOUT disposing the connection the request is still using.
		FakeMailProvider provider = new();
		BackendSessionFactory factory = NewFactory(provider, sessionIdleMinutes: -1); // everything is "idle"

		// The active request gets its session and keeps using it.
		IBackendSession held = await factory.GetSessionAsync(Creds, 1, "dev-1", CancellationToken.None);
		FakeResource resource = provider.LastResource!;

		// The idle sweep runs concurrently with the still-open request.
		InvokeEvictIdleSessions(factory);

		Assert.Empty(factory.SnapshotSessions()); // the sweep DID evict it from the cache
		// ...but the connection the request holds must survive until the request releases it.
		await Task.Delay(300);
		Assert.False(resource.Disposed);

		// Releasing the request's lease is what finally tears the connection down.
		await held.DisposeAsync();
		Assert.True(await WaitUntil(() => resource.Disposed, TimeSpan.FromSeconds(2)));
	}

	private static async Task<bool> WaitUntil(Func<bool> condition, TimeSpan timeout)
	{
		DateTime deadline = DateTime.UtcNow + timeout;
		while (DateTime.UtcNow < deadline)
		{
			if (condition())
				return true;
			await Task.Delay(20);
		}

		return condition();
	}

	[Fact]
	public async Task ShareGrants_AreReadOncePerBuild_NotPerRequest()
	{
		// LoadShareGrantsAsync opened a DbContext on every GetSessionAsync, though the grants
		// are consumed only when a session is actually built. A cache hit must not touch the DB.
		FakeMailProvider provider = new();
		CountingContextFactory counting = new(_connection);
		BackendSessionFactory factory = NewFactory(provider, dbFactory: counting);

		await factory.GetSessionAsync(Creds, 1, "dev-1", CancellationToken.None);
		int afterBuild = counting.Created;
		await factory.GetSessionAsync(Creds, 1, "dev-1", CancellationToken.None); // cache hit
		await factory.GetSessionAsync(Creds, 1, "dev-1", CancellationToken.None); // cache hit

		Assert.Equal(afterBuild, counting.Created); // no further DB reads for the cache hits
	}

	[Fact]
	public async Task AuthCache_RejectsAVerdictStampedWithAnOlderSnapshotVersion()
	{
		// SnapshotChanged clears the auth caches, but a verdict already IN FLIGHT against the
		// OLD snapshot can still write back to the (now-empty) cache AFTER the clear — a stale
		// positive verdict re-populates the cache (TOCTOU: the edit that should have invalidated
		// it already ran). It must be stamped with the snapshot version it was computed under and
		// rejected on a later read once the version has moved on, so the second attempt re-probes
		// instead of trusting the stale entry.
		ControllableVerifierProvider provider = new();
		IConfigurationRoot config = new ConfigurationBuilder().AddInMemoryCollection(
			new Dictionary<string, string?>
			{
				["ActiveSync:Backends:MailStore:Provider"] = "fake",
				["ActiveSync:Backends:MailSubmit:Provider"] = "fake",
			}).Build();
		BackendRolesProvider rolesProvider = new(config);
		BackendProviderRegistry registry = new([provider], NullLogger<BackendProviderRegistry>.Instance);
		UserStore store = new(_dbFactory);
		ActiveSyncOptions options = new()
		{
			Encryption = new EncryptionOptions { AllowPlaintext = true },
			Auth = new AuthOptions { UsersRefreshSeconds = 0 }, // "0 = check on every request"
			Eas = new EasOptions()
		};
		IOptionsMonitor<ActiveSyncOptions> monitor = TestOptionsMonitor.Of(options);
		UserResolver resolver = new(monitor, rolesProvider, registry, store);
		BackendSessionFactory factory = new(monitor, resolver, rolesProvider, _dbFactory, registry,
			NullLogger<BackendSessionFactory>.Instance);

		provider.Gate = new TaskCompletionSource();
		BackendCredentials creds = new() { UserName = "racer@x", Password = "pw" };
		Task<bool> authTask = factory.AuthenticateAsync(creds, CancellationToken.None);

		// Wait until the probe actually started (captured its snapshot version) before racing a
		// rebuild underneath it.
		Assert.True(await provider.Entered.WaitAsync(TimeSpan.FromSeconds(2)));

		// A concurrent `eas user` edit lands and the resolver rebuilds — bumps the snapshot
		// version and clears the (still-empty) auth caches.
		await store.UpsertAsync("someone-else", new UserOptions(), CancellationToken.None);
		await resolver.EnsureFreshAsync(true, CancellationToken.None);

		// Let the in-flight probe finish; its verdict now writes back into the cache AFTER the
		// rebuild — tagged with the OLD version if the fix is in place.
		provider.Gate.SetResult();
		Assert.True(await authTask);

		// A fresh authentication attempt reads the CURRENT snapshot version. If the stale cached
		// verdict were trusted, this second probe would never run.
		Assert.True(await factory.AuthenticateAsync(creds, CancellationToken.None));
		Assert.Equal(2, provider.VerifyCallCount);
	}

	[Fact]
	public async Task IdleSweep_DoesNotCountAFaultedSessionSlotAsActive()
	{
		// EvictIdleSessionsCore derived `activeUsers` from raw _sessions.Keys, which includes
		// a slot whose build FAULTED (e.g. a transient backend outage in CreateConnectionAsync) —
		// IsValueCreated but not IsCompletedSuccessfully. A user whose only slot is faulted has no
		// live session, so a provider's per-user resources (e.g. IDLE watchers) for that user must
		// be trimmed on the sweep, not pinned by a phantom "active" entry.
		FailingResourceOwnerProvider provider = new();
		BackendSessionFactory factory = NewFactory(provider);

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			factory.GetSessionAsync(Creds, 1, "dev-1", CancellationToken.None));

		InvokeEvictIdleSessions(factory);

		Assert.NotNull(provider.LastActiveUsers);
		Assert.DoesNotContain(Creds.UserName, provider.LastActiveUsers!);
	}

	[Fact]
	public async Task GetSession_RecoversFromASingleFaultedBuild_WithoutWedgingTheSlot()
	{
		// A faulted Lazy build (e.g. a transient backend outage during
		// CreateConnectionAsync) was never swept from the cache, so every subsequent
		// GetSessionAsync call for the same (user, device) re-awaited the SAME faulted Task and
		// rethrew the SAME exception forever — wedged until restart. A single faulted attempt
		// must drop the stale slot and rebuild once, so a transient outage self-heals within the
		// SAME call instead of wedging the slot for every future request.
		FlakyProvider provider = new();
		BackendSessionFactory factory = NewFactory(provider);

		IBackendSession session = await factory.GetSessionAsync(Creds, 1, "dev-1", CancellationToken.None);

		Assert.NotNull(session);
		Assert.Equal(2, provider.AttemptCount); // one faulted attempt, then one that succeeded
		await session.DisposeAsync();
	}

	[Fact]
	public async Task GetSession_KeyedOnLoginAlone_ServesAReissuedLoginTheOldHoldersSession()
	{
		// The cache key was `$"{credentials.UserName}\n{deviceId}"` — no UserId. If a login is
		// freed (rename) and reissued to a DIFFERENT person with the SAME presented password, the
		// second GetSessionAsync for that login/device must build a session scoped to the NEW
		// UserId, not reuse the stale entry still carrying the OLD UserId (DB scoping / AAD leak).
		FakeMailProvider provider = new();
		BackendSessionFactory factory = NewFactory(provider);

		IBackendSession first = await factory.GetSessionAsync(Creds, userId: 1, "dev-1", CancellationToken.None);
		Assert.Equal(1, first.UserId);
		await first.DisposeAsync();

		// Same login, same device, same presented password — but now a DIFFERENT UserId (the login
		// was reissued to someone else).
		IBackendSession second = await factory.GetSessionAsync(Creds, userId: 2, "dev-1", CancellationToken.None);
		Assert.Equal(2, second.UserId);
		await second.DisposeAsync();
	}

	[Fact]
	public async Task IdleSweep_RemovesAFaultedSessionSlot()
	{
		// A faulted slot is never IsBuilt, so the idle-timeout eviction (which only ever
		// checked IsBuilt) never qualified it — it sat in the cache forever. The sweep must
		// remove a faulted slot on its own too, so a (user, device) that's never retried again
		// doesn't leak.
		FailingResourceOwnerProvider provider = new(); // CreateConnectionAsync always throws
		BackendSessionFactory factory = NewFactory(provider);
		string key = $"1\n{Creds.UserName}\ndev-1";

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			factory.GetSessionAsync(Creds, 1, "dev-1", CancellationToken.None));
		Assert.True(SessionsContainsKey(factory, key)); // the faulted slot is still cached

		InvokeEvictIdleSessions(factory);

		Assert.False(SessionsContainsKey(factory, key)); // the sweep removed it
	}

	[Fact]
	public void IdleSweep_RemovalMustBeValueCompared_NotKeyOnly_COVERAGE()
	{
		// COVERAGE, not red-first proof. EvictIdleSessionsCore's foreach captures (key, lazy)
		// from ONE enumeration pass over the live _sessions ConcurrentDictionary, then a few
		// instructions later removes by KEY ALONE: `_sessions.TryRemove(key, out removed)`. If a
		// concurrent installer (GetSessionAsync's password-rotation path, or a rebuild after a
		// failed TryAcquireLease) replaces _sessions[key] with a freshly-installed, DIFFERENT Lazy
		// in that window, the key-only removal evicts and (if built) tears down whatever is
		// CURRENTLY there — not the stale entry the sweep actually judged idle — silently losing
		// the replacement session. Every other removal in this file (the faulted-slot branch ten
		// lines below the buggy one, and all three in GetSessionAsync) uses the value-compared
		// TryRemove(KeyValuePair) overload precisely so a concurrent replacement is left alone.
		//
		// This is a genuine two-thread race with NO deterministic trigger reachable from a test:
		// the gap between the enumeration read and the TryRemove call is a handful of CPU
		// instructions with no await point (EvictIdleSessionsCore is fully synchronous) to gate a
		// test on, unlike e.g. AuthCache_RejectsAVerdictStampedWithAnOlderSnapshotVersion above,
		// which can pause the racing method at a real `await`. Reproducing it end-to-end would
		// need either a production-only test seam or a probabilistic stress loop — neither
		// appropriate for a Low-severity, single-line fix. So this test instead pins the exact
		// removal-semantics contract the fix (and its three siblings) relies on, using the same
		// ConcurrentDictionary<TKey,TValue> type _sessions is: a value-compared TryRemove is a
		// no-op once the slot has moved on, while a key-only TryRemove is not.
		ConcurrentDictionary<string, int> sessions = new();
		sessions["k"] = 1; // what the sweep's enumeration captured as "idle-eligible"
		sessions["k"] = 2; // a concurrent installer replaced it before the sweep's removal ran

		// The buggy shape (BackendSessionFactory.cs:353 before the fix): removes whatever is
		// there NOW, tearing down the concurrently-installed replacement instead of leaving it.
		Assert.True(sessions.TryRemove("k", out int removedByKeyOnly));
		Assert.Equal(2, removedByKeyOnly);
		sessions["k"] = 2; // reset for the fixed shape below

		// The fixed shape: only removes when the CURRENT value still matches the stale one that
		// was captured — the replacement survives untouched.
		Assert.False(sessions.TryRemove(new KeyValuePair<string, int>("k", 1)));
		Assert.True(sessions.TryGetValue("k", out int survivor));
		Assert.Equal(2, survivor);
	}

	[Fact]
	public async Task RecycleAll_OneThrowingProviderTrim_StillTrimsTheRestUnaffected()
	{
		// RecycleAll's per-user resource trim loop was unguarded, unlike the IDENTICAL loop in
		// EvictIdleSessionsCore, which wraps the whole sweep specifically "because the trim runs
		// plugin code". RecycleAll is reached from BackendRolesProvider.Changed (a live
		// `eas config set Backends:...`), so one provider throwing there must not stop the rest of
		// the fleet from being trimmed against the new settings.
		FakeMailProvider mail = new(); // supports the mandatory MailStore/MailSubmit roles
		ThrowingResourceOwnerProvider throwing = new(); // registered FIRST, so an unguarded loop
		TrackingResourceOwnerProvider tracking = new(); // never reaches this one
		BackendProviderRegistry registry = new([mail, throwing, tracking], NullLogger<BackendProviderRegistry>.Instance);

		// FakeMailProvider supports every role, so assigning "fake" everywhere avoids needing the
		// "local" fallback provider (which isn't registered in this test's registry) — otherwise
		// ValidateProviders' per-role lookup would fail and the rebuild would never fire Changed.
		Dictionary<string, string?> backendConfig = new()
		{
			["ActiveSync:Backends:MailStore:Provider"] = "fake",
			["ActiveSync:Backends:MailStore:Host"] = "old.example.com",
			["ActiveSync:Backends:MailSubmit:Provider"] = "fake",
		};
		foreach (string role in new[] { "Calendar", "Contacts", "Tasks", "Notes" })
			backendConfig[$"ActiveSync:Backends:{role}:Provider"] = "fake";

		DbSettingsConfigurationSource dbSource = new();
		IConfigurationRoot config = new ConfigurationBuilder()
			.AddInMemoryCollection(backendConfig)
			.Add(dbSource)
			.Build();
		BackendRolesProvider rolesProvider = new(config, registry);

		ActiveSyncOptions options = new()
		{
			Encryption = new EncryptionOptions { AllowPlaintext = true }, Eas = new EasOptions()
		};
		IOptionsMonitor<ActiveSyncOptions> monitor = TestOptionsMonitor.Of(options);
		UserResolver resolver = new(monitor, rolesProvider, registry);
		// The factory's ctor subscribes RecycleAll to rolesProvider.Changed — the live recycle path.
		await using BackendSessionFactory factory = new(monitor, resolver, rolesProvider, _dbFactory, registry,
			NullLogger<BackendSessionFactory>.Instance);

		// A live backend-settings edit (e.g. `eas config set ActiveSync:Backends:MailStore:Host ...`)
		// changes the Backends subtree, firing BackendRolesProvider.Changed -> RecycleAll.
		Record.Exception(() => dbSource.Provider.SetData(new Dictionary<string, string?>
		{
			["ActiveSync:Backends:MailStore:Provider"] = "fake",
			["ActiveSync:Backends:MailStore:Host"] = "new.example.com",
			["ActiveSync:Backends:MailSubmit:Provider"] = "fake",
		}));

		Assert.True(throwing.WasCalled); // the throwing provider was reached
		Assert.True(tracking.WasCalled); // and the sibling AFTER it in the loop still got trimmed
	}

	private static bool SessionsContainsKey(BackendSessionFactory factory, string key)
	{
		object sessions = typeof(BackendSessionFactory)
			.GetField("_sessions", BindingFlags.NonPublic | BindingFlags.Instance)!
			.GetValue(factory)!;
		return ((System.Collections.IDictionary)sessions).Contains(key);
	}

	[Fact]
	public async Task DisposedFactory_UnsubscribesFromSettingsEvents()
	{
		// The factory subscribed to BackendRolesProvider.Changed / UserResolver.SnapshotChanged
		// but never unsubscribed. After disposal it must detach both handlers, otherwise the disposed
		// (dead) factory stays reachable and its handlers keep firing on cleared state.
		FakeMailProvider provider = new();
		IOptionsMonitor<ActiveSyncOptions> monitor = TestOptionsMonitor.Of(new ActiveSyncOptions
		{
			Encryption = new EncryptionOptions { AllowPlaintext = true }, Eas = new EasOptions()
		});
		BackendRolesProvider roles = RolesProvider();
		BackendProviderRegistry registry = new([provider], NullLogger<BackendProviderRegistry>.Instance);
		UserResolver resolver = new(monitor, roles, registry);
		BackendSessionFactory factory = new(monitor, resolver, roles, _dbFactory, registry,
			NullLogger<BackendSessionFactory>.Instance);

		// Both events carry a handler that targets the factory while it is alive (the resolver also
		// subscribes to roles.Changed for itself — so we check specifically for the factory's handler).
		Assert.True(HasHandlerTargeting(roles, "Changed", factory));
		Assert.True(HasHandlerTargeting(resolver, "SnapshotChanged", factory));

		await factory.DisposeAsync();

		Assert.False(HasHandlerTargeting(roles, "Changed", factory));
		Assert.False(HasHandlerTargeting(resolver, "SnapshotChanged", factory));
	}

	[Fact]
	public async Task DisposedFactory_ClearsTheStaticSessionsObserver()
	{
		// The ctor wires GatewayMetrics.SetSessionsObserver to a closure over THIS factory's
		// _sessions dictionary (the activesync_backend_sessions_active gauge). DisposeAsync
		// detaches the SnapshotChanged/Changed handlers, per the disposal test above, but leaves this observer
		// installed — a disposed factory's closure stays reachable in the static slot until some
		// LATER factory happens to overwrite it (last-write-wins), which never happens in a
		// single-host process. After disposal the static slot must no longer target this factory.
		FakeMailProvider provider = new();
		BackendSessionFactory factory = NewFactory(provider);

		Assert.True(SessionsObserverTargets(factory)); // the ctor installed a closure over this factory

		await factory.DisposeAsync();

		Assert.False(SessionsObserverTargets(factory)); // disposal must clear/replace it
	}

	private static bool SessionsObserverTargets(BackendSessionFactory factory)
	{
		Delegate? observer = (Delegate?)typeof(GatewayMetrics)
			.GetField("_sessionsObserver", BindingFlags.NonPublic | BindingFlags.Static)!
			.GetValue(null);
		return ReferenceEquals(observer?.Target, factory);
	}

	[Fact]
	public void IdleSweep_SwallowsAnEscapingException()
	{
		// EvictIdleSessions is a System.Threading.Timer callback — an escaping exception
		// terminates the process. Reading _options.CurrentValue can throw (live-editable settings),
		// so the whole body must be guarded.
		FakeMailProvider provider = new();
		ToggleThrowMonitor monitor = new(new ActiveSyncOptions
		{
			Encryption = new EncryptionOptions { AllowPlaintext = true }, Eas = new EasOptions()
		});
		BackendRolesProvider roles = RolesProvider();
		BackendProviderRegistry registry = new([provider], NullLogger<BackendProviderRegistry>.Instance);
		UserResolver resolver = new(monitor, roles, registry);
		BackendSessionFactory factory = new(monitor, resolver, roles, _dbFactory, registry,
			NullLogger<BackendSessionFactory>.Instance);

		monitor.Throw = true; // the sweep will now hit a throwing options read
		Exception? escaped = Record.Exception(() => InvokeEvictIdleSessions(factory));
		Assert.Null(escaped); // the timer callback must not let it escape
	}

	// ---------- harness ----------

	private sealed class ToggleThrowMonitor(ActiveSyncOptions value) : IOptionsMonitor<ActiveSyncOptions>
	{
		public bool Throw { get; set; }
		public ActiveSyncOptions CurrentValue => Throw ? throw new InvalidOperationException("options invalid") : value;
		public ActiveSyncOptions Get(string? name) => CurrentValue;
		public IDisposable? OnChange(Action<ActiveSyncOptions, string?> listener) => null;
	}

	private static bool HasHandlerTargeting(object eventSource, string eventName, object handlerTarget)
	{
		Delegate? backing = (Delegate?)eventSource.GetType()
			.GetField(eventName, BindingFlags.NonPublic | BindingFlags.Instance)!
			.GetValue(eventSource);
		return backing?.GetInvocationList().Any(d => ReferenceEquals(d.Target, handlerTarget)) ?? false;
	}

	private static void InvokeEvictIdleSessions(BackendSessionFactory factory) =>
		typeof(BackendSessionFactory)
			.GetMethod("EvictIdleSessions", BindingFlags.NonPublic | BindingFlags.Instance)!
			.Invoke(factory, null);

	private BackendSessionFactory NewFactory(
		IBackendProvider provider,
		int sessionIdleMinutes = 15,
		ISyncDbContextFactory? dbFactory = null,
		BackendRolesProvider? roles = null)
	{
		ActiveSyncOptions options = new()
		{
			Encryption = new EncryptionOptions { AllowPlaintext = true },
			Eas = new EasOptions { SessionIdleMinutes = sessionIdleMinutes }
		};
		IOptionsMonitor<ActiveSyncOptions> monitor = TestOptionsMonitor.Of(options);
		BackendRolesProvider rolesProvider = roles ?? RolesProvider();
		BackendProviderRegistry registry =
			new([provider], NullLogger<BackendProviderRegistry>.Instance);
		UserResolver resolver = new(monitor, rolesProvider, registry);
		return new BackendSessionFactory(monitor, resolver, rolesProvider, dbFactory ?? _dbFactory, registry,
			NullLogger<BackendSessionFactory>.Instance);
	}

	private static BackendRolesProvider RolesProvider()
	{
		Dictionary<string, string?> config = new();
		foreach (string role in new[] { "MailStore", "MailSubmit", "Calendar", "Contacts", "Tasks", "Notes" })
			config[$"ActiveSync:Backends:{role}:Provider"] = "fake";
		IConfigurationRoot root = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
		return new BackendRolesProvider(root);
	}

	private sealed class TestContextFactory(SqliteConnection connection) : ISyncDbContextFactory
	{
		public SyncDbContext CreateDbContext() =>
			new SqliteSyncDbContext(new DbContextOptionsBuilder<SqliteSyncDbContext>()
				.UseSqlite(connection).Options);
	}

	private sealed class CountingContextFactory(SqliteConnection connection) : ISyncDbContextFactory
	{
		public int Created { get; private set; }

		public SyncDbContext CreateDbContext()
		{
			Created++;
			return new SqliteSyncDbContext(new DbContextOptionsBuilder<SqliteSyncDbContext>()
				.UseSqlite(connection).Options);
		}
	}

	private sealed class FakeMailProvider : IBackendProvider
	{
		private static readonly IReadOnlySet<BackendRole> All = new HashSet<BackendRole>
		{
			BackendRole.MailStore, BackendRole.MailSubmit, BackendRole.Calendar,
			BackendRole.Contacts, BackendRole.Tasks, BackendRole.Notes
		};

		public FakeResource? LastResource { get; private set; }

		public string Name => "fake";
		public IReadOnlySet<BackendRole> SupportedRoles => All;

		public void ValidateConfiguration(BackendRole role, ProviderSettings settings, IList<string> failures) { }
		public string DescribeRole(BackendRole role, ProviderSettings settings) => "fake";

		public Task<IBackendConnection> CreateConnectionAsync(BackendConnectionContext context, CancellationToken ct)
		{
			LastResource = new FakeResource();
			return Task.FromResult<IBackendConnection>(
				new BackendConnection([new FakeMailStore()], new FakeSubmit(), ownedResources: [OwnedResource.OfAsync(LastResource)]));
		}
	}

	/// <summary>A provider with no roles whose <see cref="TrimUserResources" /> always throws.</summary>
	private sealed class ThrowingResourceOwnerProvider : IBackendProvider, IPerUserResourceOwner
	{
		private static readonly IReadOnlySet<BackendRole> None = new HashSet<BackendRole>();

		public bool WasCalled { get; private set; }

		public string Name => "throwing";
		public IReadOnlySet<BackendRole> SupportedRoles => None;

		public void ValidateConfiguration(BackendRole role, ProviderSettings settings, IList<string> failures) { }
		public string DescribeRole(BackendRole role, ProviderSettings settings) => "throwing";

		public Task<IBackendConnection> CreateConnectionAsync(BackendConnectionContext context, CancellationToken ct) =>
			throw new NotSupportedException();

		public void TrimUserResources(IReadOnlySet<string> activeGatewayLogins)
		{
			WasCalled = true;
			throw new InvalidOperationException("simulated plugin failure trimming per-user resources");
		}
	}

	/// <summary>A provider with no roles that just records whether it was trimmed.</summary>
	private sealed class TrackingResourceOwnerProvider : IBackendProvider, IPerUserResourceOwner
	{
		private static readonly IReadOnlySet<BackendRole> None = new HashSet<BackendRole>();

		public bool WasCalled { get; private set; }

		public string Name => "tracking";
		public IReadOnlySet<BackendRole> SupportedRoles => None;

		public void ValidateConfiguration(BackendRole role, ProviderSettings settings, IList<string> failures) { }
		public string DescribeRole(BackendRole role, ProviderSettings settings) => "tracking";

		public Task<IBackendConnection> CreateConnectionAsync(BackendConnectionContext context, CancellationToken ct) =>
			throw new NotSupportedException();

		public void TrimUserResources(IReadOnlySet<string> activeGatewayLogins) => WasCalled = true;
	}

	/// <summary>
	///   A MailStore provider whose credential probe pauses on <see cref="Gate" /> until released,
	///   so a test can control exactly when a concurrent verdict is "in flight" relative to a
	///   snapshot rebuild.
	/// </summary>
	private sealed class ControllableVerifierProvider : IBackendProvider, ICredentialVerifier
	{
		private static readonly IReadOnlySet<BackendRole> All = new HashSet<BackendRole> { BackendRole.MailStore };

		public int VerifyCallCount { get; private set; }
		public TaskCompletionSource? Gate { get; set; }
		public SemaphoreSlim Entered { get; } = new(0);

		public string Name => "fake";
		public IReadOnlySet<BackendRole> SupportedRoles => All;

		public void ValidateConfiguration(BackendRole role, ProviderSettings settings, IList<string> failures) { }
		public string DescribeRole(BackendRole role, ProviderSettings settings) => "fake";

		public Task<IBackendConnection> CreateConnectionAsync(BackendConnectionContext context, CancellationToken ct) =>
			throw new NotSupportedException();

		public async Task<bool> VerifyCredentialsAsync(ResolvedRole role, CancellationToken ct)
		{
			VerifyCallCount++;
			Entered.Release();
			if (Gate is not null)
				// VSTHRD003: awaiting the test's TaskCompletionSource on purpose — it is the
				// synchronization gate the test uses to pause this "in-flight probe" until it has
				// deliberately raced a snapshot rebuild underneath it.
#pragma warning disable VSTHRD003
				await Gate.Task.ConfigureAwait(false);
#pragma warning restore VSTHRD003
			return true;
		}
	}

	/// <summary>
	///   A MailStore provider whose connection build always FAULTS (a transient backend outage) —
	///   and that also owns per-user resources, so a test can observe exactly which set the idle
	///   sweep considers "active".
	/// </summary>
	private sealed class FailingResourceOwnerProvider : IBackendProvider, IPerUserResourceOwner
	{
		private static readonly IReadOnlySet<BackendRole> All = new HashSet<BackendRole>
		{
			BackendRole.MailStore, BackendRole.MailSubmit, BackendRole.Calendar,
			BackendRole.Contacts, BackendRole.Tasks, BackendRole.Notes
		};

		public IReadOnlySet<string>? LastActiveUsers { get; private set; }

		public string Name => "fake";
		public IReadOnlySet<BackendRole> SupportedRoles => All;

		public void ValidateConfiguration(BackendRole role, ProviderSettings settings, IList<string> failures) { }
		public string DescribeRole(BackendRole role, ProviderSettings settings) => "fake";

		public Task<IBackendConnection> CreateConnectionAsync(BackendConnectionContext context, CancellationToken ct) =>
			throw new InvalidOperationException("simulated transient backend outage");

		public void TrimUserResources(IReadOnlySet<string> activeGatewayLogins) => LastActiveUsers = activeGatewayLogins;
	}

	/// <summary>
	///   A MailStore provider whose connection build FAULTS on the first attempt (a transient
	///   backend outage) and succeeds on every attempt after.
	/// </summary>
	private sealed class FlakyProvider : IBackendProvider
	{
		private static readonly IReadOnlySet<BackendRole> All = new HashSet<BackendRole>
		{
			BackendRole.MailStore, BackendRole.MailSubmit, BackendRole.Calendar,
			BackendRole.Contacts, BackendRole.Tasks, BackendRole.Notes
		};

		private int _attempts;

		public int AttemptCount => _attempts;

		public string Name => "fake";
		public IReadOnlySet<BackendRole> SupportedRoles => All;

		public void ValidateConfiguration(BackendRole role, ProviderSettings settings, IList<string> failures) { }
		public string DescribeRole(BackendRole role, ProviderSettings settings) => "fake";

		public Task<IBackendConnection> CreateConnectionAsync(BackendConnectionContext context, CancellationToken ct)
		{
			if (Interlocked.Increment(ref _attempts) == 1)
				throw new InvalidOperationException("simulated transient backend outage");
			return Task.FromResult<IBackendConnection>(
				new BackendConnection([new FakeMailStore()], new FakeSubmit(), ownedResources: [OwnedResource.OfAsync(new FakeResource())]));
		}
	}

	private sealed class FakeResource : IAsyncDisposable
	{
		public bool Disposed { get; private set; }

		public ValueTask DisposeAsync()
		{
			Disposed = true;
			return ValueTask.CompletedTask;
		}
	}

	private sealed class FakeSubmit : IMailSubmitOperations
	{
		public Task SendAsync(ReadOnlyMemory<byte> rfc822, CancellationToken ct) => Task.CompletedTask;
	}

	private sealed class FakeMailStore : IMailStore, IMailboxOperations
	{
		public bool OwnsKey(FolderKey key) => key.Value.StartsWith("fake:", StringComparison.Ordinal);

		public Task<IReadOnlyList<BackendFolder>> ListFoldersAsync(CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<IReadOnlyDictionary<ItemKey, ItemRevision>> GetItemRevisionsAsync(
			FolderKey folder, ContentFilter filter, CancellationToken ct) => throw new NotSupportedException();

		public Task<MailItem?> GetItemAsync(
			FolderKey folder, ItemKey item, MailFetchOptions options, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<(ItemKey Key, ItemRevision Revision)> CreateDraftAsync(
			FolderKey folder, MailItem item, CancellationToken ct) => throw new NotSupportedException();

		public Task<ItemRevision> UpdateFlagsAsync(
			FolderKey folder, ItemKey item, MailFlagsPatch patch, ItemRevision? expected, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<(ItemKey Key, ItemRevision Revision)> ReplaceDraftAsync(
			FolderKey folder, ItemKey item, MailItem value, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task DeleteItemAsync(FolderKey folder, ItemKey item, bool permanent, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<IReadOnlyList<FolderKey>> WaitForChangesAsync(
			IReadOnlyList<FolderKey> folders, TimeSpan timeout, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task SaveToSentAsync(ReadOnlyMemory<byte> rfc822, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<ReadOnlyMemory<byte>?> GetRawMessageAsync(FolderKey folder, ItemKey item, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task SetAnsweredAsync(FolderKey folder, ItemKey item, bool forwarded, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<IReadOnlyList<SearchHit>> SearchAsync(
			FolderKey? folder, string freeText, DateTimeOffset? since, int maxResults, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task EmptyFolderAsync(FolderKey folder, CancellationToken ct) => throw new NotSupportedException();
	}
}

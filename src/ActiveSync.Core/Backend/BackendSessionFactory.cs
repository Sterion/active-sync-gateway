using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using ActiveSync.Core.Accounts;
using ActiveSync.Core.Observability;
using ActiveSync.Core.Options;
using ActiveSync.Core.State;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using ActiveSync.Contracts;

namespace ActiveSync.Core.Backend;

/// <summary>
///   Caches one <see cref="CompositeBackendSession" /> per (user, device) so consecutive EAS
///   requests reuse live backend connections. Idle sessions are evicted in the background —
///   providers holding per-user caches (IDLE watchers) trim them on the same sweep via
///   <see cref="IPerUserResourceOwner" />. Successful authentications are cached briefly so
///   every HTTP request does not trigger a backend login round-trip.
/// </summary>
public sealed class BackendSessionFactory : IBackendSessionFactory, IAsyncDisposable
{
	private readonly ConcurrentDictionary<string, (string PasswordHash, DateTime ExpiresUtc)> _authCache = new();
	private readonly ConcurrentDictionary<string, (string PasswordHash, DateTime ExpiresUtc)> _authNegativeCache = new();
	private readonly ISyncDbContextFactory _dbFactory;
	private readonly Timer _evictionTimer;
	private readonly ILogger<BackendSessionFactory> _logger;
	private readonly IOptionsMonitor<ActiveSyncOptions> _options;
	private readonly BackendProviderRegistry _registry;
	private readonly AccountResolver _resolver;
	private readonly BackendRolesProvider _rolesProvider;
	// K61: CompositeBackendSession is now built asynchronously (providers open their transport in
	// CreateConnectionAsync), so the per-(user, device) cache holds a Lazy<Task<...>> — concurrent
	// callers await the one shared build instead of racing to construct duplicate sessions.
	private readonly ConcurrentDictionary<string, Lazy<Task<CompositeBackendSession>>> _sessions = new();
	// A28: held in fields so DisposeAsync can detach them — the resolver/roles-provider are
	// singletons that outlive this factory (notably across test fixtures), and a leaked handler
	// keeps a disposed factory reachable and fires on its cleared state.
	private readonly Action _onSnapshotChanged;

	public BackendSessionFactory(
		IOptionsMonitor<ActiveSyncOptions> options,
		AccountResolver resolver,
		BackendRolesProvider rolesProvider,
		ISyncDbContextFactory dbFactory,
		BackendProviderRegistry registry,
		ILogger<BackendSessionFactory> logger)
	{
		_options = options;
		_resolver = resolver;
		_rolesProvider = rolesProvider;
		_dbFactory = dbFactory;
		_registry = registry;
		_logger = logger;
		_evictionTimer = new Timer(_ => EvictIdleSessions(), null,
			TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
		// Account edits (eas user ...) must apply on the next request, not after the auth
		// cache TTL — a rebuilt snapshot resets both verdict caches.
		_onSnapshotChanged = () =>
		{
			_authCache.Clear();
			_authNegativeCache.Clear();
		};
		_resolver.SnapshotChanged += _onSnapshotChanged;
		// A live global backend-settings change (eas config set Backends:...) recycles every
		// session so the next request rebuilds connections against the new host/port/settings.
		_rolesProvider.Changed += RecycleAll;
		// Per-user live-count gauge. Keys are "user\ndevice"; only materialized Lazy
		// values count (an unrealized slot is not a live connection).
		GatewayMetrics.SetSessionsObserver(() => _sessions
			.Where(pair => IsBuilt(pair.Value))
			.GroupBy(pair => pair.Key.Split('\n')[0], StringComparer.OrdinalIgnoreCase)
			.Select(g => new Measurement<long>(g.Count(),
				new KeyValuePair<string, object?>("user", GatewayMetrics.PerUserLabels ? g.Key : "-"))));
	}

	public async ValueTask DisposeAsync()
	{
		// A28: detach the event handlers first — the resolver/roles-provider outlive this factory.
		_resolver.SnapshotChanged -= _onSnapshotChanged;
		_rolesProvider.Changed -= RecycleAll;
		await _evictionTimer.DisposeAsync().ConfigureAwait(false);
		foreach ((string _, Lazy<Task<CompositeBackendSession>> lazy) in _sessions)
			await DisposeLazyAsync(lazy).ConfigureAwait(false);
		_sessions.Clear();
	}

	public async Task<bool> AuthenticateAsync(BackendCredentials credentials, CancellationToken ct)
	{
		await _resolver.EnsureFreshAsync(false, ct).ConfigureAwait(false);
		string cacheKey = credentials.UserName;
		string passwordHash = Hash(credentials.Password);
		if (_options.CurrentValue.Auth.SuccessCacheMinutes > 0 &&
		    _authCache.TryGetValue(cacheKey, out (string PasswordHash, DateTime ExpiresUtc) cached) &&
		    cached.PasswordHash == passwordHash && cached.ExpiresUtc > DateTime.UtcNow)
			return true;
		// Repeats of a known-bad password are refused without a backend round-trip, so the
		// gateway cannot be used to hammer the mail backend with login attempts.
		if (_options.CurrentValue.Auth.NegativeCacheSeconds > 0 &&
		    _authNegativeCache.TryGetValue(cacheKey, out (string PasswordHash, DateTime ExpiresUtc) negative) &&
		    negative.PasswordHash == passwordHash && negative.ExpiresUtc > DateTime.UtcNow)
			return false;

		// Declared users may carry a local rule (gateway Password override or a configured
		// Imap:Password the presented value must equal); RequireDeclaredUsers rejects
		// undeclared logins outright. The caches above bound the PBKDF2/compare cost, and
		// a false verdict feeds the per-IP throttle via the normal 401 path.
		if (_resolver.VerifyLocally(credentials.UserName, credentials.Password) is { } verified)
		{
			CacheVerdict(cacheKey, passwordHash, verified);
			return verified;
		}

		// No local rule: the presented password is the mail password — the MailStore role's
		// provider probes the user's EFFECTIVE endpoint/username, so per-user overrides
		// apply. A provider without verification support cannot admit pass-through logins.
		ResolvedAccount probeAccount = _resolver.Resolve(credentials);
		ResolvedRole mailRole = probeAccount.Roles[BackendRole.MailStore];
		if (_registry.GetFor(mailRole.ProviderName, BackendRole.MailStore) is not ICredentialVerifier verifier)
		{
			_logger.LogWarning(
				"Provider {Provider} cannot verify credentials and no local rule decides {User}; refusing login",
				mailRole.ProviderName, credentials.UserName);
			return false;
		}

		bool ok = await verifier.VerifyCredentialsAsync(mailRole, ct).ConfigureAwait(false);
		CacheVerdict(cacheKey, passwordHash, ok);
		return ok;
	}

	/// <summary>
	///   Live (materialized) backend sessions for the admin dashboard — an unrealized Lazy
	///   slot is not a live connection. Read-only over the concurrent map; never blocks.
	/// </summary>
	public IReadOnlyList<BackendSessionInfo> SnapshotSessions()
	{
		List<BackendSessionInfo> sessions = new();
		foreach ((string key, Lazy<Task<CompositeBackendSession>> lazy) in _sessions)
		{
			// Only a materialized, successfully-built session is a live connection — an unrealized
			// slot or one still building (or faulted) is not.
			if (!IsBuilt(lazy))
				continue;
			int separator = key.IndexOf('\n');
			sessions.Add(new BackendSessionInfo(
				separator < 0 ? key : key[..separator],
				separator < 0 ? "" : key[(separator + 1)..],
				Built(lazy).LastUsedUtc));
		}

		return sessions;
	}

	public async Task<IBackendSession> GetSessionAsync(
		BackendCredentials credentials, string deviceId, CancellationToken ct)
	{
		await _resolver.EnsureFreshAsync(false, ct).ConfigureAwait(false);
		ResolvedAccount account = _resolver.Resolve(credentials);
		IReadOnlyList<ResolvedRole> roles = account.OrderedRoles;

		// Cache keys and rotation compares stay on the GATEWAY login/password — per-backend
		// user names never become identity, and in Accounts mode the backend credentials are
		// config-static (restart to apply changes).
		string key = $"{credentials.UserName}\n{deviceId}";

		// A2: rebuild loop. The cached session may need recycling (password rotation) or may have
		// been evicted and torn down between our read of the cache and our lease acquisition; either
		// way we drop the stale entry and go round again. The common path runs exactly once.
		while (true)
		{
			bool created = false;

			// The build is SHARED across every concurrent caller for this (user, device), so it runs
			// uncancellable (CancellationToken.None): one request cancelling must not fault the session
			// the others are awaiting. This matches the pre-K61 synchronous, uncancellable build.
			// A11: the shared-calendar grants are read HERE, inside the build, so a cache hit never
			// opens a DbContext — a session carries the grants from its build time (`eas share`
			// changes apply when the session is next rebuilt), and the calendar provider merges them
			// with its own configured SharedCollections.
			Lazy<Task<CompositeBackendSession>> NewLazy() =>
				MakeLazy(async () =>
				{
					created = true;
					IReadOnlyList<SharedCollection> sharedCalendars =
						await LoadShareGrantsAsync(credentials.UserName, CancellationToken.None).ConfigureAwait(false);
					return await CompositeBackendSession.CreateAsync(
						_registry, credentials, account.MailAddress, roles, sharedCalendars, CancellationToken.None)
						.ConfigureAwait(false);
				});

			Lazy<Task<CompositeBackendSession>> lazy = _sessions.GetOrAdd(key, _ => NewLazy());
			CompositeBackendSession session = await AwaitBuild(lazy).ConfigureAwait(false);

			// Credentials changed (e.g. password rotation): recycle the cached session and rebuild.
			if (session.Credentials.Password != credentials.Password)
			{
				if (_sessions.TryRemove(new KeyValuePair<string, Lazy<Task<CompositeBackendSession>>>(key, lazy)))
					_ = DisposeLazyAsync(lazy); // releases the cache's lease on the stale session
				continue;
			}

			// Take a lease before handing the session out. A false return means the session was
			// evicted and its connections torn down between the cache read and now — drop the dead
			// entry and rebuild.
			if (!session.TryAcquireLease())
			{
				_sessions.TryRemove(new KeyValuePair<string, Lazy<Task<CompositeBackendSession>>>(key, lazy));
				continue;
			}

			if (created)
				_logger.LogInformation("Opened backend session for {User} (device {DeviceId})",
					credentials.UserName, deviceId);

			session.LastUsedUtc = DateTime.UtcNow;
			return session;
		}
	}

	/// <summary>
	///   The user's runtime `eas share` grants — handed to every provider connection; the
	///   calendar provider merges them with its own configured SharedCollections (a grant
	///   for the same collection overrides the config entry's mode).
	/// </summary>
	private async Task<IReadOnlyList<SharedCollection>> LoadShareGrantsAsync(
		string userName, CancellationToken ct)
	{
		await using SyncDbContext db = _dbFactory.CreateDbContext();
		List<SharedCalendarGrant> grants = await db.SharedCalendarGrants.AsNoTracking()
			.Where(g => g.UserName == userName)
			.ToListAsync(ct).ConfigureAwait(false);
		return grants
			.Select(g => new SharedCollection(g.CollectionHref, g.ReadOnly))
			.ToList();
	}

	private void CacheVerdict(string cacheKey, string passwordHash, bool verified)
	{
		if (verified)
		{
			if (_options.CurrentValue.Auth.SuccessCacheMinutes > 0)
				_authCache[cacheKey] = (passwordHash, DateTime.UtcNow.AddMinutes(_options.CurrentValue.Auth.SuccessCacheMinutes));
			_authNegativeCache.TryRemove(cacheKey, out _);
		}
		else
		{
			_authCache.TryRemove(cacheKey, out _);
			if (_options.CurrentValue.Auth.NegativeCacheSeconds > 0)
				_authNegativeCache[cacheKey] =
					(passwordHash, DateTime.UtcNow.AddSeconds(_options.CurrentValue.Auth.NegativeCacheSeconds));
		}
	}

	/// <summary>
	///   Recycles every cached session and per-user watcher after a live global backend-settings
	///   change, so the next request rebuilds connections against the new host/port/settings. EAS
	///   is stateless HTTP — evicting a session does NOT disconnect the phone; its next Ping/Sync
	///   transparently reopens the backend connection. Auth verdict caches are cleared too.
	/// </summary>
	private void RecycleAll()
	{
		_logger.LogInformation("Backend settings changed — recycling all backend sessions");
		foreach (string key in _sessions.Keys.ToList())
			if (_sessions.TryRemove(key, out Lazy<Task<CompositeBackendSession>>? removed))
				_ = DisposeLazyAsync(removed);
		_authCache.Clear();
		_authNegativeCache.Clear();
		// With no live sessions left, every provider's per-user resources (IDLE watchers) are trimmed.
		foreach (IBackendProvider provider in _registry.All)
			if (provider is IPerUserResourceOwner owner)
				owner.TrimUserResources(new HashSet<string>(StringComparer.Ordinal));
	}

	private void EvictIdleSessions()
	{
		DateTime cutoff = DateTime.UtcNow.AddMinutes(-_options.CurrentValue.Eas.SessionIdleMinutes);
		foreach ((string key, Lazy<Task<CompositeBackendSession>> lazy) in _sessions)
			if (IsBuilt(lazy) && Built(lazy).LastUsedUtc < cutoff &&
			    _sessions.TryRemove(key, out Lazy<Task<CompositeBackendSession>>? removed))
			{
				_logger.LogDebug("Evicting idle backend session {Key}", key.Replace('\n', '/'));
				_ = DisposeLazyAsync(removed);
			}

		// Expired auth-cache entries are dead weight; drop them so the caches stay
		// bounded by the set of recently active usernames.
		DateTime nowUtc = DateTime.UtcNow;
		foreach (KeyValuePair<string, (string PasswordHash, DateTime ExpiresUtc)> pair in _authCache)
			if (pair.Value.ExpiresUtc <= nowUtc)
				_authCache.TryRemove(pair); // pair-overload: no-op if refreshed meanwhile
		foreach (KeyValuePair<string, (string PasswordHash, DateTime ExpiresUtc)> pair in _authNegativeCache)
			if (pair.Value.ExpiresUtc <= nowUtc)
				_authNegativeCache.TryRemove(pair);

		// Providers with per-user caches (IDLE watchers) trim users without live sessions.
		HashSet<string> activeUsers = _sessions.Keys
			.Select(k => k[..k.IndexOf('\n')])
			.ToHashSet(StringComparer.Ordinal);
		foreach (IBackendProvider provider in _registry.All)
			if (provider is IPerUserResourceOwner owner)
				owner.TrimUserResources(activeUsers);
	}

	/// <summary>
	///   Awaits a lazily-built session (K61) and disposes it. A never-materialized slot owns
	///   nothing; a build that faulted left no session to dispose.
	/// </summary>
	private async Task DisposeLazyAsync(Lazy<Task<CompositeBackendSession>> lazy)
	{
		if (!lazy.IsValueCreated)
			return;
		CompositeBackendSession session;
		try
		{
			session = await AwaitBuild(lazy).ConfigureAwait(false);
		}
		catch
		{
			return;
		}

		await DisposeSessionAsync(session).ConfigureAwait(false);
	}

	// K61: the per-(user, device) cache holds a Lazy<Task<CompositeBackendSession>> — the standard
	// async-lazy idiom (Stephen Toub's). The value factory returns the hot Task from
	// CompositeBackendSession.CreateAsync, which yields at its first await, so `.Value` returns that
	// Task WITHOUT ever blocking a thread — the VSTHRD011 "blocking value factory" deadlock case does
	// not apply. AwaitBuild awaits that shared Task (VSTHRD003: safe here — the whole codebase runs
	// with no synchronization context and ConfigureAwait(false), so the "foreign task" deadlock cannot
	// arise). Result is only read through Built(), always guarded by IsBuilt(), so the task is already
	// completed and `.Result` returns synchronously (VSTHRD002). This block is the single, justified
	// home for those suppressions.
#pragma warning disable VSTHRD002, VSTHRD003, VSTHRD011
	private static Lazy<Task<CompositeBackendSession>> MakeLazy(Func<Task<CompositeBackendSession>> factory) =>
		new(factory);

	private static async Task<CompositeBackendSession> AwaitBuild(Lazy<Task<CompositeBackendSession>> lazy) =>
		await lazy.Value.ConfigureAwait(false);

	private static bool IsBuilt(Lazy<Task<CompositeBackendSession>> lazy) =>
		lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully;

	private static CompositeBackendSession Built(Lazy<Task<CompositeBackendSession>> lazy) => lazy.Value.Result;
#pragma warning restore VSTHRD002, VSTHRD003, VSTHRD011

	private async Task DisposeSessionAsync(CompositeBackendSession session)
	{
		try
		{
			await session.DisposeAsync().ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			_logger.LogDebug(ex, "Error disposing backend session");
		}
	}

	private static string Hash(string value)
	{
		byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
		return Convert.ToHexString(bytes);
	}
}

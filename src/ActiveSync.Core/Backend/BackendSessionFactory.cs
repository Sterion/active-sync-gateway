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
	private readonly ConcurrentDictionary<string, Lazy<CompositeBackendSession>> _sessions = new();

	public BackendSessionFactory(
		IOptionsMonitor<ActiveSyncOptions> options,
		AccountResolver resolver,
		ISyncDbContextFactory dbFactory,
		BackendProviderRegistry registry,
		ILogger<BackendSessionFactory> logger)
	{
		_options = options;
		_resolver = resolver;
		_dbFactory = dbFactory;
		_registry = registry;
		_logger = logger;
		_evictionTimer = new Timer(_ => EvictIdleSessions(), null,
			TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
		// Account edits (eas user ...) must apply on the next request, not after the auth
		// cache TTL — a rebuilt snapshot resets both verdict caches.
		_resolver.SnapshotChanged += () =>
		{
			_authCache.Clear();
			_authNegativeCache.Clear();
		};
		// Per-user live-count gauge. Keys are "user\ndevice"; only materialized Lazy
		// values count (an unrealized slot is not a live connection).
		GatewayMetrics.SetSessionsObserver(() => _sessions
			.Where(pair => pair.Value.IsValueCreated)
			.GroupBy(pair => pair.Key.Split('\n')[0], StringComparer.OrdinalIgnoreCase)
			.Select(g => new Measurement<long>(g.Count(),
				new KeyValuePair<string, object?>("user", GatewayMetrics.PerUserLabels ? g.Key : "-"))));
	}

	public async ValueTask DisposeAsync()
	{
		await _evictionTimer.DisposeAsync().ConfigureAwait(false);
		foreach ((string _, Lazy<CompositeBackendSession> lazy) in _sessions)
			if (lazy.IsValueCreated)
				await DisposeSessionAsync(lazy.Value).ConfigureAwait(false);
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

	public async Task<IBackendSession> GetSessionAsync(
		BackendCredentials credentials, string deviceId, CancellationToken ct)
	{
		await _resolver.EnsureFreshAsync(false, ct).ConfigureAwait(false);
		ResolvedAccount account = _resolver.Resolve(credentials);
		IReadOnlyList<ResolvedRole> roles = account.OrderedRoles;

		// Shared-calendar grants are read here (async) because the session constructor is
		// synchronous; a session therefore carries the grants from its build time — `eas
		// share` changes apply when the session is next rebuilt (idle eviction, restart).
		// The calendar provider merges these with its own configured SharedCollections.
		IReadOnlyList<SharedCollection> sharedCalendars =
			await LoadShareGrantsAsync(credentials.UserName, ct).ConfigureAwait(false);

		// Cache keys and rotation compares stay on the GATEWAY login/password — per-backend
		// user names never become identity, and in Accounts mode the backend credentials are
		// config-static (restart to apply changes).
		string key = $"{credentials.UserName}\n{deviceId}";
		bool created = false;
		Lazy<CompositeBackendSession> lazy = _sessions.GetOrAdd(key, _ => new Lazy<CompositeBackendSession>(() =>
		{
			created = true;
			return new CompositeBackendSession(
				_registry, credentials, account.MailAddress, roles, sharedCalendars);
		}));
		CompositeBackendSession session = lazy.Value;
		if (created)
			_logger.LogInformation("Opened backend session for {User} (device {DeviceId})",
				credentials.UserName, deviceId);

		// Credentials changed (e.g. password rotation): rebuild the session.
		if (session.Credentials.Password != credentials.Password)
		{
			if (_sessions.TryRemove(key, out Lazy<CompositeBackendSession>? stale) && stale.IsValueCreated)
				_ = DisposeSessionAsync(stale.Value);
			lazy = _sessions.GetOrAdd(key,
				_ => new Lazy<CompositeBackendSession>(() =>
					new CompositeBackendSession(
						_registry, credentials, account.MailAddress, roles, sharedCalendars)));
			session = lazy.Value;
		}

		session.LastUsedUtc = DateTime.UtcNow;
		return session;
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

	private void EvictIdleSessions()
	{
		DateTime cutoff = DateTime.UtcNow.AddMinutes(-_options.CurrentValue.Eas.SessionIdleMinutes);
		foreach ((string key, Lazy<CompositeBackendSession> lazy) in _sessions)
			if (lazy.IsValueCreated && lazy.Value.LastUsedUtc < cutoff &&
			    _sessions.TryRemove(key, out Lazy<CompositeBackendSession>? removed))
			{
				_logger.LogDebug("Evicting idle backend session {Key}", key.Replace('\n', '/'));
				if (removed.IsValueCreated)
					_ = DisposeSessionAsync(removed.Value);
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

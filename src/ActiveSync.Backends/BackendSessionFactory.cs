using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using ActiveSync.Backends.Imap;
using ActiveSync.Backends.Local;
using ActiveSync.Core.Accounts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using ActiveSync.Core.Security;
using ActiveSync.Core.State;
using MailKit.Net.Imap;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ActiveSync.Backends;

/// <summary>
///   Caches one <see cref="BackendSession" /> per (user, device) so consecutive EAS requests reuse
///   live IMAP connections. Idle sessions are evicted in the background. Successful authentications
///   are cached briefly so every HTTP request does not trigger an IMAP login round-trip.
/// </summary>
public sealed class BackendSessionFactory : IBackendSessionFactory, IAsyncDisposable
{
	private readonly ConcurrentDictionary<string, (string PasswordHash, DateTime ExpiresUtc)> _authCache = new();
	private readonly ConcurrentDictionary<string, (string PasswordHash, DateTime ExpiresUtc)> _authNegativeCache = new();
	private readonly ISyncDbContextFactory _dbFactory;
	private readonly Timer _evictionTimer;
	private readonly ILogger<BackendSessionFactory> _logger;
	private readonly LocalChangeNotifier _notifier;
	private readonly ActiveSyncOptions _options;
	private readonly LocalContentProtector _protector;
	private readonly AccountResolver _resolver;
	private readonly ConcurrentDictionary<string, Lazy<BackendSession>> _sessions = new();
	private readonly ConcurrentDictionary<string, Lazy<ImapIdleWatcher>> _watchers = new();
	private readonly ILoggerFactory _loggerFactory;
	// Verbose wire logging gets a per-backend category so one backend can be traced alone.
	private readonly ILogger _imapWireLogger;

	public BackendSessionFactory(
		IOptions<ActiveSyncOptions> options,
		AccountResolver resolver,
		ISyncDbContextFactory dbFactory,
		LocalChangeNotifier notifier,
		LocalContentProtector protector,
		ILogger<BackendSessionFactory> logger,
		ILoggerFactory loggerFactory)
	{
		_options = options.Value;
		_resolver = resolver;
		_dbFactory = dbFactory;
		_notifier = notifier;
		_protector = protector;
		_logger = logger;
		_loggerFactory = loggerFactory;
		_imapWireLogger = loggerFactory.CreateLogger("ActiveSync.Backends.Imap");
		_evictionTimer = new Timer(_ => EvictIdleSessions(), null,
			TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
		// Account edits (eas user ...) must apply on the next request, not after the auth
		// cache TTL — a rebuilt snapshot resets both verdict caches.
		_resolver.SnapshotChanged += () =>
		{
			_authCache.Clear();
			_authNegativeCache.Clear();
		};
	}

	public async ValueTask DisposeAsync()
	{
		await _evictionTimer.DisposeAsync().ConfigureAwait(false);
		foreach ((string _, Lazy<BackendSession> lazy) in _sessions)
			if (lazy.IsValueCreated)
				await DisposeSessionAsync(lazy.Value).ConfigureAwait(false);
		_sessions.Clear();
		foreach ((string _, Lazy<ImapIdleWatcher> lazy) in _watchers)
			if (lazy.IsValueCreated)
				await DisposeWatcherAsync(lazy.Value).ConfigureAwait(false);
		_watchers.Clear();
	}

	public async Task<bool> AuthenticateAsync(BackendCredentials credentials, CancellationToken ct)
	{
		await _resolver.EnsureFreshAsync(false, ct).ConfigureAwait(false);
		string cacheKey = credentials.UserName;
		string passwordHash = Hash(credentials.Password);
		if (_options.Auth.SuccessCacheMinutes > 0 &&
		    _authCache.TryGetValue(cacheKey, out (string PasswordHash, DateTime ExpiresUtc) cached) &&
		    cached.PasswordHash == passwordHash && cached.ExpiresUtc > DateTime.UtcNow)
			return true;
		// Repeats of a known-bad password are refused without an IMAP round-trip, so the
		// gateway cannot be used to hammer the mail backend with login attempts.
		if (_options.Auth.NegativeCacheSeconds > 0 &&
		    _authNegativeCache.TryGetValue(cacheKey, out (string PasswordHash, DateTime ExpiresUtc) negative) &&
		    negative.PasswordHash == passwordHash && negative.ExpiresUtc > DateTime.UtcNow)
			return false;

		// Declared users may carry a local rule (gateway Password override or a configured
		// Imap:Password the presented value must equal); RequireDeclaredUsers rejects
		// undeclared logins outright. The caches above bound the PBKDF2/compare cost, and
		// a false verdict feeds the per-IP throttle via the normal 401 path.
		if (_resolver.VerifyLocally(credentials.UserName, credentials.Password) is { } verified)
		{
			if (verified)
			{
				if (_options.Auth.SuccessCacheMinutes > 0)
					_authCache[cacheKey] = (passwordHash, DateTime.UtcNow.AddMinutes(_options.Auth.SuccessCacheMinutes));
				_authNegativeCache.TryRemove(cacheKey, out _);
			}
			else
			{
				_authCache.TryRemove(cacheKey, out _);
				if (_options.Auth.NegativeCacheSeconds > 0)
					_authNegativeCache[cacheKey] =
						(passwordHash, DateTime.UtcNow.AddSeconds(_options.Auth.NegativeCacheSeconds));
			}

			return verified;
		}

		// No local rule: the presented password is the IMAP password — probe the user's
		// EFFECTIVE IMAP endpoint/username, so per-user Host/UserName overrides apply.
		ResolvedAccount probeAccount = _resolver.Resolve(credentials);
		try
		{
			using ImapClient client = await ImapConnectionFactory
				.ConnectAsync(probeAccount.Imap.Options, probeAccount.Imap.Credentials, ct, _imapWireLogger)
				.ConfigureAwait(false);
			await client.DisconnectAsync(true, ct).ConfigureAwait(false);
			if (_options.Auth.SuccessCacheMinutes > 0)
				_authCache[cacheKey] = (passwordHash, DateTime.UtcNow.AddMinutes(_options.Auth.SuccessCacheMinutes));
			_authNegativeCache.TryRemove(cacheKey, out _);
			return true;
		}
		catch (AuthenticationException)
		{
			_authCache.TryRemove(cacheKey, out _);
			if (_options.Auth.NegativeCacheSeconds > 0)
				_authNegativeCache[cacheKey] =
					(passwordHash, DateTime.UtcNow.AddSeconds(_options.Auth.NegativeCacheSeconds));
			return false;
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			_logger.LogError(ex, "IMAP authentication probe failed for {User}", credentials.UserName);
			throw new BackendException("Mail backend unreachable.", ex);
		}
	}

	public async Task<IBackendSession> GetSessionAsync(
		BackendCredentials credentials, string deviceId, CancellationToken ct)
	{
		await _resolver.EnsureFreshAsync(false, ct).ConfigureAwait(false);
		ResolvedAccount account = _resolver.Resolve(credentials);

		// Watchers are resolved lazily per folder at wait time (a Ping decides then which
		// folder deserves the IDLE connection), so the session gets a provider closure.
		ImapIdleWatcher? WatcherProvider(string folderFullName)
		{
			return GetOrCreateWatcher(account, folderFullName);
		}

		// Shared-calendar grants are read here (async) because the session constructor is
		// synchronous; a session therefore carries the grants from its build time — `eas
		// share` changes apply when the session is next rebuilt (idle eviction, restart).
		IReadOnlyList<SharedCollection> sharedCalendars =
			await LoadSharedCalendarsAsync(account, credentials.UserName, ct).ConfigureAwait(false);

		// Cache keys and rotation compares stay on the GATEWAY login/password — per-backend
		// user names never become identity, and in Accounts mode the backend credentials are
		// config-static (restart to apply changes).
		string key = $"{credentials.UserName}\n{deviceId}";
		bool created = false;
		Lazy<BackendSession> lazy = _sessions.GetOrAdd(key, _ => new Lazy<BackendSession>(() =>
		{
			created = true;
			return new BackendSession(
				account, credentials, _dbFactory, _notifier, _protector, WatcherProvider, _logger, _loggerFactory,
				sharedCalendars);
		}));
		BackendSession session = lazy.Value;
		if (created)
			_logger.LogInformation("Opened backend session for {User} (device {DeviceId})",
				credentials.UserName, deviceId);

		// Credentials changed (e.g. password rotation): rebuild the session.
		if (session.Credentials.Password != credentials.Password)
		{
			if (_sessions.TryRemove(key, out Lazy<BackendSession>? stale) && stale.IsValueCreated)
				_ = DisposeSessionAsync(stale.Value);
			lazy = _sessions.GetOrAdd(key,
				_ => new Lazy<BackendSession>(() =>
					new BackendSession(
						account, credentials, _dbFactory, _notifier, _protector, WatcherProvider, _logger,
						_loggerFactory, sharedCalendars)));
			session = lazy.Value;
		}

		session.LastUsedUtc = DateTime.UtcNow;
		return session;
	}

	/// <summary>
	///   Config SharedCollections ∪ database `eas share` grants for this user; a grant for
	///   the same collection overrides the config entry's mode. Empty without a CalDAV side.
	/// </summary>
	private async Task<IReadOnlyList<SharedCollection>> LoadSharedCalendarsAsync(
		ResolvedAccount account, string userName, CancellationToken ct)
	{
		if (account.CalDav is null)
			return [];
		List<SharedCollection> merged = (account.CalDav.Options.SharedCollections ?? [])
			.Select(SharedCollection.Parse)
			.ToList();
		await using SyncDbContext db = _dbFactory.CreateDbContext();
		List<SharedCalendarGrant> grants = await db.SharedCalendarGrants.AsNoTracking()
			.Where(g => g.UserName == userName)
			.ToListAsync(ct).ConfigureAwait(false);
		foreach (SharedCalendarGrant grant in grants)
		{
			merged.RemoveAll(c => c.Href.TrimEnd('/') == grant.CollectionHref.TrimEnd('/'));
			merged.Add(new SharedCollection(grant.CollectionHref, grant.ReadOnly));
		}

		return merged;
	}

	/// <summary>
	///   One shared IDLE watcher per (gateway user, folder) — all of the user's devices reuse
	///   it. Rebuilt on password rotation; null when IDLE is disabled by configuration.
	/// </summary>
	private ImapIdleWatcher? GetOrCreateWatcher(ResolvedAccount account, string folderFullName)
	{
		if (!_options.Eas.UseImapIdle)
			return null;
		string key = $"{account.GatewayLogin}\n{folderFullName}";
		Lazy<ImapIdleWatcher> lazy = _watchers.GetOrAdd(key,
			_ => new Lazy<ImapIdleWatcher>(() =>
				new ImapIdleWatcher(
					account.Imap.Options, account.Imap.Credentials, folderFullName, _logger, _imapWireLogger)));
		ImapIdleWatcher watcher = lazy.Value;
		if (watcher.Credentials.Password != account.Imap.Credentials.Password)
		{
			if (_watchers.TryRemove(key, out Lazy<ImapIdleWatcher>? stale) && stale.IsValueCreated)
				_ = DisposeWatcherAsync(stale.Value);
			watcher = _watchers.GetOrAdd(key,
					_ => new Lazy<ImapIdleWatcher>(() =>
						new ImapIdleWatcher(
							account.Imap.Options, account.Imap.Credentials, folderFullName, _logger, _imapWireLogger)))
				.Value;
		}

		return watcher;
	}

	private void EvictIdleSessions()
	{
		DateTime cutoff = DateTime.UtcNow.AddMinutes(-_options.Eas.SessionIdleMinutes);
		foreach ((string key, Lazy<BackendSession> lazy) in _sessions)
			if (lazy.IsValueCreated && lazy.Value.LastUsedUtc < cutoff &&
			    _sessions.TryRemove(key, out Lazy<BackendSession>? removed))
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

		// A user's shared IDLE watchers live exactly as long as any of their sessions.
		HashSet<string> activeUsers = _sessions.Keys
			.Select(k => k[..k.IndexOf('\n')])
			.ToHashSet(StringComparer.Ordinal);
		foreach ((string key, Lazy<ImapIdleWatcher> lazy) in _watchers)
		{
			string user = key[..key.IndexOf('\n')];
			if (!activeUsers.Contains(user) && _watchers.TryRemove(key, out Lazy<ImapIdleWatcher>? removed))
			{
				_logger.LogDebug("Evicting IMAP IDLE watcher {Key}", key.Replace('\n', '/'));
				if (removed.IsValueCreated)
					_ = DisposeWatcherAsync(removed.Value);
			}
		}
	}

	private async Task DisposeWatcherAsync(ImapIdleWatcher watcher)
	{
		try
		{
			await watcher.DisposeAsync().ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			_logger.LogDebug(ex, "Error disposing IMAP IDLE watcher");
		}
	}

	private async Task DisposeSessionAsync(BackendSession session)
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

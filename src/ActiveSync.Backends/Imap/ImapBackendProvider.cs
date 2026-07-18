using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Observability;
using ActiveSync.Core.Options;
using MailKit.Net.Imap;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ActiveSync.Backends.Imap;

/// <summary>
///   The "imap" provider: fills the MailStore role with <see cref="ImapMailBackend" /> over a
///   per-session <see cref="ImapSession" />, verifies credentials via a login probe, and owns
///   the shared per-(user, folder) IDLE watcher cache — all of a user's devices reuse one
///   watcher, and the session factory's eviction sweep trims watchers for users without
///   live sessions.
/// </summary>
public sealed class ImapBackendProvider : IBackendProvider, ICredentialVerifier, IPerUserResourceOwner,
	IAsyncDisposable
{
	private static readonly IReadOnlySet<BackendRole> Roles = new HashSet<BackendRole> { BackendRole.MailStore };

	private readonly ActiveSyncOptions _options;
	private readonly ILogger _logger;
	private readonly ILogger _wireLogger;
	private readonly ConcurrentDictionary<string, Lazy<ImapIdleWatcher>> _watchers = new();

	public ImapBackendProvider(IOptions<ActiveSyncOptions> options, ILoggerFactory loggerFactory)
	{
		_options = options.Value;
		_logger = loggerFactory.CreateLogger<ImapBackendProvider>();
		// Verbose wire logging gets a per-backend category so one backend can be traced alone.
		_wireLogger = loggerFactory.CreateLogger("ActiveSync.Backends.Imap");
		// Per-user live-count gauge. Keys are "user\nfolder"; only materialized Lazy
		// values count (an unrealized slot is not a live connection).
		GatewayMetrics.SetIdleWatchersObserver(() => _watchers
			.Where(pair => pair.Value.IsValueCreated)
			.GroupBy(pair => pair.Key.Split('\n')[0], StringComparer.OrdinalIgnoreCase)
			.Select(g => new Measurement<long>(g.Count(),
				new KeyValuePair<string, object?>("user", GatewayMetrics.PerUserLabels ? g.Key : "-"))));
	}

	public string Name => "imap";
	public IReadOnlySet<BackendRole> SupportedRoles => Roles;

	public IBackendConnection CreateConnection(BackendConnectionContext context)
	{
		ResolvedRole role = context.Roles.Single(r => r.Role == BackendRole.MailStore);
		ImapOptions options = (ImapOptions)role.Settings!;
		string gatewayLogin = context.GatewayCredentials.UserName;
		ImapSession session = new(options, role.Credentials, _logger, _wireLogger);

		// Watchers are resolved lazily per folder at wait time (a Ping decides then which
		// folder deserves the IDLE connection), so the backend gets a provider closure.
		ImapIdleWatcher? WatcherProvider(string folderFullName)
		{
			return GetOrCreateWatcher(gatewayLogin, options, role.Credentials, folderFullName);
		}

		ImapMailBackend backend = new(session, context.MailAddress, WatcherProvider, _logger);
		return new BackendConnection([backend], ownedResources: [session]);
	}

	public async Task<bool> VerifyCredentialsAsync(ResolvedRole role, CancellationToken ct)
	{
		try
		{
			using ImapClient client = await ImapConnectionFactory
				.ConnectAsync((ImapOptions)role.Settings!, role.Credentials, ct, _wireLogger)
				.ConfigureAwait(false);
			await client.DisconnectAsync(true, ct).ConfigureAwait(false);
			return true;
		}
		catch (AuthenticationException)
		{
			return false;
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			_logger.LogError(ex, "IMAP authentication probe failed for {User}", role.Credentials.UserName);
			throw new BackendException("Mail backend unreachable.", ex);
		}
	}

	public void TrimUserResources(IReadOnlySet<string> activeGatewayLogins)
	{
		// A user's shared IDLE watchers live exactly as long as any of their sessions.
		foreach ((string key, Lazy<ImapIdleWatcher> lazy) in _watchers)
		{
			string user = key[..key.IndexOf('\n')];
			if (!activeGatewayLogins.Contains(user) && _watchers.TryRemove(key, out Lazy<ImapIdleWatcher>? removed))
			{
				_logger.LogDebug("Evicting IMAP IDLE watcher {Key}", key.Replace('\n', '/'));
				if (removed.IsValueCreated)
					_ = DisposeWatcherAsync(removed.Value);
			}
		}
	}

	public async ValueTask DisposeAsync()
	{
		foreach ((string _, Lazy<ImapIdleWatcher> lazy) in _watchers)
			if (lazy.IsValueCreated)
				await DisposeWatcherAsync(lazy.Value).ConfigureAwait(false);
		_watchers.Clear();
	}

	/// <summary>
	///   One shared IDLE watcher per (gateway user, folder) — all of the user's devices reuse
	///   it. Rebuilt on password rotation; null when IDLE is disabled by configuration.
	/// </summary>
	private ImapIdleWatcher? GetOrCreateWatcher(
		string gatewayLogin, ImapOptions options, BackendCredentials credentials, string folderFullName)
	{
		if (!_options.Eas.UseImapIdle)
			return null;
		string key = $"{gatewayLogin}\n{folderFullName}";
		Lazy<ImapIdleWatcher> lazy = _watchers.GetOrAdd(key,
			_ => new Lazy<ImapIdleWatcher>(() =>
				new ImapIdleWatcher(options, credentials, folderFullName, _logger, _wireLogger)));
		ImapIdleWatcher watcher = lazy.Value;
		if (watcher.Credentials.Password != credentials.Password)
		{
			if (_watchers.TryRemove(key, out Lazy<ImapIdleWatcher>? stale) && stale.IsValueCreated)
				_ = DisposeWatcherAsync(stale.Value);
			watcher = _watchers.GetOrAdd(key,
					_ => new Lazy<ImapIdleWatcher>(() =>
						new ImapIdleWatcher(options, credentials, folderFullName, _logger, _wireLogger)))
				.Value;
		}

		return watcher;
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
}

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using ActiveSync.Backends.Common;
using ActiveSync.Contracts;
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
	IReadinessSource, IWatcherDiagnostics, IAsyncDisposable
{
	private static readonly IReadOnlySet<BackendRole> Roles = new HashSet<BackendRole> { BackendRole.MailStore };

	private readonly IOptionsMonitor<ActiveSyncOptions> _options;
	private readonly ILogger _logger;
	private readonly ILogger _wireLogger;
	private readonly ConcurrentDictionary<string, Lazy<ImapIdleWatcher>> _watchers = new();

	public ImapBackendProvider(IOptionsMonitor<ActiveSyncOptions> options, ILoggerFactory loggerFactory)
	{
		_options = options;
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

	public void ValidateConfiguration(BackendRole role, ProviderSettings settings, IList<string> failures)
	{
		ImapOptions options = settings.Bind<ImapOptions>();
		string context = $"imap ({role})";
		BackendSettingsValidation.RequiredHost(options.Host, context, failures);
		BackendSettingsValidation.Port(options.Port, context, failures);
		BackendSettingsValidation.Choice(options.Security, "Security", context, failures,
			"None", "SslOnConnect", "StartTls", "StartTlsWhenAvailable", "Auto");
		BackendSettingsValidation.CaPath(options.CaCertificatePath, context, failures);
	}

	public IReadOnlyList<BackendConfigField> DescribeConfiguration(BackendRole role)
	{
		return
		[
			.. BackendSchemaFields.MailConnection(993),
			new BackendConfigField("PathSeparator", "Folder path separator", BackendFieldType.String,
				Help: "Override for the IMAP hierarchy delimiter (a single character). Autodetected when empty.")
		];
	}

	public string DescribeRole(BackendRole role, ProviderSettings settings)
	{
		ImapOptions options = settings.Bind<ImapOptions>();
		return $"imap {options.Host}:{options.Port} " +
		       $"(ssl={(options.UseSsl ? "on" : "off")}, security={options.Security ?? "auto"}, " +
		       $"cert={BackendDescription.DescribeCert(options.AllowInvalidCertificates, options.CaCertificatePath)})";
	}

	public IBackendConnection CreateConnection(BackendConnectionContext context)
	{
		ResolvedRole role = context.Roles.Single(r => r.Role == BackendRole.MailStore);
		ImapOptions options = role.Settings.Bind<ImapOptions>();
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
				.ConnectAsync(role.Settings.Bind<ImapOptions>(), role.Credentials, ct, _wireLogger)
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

	/// <summary>Reachability only — a TCP connect to the configured listener, no credentials.</summary>
	public async Task<bool> ProbeReadinessAsync(ProviderSettings settings, CancellationToken ct)
	{
		ImapOptions options = settings.Bind<ImapOptions>();
		using System.Net.Sockets.TcpClient client = new();
		await client.ConnectAsync(options.Host, options.Port, ct).ConfigureAwait(false);
		return true;
	}

	/// <summary>Live (materialized) IDLE watchers for the admin dashboard.</summary>
	public IReadOnlyList<WatcherInfo> SnapshotWatchers()
	{
		List<WatcherInfo> watchers = new();
		foreach ((string key, Lazy<ImapIdleWatcher> lazy) in _watchers)
		{
			if (!lazy.IsValueCreated)
				continue;
			int separator = key.IndexOf('\n');
			watchers.Add(new WatcherInfo(
				separator < 0 ? key : key[..separator],
				separator < 0 ? "" : key[(separator + 1)..]));
		}

		return watchers;
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
		if (!_options.CurrentValue.Eas.UseImapIdle)
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

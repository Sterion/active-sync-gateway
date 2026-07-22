using System.Collections.Concurrent;
using ActiveSync.Backends.Common;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ActiveSync.Backends.Jmap;

/// <summary>
///   The "jmap" provider: fills mail roles (store + submit) over a single JMAP HTTP session
///   per account (RFC 8620/8621). Later stages add OOF, Contacts and Calendar to the same
///   session. Verifies credentials by fetching the session resource, and probes readiness
///   with an unauthenticated session-resource GET.
/// </summary>
public sealed class JmapBackendProvider : IBackendProvider, ICredentialVerifier, IReadinessSource,
	IPerUserResourceOwner, IAsyncDisposable
{
	private static readonly IReadOnlySet<BackendRole> Roles = new HashSet<BackendRole>
	{
		BackendRole.MailStore, BackendRole.MailSubmit, BackendRole.Oof,
		BackendRole.Contacts, BackendRole.Calendar
	};

	private readonly IOptionsMonitor<ActiveSyncOptions> _options;
	private readonly ILogger _logger;
	private readonly ILogger _wireLogger;

	// One shared EventSource (SSE) watcher per gateway user — all their sessions reuse it, and
	// the session factory's eviction sweep trims watchers for users with no live session.
	private readonly ConcurrentDictionary<string, Lazy<JmapEventSourceWatcher>> _watchers = new();

	public JmapBackendProvider(IOptionsMonitor<ActiveSyncOptions> options, ILoggerFactory loggerFactory)
	{
		_options = options;
		_logger = loggerFactory.CreateLogger<JmapBackendProvider>();
		_wireLogger = loggerFactory.CreateLogger("ActiveSync.Backends.Jmap");
	}

	public string Name => "jmap";
	public IReadOnlySet<BackendRole> SupportedRoles => Roles;

	public void ValidateConfiguration(BackendRole role, ProviderSettings settings, IList<string> failures)
	{
		JmapOptions options = settings.Bind<JmapOptions>();
		string context = $"jmap ({role})";
		BackendSettingsValidation.AbsoluteHttpUrl(options.BaseUrl, context, failures);
		BackendSettingsValidation.CaPath(options.CaCertificatePath, context, failures);
	}

	public IReadOnlyList<BackendConfigField> DescribeConfiguration(BackendRole role)
	{
		return
		[
			new BackendConfigField("BaseUrl", "Base URL", BackendFieldType.Url, Required: true,
				Help: "Absolute http(s) URL of the JMAP server, e.g. https://mail.example.com. " +
				      "The session is fetched from /.well-known/jmap. " +
				      "One server serving several roles repeats the same URL in each."),
			.. BackendSchemaFields.Network()
		];
	}

	public string DescribeRole(BackendRole role, ProviderSettings settings)
	{
		JmapOptions options = settings.Bind<JmapOptions>();
		return $"jmap {options.BaseUrl} " +
		       $"(cert={BackendDescription.DescribeCert(options.AllowInvalidCertificates, options.CaCertificatePath)})";
	}

	public Task<IBackendConnection> CreateConnectionAsync(BackendConnectionContext context, CancellationToken ct)
	{
		// One session serves every jmap role assigned to this account; anchor it on the
		// MailStore role's settings/credentials when present (the auth identity), else the
		// first assigned role.
		ResolvedRole primary = context.Roles.FirstOrDefault(r => r.Role == BackendRole.MailStore)
		                       ?? context.Roles[0];
		JmapOptions options = primary.Settings.Bind<JmapOptions>();
		JmapClient client = new(
			new Uri(options.BaseUrl), primary.Credentials,
			options.AllowInvalidCertificates, options.CaCertificatePath, _wireLogger);

		// Mail Ping/Sync waits accelerate off a shared per-user EventSource watcher (poll is the
		// backstop). The watcher self-disables if the server advertises no eventSourceUrl.
		Func<DateTime, CancellationToken, Task>? waitForPush = null;
		if (context.Roles.Any(r => r.Role == BackendRole.MailStore))
			waitForPush = GetOrCreateWatcher(context.GatewayCredentials.UserName, options, primary.Credentials)
				.WaitForChangeAsync;

		List<IContentStore> stores = new();
		IMailSubmitOperations? submit = null;
		IOofBackend? oof = null;
		foreach (ResolvedRole role in context.Roles)
			switch (role.Role)
			{
				case BackendRole.MailStore:
					stores.Add(new JmapMailStore(client, context.MailAddress, _options.CurrentValue.Eas.DavPollSeconds, waitForPush));
					break;
				case BackendRole.MailSubmit:
					submit = new JmapMailSubmit(client, context.MailAddress, _logger);
					break;
				case BackendRole.Oof:
					oof = new JmapOofBackend(client);
					break;
				case BackendRole.Contacts:
					stores.Add(new JmapContactStore(client, _options.CurrentValue.Eas.DavPollSeconds));
					break;
				case BackendRole.Calendar:
					stores.Add(new JmapCalendarStore(client, context.MailAddress, _options.CurrentValue.Eas.DavPollSeconds));
					break;
			}

		return Task.FromResult<IBackendConnection>(new BackendConnection(stores, submit, oof, ownedResources: [client]));
	}

	public async Task<bool> VerifyCredentialsAsync(ResolvedRole role, CancellationToken ct)
	{
		JmapOptions options = role.Settings.Bind<JmapOptions>();
		using JmapClient client = new(
			new Uri(options.BaseUrl), role.Credentials,
			options.AllowInvalidCertificates, options.CaCertificatePath, _wireLogger);
		try
		{
			await client.GetSessionAsync(ct).ConfigureAwait(false);
			return true;
		}
		catch (JmapAuthenticationException)
		{
			return false;
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			_logger.LogError(ex, "JMAP authentication probe failed for {User}", role.Credentials.UserName);
			throw new BackendException("Mail backend unreachable.", ex);
		}
	}

	/// <summary>Reachability only — an unauthenticated GET of the session resource; any HTTP answer counts.</summary>
	public async Task<bool> ProbeReadinessAsync(ProviderSettings settings, CancellationToken ct)
	{
		JmapOptions options = settings.Bind<JmapOptions>();
		if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out Uri? baseUri))
			return true; // misconfiguration is a startup-validation concern, not a readiness failure
		// H26: reuse a pooled handler per TLS shape instead of building and discarding one (with
		// its connection pool) on every readiness sweep.
		using HttpClient http = BackendHttpClientFactory.CreateProbeClient(
			options.AllowInvalidCertificates, options.CaCertificatePath, TimeSpan.FromSeconds(5));
		// H31: the response is needed only for disposal (any HTTP answer = reachable), so it is a
		// throwaway rather than a read local.
		using HttpResponseMessage _ = await http.GetAsync(
			new Uri(baseUri, "/.well-known/jmap"), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
		return true;
	}

	public void TrimUserResources(IReadOnlySet<string> activeGatewayLogins)
	{
		foreach ((string user, Lazy<JmapEventSourceWatcher> lazy) in _watchers)
			if (!activeGatewayLogins.Contains(user) && _watchers.TryRemove(user, out Lazy<JmapEventSourceWatcher>? removed))
			{
				_logger.LogDebug("Evicting JMAP EventSource watcher for {User}", user);
				if (removed.IsValueCreated)
					_ = DisposeWatcherAsync(removed.Value);
				_ = lazy;
			}
	}

	public async ValueTask DisposeAsync()
	{
		foreach ((string _, Lazy<JmapEventSourceWatcher> lazy) in _watchers)
			if (lazy.IsValueCreated)
				await DisposeWatcherAsync(lazy.Value).ConfigureAwait(false);
		_watchers.Clear();
	}

	/// <summary>One shared EventSource watcher per gateway user; rebuilt on password rotation.</summary>
	private JmapEventSourceWatcher GetOrCreateWatcher(string gatewayLogin, JmapOptions options, BackendCredentials credentials)
	{
		JmapEventSourceWatcher Build()
		{
			// Infinite HTTP timeout: this client holds a long-lived EventSource (SSE) stream open, so
			// the default 100 s request cap would abort it mid-flight every ~100 s (H17). The stream's
			// own ping keep-alive and the watcher's cancellation token bound its lifetime instead.
			JmapClient watcherClient = new(
				new Uri(options.BaseUrl), credentials,
				options.AllowInvalidCertificates, options.CaCertificatePath, _wireLogger,
				httpTimeout: Timeout.InfiniteTimeSpan);
			return new JmapEventSourceWatcher(watcherClient, credentials, _logger);
		}

		JmapEventSourceWatcher watcher = _watchers
			.GetOrAdd(gatewayLogin, _ => new Lazy<JmapEventSourceWatcher>(Build)).Value;
		if (watcher.Credentials.Password != credentials.Password)
		{
			if (_watchers.TryRemove(gatewayLogin, out Lazy<JmapEventSourceWatcher>? stale) && stale.IsValueCreated)
				_ = DisposeWatcherAsync(stale.Value);
			watcher = _watchers.GetOrAdd(gatewayLogin, _ => new Lazy<JmapEventSourceWatcher>(Build)).Value;
		}

		return watcher;
	}

	private async Task DisposeWatcherAsync(JmapEventSourceWatcher watcher)
	{
		try
		{
			await watcher.DisposeAsync().ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			_logger.LogDebug(ex, "Error disposing JMAP EventSource watcher");
		}
	}
}

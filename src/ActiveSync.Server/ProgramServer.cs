using System.Security.Cryptography.X509Certificates;
using ActiveSync.Backends.Local;
using ActiveSync.Core.Accounts;
using ActiveSync.Core.Administration;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using ActiveSync.Core.Security;
using ActiveSync.Core.Settings;
using ActiveSync.Core.State;
using ActiveSync.Server;
using ActiveSync.Server.Eas;
using ActiveSync.Server.Setup;
using ActiveSync.WebUi.Setup;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using Serilog;
using ILogger = Microsoft.Extensions.Logging.ILogger;

// Partial of the top-level-statements Program (global namespace) — the WebApplicationFactory
// marker class. The server pipeline lives here so Program.cs stays a thin CLI dispatcher.
public partial class Program
{
	/// <summary>The post-build initialization results carried from <see cref="InitializeAsync" /> to the banner.</summary>
	private readonly record struct ServerInitResult(
		ILogger StartupLogger,
		X509Certificate2? Certificate,
		TlsCertificateSource TlsSource,
		AccountResolver Resolver);

	/// <summary>
	///   The gateway web host — the `serve` command. Args flow into configuration, so
	///   `--ActiveSync:Section:Key=value` overrides keep working. This method is the composition
	///   root; the phases are extracted into <see cref="ConfigureConfiguration" />,
	///   <see cref="ConfigureHosting" />, <see cref="InitializeAsync" /> and
	///   <see cref="LogStartupBanner" />.
	/// </summary>
	internal static async Task<int> RunServerAsync(string[] args)
	{
		Log.Logger = new LoggerConfiguration()
			.WriteTo.Console()
			.CreateBootstrapLogger();

		WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

		DbSettingsConfigurationSource settingsSource = ConfigureConfiguration(builder);

		// A completely absent ActiveSync section is fine — the gateway starts UNCONFIGURED (mail
		// can be set later via `eas config set`). Only the bootstrap Encryption key/AllowPlaintext
		// is still required, validated after Build.
		ActiveSyncOptions options = builder.Configuration.GetSection("ActiveSync").Get<ActiveSyncOptions>()
			?? new ActiveSyncOptions();

		// HTTPS: the certificate is loaded after migrations (InitializeAsync) but Kestrel binds the
		// listener now, so its selector reads this holder per handshake. The volatile field inside
		// gives the cross-thread ordering the startup path and the Kestrel connection threads need
		// (E20). Assigned once below.
		CertificateHolder certificateHolder = new();

		// Persists Information+ to the state database; its background drain begins after migrations
		// (Activate in InitializeAsync), so it never writes before the table exists.
		DatabaseLogSink databaseLogSink = new();

		ConfigureHosting(builder, options, settingsSource, databaseLogSink, () => certificateHolder.Current);

		WebApplication app = builder.Build();

		ServerInitResult init = await InitializeAsync(app, options, databaseLogSink);
		certificateHolder.Current = init.Certificate;

		// K5: feed the serving certificate's expiry to the TLS-expiry gauge (null when plaintext).
		ActiveSync.Core.Observability.GatewayMetrics.SetCertificateExpiryObserver(
			() => certificateHolder.Current is { } cert ? new DateTimeOffset(cert.NotAfter.ToUniversalTime()) : null);

		LogStartupBanner(app, options, init);

		// Report the bound addresses and public endpoints once the server is listening.
		app.Lifetime.ApplicationStarted.Register(() =>
		{
			ICollection<string>? addresses = app.Services.GetService<IServer>()?
				.Features.Get<IServerAddressesFeature>()?
				.Addresses;
			init.StartupLogger.LogInformation(
				"ActiveSync gateway ready. Listening on {Addresses}. EAS: {Eas}  Autodiscover: {Autodiscover}  Health: /healthz  Ready: /readyz{Metrics}",
				addresses is { Count: > 0 } ? string.Join(", ", addresses) : "(see Kestrel config)",
				EasEndpoint.Path, "/autodiscover/autodiscover.xml",
				options.Metrics.Enabled
					? options.Metrics.Port is { } port ? $"  Metrics: /metrics (port {port})" : "  Metrics: /metrics"
					: "");
		});

		// FIRST, before anything else the app registers: nothing may escape into the
		// developer exception page WebApplication auto-inserts in Development, which would
		// render stack traces and request headers to an unauthenticated caller.
		app.UseUnhandledExceptionShield();

		// Correct the request scheme (behind a TLS-terminating proxy the gateway is hit on
		// HTTP), so every downstream absolute URL — notably the OIDC redirect_uri — is https.
		app.UsePublicScheme();
		app.UseEasMetrics();
		app.UseEasRequestLogging();
		app.UseNosniffHeader();

		app.MapGet("/", () => Results.Text("ActiveSync gateway is running. EAS endpoint: /Microsoft-Server-ActiveSync"));
		app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
		app.MapGet("/readyz", async (HttpContext http, ReadinessProbe probe, CancellationToken ct) =>
		{
			(bool ready, Dictionary<string, bool> components) = await probe.CheckAsync(ct);
			// E16: withhold the component topology from anonymous, non-local callers — the HTTP
			// status is the readiness verdict; only a local caller (k8s node probe, operator) sees
			// which backend roles are configured.
			object body = ReadinessResponse.Body(ready, components, ReadinessResponse.IsLocal(http));
			return ready ? Results.Ok(body) : Results.Json(body, statusCode: 503);
		});
		if (options.Metrics.Enabled)
		{
			// With a dedicated port, gate on Connection.LocalPort (not spoofable, unlike
			// Host-header checks); without one, /metrics shares the main listeners and the
			// operator protects it via ingress/network policy.
			int? metricsPort = options.Metrics.Port;
			app.MapPrometheusScrapingEndpoint()
				.AddEndpointFilter(async (context, next) =>
					metricsPort is null || context.HttpContext.Connection.LocalPort == metricsPort
						? await next(context)
						: Results.NotFound());
		}

		EasEndpoint.Map(app);
		AutodiscoverEndpoint.Map(app);
		app.MapWebUi();
		ActiveSync.Server.Cli.LocalCliEndpoint.Map(app);

		await app.RunAsync();
		return 0;
	}

	/// <summary>
	///   Builds the configuration: merges the mounted users file (k8s Secret/ConfigMap) and layers
	///   the database-backed global settings LAST so database values win over appsettings/env, which
	///   win over the code (POCO) defaults. The settings are loaded synchronously from the bootstrap
	///   Database options BEFORE the host is built, so restart-bound settings stored only in the
	///   database (listener ports, TLS/metrics enable) take effect on the next start; live changes are
	///   then polled by SettingsRefreshService. The two bootstrap sections (Database, Encryption) are
	///   never stored in the database — they are needed to open and decrypt it.
	/// </summary>
	private static DbSettingsConfigurationSource ConfigureConfiguration(WebApplicationBuilder builder)
	{
		// Full configuration shape ({ "ActiveSync": { "Users": ... } }); restart to apply changes.
		if (ResolveUsersFilePath(builder.Configuration["ActiveSync:UsersFile"]) is { } usersFilePath)
			builder.Configuration.AddJsonFile(usersFilePath, false, false);

		DbSettingsConfigurationSource settingsSource = new();
		DatabaseOptions bootstrapDatabase =
			builder.Configuration.GetSection("ActiveSync:Database").Get<DatabaseOptions>() ?? new DatabaseOptions();
		settingsSource.Provider.SetData(DbSettingsLoader.TryLoad(bootstrapDatabase,
			new Serilog.Extensions.Logging.SerilogLoggerFactory(Log.Logger).CreateLogger("ActiveSync.Settings")));
		builder.Configuration.Sources.Add(settingsSource);
		return settingsSource;
	}

	/// <summary>
	///   Resolves the optional mounted users file (<c>ActiveSync:UsersFile</c>) to an absolute
	///   path, or null when the setting is unset. Throws a message naming the setting and the
	///   resolved absolute path when the file is missing (E19) — otherwise the operator gets a
	///   raw <see cref="FileNotFoundException" /> from deep in the configuration builder with no
	///   hint at the typo'd mount path.
	/// </summary>
	internal static string? ResolveUsersFilePath(string? usersFile)
	{
		if (string.IsNullOrWhiteSpace(usersFile))
			return null;
		string resolved = Path.GetFullPath(usersFile);
		if (!File.Exists(resolved))
			throw new InvalidOperationException(
				$"The users file configured by ActiveSync:UsersFile was not found: '{resolved}'.");
		return resolved;
	}

	/// <summary>
	///   Configures the host: service-provider validation, Serilog, options binding, Kestrel limits
	///   and TLS listener, all DI registrations, and out-of-repo plugin loading. The Kestrel HTTPS
	///   selector reads the certificate through <paramref name="certificateSelector" /> so the
	///   listener can bind before the certificate is loaded in <see cref="InitializeAsync" />.
	/// </summary>
	private static void ConfigureHosting(
		WebApplicationBuilder builder, ActiveSyncOptions options,
		DbSettingsConfigurationSource settingsSource, DatabaseLogSink databaseLogSink,
		Func<X509Certificate2?> certificateSelector)
	{
		// DI lifetime bugs must fail fast in every environment (Development-only scope validation
		// once hid a scoped-from-root resolve that crashed real deployments at startup). The
		// per-resolution cost is negligible at this app's request volume.
		builder.Host.UseDefaultServiceProvider(o =>
		{
			o.ValidateScopes = true;
			o.ValidateOnBuild = true;
		});

		builder.Host.UseSerilog((context, configuration) =>
		{
			configuration
				.ReadFrom.Configuration(context.Configuration)
				.Enrich.FromLogContext();
			LoggingSetup.ConfigureConsole(configuration, context.Configuration);
			configuration.WriteTo.Sink(databaseLogSink);
		});

		builder.Services.AddOptions<ActiveSyncOptions>()
			.Bind(builder.Configuration.GetSection("ActiveSync"));

		// Long-polls end themselves on ApplicationStopping (Ping answers status 1, Sync answers
		// empty), so shutdown is normally instant; this backstop bounds pathological cases.
		builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));

		// HTTPS: on by default so phones connect without a reverse proxy. The certificate is
		// either operator-supplied (ActiveSync:Tls:CertificatePath — a mounted PEM/PFX) or a
		// self-signed one persisted in the database; both are resolved by TlsCertificateResolver
		// after migrations run. The selector reads it per handshake, so Kestrel can bind the
		// listener before the certificate is loaded.
		bool tlsEnabled = options.Tls.Enabled;

		// Long-poll friendly Kestrel limits (Ping can hold a request open for up to an hour).
		builder.WebHost.ConfigureKestrel(kestrel =>
		{
			kestrel.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(65);
			kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(60);
			kestrel.Limits.MaxRequestBodySize = 64 * 1024 * 1024;
			if (tlsEnabled)
				kestrel.ListenAnyIP(options.Tls.Port, listen =>
					listen.UseHttps(new HttpsConnectionAdapterOptions
					{
						ServerCertificateSelector = (_, _) => certificateSelector(),
					}));
			// Dedicated scrape listener: /metrics only answers on this port (see the map
			// in RunServerAsync), keeping the metric surface off the phone-facing listeners.
			if (options.Metrics is { Enabled: true, Port: { } metricsPort })
				kestrel.ListenAnyIP(metricsPort);
		});

		builder.Services.AddSyncDatabase(PostgresConnectionUri.EffectiveProvider(options.Database));
		builder.Services.AddLocalContentProtection();
		builder.Services.AddSingleton<GatewayCertificateStore>();
		builder.Services.AddSingleton<TlsCertificateResolver>();

		// Metrics: BCL Meter instruments (GatewayMetrics) exported via OpenTelemetry.
		ActiveSync.Core.Observability.GatewayMetrics.PerUserLabels = options.Metrics.PerUser;
		if (options.Metrics.Enabled)
			builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics
				.AddMeter(ActiveSync.Core.Observability.GatewayMetrics.MeterName)
				.AddPrometheusExporter());

		builder.Services.AddSingleton<ReadinessProbe>();
		builder.Services.AddScoped<SyncStateService>();
		builder.Services.AddScoped<FolderService>();
		builder.Services.AddSingleton<AccountStore>();
		builder.Services.AddSingleton<AccountResolver>();
		builder.Services.AddAdministrationServices();
		builder.Services.AddSingleton<PassThroughProvisioner>();
		builder.Services.AddSingleton<GlobalSettingStore>();
		builder.Services.AddSingleton(settingsSource.Provider);
		builder.Services.AddSingleton<SettingsRefresher>();
		builder.Services.AddHostedService<SettingsRefreshService>();
		builder.Services.AddHostedService<LogRetentionService>();
		builder.Services.AddHostedService<FolderRetentionService>();
		builder.Services.AddSingleton<AuthThrottle>();
		builder.Services.AddSingleton<LocalChangeNotifier>();
		builder.Services.AddBackendProviders();
		// Out-of-repo backend plugins register their providers here (before the registry is
		// built). A broken/incompatible plugin fails startup — see PluginLoader.
		ActiveSync.Core.Plugins.PluginLoader.LoadInto(builder.Services, builder.Configuration,
			new Serilog.Extensions.Logging.SerilogLoggerFactory(Log.Logger).CreateLogger("ActiveSync.Plugins"));
		builder.Services.AddSingleton<BackendSessionFactory>();
		builder.Services.AddSingleton<IBackendSessionFactory>(sp => sp.GetRequiredService<BackendSessionFactory>());
		builder.Services.AddEasHandlers();
		builder.AddWebUi();
	}

	/// <summary>
	///   Runs the post-build initialization in order: validate the effective (database-overlaid)
	///   configuration, validate backend roles + declared users, apply EF migrations, upgrade legacy
	///   account rows, prime the live settings view, activate the DB log sink, load the HTTPS
	///   certificate, and load database-declared users into the resolver. Fails fast on any error.
	/// </summary>
	private static async Task<ServerInitResult> InitializeAsync(
		WebApplication app, ActiveSyncOptions options, DatabaseLogSink databaseLogSink)
	{
		ILogger startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ActiveSync.Startup");

		// Validate the effective (post-build, database-overlaid) configuration once and fail fast —
		// deliberately NOT via a registered IValidateOptions / ValidateOnStart: options are now
		// live-reloadable from the database, and a bad live value must never make IOptionsMonitor /
		// IOptionsSnapshot throw on read and take a running gateway down (`eas config set` validates
		// before storing). Runs after Build so it sees the final configuration.
		ValidateOptionsResult startupValidation = new ActiveSyncOptionsValidator()
			.Validate(null, app.Services.GetRequiredService<IOptions<ActiveSyncOptions>>().Value);
		if (startupValidation.Failed)
			throw new InvalidOperationException("Invalid ActiveSync configuration:" + Environment.NewLine +
				string.Join(Environment.NewLine, startupValidation.Failures ?? []));

		// Backend role sections + declared users are validated here (not via ValidateOnStart)
		// because every named provider must exist in the registry and validate its own
		// settings — this must run before anything resolves the role config or the resolver.
		app.Services.GetRequiredService<BackendConfigurationValidator>().Validate();

		// Apply EF Core migrations first so the accounts snapshot (and the banner reading it)
		// can query the database on a fresh install. Pass ApplicationStopping (E22) so a container
		// SIGTERMed during a slow first-boot migration is interrupted rather than SIGKILLed.
		await app.ApplyMigrationsAsync(startupLogger, app.Lifetime.ApplicationStopping);

		// One-time upgrade of pre-role-model account rows (imap/calDav/... JSON shapes) —
		// without it the deserializer would silently DROP those overrides.
		await app.Services.GetRequiredService<AccountStore>()
			.UpgradeLegacyRowsAsync(startupLogger, CancellationToken.None);

		// Refresh the live database settings view now the schema exists (the build-time load
		// already covered host construction) and prime the change-stamp poll before the banner.
		await app.Services.GetRequiredService<SettingsRefresher>()
			.EnsureFreshAsync(true, CancellationToken.None);

		// Start persisting logs now the LogEntries table exists (events buffered since startup flush).
		databaseLogSink.Activate(app.Services.GetRequiredService<ISyncDbContextFactory>(),
			app.Services.GetRequiredService<IOptionsMonitor<ActiveSyncOptions>>());

		// Load the HTTPS certificate (external mount or self-signed) — the Kestrel selector picks it
		// up when the server starts listening. An unloadable external certificate throws here: fail
		// fast rather than silently serving a self-signed one.
		X509Certificate2? serverCertificate = null;
		TlsCertificateSource tlsSource = TlsCertificateSource.Disabled;
		if (options.Tls.Enabled)
			(serverCertificate, tlsSource) = await app.Services.GetRequiredService<TlsCertificateResolver>()
				.LoadForServingAsync(startupLogger, CancellationToken.None);

		// Load database-declared users into the resolver before the first request.
		AccountResolver resolver = app.Services.GetRequiredService<AccountResolver>();
		await resolver.EnsureFreshAsync(true, CancellationToken.None);

		return new ServerInitResult(startupLogger, serverCertificate, tlsSource, resolver);
	}

	/// <summary>
	///   Logs the effective-configuration banner (reads the resolved IOptions, so test/override
	///   values show) and the "no mail backend yet" hint when the gateway is unconfigured.
	/// </summary>
	private static void LogStartupBanner(WebApplication app, ActiveSyncOptions options, ServerInitResult init)
	{
		X509Certificate2? serverCertificate = init.Certificate;
		string httpsSummary = serverCertificate is null
			? "off (Tls:Enabled=false) — terminate TLS in front of the gateway"
			: init.TlsSource == TlsCertificateSource.External
				? $"mounted certificate on :{options.Tls.Port} — SHA-256 " +
				  $"{GatewayCertificateStore.Fingerprint(serverCertificate)} (from {options.Tls.CertificatePath})"
				: $"self-signed on :{options.Tls.Port} — SHA-256 " +
				  $"{GatewayCertificateStore.Fingerprint(serverCertificate)}  (trust it on the device, or mount a real certificate)";

		StartupSummary.Log(init.StartupLogger,
			app.Services.GetRequiredService<IOptions<ActiveSyncOptions>>().Value,
			init.Resolver.Roles, app.Services.GetRequiredService<BackendProviderRegistry>(),
			init.Resolver.MergedUsers, httpsSummary);

		if (!init.Resolver.Roles.IsMailConfigured)
			init.StartupLogger.LogInformation(
				"No mail backend is set yet — EAS and Autodiscover answer 503 until one is. Set it up in " +
				"the web admin under Backends (enable it with 'eas config set " +
				"ActiveSync:WebUi:Admin:Enabled true'), or from the CLI, e.g. 'eas config set " +
				"ActiveSync:Backends:MailStore:Provider imap' (and Host, and the MailSubmit role).");
	}
}

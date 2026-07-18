using System.Security.Cryptography.X509Certificates;
using ActiveSync.Backends.Local;
using ActiveSync.Core.Accounts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using ActiveSync.Core.Security;
using ActiveSync.Core.State;
using ActiveSync.Server;
using ActiveSync.Server.Eas;
using ActiveSync.Server.Setup;
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
	/// <summary>
	///   The gateway web host — the `serve` command. Args flow into configuration, so
	///   `--ActiveSync:Section:Key=value` overrides keep working.
	/// </summary>
	internal static async Task<int> RunServerAsync(string[] args)
	{
		Log.Logger = new LoggerConfiguration()
			.WriteTo.Console()
			.CreateBootstrapLogger();

		WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

		// Accounts mode: merge the mounted users file (k8s Secret/ConfigMap) before options bind.
		// Full configuration shape ({ "ActiveSync": { "Users": ... } }); restart to apply changes.
		string? usersFile = builder.Configuration["ActiveSync:UsersFile"];
		if (!string.IsNullOrWhiteSpace(usersFile))
			builder.Configuration.AddJsonFile(Path.GetFullPath(usersFile), false, false);

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
		});

		builder.Services.AddOptions<ActiveSyncOptions>()
			.Bind(builder.Configuration.GetSection("ActiveSync"))
			.ValidateOnStart();
		builder.Services.AddSingleton<IValidateOptions<ActiveSyncOptions>, ActiveSyncOptionsValidator>();

		ActiveSyncOptions options = builder.Configuration.GetSection("ActiveSync").Get<ActiveSyncOptions>()
			?? throw new InvalidOperationException("Missing 'ActiveSync' configuration section.");

		// Long-polls end themselves on ApplicationStopping (Ping answers status 1, Sync answers
		// empty), so shutdown is normally instant; this backstop bounds pathological cases.
		builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));

		// Self-signed HTTPS: on by default so phones can connect without a reverse proxy. A
		// Kestrel HTTPS endpoint in configuration (mounted real certificates) always wins —
		// the self-signed endpoint is then skipped and the database never gets involved.
		bool configuredHttps = builder.Configuration.GetSection("Kestrel:Endpoints").GetChildren()
			.Any(endpoint => endpoint["Url"]?.StartsWith("https", StringComparison.OrdinalIgnoreCase) == true);
		bool selfSignedTls = options.SelfSignedTls.Enabled && !configuredHttps;
		// Populated from the database after migrations run; the selector below reads it per
		// TLS handshake, so Kestrel can be configured before the certificate exists.
		X509Certificate2? selfSignedCertificate = null;

		// Long-poll friendly Kestrel limits (Ping can hold a request open for up to an hour).
		builder.WebHost.ConfigureKestrel(kestrel =>
		{
			kestrel.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(65);
			kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(60);
			kestrel.Limits.MaxRequestBodySize = 64 * 1024 * 1024;
			if (selfSignedTls)
				kestrel.ListenAnyIP(options.SelfSignedTls.Port, listen =>
					listen.UseHttps(new HttpsConnectionAdapterOptions
					{
						ServerCertificateSelector = (_, _) => selfSignedCertificate,
					}));
			// Dedicated scrape listener: /metrics only answers on this port (see the map
			// below), keeping the metric surface off the phone-facing listeners entirely.
			if (options.Metrics is { Enabled: true, Port: { } metricsPort })
				kestrel.ListenAnyIP(metricsPort);
		});

		builder.Services.AddSyncDatabase(PostgresConnectionUri.EffectiveProvider(options.Database));
		builder.Services.AddLocalContentProtection();
		builder.Services.AddSingleton<GatewayCertificateStore>();

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
		builder.Services.AddSingleton<AuthThrottle>();
		builder.Services.AddSingleton<LocalChangeNotifier>();
		builder.Services.AddBackendProviders();
		builder.Services.AddSingleton<BackendSessionFactory>();
		builder.Services.AddSingleton<IBackendSessionFactory>(sp => sp.GetRequiredService<BackendSessionFactory>());
		builder.Services.AddEasHandlers();

		WebApplication app = builder.Build();

		ILogger startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ActiveSync.Startup");

		// Backend role sections + declared users are validated here (not via ValidateOnStart)
		// because every named provider must exist in the registry and validate its own
		// settings — this must run before anything resolves the role config or the resolver.
		app.Services.GetRequiredService<BackendConfigurationValidator>().Validate();

		// Apply EF Core migrations first so the accounts snapshot (and the banner reading it)
		// can query the database on a fresh install.
		await app.ApplyMigrationsAsync(startupLogger);

		// One-time upgrade of pre-role-model account rows (imap/calDav/... JSON shapes) —
		// without it the deserializer would silently DROP those overrides.
		await app.Services.GetRequiredService<AccountStore>()
			.UpgradeLegacyRowsAsync(startupLogger, CancellationToken.None);

		// Load (or generate once) the self-signed certificate — the Kestrel selector above
		// picks it up when the server starts listening a few lines further down.
		if (selfSignedTls)
			selfSignedCertificate = await app.Services.GetRequiredService<GatewayCertificateStore>()
				.GetOrCreateAsync(GatewayCertificateStore.HostFromPublicUrl(options.PublicUrl),
					startupLogger, CancellationToken.None);

		// Load database-declared users into the resolver before the first request.
		AccountResolver resolver = app.Services.GetRequiredService<AccountResolver>();
		await resolver.EnsureFreshAsync(true, CancellationToken.None);

		string httpsSummary = selfSignedCertificate is not null
			? $"self-signed on :{options.SelfSignedTls.Port} — SHA-256 {GatewayCertificateStore.Fingerprint(selfSignedCertificate)}"
			  + "  (trust it on the device, or configure real TLS)"
			: configuredHttps
				? "Kestrel endpoint from configuration (self-signed endpoint skipped)"
				: "off (SelfSignedTls:Enabled=false) — terminate TLS in front of the gateway";

		// Log the effective configuration (reads the resolved IOptions, so test/override values show).
		StartupSummary.Log(startupLogger,
			app.Services.GetRequiredService<IOptions<ActiveSyncOptions>>().Value,
			resolver.Roles, app.Services.GetRequiredService<BackendProviderRegistry>(),
			resolver.MergedUsers, httpsSummary);

		// Report the bound addresses and public endpoints once the server is listening.
		app.Lifetime.ApplicationStarted.Register(() =>
		{
			ICollection<string>? addresses = app.Services.GetService<IServer>()?
				.Features.Get<IServerAddressesFeature>()?
				.Addresses;
			startupLogger.LogInformation(
				"ActiveSync gateway ready. Listening on {Addresses}. EAS: {Eas}  Autodiscover: {Autodiscover}  Health: /healthz  Ready: /readyz{Metrics}",
				addresses is { Count: > 0 } ? string.Join(", ", addresses) : "(see Kestrel config)",
				EasEndpoint.Path, "/autodiscover/autodiscover.xml",
				options.Metrics.Enabled
					? options.Metrics.Port is { } port ? $"  Metrics: /metrics (port {port})" : "  Metrics: /metrics"
					: "");
		});

		app.UseEasMetrics();
		app.UseEasRequestLogging();
		app.UseNosniffHeader();

		app.MapGet("/", () => Results.Text("ActiveSync gateway is running. EAS endpoint: /Microsoft-Server-ActiveSync"));
		app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
		app.MapGet("/readyz", async (ReadinessProbe probe, CancellationToken ct) =>
		{
			(bool ready, Dictionary<string, bool> components) = await probe.CheckAsync(ct);
			return ready
				? Results.Ok(new { status = "ready", components })
				: Results.Json(new { status = "not ready", components }, statusCode: 503);
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

		await app.RunAsync();
		return 0;
	}
}

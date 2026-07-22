using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ActiveSync.Backends.Dav;
using ActiveSync.Contracts;
using ActiveSync.Core.Accounts;
using ActiveSync.Core.Administration;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using ActiveSync.Core.Security;
using ActiveSync.Core.Settings;
using ActiveSync.Core.State;
using ActiveSync.WebUi.Auth;
using ActiveSync.WebUi.Setup;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ActiveSync.WebUi.Tests;

/// <summary>
///   The whole web UI pipeline in-process over a TestServer: real cookie authentication, real
///   authorization policies, the real CSRF filter and the real endpoint delegates. Tests that
///   need to prove what an HTTP caller can and cannot do have to go through this — the
///   endpoint bodies are lambdas closed over DI and are not reachable any other way.
///
///   Two deliberate deviations from production, both required to talk to it over plain http
///   from a test: <c>AllowInsecureCookies</c> (a Secure cookie would be dropped by the client)
///   and a stub <see cref="IBackendSessionFactory" /> that accepts every credential (the real
///   one needs a mail server). Neither weakens what is under test — the login endpoint's own
///   rules (declared account, Enabled, block, Admin flag) all still run.
/// </summary>
internal sealed class WebUiHost : IAsyncDisposable
{
	internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

	private readonly SqliteConnection _connection;
	private readonly WebApplication _app;

	private WebUiHost(SqliteConnection connection, WebApplication app, ISyncDbContextFactory factory)
	{
		_connection = connection;
		_app = app;
		Factory = factory;
	}

	internal ISyncDbContextFactory Factory { get; }

	/// <summary>
	///   Builds and starts the host. <paramref name="users" /> are the declared accounts;
	///   <paramref name="settings" /> is merged into the configuration (role assignments etc.).
	/// </summary>
	internal static async Task<WebUiHost> StartAsync(
		Dictionary<string, AccountOptions> users, Dictionary<string, string?>? settings = null)
	{
		SqliteConnection connection = new("Data Source=:memory:");
		await connection.OpenAsync();
		ConnectionFactory factory = new(connection);
		using (SyncDbContext db = factory.CreateDbContext())
			await db.Database.EnsureCreatedAsync();

		Dictionary<string, string?> configuration = new()
		{
			["ActiveSync:Encryption:AllowPlaintext"] = "true",
			["ActiveSync:WebUi:AllowInsecureCookies"] = "true",
			["ActiveSync:WebUi:Admin:Enabled"] = "true",
			["ActiveSync:WebUi:UserPortal:Enabled"] = "true"
		};
		foreach ((string key, string? value) in settings ?? [])
			configuration[key] = value;

		WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
		builder.Configuration.AddInMemoryCollection(configuration);
		builder.WebHost.UseTestServer();
		builder.Logging.ClearProviders();

		ActiveSyncOptions options = builder.Configuration.GetSection("ActiveSync").Get<ActiveSyncOptions>()
			?? new ActiveSyncOptions();
		options.Users = users;

		BackendProviderRegistry registry = new(
			[
				new ActiveSync.Backends.Local.LocalBackendProvider(null!, null!, null!),
				new CalDavBackendProvider(NullLoggerFactory.Instance),
				new CardDavBackendProvider(NullLoggerFactory.Instance)
			],
			NullLogger<BackendProviderRegistry>.Instance);
		BackendRolesProvider rolesProvider = new(builder.Configuration);

		builder.Services.AddSingleton<IOptions<ActiveSyncOptions>>(Options.Create(options));
		builder.Services.AddSingleton<IOptionsMonitor<ActiveSyncOptions>>(new StaticOptionsMonitor(options));
		builder.Services.AddSingleton<ISyncDbContextFactory>(factory);
		builder.Services.AddSingleton(new AccountStore(factory));
		builder.Services.AddAdministrationServices();
		builder.Services.AddSingleton(registry);
		builder.Services.AddSingleton(rolesProvider);
		builder.Services.AddSingleton(rolesProvider.Current);
		builder.Services.AddSingleton(provider => new AccountResolver(
			provider.GetRequiredService<IOptionsMonitor<ActiveSyncOptions>>(),
			rolesProvider, registry, provider.GetRequiredService<AccountStore>()));
		builder.Services.AddSingleton<AuthThrottle>();
		builder.Services.AddSingleton(new GlobalSettingStore(factory));
		builder.Services.AddSingleton(LocalContentProtector.CreatePlaintext());
		builder.Services.AddSingleton<GatewayCertificateStore>();
		builder.Services.AddSingleton<TlsCertificateResolver>();
		// The stub answers authentication; the concrete factory is registered only because the
		// /admin/api/state endpoint takes it for watcher diagnostics.
		builder.Services.AddSingleton<IBackendSessionFactory, AcceptEverySession>();
		builder.Services.AddSingleton<BackendSessionFactory>();
		builder.Services.AddScoped(_ => factory.CreateDbContext());
		builder.Services.AddScoped<SyncStateService>();
		builder.AddWebUi();

		WebApplication app = builder.Build();
		app.MapWebUi();
		await app.StartAsync();
		return new WebUiHost(connection, app, factory);
	}

	/// <summary>A client already carrying the session cookie of <paramref name="login" />.</summary>
	internal async Task<HttpClient> SignInAsync(string login, bool admin)
	{
		HttpClient client = _app.GetTestClient();
		client.DefaultRequestHeaders.Add(WebUiAuth.CsrfHeader, "1");
		string portal = admin ? "admin" : "user";
		HttpResponseMessage response = await client.PostAsJsonAsync(
			$"/{portal}/api/login", new { username = login, password = "irrelevant" });
		if (!response.IsSuccessStatusCode)
			throw new InvalidOperationException($"login for {login} failed: {response.StatusCode}");
		string cookie = response.Headers.GetValues("Set-Cookie")
			.First(value => value.StartsWith(WebUiAuth.CookieName, StringComparison.Ordinal))
			.Split(';')[0];
		client.DefaultRequestHeaders.Add("Cookie", cookie);
		return client;
	}

	/// <summary>An anonymous client with the CSRF header, for endpoints that need no session.</summary>
	internal HttpClient Anonymous()
	{
		HttpClient client = _app.GetTestClient();
		client.DefaultRequestHeaders.Add(WebUiAuth.CsrfHeader, "1");
		return client;
	}

	internal async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
	{
		return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(), Json);
	}

	public async ValueTask DisposeAsync()
	{
		await _app.StopAsync();
		await _app.DisposeAsync();
		_connection.Dispose();
	}

	internal static Dictionary<string, AccountOptions> Users(
		params (string Login, AccountOptions Options)[] users)
	{
		return users.ToDictionary(u => u.Login, u => u.Options, StringComparer.OrdinalIgnoreCase);
	}

	private sealed class AcceptEverySession : IBackendSessionFactory
	{
		public Task<bool> AuthenticateAsync(BackendCredentials credentials, CancellationToken ct)
		{
			return Task.FromResult(true);
		}

		public Task<IBackendSession> GetSessionAsync(
			BackendCredentials credentials, string deviceId, CancellationToken ct)
		{
			throw new NotSupportedException();
		}
	}

	private sealed class StaticOptionsMonitor(ActiveSyncOptions value) : IOptionsMonitor<ActiveSyncOptions>
	{
		public ActiveSyncOptions CurrentValue => value;

		public ActiveSyncOptions Get(string? name)
		{
			return value;
		}

		public IDisposable? OnChange(Action<ActiveSyncOptions, string?> listener)
		{
			return null;
		}
	}

	private sealed class ConnectionFactory(SqliteConnection connection) : ISyncDbContextFactory
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

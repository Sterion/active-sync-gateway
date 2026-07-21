using System.Net;
using System.Net.Http.Json;
using ActiveSync.Core.Security;
using ActiveSync.Core.Settings;
using ActiveSync.Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.Extensions.DependencyInjection;

namespace ActiveSync.Integration.Tests.Scenarios;

/// <summary>
///   The web admin interface + user portal: disabled-by-default 404s, the live enable flip,
///   the cookie login flow (declared accounts only, admin flag gating, CSRF header), and the
///   unconfigured-mode bootstrap path.
/// </summary>
[Collection("gateway")]
[Trait("Category", "Integration")]
public sealed class WebUiTests(GatewayFixture gateway)
{
	private const string AdminUser = "webadmin";
	private const string PlainUser = "webuser";
	private const string Password = "portal-pa55!";

	/// <summary>Declared users: an admin and a plain user, both with local (hashed) passwords.</summary>
	private static Dictionary<string, string?> UserSettings(bool adminOn = true, bool portalOn = true)
	{
		return new Dictionary<string, string?>
		{
			["ActiveSync:WebUi:Admin:Enabled"] = adminOn ? "true" : "false",
			["ActiveSync:WebUi:UserPortal:Enabled"] = portalOn ? "true" : "false",
			[$"ActiveSync:Users:{AdminUser}:Password"] = GatewayPasswordHasher.Hash(Password),
			[$"ActiveSync:Users:{AdminUser}:Admin"] = "true",
			[$"ActiveSync:Users:{PlainUser}:Password"] = GatewayPasswordHasher.Hash(Password)
		};
	}

	private static HttpClient Client(WebApplicationFactory<Program> factory)
	{
		// Cookie-aware client: the login sets the session cookie the API calls ride on.
		return factory.CreateDefaultClient(new CookieContainerHandler());
	}

	private static async Task<HttpResponseMessage> LoginAsync(
		HttpClient client, string portal, string user, string password)
	{
		using HttpRequestMessage request = new(HttpMethod.Post, $"/{portal}/api/login");
		request.Headers.Add("X-EAS-WebUi", "1");
		request.Content = JsonContent.Create(new { username = user, password });
		return await client.SendAsync(request);
	}

	[Fact]
	public async Task DisabledByDefault_EverythingIs404()
	{
		HttpClient client = gateway.CreateHttpClient();
		Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/admin")).StatusCode);
		Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/user")).StatusCode);
		Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/admin/api/auth/mode")).StatusCode);
		Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/shared/theme.css")).StatusCode);
	}

	[Fact]
	public async Task LoginFlow_AdminGate_CsrfHeader_AndShellAssets()
	{
		await using WebApplicationFactory<Program> factory = gateway.CreateIsolatedFactory(UserSettings());
		HttpClient client = Client(factory);

		// The shell + shared assets are served (unauthenticated — they carry no data).
		HttpResponseMessage shell = await client.GetAsync("/admin");
		Assert.Equal(HttpStatusCode.OK, shell.StatusCode);
		Assert.Contains("ActiveSync", await shell.Content.ReadAsStringAsync());
		Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/shared/theme.css")).StatusCode);
		Assert.Equal("default-src 'self'; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'",
			shell.Headers.GetValues("Content-Security-Policy").Single());

		// API without a session: 401. Login without the CSRF header: 403.
		Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/admin/api/session")).StatusCode);
		HttpResponseMessage noCsrf = await client.PostAsJsonAsync("/admin/api/login",
			new { username = AdminUser, password = Password });
		Assert.Equal(HttpStatusCode.Forbidden, noCsrf.StatusCode);

		// Wrong password: 401. Unknown login: same 401 (no enumeration).
		Assert.Equal(HttpStatusCode.Unauthorized,
			(await LoginAsync(client, "admin", AdminUser, "wrong")).StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized,
			(await LoginAsync(client, "admin", "nobody", Password)).StatusCode);

		// A declared non-admin: correct password but no admin flag -> 403 on /admin...
		Assert.Equal(HttpStatusCode.Forbidden,
			(await LoginAsync(client, "admin", PlainUser, Password)).StatusCode);
		// ...while the same account logs into the portal fine.
		Assert.Equal(HttpStatusCode.OK,
			(await LoginAsync(client, "user", PlainUser, Password)).StatusCode);

		// The admin logs in; the cookie carries the session.
		Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, "admin", AdminUser, Password)).StatusCode);
		HttpResponseMessage session = await client.GetAsync("/admin/api/session");
		Assert.Equal(HttpStatusCode.OK, session.StatusCode);
		string body = await session.Content.ReadAsStringAsync();
		Assert.Contains(AdminUser, body);
		Assert.Contains("true", body);

		// Logout kills the session.
		using HttpRequestMessage logout = new(HttpMethod.Post, "/admin/api/logout");
		logout.Headers.Add("X-EAS-WebUi", "1");
		Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(logout)).StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/admin/api/session")).StatusCode);
	}

	[Fact]
	public async Task SessionCookie_CarriesSecure_WhenTheHttpOptOutIsOff()
	{
		// GatewayFixture turns ActiveSync:WebUi:AllowInsecureCookies ON, because the suite talks
		// to the portals over plain http and a cookie container would drop a Secure cookie on
		// every response. This host turns it back OFF — the production default — so the harness
		// opt-out cannot quietly become a blind spot for C2: the real Set-Cookie the gateway
		// emits has to carry Secure.
		Dictionary<string, string?> settings = UserSettings();
		settings["ActiveSync:WebUi:AllowInsecureCookies"] = "false";
		await using WebApplicationFactory<Program> factory = gateway.CreateIsolatedFactory(settings);
		// Deliberately NOT the cookie-container client: the raw Set-Cookie header is the subject.
		using HttpClient client = factory.CreateClient(
			new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

		HttpResponseMessage login = await LoginAsync(client, "admin", AdminUser, Password);
		Assert.Equal(HttpStatusCode.OK, login.StatusCode);
		string cookie = Assert.Single(login.Headers.GetValues("Set-Cookie"),
			value => value.StartsWith("eas.webui=", StringComparison.Ordinal));
		Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task EnableFlag_AppliesLive_WithoutRestart()
	{
		// The enable keys stay ABSENT from the test config (the in-memory test collection is
		// the last configuration source, so an explicit "false" would shadow the database
		// write below — in production the database source is last and always wins).
		Dictionary<string, string?> settings = UserSettings();
		settings.Remove("ActiveSync:WebUi:Admin:Enabled");
		settings.Remove("ActiveSync:WebUi:UserPortal:Enabled");
		await using WebApplicationFactory<Program> factory = gateway.CreateIsolatedFactory(settings);
		HttpClient client = Client(factory);
		Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/admin")).StatusCode);

		// A database settings write (what `eas config set` does) flips the gate within ~1 s.
		GlobalSettingStore store = factory.Services.GetRequiredService<GlobalSettingStore>();
		await store.UpsertAsync("ActiveSync:WebUi:Admin:Enabled", "true", CancellationToken.None);
		await WaitUntil.TrueAsync(
			async () => (await client.GetAsync("/admin")).StatusCode == HttpStatusCode.OK,
			"the admin UI to enable live", TimeSpan.FromSeconds(15));

		// The portal stays independently off.
		Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/user")).StatusCode);
	}

	[Fact]
	public async Task UnconfiguredGateway_AdminLoginStillWorks_TheBootstrapPath()
	{
		// No mail backend at all — the hashed gateway password verifies locally, so the
		// admin UI is reachable to configure the gateway (the bootstrap recipe).
		await using WebApplicationFactory<Program> unconfigured =
			gateway.CreateUnconfiguredFactory(UserSettings());
		HttpClient client = Client(unconfigured);

		Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, "admin", AdminUser, Password)).StatusCode);
		Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin/api/session")).StatusCode);
	}

	[Fact]
	public async Task OidcConfigured_DisablesLocalLogin()
	{
		Dictionary<string, string?> settings = UserSettings();
		settings["ActiveSync:WebUi:Oidc:Authority"] = "https://id.example.invalid/realms/test";
		settings["ActiveSync:WebUi:Oidc:ClientId"] = "eas-gateway";
		await using WebApplicationFactory<Program> factory = gateway.CreateIsolatedFactory(settings);
		HttpClient client = Client(factory);

		// The login view switches to SSO...
		HttpResponseMessage mode = await client.GetAsync("/admin/api/auth/mode");
		Assert.Contains("oidc", await mode.Content.ReadAsStringAsync());

		// ...and the local login form no longer exists — the local password is really the
		// ActiveSync connect password, never a web credential under OIDC.
		Assert.Equal(HttpStatusCode.NotFound,
			(await LoginAsync(client, "admin", AdminUser, Password)).StatusCode);
		Assert.Equal(HttpStatusCode.NotFound,
			(await LoginAsync(client, "user", PlainUser, Password)).StatusCode);
	}

	[Fact]
	public async Task EasEndpoint_Unaffected_WhenWebUiEnabled()
	{
		await using WebApplicationFactory<Program> factory = gateway.CreateIsolatedFactory(UserSettings());
		HttpClient client = Client(factory);
		// The EAS endpoint still challenges Basic auth (the passive cookie scheme never intercepts).
		HttpResponseMessage response = await client.PostAsync("/Microsoft-Server-ActiveSync", null);
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
		Assert.Contains("Basic", response.Headers.WwwAuthenticate.ToString());
	}

	[Fact]
	public async Task CliEndpoint_RejectsNonLoopbackCallers()
	{
		// The loopback gate is the whole auth boundary for /cli. TestServer requests carry no
		// remote address (never loopback), so the endpoint must answer 404 — never execute a
		// command for a caller it can't prove is on the loopback interface.
		HttpClient client = gateway.CreateHttpClient();
		HttpResponseMessage response = await client.PostAsJsonAsync(
			"/cli", new { args = new[] { "config", "list" }, stdin = (string?)null });
		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}
}

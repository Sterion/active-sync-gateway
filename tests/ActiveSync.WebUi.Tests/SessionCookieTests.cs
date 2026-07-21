using ActiveSync.Core.State;
using ActiveSync.WebUi.Setup;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ActiveSync.WebUi.Tests;

/// <summary>
///   Cookie hardening of the web session as it is actually wired into DI (C2/C4): the session
///   cookie and the OIDC correlation/nonce cookies must carry Secure unconditionally, with one
///   explicit opt-out for plain-http local development.
/// </summary>
public sealed class SessionCookieTests : IDisposable
{
	private readonly SqliteConnection _connection;

	public SessionCookieTests()
	{
		// The DataProtection key ring is read through the state database during cookie
		// post-configuration, so a real (in-memory) schema has to be there.
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();
		using SyncDbContext db = new ConnectionFactory(_connection).CreateDbContext();
		db.Database.EnsureCreated();
	}

	public void Dispose()
	{
		_connection.Dispose();
	}

	private ServiceProvider BuildServices(params (string Key, string Value)[] settings)
	{
		WebApplicationBuilder builder = WebApplication.CreateBuilder();
		Dictionary<string, string?> configuration = new()
		{
			["ActiveSync:Encryption:AllowPlaintext"] = "true"
		};
		foreach ((string key, string value) in settings)
			configuration[key] = value;
		builder.Configuration.AddInMemoryCollection(configuration);
		builder.Services.Configure<ActiveSync.Core.Options.ActiveSyncOptions>(
			builder.Configuration.GetSection("ActiveSync"));
		builder.Services.AddSingleton<ISyncDbContextFactory>(new ConnectionFactory(_connection));
		builder.AddWebUi();
		return builder.Services.BuildServiceProvider();
	}

	private static (string Key, string Value)[] OidcConfigured()
	{
		return
		[
			("ActiveSync:WebUi:Oidc:Enabled", "true"),
			("ActiveSync:WebUi:Oidc:Authority", "https://id.example.com"),
			("ActiveSync:WebUi:Oidc:ClientId", "eas")
		];
	}

	[Fact]
	public void SessionCookie_IsSecure_ByDefault()
	{
		using ServiceProvider services = BuildServices();
		CookieAuthenticationOptions cookie = services
			.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get("WebUi");

		// SameAsRequest drops Secure on any http-resolving request — behind a TLS-terminating
		// proxy that forwards neither PublicUrl nor X-Forwarded-Proto that is the admin cookie
		// in cleartext.
		Assert.Equal(CookieSecurePolicy.Always, cookie.Cookie.SecurePolicy);
		Assert.True(cookie.Cookie.HttpOnly);
		Assert.Equal(SameSiteMode.Strict, cookie.Cookie.SameSite);
	}

	[Fact]
	public void SessionCookie_AllowsInsecure_OnlyWhenExplicitlyOptedOut()
	{
		using ServiceProvider services =
			BuildServices(("ActiveSync:WebUi:AllowInsecureCookies", "true"));
		CookieAuthenticationOptions cookie = services
			.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get("WebUi");

		Assert.Equal(CookieSecurePolicy.SameAsRequest, cookie.Cookie.SecurePolicy);
	}

	[Fact]
	public void OidcCorrelationAndNonceCookies_AreSecure_ByDefault()
	{
		using ServiceProvider services = BuildServices(OidcConfigured());
		OpenIdConnectOptions oidc = services
			.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get("Oidc");

		// COVERAGE, not a reproducer: ASP.NET Core already defaults CorrelationCookie and
		// NonceCookie to SecurePolicy.Always on this framework version, so C4's symptom does not
		// bite here. The assertion pins the property against a future framework default change
		// and against anyone re-deriving these cookies from the session cookie's settings.
		Assert.Equal(CookieSecurePolicy.Always, oidc.CorrelationCookie.SecurePolicy);
		Assert.Equal(CookieSecurePolicy.Always, oidc.NonceCookie.SecurePolicy);
	}

	[Fact]
	public void OidcCorrelationAndNonceCookies_FallBackToLax_WhenInsecureIsAllowed()
	{
		using ServiceProvider services = BuildServices(
			[.. OidcConfigured(), ("ActiveSync:WebUi:AllowInsecureCookies", "true")]);
		OpenIdConnectOptions oidc = services
			.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get("Oidc");

		// SameSite=None without Secure is discarded by every current browser, so the dev
		// opt-out has to move them to Lax — which still survives the IdP redirect back.
		Assert.Equal(CookieSecurePolicy.SameAsRequest, oidc.CorrelationCookie.SecurePolicy);
		Assert.Equal(SameSiteMode.Lax, oidc.CorrelationCookie.SameSite);
		Assert.Equal(SameSiteMode.Lax, oidc.NonceCookie.SameSite);
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

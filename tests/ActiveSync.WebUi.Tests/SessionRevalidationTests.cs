using System.Security.Claims;
using ActiveSync.Core.Accounts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using ActiveSync.Core.State;
using ActiveSync.Crypto;
using ActiveSync.WebUi.Auth;
using ActiveSync.WebUi.Setup;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ActiveSync.WebUi.Tests;

/// <summary>
///   Live revalidation of an existing web session (C3): disabling an account, blocking the
///   login, deleting it or clearing its Admin flag has to reach sessions that are ALREADY
///   signed in — the ticket is self-contained and slides for 12 hours, so without this the
///   two features the UI advertises as the lockout mechanism do nothing until it expires.
/// </summary>
public sealed class SessionRevalidationTests : IDisposable
{
	private readonly SqliteConnection _connection;

	public SessionRevalidationTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();
		using SyncDbContext db = new ConnectionFactory(_connection).CreateDbContext();
		db.Database.EnsureCreated();
	}

	public void Dispose()
	{
		_connection.Dispose();
	}

	/// <summary>Signs a principal the way the login endpoint does, then runs the cookie's validation hook.</summary>
	private async Task<CookieValidatePrincipalContext> ValidateAsync(
		Dictionary<string, AccountOptions>? users, string login, bool admin,
		bool blocked = false, DateTime? revokedAtUtc = null,
		params (string Type, string Value)[] extraClaims)
	{
		WebApplicationBuilder builder = WebApplication.CreateBuilder();
		builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
		{
			["ActiveSync:Encryption:AllowPlaintext"] = "true"
		});
		ActiveSyncOptions options = new()
		{
			Encryption = new EncryptionOptions { AllowPlaintext = true },
			Users = users
		};
		ConnectionFactory factory = new(_connection);
		builder.Services.AddSingleton<IOptions<ActiveSyncOptions>>(Options.Create(options));
		builder.Services.AddSingleton<IOptionsMonitor<ActiveSyncOptions>>(
			new StaticOptionsMonitor(options));
		builder.Services.AddSingleton<ISyncDbContextFactory>(factory);
		builder.Services.AddSingleton(new AccountStore(factory));
		builder.Services.AddSingleton(provider => new AccountResolver(
			provider.GetRequiredService<IOptionsMonitor<ActiveSyncOptions>>(),
			new BackendRolesProvider(new ConfigurationBuilder().Build()),
			new BackendProviderRegistry(
				[new ActiveSync.Backends.Local.LocalBackendProvider(null!, null!, null!)],
				NullLogger<BackendProviderRegistry>.Instance),
			provider.GetRequiredService<AccountStore>()));
		builder.Services.AddScoped<SyncDbContext>(_ => factory.CreateDbContext());
		builder.Services.AddScoped<SyncStateService>();
		builder.AddWebUi();

		using ServiceProvider services = builder.Services.BuildServiceProvider();
		if (blocked)
		{
			await using SyncDbContext db = factory.CreateDbContext();
			// DbSet.Add is synchronous and local (no I/O); AddAsync exists only for async value
			// generators, which this model does not use.
#pragma warning disable VSTHRD103
			db.LoginBlocks.Add(new LoginBlock { UserName = login, CreatedUtc = DateTime.UtcNow });
#pragma warning restore VSTHRD103
			await db.SaveChangesAsync(CancellationToken.None);
		}

		if (revokedAtUtc is { } revoked)
		{
			await using SyncDbContext db = factory.CreateDbContext();
			await new SyncStateService(db)
				.RevokeSessionsBeforeAsync(login, revoked, CancellationToken.None);
		}

		using IServiceScope scope = services.CreateScope();
		// Built up front so a resolver fault surfaces as a test failure rather than being
		// swallowed by the hook's deliberate fail-open.
		await scope.ServiceProvider.GetRequiredService<AccountResolver>()
			.EnsureFreshAsync(false, CancellationToken.None);
		DefaultHttpContext http = new() { RequestServices = scope.ServiceProvider };
		List<Claim> claims = [new Claim(ClaimTypes.Name, login)];
		if (admin)
			claims.Add(new Claim(WebUiAuth.AdminClaim, "true"));
		claims.AddRange(extraClaims.Select(c => new Claim(c.Type, c.Value)));
		AuthenticationTicket ticket = new(
			new ClaimsPrincipal(new ClaimsIdentity(claims, WebUiAuth.Scheme)),
			new AuthenticationProperties { IssuedUtc = DateTimeOffset.UtcNow },
			WebUiAuth.Scheme);

		CookieAuthenticationOptions cookie = services
			.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(WebUiAuth.Scheme);
		CookieValidatePrincipalContext context = new(http,
			new AuthenticationScheme(WebUiAuth.Scheme, null, typeof(CookieAuthenticationHandler)),
			cookie, ticket);
		await cookie.Events.OnValidatePrincipal(context);
		return context;
	}

	private static Dictionary<string, AccountOptions> Users(params (string Login, AccountOptions Options)[] users)
	{
		return users.ToDictionary(u => u.Login, u => u.Options, StringComparer.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task DisabledAccount_LosesItsLiveSession()
	{
		CookieValidatePrincipalContext context = await ValidateAsync(
			Users(("alice", new AccountOptions { Admin = true, Enabled = false })), "alice", admin: true);
		Assert.Null(context.Principal);
	}

	[Fact]
	public async Task BlockedLogin_LosesItsLiveSession()
	{
		CookieValidatePrincipalContext context = await ValidateAsync(
			Users(("alice", new AccountOptions { Admin = true })), "alice", admin: true, blocked: true);
		Assert.Null(context.Principal);
	}

	[Fact]
	public async Task DeletedAccount_LosesItsLiveSession()
	{
		CookieValidatePrincipalContext context = await ValidateAsync(
			Users(("bob", new AccountOptions())), "alice", admin: false);
		Assert.Null(context.Principal);
	}

	[Fact]
	public async Task RevokedAdmin_KeepsTheSession_ButLosesTheAdminClaim()
	{
		CookieValidatePrincipalContext context = await ValidateAsync(
			Users(("alice", new AccountOptions { Admin = false })), "alice", admin: true);
		Assert.NotNull(context.Principal);
		Assert.False(context.Principal!.HasClaim(WebUiAuth.AdminClaim, "true"));
		Assert.Equal("alice", context.Principal.Identity?.Name);
		Assert.True(context.ShouldRenew);
	}

	[Fact]
	public async Task HealthyAdmin_KeepsEverything()
	{
		CookieValidatePrincipalContext context = await ValidateAsync(
			Users(("alice", new AccountOptions { Admin = true })), "alice", admin: true);
		Assert.NotNull(context.Principal);
		Assert.True(context.Principal!.HasClaim(WebUiAuth.AdminClaim, "true"));
	}

	[Fact]
	public async Task RecentlyValidatedSession_IsNotRecheckedOnEveryRequest()
	{
		// The check is rate-limited to once a minute per session, so a ticket stamped just now
		// passes through untouched — that is what keeps this off the per-request hot path.
		string stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
		CookieValidatePrincipalContext context = await ValidateAsync(
			Users(("alice", new AccountOptions { Admin = true, Enabled = false })), "alice", admin: true,
			blocked: false, revokedAtUtc: null, (SessionValidation.ValidatedAtClaim, stamp));
		Assert.NotNull(context.Principal);
		Assert.False(context.ShouldRenew);

		// One interval later the same session is caught.
		string stale = DateTimeOffset.UtcNow.Add(-SessionValidation.Interval)
			.AddSeconds(-1).ToUnixTimeSeconds().ToString();
		CookieValidatePrincipalContext expired = await ValidateAsync(
			Users(("alice", new AccountOptions { Admin = true, Enabled = false })), "alice", admin: true,
			blocked: false, revokedAtUtc: null, (SessionValidation.ValidatedAtClaim, stale));
		Assert.Null(expired.Principal);
	}

	[Fact]
	public async Task OidcClaimGrantedAdmin_SurvivesRevalidation()
	{
		// Admin granted by the IdP claim has no account flag behind it, and the IdP's claims
		// deliberately never enter the session — re-deriving from the flag alone would strip
		// every OIDC admin within a minute of signing in.
		CookieValidatePrincipalContext context = await ValidateAsync(
			Users(("alice", new AccountOptions())), "alice", admin: true, blocked: false,
			revokedAtUtc: null,
			(SessionValidation.AdminSourceClaim, SessionValidation.OidcAdminSource));
		Assert.NotNull(context.Principal);
		Assert.True(context.Principal!.HasClaim(WebUiAuth.AdminClaim, "true"));
		Assert.True(context.Principal.HasClaim(
			SessionValidation.AdminSourceClaim, SessionValidation.OidcAdminSource));
	}

	[Fact]
	public async Task RevokedSession_IsRefused_EvenThoughTheAccountIsHealthy()
	{
		// Signing out deletes the browser's copy of the cookie; a copy taken beforehand stays
		// cryptographically valid for the rest of the 12 hours unless the server records a
		// cut-off. The account itself is untouched here — this is purely the logout half.
		DateTimeOffset started = DateTimeOffset.UtcNow.AddMinutes(-10);
		CookieValidatePrincipalContext revoked = await ValidateAsync(
			Users(("alice", new AccountOptions { Admin = true })), "alice", admin: true,
			blocked: false, revokedAtUtc: DateTime.UtcNow.AddMinutes(-5),
			(SessionValidation.SessionStartClaim, started.ToUnixTimeSeconds().ToString()));
		Assert.Null(revoked.Principal);

		// A session started AFTER the cut-off — signing back in — is unaffected.
		CookieValidatePrincipalContext fresh = await ValidateAsync(
			Users(("alice", new AccountOptions { Admin = true })), "alice", admin: true,
			blocked: false, revokedAtUtc: DateTime.UtcNow.AddMinutes(-5),
			(SessionValidation.SessionStartClaim, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()));
		Assert.NotNull(fresh.Principal);
	}

	[Fact]
	public async Task SessionStart_SurvivesRevalidation()
	{
		// The re-minted principal has to carry the start stamp forward, or a revocation would
		// stop biting the moment the session was renewed.
		string started = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds().ToString();
		CookieValidatePrincipalContext context = await ValidateAsync(
			Users(("alice", new AccountOptions { Admin = true })), "alice", admin: true,
			blocked: false, revokedAtUtc: null, (SessionValidation.SessionStartClaim, started));
		Assert.Equal(started, context.Principal?.FindFirst(SessionValidation.SessionStartClaim)?.Value);
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

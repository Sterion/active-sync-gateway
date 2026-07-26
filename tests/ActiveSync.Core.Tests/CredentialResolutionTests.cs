using ActiveSync.Contracts;
using ActiveSync.Core.Accounts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using ActiveSync.Core.State;
using ActiveSync.Crypto;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Core.Tests;

/// <summary>
///   Item 5 of docs/design/db-restructure.md — backend credentials resolve with one extra tier
///   of SCOPE on top of the per-field rule:
///   <c>user · role → user · default → pass-through</c>.
///   <para>
///     The FALLBACKS are what matter and are asserted first: they are what preserves the
///     zero-administration baseline the project leads with. The overrides only matter once the
///     fallbacks are proven.
///   </para>
/// </summary>
public sealed class CredentialResolutionTests : IDisposable
{
	private const string GatewayLogin = "anna";
	private const string Presented = "presented-eas-password";

	private readonly SqliteConnection _connection;
	private readonly TestContextFactory _factory;
	private readonly UserStore _store;

	public CredentialResolutionTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();
		_factory = new TestContextFactory(_connection);
		using SyncDbContext db = _factory.CreateDbContext();
		db.Database.EnsureCreated();
		_store = new UserStore(_factory);
	}

	public void Dispose() => _connection.Dispose();

	private static ActiveSyncOptions Options() => new()
	{
		Encryption = new EncryptionOptions { AllowPlaintext = true },
		Auth = new AuthOptions { UsersRefreshSeconds = 0 },
	};

	private UserResolver Resolver(ActiveSyncOptions options)
	{
		IConfigurationRoot config = new ConfigurationBuilder().AddInMemoryCollection(
			new Dictionary<string, string?>
			{
				["ActiveSync:Backends:MailStore:Provider"] = "imap",
				["ActiveSync:Backends:MailStore:Host"] = "imap.global",
				["ActiveSync:Backends:MailSubmit:Provider"] = "smtp",
				["ActiveSync:Backends:MailSubmit:Host"] = "smtp.global",
				["ActiveSync:Backends:Calendar:Provider"] = "caldav",
				["ActiveSync:Backends:Calendar:BaseUrl"] = "https://dav.global",
			}).Build();
		BackendProviderRegistry registry = new(
		[
			new ActiveSync.Backends.Imap.ImapBackendProvider(
				TestOptionsMonitor.Of(options), NullLoggerFactory.Instance),
			new ActiveSync.Backends.Smtp.SmtpBackendProvider(NullLoggerFactory.Instance),
			new ActiveSync.Backends.Dav.CalDavBackendProvider(
				TestOptionsMonitor.Of(options), NullLoggerFactory.Instance),
			new ActiveSync.Backends.Local.LocalBackendProvider(null!, null!, null!)
		], NullLogger<BackendProviderRegistry>.Instance);
		return new UserResolver(
			TestOptionsMonitor.Of(options), new BackendRolesProvider(config), registry, _store);
	}

	private async Task<ResolvedUser> ResolveAsync(UserOptions? declaration)
	{
		ActiveSyncOptions options = Options();
		UserResolver resolver = Resolver(options);
		if (declaration is not null)
			await _store.UpsertAsync(GatewayLogin, declaration, CancellationToken.None);
		await resolver.EnsureFreshAsync(true, CancellationToken.None);
		return resolver.Resolve(new BackendCredentials(GatewayLogin, Presented));
	}

	// ---- the fallbacks (proven first) ----

	[Fact]
	public async Task NothingDeclared_IsPassThrough_Unchanged()
	{
		ResolvedUser resolved = await ResolveAsync(null);

		foreach (BackendRole role in new[] { BackendRole.MailStore, BackendRole.MailSubmit, BackendRole.Calendar })
		{
			Assert.Equal(GatewayLogin, resolved.Roles[role].Credentials.UserName);
			Assert.Equal(Presented, resolved.Roles[role].Credentials.Password);
		}
	}

	[Fact]
	public async Task AnEmptyDeclaration_StillForwardsThePresentedCredential()
	{
		// A declared-but-empty entry (the allowlist grant) must behave exactly like pass-through.
		ResolvedUser resolved = await ResolveAsync(new UserOptions());

		Assert.Equal(GatewayLogin, resolved.Roles[BackendRole.MailStore].Credentials.UserName);
		Assert.Equal(Presented, resolved.Roles[BackendRole.MailStore].Credentials.Password);
	}

	[Fact]
	public async Task UnsetDefaultBackendPassword_ForwardsThePresentedEasPassword()
	{
		// The user sets a LOGIN but no password: the presented EAS password still goes through.
		ResolvedUser resolved = await ResolveAsync(new UserOptions { DefaultBackendLogin = "backend-anna" });

		Assert.Equal("backend-anna", resolved.Roles[BackendRole.MailStore].Credentials.UserName);
		Assert.Equal(Presented, resolved.Roles[BackendRole.MailStore].Credentials.Password);
	}

	[Fact]
	public async Task UnsetDefaultBackendLogin_UsesTheGatewayLogin()
	{
		ResolvedUser resolved = await ResolveAsync(new UserOptions { DefaultBackendPassword = "backend-secret" });

		Assert.Equal(GatewayLogin, resolved.Roles[BackendRole.MailStore].Credentials.UserName);
		Assert.Equal("backend-secret", resolved.Roles[BackendRole.MailStore].Credentials.Password);
	}

	// ---- the scope tier ----

	[Fact]
	public async Task TheDefaults_ApplyToEveryRole()
	{
		ResolvedUser resolved = await ResolveAsync(new UserOptions
		{
			DefaultBackendLogin = "backend-anna",
			DefaultBackendPassword = "backend-secret",
		});

		foreach (BackendRole role in new[] { BackendRole.MailStore, BackendRole.MailSubmit, BackendRole.Calendar })
		{
			Assert.Equal("backend-anna", resolved.Roles[role].Credentials.UserName);
			Assert.Equal("backend-secret", resolved.Roles[role].Credentials.Password);
		}
	}

	[Fact]
	public async Task ARoleOverride_BeatsTheDefault_PerFieldAndPerRole()
	{
		ResolvedUser resolved = await ResolveAsync(new UserOptions
		{
			DefaultBackendLogin = "backend-anna",
			DefaultBackendPassword = "backend-secret",
			Backends = new Dictionary<string, BackendRoleOverride>
			{
				// Only the SMTP login differs; its password still comes from the default.
				["MailSubmit"] = new() { UserName = "relay-anna" },
				// Calendar deviates on both.
				["Calendar"] = new() { UserName = "dav-anna", Password = "dav-secret" },
			},
		});

		Assert.Equal("backend-anna", resolved.Roles[BackendRole.MailStore].Credentials.UserName);
		Assert.Equal("backend-secret", resolved.Roles[BackendRole.MailStore].Credentials.Password);

		Assert.Equal("relay-anna", resolved.Roles[BackendRole.MailSubmit].Credentials.UserName);
		Assert.Equal("backend-secret", resolved.Roles[BackendRole.MailSubmit].Credentials.Password);

		Assert.Equal("dav-anna", resolved.Roles[BackendRole.Calendar].Credentials.UserName);
		Assert.Equal("dav-secret", resolved.Roles[BackendRole.Calendar].Credentials.Password);
	}

	[Fact]
	public async Task MailStoreIsJustAnotherRole_ItNoLongerTemplatesTheOthers()
	{
		// BEHAVIOUR CHANGE (item 5): MailStore used to do double duty — the mail backend AND the
		// template every other role copied from, which only ever worked while the device
		// credential WAS the mail password. Other roles now fall back to the explicit defaults.
		ResolvedUser resolved = await ResolveAsync(new UserOptions
		{
			Backends = new Dictionary<string, BackendRoleOverride>
			{
				["MailStore"] = new() { UserName = "imap-only", Password = "imap-only-secret" },
			},
		});

		Assert.Equal("imap-only", resolved.Roles[BackendRole.MailStore].Credentials.UserName);
		Assert.Equal("imap-only-secret", resolved.Roles[BackendRole.MailStore].Credentials.Password);
		// Calendar does NOT inherit the MailStore pair — it falls back to the defaults, which are
		// unset here, so it is pass-through.
		Assert.Equal(GatewayLogin, resolved.Roles[BackendRole.Calendar].Credentials.UserName);
		Assert.Equal(Presented, resolved.Roles[BackendRole.Calendar].Credentials.Password);
	}

	[Fact]
	public async Task DatabaseAndConfigCombine_AcrossTheScopeTiers()
	{
		// The two axes at once: config supplies the default login, the database overrides one
		// role's password. Everything else falls through.
		ActiveSyncOptions options = Options();
		options.Users = new Dictionary<string, UserOptions>
		{
			[GatewayLogin] = new()
			{
				DefaultBackendLogin = "config-backend-anna",
				Backends = new Dictionary<string, BackendRoleOverride>
				{
					["Calendar"] = new() { UserName = "config-dav" },
				},
			},
		};
		await _store.UpsertAsync(GatewayLogin, new UserOptions
		{
			Backends = new Dictionary<string, BackendRoleOverride>
			{
				["Calendar"] = new() { Password = "db-dav-secret" },
			},
		}, CancellationToken.None);

		UserResolver resolver = Resolver(options);
		await resolver.EnsureFreshAsync(true, CancellationToken.None);
		ResolvedUser resolved = resolver.Resolve(new BackendCredentials(GatewayLogin, Presented));

		// MailStore: no role override anywhere → config default login + presented password.
		Assert.Equal("config-backend-anna", resolved.Roles[BackendRole.MailStore].Credentials.UserName);
		Assert.Equal(Presented, resolved.Roles[BackendRole.MailStore].Credentials.Password);
		// Calendar: config supplied the name, the database the password.
		Assert.Equal("config-dav", resolved.Roles[BackendRole.Calendar].Credentials.UserName);
		Assert.Equal("db-dav-secret", resolved.Roles[BackendRole.Calendar].Credentials.Password);
	}

	// ---- the two chains stay separate ----

	[Fact]
	public async Task TheGatewayPassword_IsNeverSentToABackend()
	{
		ResolvedUser resolved = await ResolveAsync(new UserOptions
		{
			Password = "plaintext-gateway-password",   // device → gateway, verified locally
			DefaultBackendLogin = "backend-anna",
			DefaultBackendPassword = "backend-secret", // gateway → backends
		});

		foreach (ResolvedRole role in resolved.OrderedRoles)
		{
			Assert.NotEqual("plaintext-gateway-password", role.Credentials.Password);
			Assert.Equal("backend-secret", role.Credentials.Password);
		}
	}

	[Fact]
	public async Task AConfiguredDefaultBackendPassword_PinsThePresentedCredential()
	{
		// Whenever the gateway holds a backend password for a user, the probe would authenticate
		// with THAT password and therefore accept anything the device presented. The presented
		// value must be pinned against it, exactly as a configured MailStore password is —
		// otherwise setting a default silently turns the account into an open door.
		ActiveSyncOptions options = Options();
		UserResolver resolver = Resolver(options);
		await _store.UpsertAsync(GatewayLogin,
			new UserOptions { DefaultBackendPassword = "backend-secret" }, CancellationToken.None);
		await resolver.EnsureFreshAsync(true, CancellationToken.None);

		Assert.True(resolver.VerifyLocally(GatewayLogin, "backend-secret"));
		Assert.False(resolver.VerifyLocally(GatewayLogin, "anything-else"));
	}

	[Fact]
	public async Task AGatewayPassword_StillOutranksTheBackendPin()
	{
		// Auth precedence is unchanged: the gateway password decides first when present.
		ActiveSyncOptions options = Options();
		UserResolver resolver = Resolver(options);
		await _store.UpsertAsync(GatewayLogin, new UserOptions
		{
			Password = "phone-password",
			DefaultBackendPassword = "backend-secret",
		}, CancellationToken.None);
		await resolver.EnsureFreshAsync(true, CancellationToken.None);

		Assert.True(resolver.VerifyLocally(GatewayLogin, "phone-password"));
		Assert.False(resolver.VerifyLocally(GatewayLogin, "backend-secret"));
	}

	[Fact]
	public async Task ARoleLevelMailStorePassword_StillPins_AsBefore()
	{
		ActiveSyncOptions options = Options();
		UserResolver resolver = Resolver(options);
		await _store.UpsertAsync(GatewayLogin, new UserOptions
		{
			Backends = new Dictionary<string, BackendRoleOverride>
			{
				["MailStore"] = new() { Password = "imap-secret" },
			},
		}, CancellationToken.None);
		await resolver.EnsureFreshAsync(true, CancellationToken.None);

		Assert.True(resolver.VerifyLocally(GatewayLogin, "imap-secret"));
		Assert.False(resolver.VerifyLocally(GatewayLogin, "anything-else"));
	}

	[Fact]
	public async Task WithNoStoredBackendPassword_TheVerdictIsStillTheProbe()
	{
		// Nothing local can decide, so the resolver must defer (null) to the backend probe —
		// the pass-through baseline.
		ActiveSyncOptions options = Options();
		UserResolver resolver = Resolver(options);
		await _store.UpsertAsync(GatewayLogin,
			new UserOptions { DefaultBackendLogin = "backend-anna" }, CancellationToken.None);
		await resolver.EnsureFreshAsync(true, CancellationToken.None);

		Assert.Null(resolver.VerifyLocally(GatewayLogin, "whatever"));
	}

	private sealed class TestContextFactory(SqliteConnection connection) : ISyncDbContextFactory
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

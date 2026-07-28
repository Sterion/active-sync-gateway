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

	/// <summary>
	///   A stored MailStore secret REQUIRES a gateway password (see the probe invariant in
	///   UserResolver): without one the login could only be decided by a probe that signs in with
	///   the gateway's own copy, which succeeds whatever the device sent. So every declaration
	///   below that stores a backend secret carries this too.
	/// </summary>
	private const string PhonePassword = "phone-password";

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
			new ActiveSync.Backends.Local.LocalBackendProvider(null!, null!, null!, NullLoggerFactory.Instance)
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
		ResolvedUser resolved = await ResolveAsync(new UserOptions
		{
			Password = PhonePassword, DefaultBackendPassword = "backend-secret",
		});

		Assert.Equal(GatewayLogin, resolved.Roles[BackendRole.MailStore].Credentials.UserName);
		Assert.Equal("backend-secret", resolved.Roles[BackendRole.MailStore].Credentials.Password);
	}

	// ---- the scope tier ----

	[Fact]
	public async Task TheDefaults_ApplyToEveryRole()
	{
		ResolvedUser resolved = await ResolveAsync(new UserOptions
		{
			Password = PhonePassword,
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
			Password = PhonePassword,
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
			Password = PhonePassword,
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

	// ---- the probe invariant: a stored MailStore secret requires a gateway password ----

	[Theory]
	[InlineData("DefaultBackendPassword")]
	[InlineData("MailStore")]
	public async Task AStoredMailSecret_WithoutAGatewayPassword_IsRefused_NotUsedToAuthenticate(string where)
	{
		// A BACKEND secret must never become a DEVICE credential. The write paths refuse this
		// combination outright, so reaching the resolver with it means the row was written around
		// them (a hand-edited database, an older CLI): it fails closed, and — the point — the
		// stored secret does NOT authenticate. Compare it against the presented value and a
		// GATEWAY → BACKENDS credential silently becomes a DEVICE → GATEWAY one.
		UserOptions declaration = where == "DefaultBackendPassword"
			? new UserOptions { DefaultBackendPassword = "backend-secret" }
			: new UserOptions
			{
				Backends = new Dictionary<string, BackendRoleOverride>
				{
					["MailStore"] = new() { Password = "backend-secret" },
				},
			};

		ActiveSyncOptions options = Options();
		UserResolver resolver = Resolver(options);
		await _store.UpsertAsync(GatewayLogin, declaration, CancellationToken.None);
		await resolver.EnsureFreshAsync(true, CancellationToken.None);

		Assert.False(resolver.VerifyLocally(GatewayLogin, "backend-secret"));
		Assert.False(resolver.VerifyLocally(GatewayLogin, "anything-else"));
		// Definitively false, never null: a null verdict would hand the login to the probe, which
		// is the open door the rule exists to close.
		Assert.NotNull(resolver.VerifyLocally(GatewayLogin, Presented));
		Assert.Throws<InvalidOperationException>(
			() => resolver.Resolve(new BackendCredentials(GatewayLogin, Presented)));
	}

	[Fact]
	public async Task AGatewayPassword_MakesTheStoredSecretLegal_AndStillNeverAuthenticates()
	{
		ActiveSyncOptions options = Options();
		UserResolver resolver = Resolver(options);
		await _store.UpsertAsync(GatewayLogin, new UserOptions
		{
			Password = PhonePassword,
			DefaultBackendPassword = "backend-secret",
		}, CancellationToken.None);
		await resolver.EnsureFreshAsync(true, CancellationToken.None);

		Assert.True(resolver.VerifyLocally(GatewayLogin, PhonePassword));
		Assert.False(resolver.VerifyLocally(GatewayLogin, "backend-secret"));
		// ...and it is still the secret the backend gets.
		ResolvedUser resolved = resolver.Resolve(new BackendCredentials(GatewayLogin, PhonePassword));
		Assert.Equal("backend-secret", resolved.Roles[BackendRole.MailStore].Credentials.Password);
	}

	[Fact]
	public async Task AContentRoleSecret_NeedsNoGatewayPassword()
	{
		// The rule is about the PROBE, so it is about MailStore alone. A Calendar credential leaves
		// the probe reading the presented password, so the login stays honestly pass-through and
		// forcing a gateway password on it would be cargo cult.
		ResolvedUser resolved = await ResolveAsync(new UserOptions
		{
			Backends = new Dictionary<string, BackendRoleOverride>
			{
				["Calendar"] = new() { Password = "dav-secret" },
			},
		});

		Assert.Equal(Presented, resolved.Roles[BackendRole.MailStore].Credentials.Password);
		Assert.Equal("dav-secret", resolved.Roles[BackendRole.Calendar].Credentials.Password);
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

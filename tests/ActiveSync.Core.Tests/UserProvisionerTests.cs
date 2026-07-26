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
///   Identity provisioning at the auth boundary (db-restructure item 2): every authenticated
///   login gets a user row and a UserId — an auto-provisioned DECLARATION for undeclared logins
///   (flag on), an identity-only row for config-declared ones (no shadowing) — and the id is
///   stable across repeats and case variants.
/// </summary>
public sealed class UserProvisionerTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly TestContextFactory _factory;
	private readonly UserStore _store;

	public UserProvisionerTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();
		_factory = new TestContextFactory(_connection);
		using SyncDbContext db = _factory.CreateDbContext();
		db.Database.EnsureCreated();
		_store = new UserStore(_factory);
	}

	public void Dispose() => _connection.Dispose();

	private static BackendProviderRegistry Registry() => new(
	[
		new ActiveSync.Backends.Imap.ImapBackendProvider(
			TestOptionsMonitor.Of(new ActiveSyncOptions()), NullLoggerFactory.Instance),
		new ActiveSync.Backends.Smtp.SmtpBackendProvider(NullLoggerFactory.Instance),
		new ActiveSync.Backends.Local.LocalBackendProvider(null!, null!, null!)
	], NullLogger<BackendProviderRegistry>.Instance);

	private (UserProvisioner Provisioner, UserResolver Resolver) Build(ActiveSyncOptions options)
	{
		IConfigurationRoot config = new ConfigurationBuilder().AddInMemoryCollection(
			new Dictionary<string, string?>
			{
				["ActiveSync:Backends:MailStore:Provider"] = "imap",
				["ActiveSync:Backends:MailStore:Host"] = "imap.global",
				["ActiveSync:Backends:MailStore:Port"] = "143",
				["ActiveSync:Backends:MailSubmit:Provider"] = "smtp",
				["ActiveSync:Backends:MailSubmit:Host"] = "smtp.global",
			}).Build();
		BackendRolesProvider rolesProvider = new(config);
		BackendProviderRegistry registry = Registry();
		UserResolver resolver = new(TestOptionsMonitor.Of(options), rolesProvider, registry, _store);
		UserProvisioner provisioner = new(
			resolver, _store, registry, TestOptionsMonitor.Of(options),
			NullLogger<UserProvisioner>.Instance);
		return (provisioner, resolver);
	}

	private static ActiveSyncOptions Options(bool autoProvision) => new()
	{
		Encryption = new EncryptionOptions { AllowPlaintext = true },
		Auth = new AuthOptions { UsersRefreshSeconds = 0 },
		AutoProvisionUsers = autoProvision,
	};

	[Fact]
	public async Task Enabled_UndeclaredLogin_CreatesAutoMarkedDeclaration()
	{
		(UserProvisioner provisioner, UserResolver resolver) = Build(Options(autoProvision: true));
		await resolver.EnsureFreshAsync(true, CancellationToken.None);

		int? userId = await provisioner.EnsureUserAsync("phone@dnfl.dk", CancellationToken.None);

		Assert.NotNull(userId);
		Assert.True(userId > 0);
		UserOptions? row = await _store.GetAsync("phone@dnfl.dk", CancellationToken.None);
		Assert.NotNull(row);
		Assert.True(row!.AutoProvisioned);
		Assert.Null(row.Password);          // no gateway password: auth still probes the backend
		Assert.Null(row.Backends);          // pure overlay, nothing overridden
		Assert.True(resolver.MergedUsers.ContainsKey("phone@dnfl.dk"));
		Assert.True(resolver.MergedUsers["phone@dnfl.dk"].FromDatabase);
	}

	[Fact]
	public async Task Disabled_DeclaredDbLogin_StillGetsItsId_ButNothingNewIsDeclared()
	{
		// With the flag off an UNDECLARED login is refused before auth ever reaches the
		// provisioner (UserResolver.VerifyLocally returns false) — but a DECLARED login still
		// authenticates and must still get its identity.
		(UserProvisioner provisioner, UserResolver resolver) = Build(Options(autoProvision: false));
		await _store.UpsertAsync("phone@dnfl.dk", new UserOptions(), CancellationToken.None);
		await resolver.EnsureFreshAsync(true, CancellationToken.None);

		int? userId = await provisioner.EnsureUserAsync("phone@dnfl.dk", CancellationToken.None);

		Assert.NotNull(userId);
		Assert.Equal(userId, await _store.FindUserIdAsync("phone@dnfl.dk", CancellationToken.None));
	}

	[Fact]
	public async Task Disabled_RefusesUndeclaredBeforeAnyProbe()
	{
		(_, UserResolver resolver) = Build(Options(autoProvision: false));
		await resolver.EnsureFreshAsync(true, CancellationToken.None);

		// The refusal is the resolver's LOCAL verdict — definitive false, so the backend
		// probe never runs for an undeclared login (the brute-force shield of decision 6/7).
		Assert.False(resolver.VerifyLocally("stranger@dnfl.dk", "whatever"));
	}

	[Fact]
	public async Task ConfigDeclaredLogin_GetsIdentityOnlyRow_NoShadowingDeclaration()
	{
		ActiveSyncOptions options = Options(autoProvision: true);
		options.Users = new Dictionary<string, UserOptions>(StringComparer.OrdinalIgnoreCase)
		{
			["phone@dnfl.dk"] = new() { MailAddress = "phone@dnfl.dk" },
		};
		(UserProvisioner provisioner, UserResolver resolver) = Build(options);
		await resolver.EnsureFreshAsync(true, CancellationToken.None);

		int? userId = await provisioner.EnsureUserAsync("phone@dnfl.dk", CancellationToken.None);

		// The identity exists (sync state has something to FK to)...
		Assert.NotNull(userId);
		Assert.Equal(userId, await _store.FindUserIdAsync("phone@dnfl.dk", CancellationToken.None));
		// ...but the config declaration still owns the login — no shadowing DB declaration.
		Assert.Null(await _store.GetAsync("phone@dnfl.dk", CancellationToken.None));
		Assert.False(resolver.MergedUsers["phone@dnfl.dk"].FromDatabase);
	}

	[Fact]
	public async Task Repeat_IsIdempotent_CaseInsensitive_AndIdStable()
	{
		(UserProvisioner provisioner, UserResolver resolver) = Build(Options(autoProvision: true));
		await resolver.EnsureFreshAsync(true, CancellationToken.None);

		int? first = await provisioner.EnsureUserAsync("Phone@dnfl.dk", CancellationToken.None);
		int? second = await provisioner.EnsureUserAsync("phone@dnfl.dk", CancellationToken.None);
		int? third = await provisioner.EnsureUserAsync("PHONE@DNFL.DK", CancellationToken.None);

		Assert.Equal(first, second);
		Assert.Equal(first, third);
		List<(string Login, UserOptions Options, DateTime UpdatedUtc, bool Valid)> rows =
			await _store.ListAsync(CancellationToken.None);
		Assert.Single(rows);
	}

	[Fact]
	public async Task StructurallyInvalidLogin_IsRefused_NoIdentityMinted()
	{
		(UserProvisioner provisioner, UserResolver resolver) = Build(Options(autoProvision: true));
		await resolver.EnsureFreshAsync(true, CancellationToken.None);

		Assert.Null(await provisioner.EnsureUserAsync("bad\nlogin", CancellationToken.None));
		Assert.Null(await _store.FindUserIdAsync("bad\nlogin", CancellationToken.None));
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

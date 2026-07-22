using ActiveSync.Core.Accounts;
using ActiveSync.Contracts;
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
///   JIT pass-through provisioning: a first successful sign-in of an undeclared login writes a
///   bare, auto-marked database row when the flag is on, and does nothing when it is off, the
///   login is already declared, or on a repeat.
/// </summary>
public sealed class PassThroughProvisionerTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly TestContextFactory _factory;
	private readonly AccountStore _store;

	public PassThroughProvisionerTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();
		_factory = new TestContextFactory(_connection);
		using SyncDbContext db = _factory.CreateDbContext();
		db.Database.EnsureCreated();
		_store = new AccountStore(_factory);
	}

	public void Dispose() => _connection.Dispose();

	private static BackendProviderRegistry Registry() => new(
	[
		new ActiveSync.Backends.Imap.ImapBackendProvider(
			TestOptionsMonitor.Of(new ActiveSyncOptions()), NullLoggerFactory.Instance),
		new ActiveSync.Backends.Smtp.SmtpBackendProvider(NullLoggerFactory.Instance),
		new ActiveSync.Backends.Local.LocalBackendProvider(null!, null!, null!)
	], NullLogger<BackendProviderRegistry>.Instance);

	private (PassThroughProvisioner Provisioner, AccountResolver Resolver) Build(ActiveSyncOptions options)
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
		AccountResolver resolver = new(TestOptionsMonitor.Of(options), rolesProvider, registry, _store);
		PassThroughProvisioner provisioner = new(
			resolver, _store, registry, TestOptionsMonitor.Of(options),
			NullLogger<PassThroughProvisioner>.Instance);
		return (provisioner, resolver);
	}

	private static ActiveSyncOptions Options(bool autoProvision) => new()
	{
		Encryption = new EncryptionOptions { AllowPlaintext = true },
		Auth = new AuthOptions { UsersRefreshSeconds = 0 },
		AutoProvisionUsers = autoProvision,
	};

	[Fact]
	public async Task Enabled_UndeclaredLogin_CreatesAutoMarkedRow()
	{
		(PassThroughProvisioner provisioner, AccountResolver resolver) = Build(Options(autoProvision: true));
		await resolver.EnsureFreshAsync(true, CancellationToken.None);

		await provisioner.ProvisionIfEnabledAsync("phone@dnfl.dk", CancellationToken.None);

		AccountOptions? row = await _store.GetAsync("phone@dnfl.dk", CancellationToken.None);
		Assert.NotNull(row);
		Assert.True(row!.AutoProvisioned);
		Assert.Null(row.Password);          // no gateway password: auth still probes the backend
		Assert.Null(row.Backends);          // pure overlay, nothing overridden
		Assert.True(resolver.MergedUsers.ContainsKey("phone@dnfl.dk"));
		Assert.True(resolver.MergedUsers["phone@dnfl.dk"].FromDatabase);
	}

	[Fact]
	public async Task Disabled_DoesNothing()
	{
		(PassThroughProvisioner provisioner, AccountResolver resolver) = Build(Options(autoProvision: false));
		await resolver.EnsureFreshAsync(true, CancellationToken.None);

		await provisioner.ProvisionIfEnabledAsync("phone@dnfl.dk", CancellationToken.None);

		Assert.Null(await _store.GetAsync("phone@dnfl.dk", CancellationToken.None));
	}

	[Fact]
	public async Task AlreadyDeclaredInConfig_IsNotProvisioned()
	{
		ActiveSyncOptions options = Options(autoProvision: true);
		options.Users = new Dictionary<string, AccountOptions>(StringComparer.OrdinalIgnoreCase)
		{
			["phone@dnfl.dk"] = new() { MailAddress = "phone@dnfl.dk" },
		};
		(PassThroughProvisioner provisioner, AccountResolver resolver) = Build(options);
		await resolver.EnsureFreshAsync(true, CancellationToken.None);

		await provisioner.ProvisionIfEnabledAsync("phone@dnfl.dk", CancellationToken.None);

		// A config declaration owns the login — no shadowing database row is written.
		Assert.Null(await _store.GetAsync("phone@dnfl.dk", CancellationToken.None));
	}

	[Fact]
	public async Task Repeat_IsIdempotent_AndCaseInsensitive()
	{
		(PassThroughProvisioner provisioner, AccountResolver resolver) = Build(Options(autoProvision: true));
		await resolver.EnsureFreshAsync(true, CancellationToken.None);

		await provisioner.ProvisionIfEnabledAsync("Phone@dnfl.dk", CancellationToken.None);
		await provisioner.ProvisionIfEnabledAsync("phone@dnfl.dk", CancellationToken.None);
		await provisioner.ProvisionIfEnabledAsync("PHONE@DNFL.DK", CancellationToken.None);

		List<(string UserName, AccountOptions Options, DateTime UpdatedUtc, bool Valid)> rows =
			await _store.ListAsync(CancellationToken.None);
		Assert.Single(rows);
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

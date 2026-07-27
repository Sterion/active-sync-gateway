using ActiveSync.Core.Options;
using ActiveSync.Core.Settings;
using ActiveSync.Core.State;
using ActiveSync.Server.Cli;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Spectre.Console.Cli.Testing;

namespace ActiveSync.Server.Tests;

/// <summary>
///   E17: CLI commands running INSIDE the warm gateway (via <see cref="CliHostServices" />) must read
///   options live (<see cref="IOptionsMonitor{TOptions}" />.CurrentValue), never a captured
///   <see cref="IOptions{TOptions}" />.Value — the latter is a singleton bound ONCE at first
///   resolution and never recomputes, even though a live database settings change fires the
///   configuration reload token. Simulates that live change directly on the host's
///   <see cref="DbSettingsConfigurationProvider" /> (what <c>SettingsRefresher</c> does for real)
///   rather than depending on the polling service, which isn't registered outside the full host.
/// </summary>
[Collection("cli")]
public sealed class CliOptionsLivenessTests : IDisposable
{
	private const string KeyBase64 = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
	private readonly string _dbPath;
	private readonly Dictionary<string, string?> _originalEnv = [];

	public CliOptionsLivenessTests()
	{
		_dbPath = Path.Combine(Path.GetTempPath(), $"as-cli-options-live-{Guid.NewGuid():N}.db");
		DbContextOptions<SqliteSyncDbContext> options = new DbContextOptionsBuilder<SqliteSyncDbContext>()
			.UseSqlite($"Data Source={_dbPath}")
			.Options;
		using SqliteSyncDbContext db = new(options);
		db.Database.Migrate();

		SetEnv("ActiveSync__Database__ConnectionString", $"Data Source={_dbPath}");
		SetEnv("ActiveSync__Encryption__Key", KeyBase64);
		SetEnv("ActiveSync__Backends__MailStore__Provider", "imap");
		SetEnv("ActiveSync__Backends__MailStore__Host", "imap.test");
		SetEnv("ActiveSync__Backends__MailSubmit__Provider", "smtp");
		SetEnv("ActiveSync__Backends__MailSubmit__Host", "smtp.test");
	}

	public void Dispose()
	{
		CliHostServices.Enter(null);
		foreach ((string name, string? value) in _originalEnv)
			Environment.SetEnvironmentVariable(name, value);
		SqliteConnection.ClearAllPools();
		File.Delete(_dbPath);
	}

	private void SetEnv(string name, string? value)
	{
		_originalEnv.TryAdd(name, Environment.GetEnvironmentVariable(name));
		Environment.SetEnvironmentVariable(name, value);
	}

	private static (int ExitCode, string ConsoleOutput) Run(params string[] args)
	{
		TextWriter originalOut = Console.Out;
		TextWriter originalError = Console.Error;
		using StringWriter stdout = new();
		using StringWriter stderr = new();
		try
		{
			Console.SetOut(stdout);
			Console.SetError(stderr);
			CommandAppTester tester = new();
			tester.Configure(CliApp.Configure);
			CommandAppResult result = tester.Run(args);
			return (result.ExitCode, result.Output);
		}
		finally
		{
			Console.SetOut(originalOut);
			Console.SetError(originalError);
		}
	}

	[Fact]
	public async Task UserSet_PickupNote_ReflectsALiveSettingsChange_NotTheFrozenIOptionsSnapshot()
	{
		ServiceProvider host = (await CliServices.TryCreateAsync())!;
		Assert.NotNull(host);
		await using ServiceProvider hostOwner = host;

		// Force IOptions<ActiveSyncOptions> to bind and cache NOW, at the default UsersRefreshSeconds
		// (1s) — mirroring the singleton being resolved once during startup.
		double cachedBefore = host.GetRequiredService<IOptions<ActiveSyncOptions>>().Value.Auth.UsersRefreshSeconds;
		Assert.Equal(1, cachedBefore);

		// Simulate the live settings change SettingsRefresher applies for real: push a new
		// UsersRefreshSeconds value directly into the host's DbSettingsConfigurationProvider and fire
		// the reload token (this polling service isn't registered outside the full ProgramServer
		// host, so drive the same mechanism it uses directly).
		IConfigurationRoot config = (IConfigurationRoot)host.GetRequiredService<IConfiguration>();
		DbSettingsConfigurationProvider dbProvider = config.Providers.OfType<DbSettingsConfigurationProvider>().Single();
		dbProvider.SetData(new Dictionary<string, string?> { ["ActiveSync:Auth:UsersRefreshSeconds"] = "42" });

		// IOptionsMonitor recomputed; the captured IOptions did not — proving the two really diverge.
		Assert.Equal(42, host.GetRequiredService<IOptionsMonitor<ActiveSyncOptions>>().CurrentValue.Auth.UsersRefreshSeconds);
		Assert.Equal(1, host.GetRequiredService<IOptions<ActiveSyncOptions>>().Value.Auth.UsersRefreshSeconds);

		CliHostServices.Enter(host);
		(int exitCode, string output) = Run("user", "set", "livetest@x", "MailAddress", "livetest@example.com");
		Assert.Equal(0, exitCode);
		Assert.Contains("picks this up within ~42s", output);
	}
}

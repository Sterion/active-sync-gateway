using ActiveSync.Core.Security;
using ActiveSync.Core.State;
using ActiveSync.Server.Cli;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console.Cli.Testing;

namespace ActiveSync.Server.Tests;

/// <summary>
///   E5: forwarded <c>config</c>/<c>logs</c>/<c>tls</c> commands must reuse the warm gateway's
///   already-built provider (via <see cref="CliHostServices" />) exactly like <see cref="DatabaseCommand{TSettings}" />
///   already does — not rebuild a parallel standalone container (and reload plugins) on every call.
///   Each test builds a "host" provider bound to a SEEDED database, then repoints the ambient
///   env configuration at a DIFFERENT, empty database before publishing the host as ambient — so a
///   command that (bug) ignores <see cref="CliHostServices.Current" /> and rebuilds from env sees
///   nothing, while one that correctly prefers the host sees the seeded value.
/// </summary>
[Collection("cli")]
public sealed class CliWarmHostReuseTests : IDisposable
{
	private const string KeyBase64 = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";

	private readonly string _hostDbPath;
	private readonly string _standaloneDbPath;
	private readonly Dictionary<string, string?> _originalEnv = [];

	public CliWarmHostReuseTests()
	{
		_hostDbPath = Path.Combine(Path.GetTempPath(), $"as-cli-warmhost-{Guid.NewGuid():N}.db");
		_standaloneDbPath = Path.Combine(Path.GetTempPath(), $"as-cli-standalone-{Guid.NewGuid():N}.db");
		foreach (string path in new[] { _hostDbPath, _standaloneDbPath })
		{
			DbContextOptions<SqliteSyncDbContext> options = new DbContextOptionsBuilder<SqliteSyncDbContext>()
				.UseSqlite($"Data Source={path}")
				.Options;
			using SqliteSyncDbContext db = new(options);
			db.Database.Migrate();
		}

		SetEnv("ActiveSync__Encryption__Key", KeyBase64);
	}

	public void Dispose()
	{
		CliHostServices.Enter(null);
		foreach ((string name, string? value) in _originalEnv)
			Environment.SetEnvironmentVariable(name, value);
		SqliteConnection.ClearAllPools();
		File.Delete(_hostDbPath);
		File.Delete(_standaloneDbPath);
	}

	private void SetEnv(string name, string? value)
	{
		_originalEnv.TryAdd(name, Environment.GetEnvironmentVariable(name));
		Environment.SetEnvironmentVariable(name, value);
	}

	private static (int ExitCode, string StdErr, string ConsoleOutput) Run(params string[] args)
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
			return (result.ExitCode, stderr.ToString(), result.Output);
		}
		finally
		{
			Console.SetOut(originalOut);
			Console.SetError(originalError);
		}
	}

	[Fact]
	public async Task ConfigGet_PrefersAmbientHostProvider_OverRebuildingFromEnv()
	{
		SetEnv("ActiveSync__Database__ConnectionString", $"Data Source={_hostDbPath}");
		ServiceProvider host = (await CliServices.TryCreateLeanAsync())!;
		Assert.NotNull(host);
		await using ServiceProvider hostOwner = host;
		await host.GetRequiredService<ActiveSync.Core.Settings.GlobalSettingStore>()
			.UpsertAsync("ActiveSync:ReadOnly", "true", CancellationToken.None);

		// Repoint env at the EMPTY standalone database before publishing the host as ambient.
		SetEnv("ActiveSync__Database__ConnectionString", $"Data Source={_standaloneDbPath}");
		CliHostServices.Enter(host);

		(int exitCode, _, string output) = Run("config", "get", "ActiveSync:ReadOnly");
		Assert.Equal(0, exitCode);
		Assert.Contains("true", output);
		Assert.Contains("source: db", output);
	}

	[Fact]
	public async Task Logs_PrefersAmbientHostProvider_OverRebuildingFromEnv()
	{
		SetEnv("ActiveSync__Database__ConnectionString", $"Data Source={_hostDbPath}");
		await using (SqliteSyncDbContext seed = new(new DbContextOptionsBuilder<SqliteSyncDbContext>()
			       .UseSqlite($"Data Source={_hostDbPath}").Options))
		{
			await seed.LogEntries.AddAsync(new LogEntry
			{
				TimestampUtc = DateTime.UtcNow, Level = "Information",
				Message = "host-only-marker-message",
			});
			await seed.SaveChangesAsync();
		}

		ServiceProvider host = (await CliServices.TryCreateLeanAsync())!;
		Assert.NotNull(host);
		await using ServiceProvider hostOwner = host;

		SetEnv("ActiveSync__Database__ConnectionString", $"Data Source={_standaloneDbPath}");
		CliHostServices.Enter(host);

		(int exitCode, _, string output) = Run("logs", "--since", "24h");
		Assert.Equal(0, exitCode);
		Assert.Contains("host-only-marker-message", output);
	}

	[Fact]
	public async Task Tls_PrefersAmbientHostProvider_OverRebuildingFromEnv()
	{
		SetEnv("ActiveSync__Database__ConnectionString", $"Data Source={_hostDbPath}");
		ServiceProvider host = (await CliServices.TryCreateLeanAsync())!;
		Assert.NotNull(host);
		await using ServiceProvider hostOwner = host;
		// Seed the self-signed certificate row in the HOST database only.
		using (await host.GetRequiredService<GatewayCertificateStore>().GetOrCreateAsync(
			       "gateway.test", NullLogger.Instance, CancellationToken.None))
		{
		}

		SetEnv("ActiveSync__Database__ConnectionString", $"Data Source={_standaloneDbPath}");
		CliHostServices.Enter(host);

		(int exitCode, _, string output) = Run("tls");
		Assert.Equal(0, exitCode);
		Assert.Contains("self-signed (stored in the database)", output);
		Assert.DoesNotContain("No self-signed certificate stored yet", output);
	}
}

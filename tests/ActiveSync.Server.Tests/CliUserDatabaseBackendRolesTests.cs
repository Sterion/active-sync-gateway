using ActiveSync.Core.State;
using ActiveSync.Server.Cli;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Spectre.Console.Cli.Testing;

namespace ActiveSync.Server.Tests;

/// <summary>
///   `eas user set` (the <see cref="DatabaseCommand{TSettings}" /> pipeline, via
///   <see cref="ActiveSync.Server.Cli.CliServices.TryCreateAsync" />) must validate against the SAME
///   effective backend-role configuration the running gateway uses — including roles that live ONLY
///   in the database, which is exactly the documented setup path (<c>eas config set
///   ActiveSync:Backends:...</c>). Deliberately does NOT set MailStore/MailSubmit via file/env
///   (unlike <see cref="CliUserTests" />'s fixture) — they are stored via <c>eas config set</c>
///   only, so this reproduces the standalone/stopped-gateway scenario just described.
/// </summary>
[Collection("cli")]
public sealed class CliUserDatabaseBackendRolesTests : IDisposable
{
	private readonly string _dbPath;
	private readonly Dictionary<string, string?> _originalEnv = [];

	public CliUserDatabaseBackendRolesTests()
	{
		_dbPath = Path.Combine(Path.GetTempPath(), $"as-cli-user-db-roles-{Guid.NewGuid():N}.db");
		DbContextOptions<SqliteSyncDbContext> options = new DbContextOptionsBuilder<SqliteSyncDbContext>()
			.UseSqlite($"Data Source={_dbPath}")
			.Options;
		using SqliteSyncDbContext db = new(options);
		db.Database.Migrate();

		SetEnv("ActiveSync__Database__ConnectionString", $"Data Source={_dbPath}");
		SetEnv("ActiveSync__Encryption__Key", "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=");
	}

	public void Dispose()
	{
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
	public void UserSet_InheritsAGlobalMailStoreRoleThatOnlyExistsInTheDatabase()
	{
		// The documented setup path: mail roles assigned entirely via `eas config set`, never in a
		// file or an environment variable.
		Assert.Equal(0, Run("config", "set", "ActiveSync:Backends:MailStore:Provider", "imap").ExitCode);
		Assert.Equal(0, Run("config", "set", "ActiveSync:Backends:MailStore:Host", "imap.example.com").ExitCode);
		Assert.Equal(0, Run("config", "set", "ActiveSync:Backends:MailSubmit:Provider", "smtp").ExitCode);
		Assert.Equal(0, Run("config", "set", "ActiveSync:Backends:MailSubmit:Host", "smtp.example.com").ExitCode);

		// A per-user MailStore override that OMITS Provider — it must INHERIT the global provider
		// and settings, which only works when the validator can see the database-stored role. On
		// the unmodified CliServices.TryCreateAsync (built from CliVerbs.BuildConfiguration, which
		// never layers the database), the global MailStore role is invisible, so this is refused
		// with "no global MailStore role is configured" even though `eas config list` would show it.
		(int exitCode, string stderr, _) = Run(
			"user", "set", "dbuser@x", "Backends:MailStore:Settings:Host", "override.example.com");
		Assert.Equal(0, exitCode);
		Assert.DoesNotContain("no global", stderr);
	}
}

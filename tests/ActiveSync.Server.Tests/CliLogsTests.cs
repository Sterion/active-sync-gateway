using ActiveSync.Core.State;
using ActiveSync.Server.Cli;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Spectre.Console.Cli.Testing;

namespace ActiveSync.Server.Tests;

/// <summary>`eas logs` against a migrated temp SQLite database seeded with a few log rows.</summary>
[Collection("cli")]
public sealed class CliLogsTests : IDisposable
{
	private readonly string _dbPath;
	private readonly Dictionary<string, string?> _originalEnv = [];

	public CliLogsTests()
	{
		_dbPath = Path.Combine(Path.GetTempPath(), $"as-cli-logs-{Guid.NewGuid():N}.db");
		DbContextOptions<SqliteSyncDbContext> options = new DbContextOptionsBuilder<SqliteSyncDbContext>()
			.UseSqlite($"Data Source={_dbPath}")
			.Options;
		using SqliteSyncDbContext db = new(options);
		db.Database.Migrate();
#pragma warning disable VSTHRD103
		db.LogEntries.AddRange(
			new LogEntry
			{
				TimestampUtc = DateTime.UtcNow.AddMinutes(-5), Level = "Information",
				Message = "recent-info-line", User = "alice", SourceContext = "ActiveSync.Endpoint",
			},
			new LogEntry
			{
				TimestampUtc = DateTime.UtcNow.AddMinutes(-2), Level = "Error",
				Message = "recent-error-line", User = "bob",
			},
			new LogEntry
			{
				TimestampUtc = DateTime.UtcNow.AddDays(-3), Level = "Warning",
				Message = "old-warning-line", User = "alice",
			});
#pragma warning restore VSTHRD103
		db.SaveChanges();

		SetEnv("ActiveSync__Database__ConnectionString", $"Data Source={_dbPath}");
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

	private static string Run(params string[] args)
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
			return tester.Run(args).Output;
		}
		finally
		{
			Console.SetOut(originalOut);
			Console.SetError(originalError);
		}
	}

	[Fact]
	public void Logs_DefaultWindow_ShowsRecentOnly()
	{
		string output = Run("logs");
		Assert.Contains("recent-info-line", output);
		Assert.Contains("recent-error-line", output);
		Assert.DoesNotContain("old-warning-line", output); // 3 days old, outside the default 1h
	}

	[Fact]
	public void Logs_LevelFilter_KeepsErrorAndAbove()
	{
		string output = Run("logs", "--level", "Error");
		Assert.Contains("recent-error-line", output);
		Assert.DoesNotContain("recent-info-line", output);
	}

	[Fact]
	public void Logs_UserAndSinceFilter()
	{
		string output = Run("logs", "--user", "alice", "--since", "7d");
		Assert.Contains("recent-info-line", output);
		Assert.Contains("old-warning-line", output); // alice, within 7 days
		Assert.DoesNotContain("recent-error-line", output); // bob
	}
}

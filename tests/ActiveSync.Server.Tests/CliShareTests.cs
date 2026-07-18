using ActiveSync.Core.State;
using ActiveSync.Server.Cli;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Spectre.Console.Cli.Testing;

namespace ActiveSync.Server.Tests;

/// <summary>
///   `eas share` grant management against a temp SQLite state database ("cli" collection
///   keeps env-touching tests sequential).
/// </summary>
[Collection("cli")]
public sealed class CliShareTests : IDisposable
{
	private readonly string _dbPath;
	private readonly Dictionary<string, string?> _originalEnv = [];

	public CliShareTests()
	{
		_dbPath = Path.Combine(Path.GetTempPath(), $"as-cli-share-{Guid.NewGuid():N}.db");
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

	private static CommandAppTester CreateTester()
	{
		CommandAppTester tester = new();
		tester.Configure(CliApp.Configure);
		return tester;
	}

	private List<SharedCalendarGrant> Grants()
	{
		DbContextOptions<SqliteSyncDbContext> options = new DbContextOptionsBuilder<SqliteSyncDbContext>()
			.UseSqlite($"Data Source={_dbPath}")
			.Options;
		using SqliteSyncDbContext db = new(options);
		return db.SharedCalendarGrants.AsNoTracking().ToList();
	}

	[Fact]
	public void ShareAdd_List_Remove_RoundTrips()
	{
		CommandAppTester tester = CreateTester();

		CommandAppResult add = tester.Run("share", "add", "alice@x", "/cal/family/");
		Assert.Equal(0, add.ExitCode);
		SharedCalendarGrant grant = Assert.Single(Grants());
		Assert.Equal("alice@x", grant.UserName);
		Assert.Equal("/cal/family/", grant.CollectionHref);
		Assert.False(grant.ReadOnly);

		// Re-adding with --read-only re-modes the existing grant instead of duplicating.
		CommandAppResult remode = tester.Run("share", "add", "alice@x", "/cal/family/", "--read-only");
		Assert.Equal(0, remode.ExitCode);
		grant = Assert.Single(Grants());
		Assert.True(grant.ReadOnly);

		CommandAppResult list = tester.Run("share", "list", "alice@x");
		Assert.Equal(0, list.ExitCode);
		Assert.Contains("/cal/family/", list.Output);
		Assert.Contains("read-only", list.Output);

		CommandAppResult remove = tester.Run("share", "remove", "alice@x", "/cal/family/");
		Assert.Equal(0, remove.ExitCode);
		Assert.Empty(Grants());
	}

	[Fact]
	public void ShareAdd_RejectsRelativePaths()
	{
		CommandAppResult result = CreateTester().Run("share", "add", "alice@x", "cal/family/");
		Assert.Equal(1, result.ExitCode);
		Assert.Empty(Grants());
	}
}

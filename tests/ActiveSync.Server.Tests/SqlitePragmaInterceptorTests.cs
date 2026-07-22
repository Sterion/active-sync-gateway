using ActiveSync.Server.Setup;
using Microsoft.Data.Sqlite;

namespace ActiveSync.Server.Tests;

/// <summary>E13 — WAL is a persistent DB property; the interceptor must apply it once, not per open.</summary>
public sealed class SqlitePragmaInterceptorTests : IDisposable
{
	private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"eas-pragma-{Guid.NewGuid():N}.db");

	public void Dispose()
	{
		foreach (string suffix in new[] { "", "-wal", "-shm" })
			File.Delete(_dbPath + suffix);
	}

	[Fact]
	public async Task Wal_AppliedOnce_AcrossManyConnectionOpens()
	{
		SqlitePragmaInterceptor interceptor = new();

		// Open three connections to the same database and run the interceptor on each — WAL is a
		// persistent property, so it must be issued exactly once, not re-run every open.
		for (int i = 0; i < 3; i++)
		{
			// Pooling=False so the connection releases the file handle on Dispose rather than
			// returning it to the pool — otherwise Dispose()'s File.Delete races the still-open
			// -wal/-shm handles and throws IOException on Windows (a unlink-of-open no-op on Linux).
			await using SqliteConnection connection = new($"Data Source={_dbPath};Pooling=False");
			await connection.OpenAsync();
			await interceptor.ApplyAsync(connection, CancellationToken.None);

			await using SqliteCommand check = connection.CreateCommand();
			check.CommandText = "PRAGMA busy_timeout;";
			// busy_timeout is connection-scoped and must be set on every connection.
			Assert.Equal(30000L, Convert.ToInt64(await check.ExecuteScalarAsync()));
		}

		Assert.Equal(1, interceptor.WalPragmaExecutions);
	}

	[Fact]
	public async Task Wal_PutsFileDatabaseIntoWalMode()
	{
		SqlitePragmaInterceptor interceptor = new();
		await using SqliteConnection connection = new($"Data Source={_dbPath};Pooling=False");
		await connection.OpenAsync();
		await interceptor.ApplyAsync(connection, CancellationToken.None);

		await using SqliteCommand check = connection.CreateCommand();
		check.CommandText = "PRAGMA journal_mode;";
		Assert.Equal("wal", ((string?)await check.ExecuteScalarAsync())?.ToLowerInvariant());
	}
}

using ActiveSync.Core.Options;
using ActiveSync.Core.State;
using ActiveSync.Server.Setup;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

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

	// E22: AddSyncDatabase passes the SAME `configure` delegate to both AddDbContextFactory and
	// AddDbContext, but the delegate itself does `new SqlitePragmaInterceptor()` — so EF Core invokes
	// it once per registration and mints TWO instances, each with its own `_walApplied` guard. The
	// WAL pragma is idempotent so nothing breaks, but the "one interceptor instance per database"
	// invariant the class's own doc comment asserts (and the WalPragmaExecutions seam measures)
	// does not actually hold at the DI level the two tests above never build.
	[Fact]
	public void AddSyncDatabase_Sqlite_SharesOneInterceptorInstance_BetweenTheFactoryAndTheScopedContext()
	{
		ServiceCollection services = new();
		services.AddLogging();
		services.AddOptions<ActiveSyncOptions>().Configure(o => o.Database.ConnectionString = $"Data Source={_dbPath}");
		services.AddSyncDatabase("Sqlite");
		using ServiceProvider provider = services.BuildServiceProvider();

		IDbContextFactory<SqliteSyncDbContext> factory =
			provider.GetRequiredService<IDbContextFactory<SqliteSyncDbContext>>();
		using SqliteSyncDbContext factoryContext = factory.CreateDbContext();

		using IServiceScope scope = provider.CreateScope();
		SyncDbContext scopedContext = scope.ServiceProvider.GetRequiredService<SyncDbContext>();

		// Every SqlitePragmaInterceptor reachable from either context must be the SAME reference —
		// the class's own doc comment ("one interceptor instance per database") and its
		// `_walApplied` guard only mean anything if that actually holds.
		HashSet<SqlitePragmaInterceptor> distinctInstances = new(
			InterceptorsOf(factoryContext).Concat(InterceptorsOf(scopedContext)),
			ReferenceEqualityComparer.Instance);

		Assert.Single(distinctInstances);
	}

	private static IEnumerable<SqlitePragmaInterceptor> InterceptorsOf(DbContext context)
	{
		return context.GetService<IDbContextOptions>().Extensions
			.OfType<CoreOptionsExtension>()
			.SelectMany(extension => extension.Interceptors ?? [])
			.OfType<SqlitePragmaInterceptor>();
	}
}

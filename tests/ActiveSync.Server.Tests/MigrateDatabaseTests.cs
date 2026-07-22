using ActiveSync.Core.State;
using ActiveSync.Server.Setup;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Server.Tests;

/// <summary>E22 — the migration bootstrap must honour cancellation so a killed container is interruptible.</summary>
public sealed class MigrateDatabaseTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly SqliteSyncDbContext _db;

	public MigrateDatabaseTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();
		DbContextOptions<SqliteSyncDbContext> options = new DbContextOptionsBuilder<SqliteSyncDbContext>()
			.UseSqlite(_connection).Options;
		_db = new SqliteSyncDbContext(options);
	}

	public void Dispose()
	{
		_db.Dispose();
		_connection.Dispose();
	}

	[Fact]
	public async Task MigrateDatabase_HonoursCancellation()
	{
		using CancellationTokenSource cts = new();
		await cts.CancelAsync();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			() => WebApplicationExtensions.MigrateDatabaseAsync(_db, NullLogger.Instance, cts.Token));
	}

	[Fact]
	public async Task MigrateDatabase_AppliesSchema_WhenNotCancelled()
	{
		await WebApplicationExtensions.MigrateDatabaseAsync(_db, NullLogger.Instance, CancellationToken.None);

		// The schema is now present: a table-backed query succeeds instead of throwing "no such table".
		Assert.Empty(await _db.Devices.ToListAsync());
	}
}

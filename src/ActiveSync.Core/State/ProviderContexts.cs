using Microsoft.EntityFrameworkCore;

namespace ActiveSync.Core.State;

/// <summary>SQLite-backed state context. Its migrations live in Migrations/Sqlite.</summary>
public sealed class SqliteSyncDbContext(DbContextOptions<SqliteSyncDbContext> options)
	: SyncDbContext(options);

/// <summary>PostgreSQL-backed state context. Its migrations live in Migrations/Npgsql.</summary>
public sealed class NpgsqlSyncDbContext(DbContextOptions<NpgsqlSyncDbContext> options)
	: SyncDbContext(options);

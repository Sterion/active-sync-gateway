using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ActiveSync.Core.State;

/// <summary>
///   Design-time factories used only by <c>dotnet ef</c> (migrations add / script). The
///   connection strings are placeholders — migration generation needs the provider and model,
///   not a live database.
/// </summary>
public sealed class SqliteSyncDbContextFactory : IDesignTimeDbContextFactory<SqliteSyncDbContext>
{
	public SqliteSyncDbContext CreateDbContext(string[] args)
	{
		DbContextOptions<SqliteSyncDbContext> options = new DbContextOptionsBuilder<SqliteSyncDbContext>()
			.UseSqlite("Data Source=design-time.db")
			.Options;
		return new SqliteSyncDbContext(options);
	}
}

public sealed class NpgsqlSyncDbContextFactory : IDesignTimeDbContextFactory<NpgsqlSyncDbContext>
{
	public NpgsqlSyncDbContext CreateDbContext(string[] args)
	{
		DbContextOptions<NpgsqlSyncDbContext> options = new DbContextOptionsBuilder<NpgsqlSyncDbContext>()
			.UseNpgsql("Host=localhost;Database=activesync;Username=postgres;Password=postgres")
			.Options;
		return new NpgsqlSyncDbContext(options);
	}
}

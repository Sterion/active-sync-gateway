using ActiveSync.Core.Options;
using ActiveSync.Core.State;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ActiveSync.Core.Settings;

/// <summary>
///   Build-time (pre-DI) synchronous read of the global settings table, using the bootstrap
///   <see cref="DatabaseOptions" /> (which must come from file/env — never the database itself).
///   Populating the configuration provider BEFORE the host is built lets even restart-bound
///   settings (listener ports, TLS/metrics enable) that live only in the database take effect on
///   the next start. Tolerant of a missing table / unreachable database (fresh install): returns an
///   empty map so startup never fails on settings. Ongoing live updates go through
///   <see cref="SettingsRefresher" /> instead.
/// </summary>
public static class DbSettingsLoader
{
	public static Dictionary<string, string?> TryLoad(DatabaseOptions database, ILogger? logger)
	{
		Dictionary<string, string?> result = new(StringComparer.OrdinalIgnoreCase);
		try
		{
			using SyncDbContext db = Create(database);
			foreach (GlobalSetting row in db.GlobalSettings.AsNoTracking().ToList())
				result[row.Key] = row.Value;
		}
		catch (Exception ex)
		{
			// Fresh install (table not created yet) or unreachable database — start from the
			// file/env values and let the post-migration refresh pick up rows once they exist.
			logger?.LogDebug(ex, "No database settings loaded at startup (fresh install or database unreachable)");
		}

		return result;
	}

	private static SyncDbContext Create(DatabaseOptions database)
	{
		string connection = database.ConnectionString;
		if (PostgresConnectionUri.IsPostgresUri(connection) &&
		    PostgresConnectionUri.TryConvert(connection, out string keywordForm, out _))
			connection = keywordForm;

		return PostgresConnectionUri.EffectiveProvider(database).ToLowerInvariant() switch
		{
			"postgres" or "postgresql" or "npgsql" => new NpgsqlSyncDbContext(
				new DbContextOptionsBuilder<NpgsqlSyncDbContext>().UseNpgsql(connection).Options),
			"sqlite" => new SqliteSyncDbContext(
				new DbContextOptionsBuilder<SqliteSyncDbContext>().UseSqlite(connection).Options),
			var other => throw new InvalidOperationException($"Unknown database provider '{other}'.")
		};
	}
}

using ActiveSync.Core.Options;
using ActiveSync.Core.State;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

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
		// A missing GlobalSettings table means "fresh install, migrations haven't run" — expected,
		// Debug. Anything else (Postgres down for 20 s at boot, a wrong connection string) silently
		// reverts restart-tier settings that live ONLY in the database — TLS/metrics enable, listener
		// ports — to their POCO defaults, which the old catch-all hid at Debug. Distinguish them, and
		// retry a couple of times so a brief connect blip at boot isn't fatal to those settings (B9).
		const int attempts = 3;
		for (int attempt = 1; ; attempt++)
		{
			try
			{
				Dictionary<string, string?> result = new(StringComparer.OrdinalIgnoreCase);
				using SyncDbContext db = Create(database);
				foreach (GlobalSetting row in db.GlobalSettings.AsNoTracking().ToList())
					result[row.Key] = row.Value;
				return result;
			}
			catch (Exception ex) when (IsMissingSettingsTable(ex))
			{
				logger?.LogDebug(ex,
					"No database settings table yet (fresh install); starting from file/env values");
				return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
			}
			catch (Exception ex)
			{
				if (attempt < attempts)
				{
					Thread.Sleep(TimeSpan.FromMilliseconds(250 * attempt));
					continue;
				}

				logger?.LogWarning(ex,
					"Could not read database-stored settings after {Attempts} attempts — database-stored " +
					"settings are NOT applied (starting from file/env values only)", attempts);
				return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
			}
		}
	}

	/// <summary>
	///   True when the failure is "the settings table does not exist yet" (a fresh install before
	///   migrations), as opposed to a genuine outage. Sqlite reports "no such table"; Postgres reports
	///   SQLSTATE 42P01 (undefined_table).
	/// </summary>
	private static bool IsMissingSettingsTable(Exception exception)
	{
		for (Exception? ex = exception; ex is not null; ex = ex.InnerException)
		{
			if (ex is PostgresException pg && pg.SqlState == PostgresErrorCodes.UndefinedTable)
				return true;
			if (ex is SqliteException && ex.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase))
				return true;
		}

		return false;
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

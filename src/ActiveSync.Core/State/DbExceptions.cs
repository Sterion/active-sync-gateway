using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ActiveSync.Core.State;

/// <summary>Narrow classification of the <see cref="DbUpdateException" />s the state layer catches.</summary>
internal static class DbExceptions
{
	/// <summary>
	///   True only for a unique- or primary-key-constraint violation — the one failure the
	///   insert-race guards may swallow and re-read. Every other <see cref="DbUpdateException" />
	///   (disk full, SQLITE_BUSY, a NOT NULL violation, a lost connection) is a real error and
	///   must propagate with its original diagnostic intact rather than be re-read into a
	///   confusing "Sequence contains no elements" (A9).
	/// </summary>
	public static bool IsUniqueViolation(DbUpdateException ex)
		=> ex.InnerException switch
		{
			// SQLITE_CONSTRAINT_UNIQUE (2067) / SQLITE_CONSTRAINT_PRIMARYKEY (1555). The primary
			// code 19 also covers NOT NULL/CHECK/FK, so match on the extended code to stay narrow.
			SqliteException s => s.SqliteExtendedErrorCode is 2067 or 1555,
			// PostgreSQL unique_violation.
			PostgresException p => p.SqlState == "23505",
			_ => false
		};
}

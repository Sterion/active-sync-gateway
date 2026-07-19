using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ActiveSync.Server.Setup;

/// <summary>
///   Applies WAL journal mode and a busy timeout to every SQLite connection. WAL lets readers run
///   concurrently with the single writer, and the busy timeout makes a contending writer WAIT
///   rather than fail with "database is locked" — necessary now that the background
///   <see cref="DatabaseLogSink" /> writes alongside the request path. No-op for PostgreSQL (this
///   interceptor is only wired on the SQLite provider); WAL is ignored for in-memory databases.
/// </summary>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
	public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
	{
		Apply(connection);
		base.ConnectionOpened(connection, eventData);
	}

	public override async Task ConnectionOpenedAsync(
		DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
	{
		Apply(connection);
		await base.ConnectionOpenedAsync(connection, eventData, cancellationToken).ConfigureAwait(false);
	}

	private static void Apply(DbConnection connection)
	{
		using DbCommand command = connection.CreateCommand();
		command.CommandText = "PRAGMA busy_timeout=30000; PRAGMA journal_mode=WAL;";
		command.ExecuteNonQuery();
	}
}

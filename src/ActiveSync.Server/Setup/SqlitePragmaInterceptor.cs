using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ActiveSync.Server.Setup;

/// <summary>
///   Applies a busy timeout to every SQLite connection and WAL journal mode ONCE. WAL lets readers
///   run concurrently with the single writer, and the busy timeout makes a contending writer WAIT
///   rather than fail with "database is locked" — necessary now that the background
///   <see cref="DatabaseLogSink" /> writes alongside the request path. WAL is a PERSISTENT database
///   property, so it is set once per interceptor instance (one per database) rather than re-run on
///   every connection open; <c>busy_timeout</c> is connection-scoped and set on each connection.
///   No-op for PostgreSQL (this interceptor is only wired on the SQLite provider); WAL is ignored
///   for in-memory databases.
/// </summary>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
	// One interceptor instance is created per database (see the AddDbContext registration), so an
	// instance-scoped guard applies WAL exactly once for that database's connections (E13).
	private int _walApplied;

	/// <summary>Test seam (E13): how many times the WAL pragma was actually issued.</summary>
	internal int WalPragmaExecutions { get; private set; }

	public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
	{
		Apply(connection);
		base.ConnectionOpened(connection, eventData);
	}

	public override async Task ConnectionOpenedAsync(
		DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
	{
		await ApplyAsync(connection, cancellationToken).ConfigureAwait(false);
		await base.ConnectionOpenedAsync(connection, eventData, cancellationToken).ConfigureAwait(false);
	}

	private void Apply(DbConnection connection)
	{
		ExecutePragma(connection, "PRAGMA busy_timeout=30000;");
		ApplyWal(connection);
	}

	internal async Task ApplyAsync(DbConnection connection, CancellationToken ct)
	{
		// busy_timeout is connection-scoped: set on every connection.
		await ExecutePragmaAsync(connection, "PRAGMA busy_timeout=30000;", ct).ConfigureAwait(false);
		await ApplyWalAsync(connection, ct).ConfigureAwait(false);
	}

	private void ApplyWal(DbConnection connection)
	{
		// Claim the one-time application; reset the guard if it throws so a later connection retries.
		if (Interlocked.CompareExchange(ref _walApplied, 1, 0) != 0)
			return;
		try
		{
			ExecutePragma(connection, "PRAGMA journal_mode=WAL;");
			WalPragmaExecutions++;
		}
		catch
		{
			Volatile.Write(ref _walApplied, 0);
			throw;
		}
	}

	private async Task ApplyWalAsync(DbConnection connection, CancellationToken ct)
	{
		if (Interlocked.CompareExchange(ref _walApplied, 1, 0) != 0)
			return;
		try
		{
			await ExecutePragmaAsync(connection, "PRAGMA journal_mode=WAL;", ct).ConfigureAwait(false);
			WalPragmaExecutions++;
		}
		catch
		{
			Volatile.Write(ref _walApplied, 0);
			throw;
		}
	}

	private static void ExecutePragma(DbConnection connection, string sql)
	{
		using DbCommand command = connection.CreateCommand();
		command.CommandText = sql;
		command.ExecuteNonQuery();
	}

	private static async Task ExecutePragmaAsync(DbConnection connection, string sql, CancellationToken ct)
	{
		await using DbCommand command = connection.CreateCommand();
		command.CommandText = sql;
		await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
	}
}

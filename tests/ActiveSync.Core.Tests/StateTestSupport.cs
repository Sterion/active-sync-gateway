using ActiveSync.Core.State;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ActiveSync.Core.Tests;

/// <summary>
///   A <see cref="SaveChangesInterceptor" /> that throws a chosen exception the next time a save
///   flushes, then disarms itself. Lets the state-layer tests reproduce a mid-save
///   <see cref="DbUpdateException" /> (a unique-constraint race, or a non-unique failure such as
///   disk-full) deterministically, without a real second writer.
/// </summary>
internal sealed class FaultInjectingInterceptor : SaveChangesInterceptor
{
	private Exception? _next;

	/// <summary>Arm the interceptor to throw <paramref name="ex" /> on the next save.</summary>
	public void ThrowOnNextSave(Exception ex) => _next = ex;

	public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
		DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
	{
		Exception? ex = _next;
		if (ex is not null)
		{
			_next = null;
			throw ex;
		}

		return base.SavingChangesAsync(eventData, result, ct);
	}

	public override InterceptionResult<int> SavingChanges(
		DbContextEventData eventData, InterceptionResult<int> result)
	{
		Exception? ex = _next;
		if (ex is not null)
		{
			_next = null;
			throw ex;
		}

		return base.SavingChanges(eventData, result);
	}
}

/// <summary>Shared builders for the in-memory SQLite contexts the state-layer tests use.</summary>
internal static class StateTestSupport
{
	public static SqliteSyncDbContext NewContext(SqliteConnection connection, FaultInjectingInterceptor? interceptor = null)
	{
		DbContextOptionsBuilder<SqliteSyncDbContext> builder = new DbContextOptionsBuilder<SqliteSyncDbContext>()
			.UseSqlite(connection);
		if (interceptor is not null)
			builder.AddInterceptors(interceptor);
		return new SqliteSyncDbContext(builder.Options);
	}
}

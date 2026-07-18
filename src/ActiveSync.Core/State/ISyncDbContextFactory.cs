using Microsoft.EntityFrameworkCore;

namespace ActiveSync.Core.State;

/// <summary>
///   Provider-neutral factory for short-lived <see cref="SyncDbContext" /> instances. Needed by
///   components that outlive a request scope (the cached backend sessions and their local
///   content stores) and therefore cannot use the request-scoped context.
/// </summary>
public interface ISyncDbContextFactory
{
	SyncDbContext CreateDbContext();
}

/// <summary>Adapts the provider-specific EF factory to the neutral interface.</summary>
public sealed class SyncDbContextFactoryAdapter<TContext>(IDbContextFactory<TContext> inner)
	: ISyncDbContextFactory
	where TContext : SyncDbContext
{
	public SyncDbContext CreateDbContext()
	{
		return inner.CreateDbContext();
	}
}

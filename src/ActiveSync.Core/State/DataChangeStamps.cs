using Microsoft.EntityFrameworkCore;

namespace ActiveSync.Core.State;

/// <summary>
///   The one place a <see cref="DataChange" /> row is read or bumped. Both stores used to
///   re-implement read-row-or-insert against their own single-row stamp table; this collapses
///   that to one call, so adding a third watched area later is a call site rather than a copy
///   (and a new table plus a migration).
/// </summary>
public static class DataChangeStamps
{
	/// <summary>
	///   The current version of one watched area, or null when nothing was ever written for it.
	///   This is the cheap point-read every replica does at most once per
	///   <c>Auth:UsersRefreshSeconds</c>; it opens its own short-lived context.
	/// </summary>
	public static async Task<Guid?> ReadAsync(
		ISyncDbContextFactory contextFactory, string area, CancellationToken ct)
	{
		await using SyncDbContext db = contextFactory.CreateDbContext();
		DataChange? row = await db.DataChanges.AsNoTracking()
			.FirstOrDefaultAsync(c => c.Key == area, ct).ConfigureAwait(false);
		return row?.Version;
	}

	/// <summary>
	///   Stages a new version for one area on <paramref name="db" />, to be committed by the
	///   CALLER'S SaveChanges — the stamp must move in the same transaction as the mutation it
	///   signals, or a replica can observe the new data under the old version (and cache it).
	///   <para>
	///     The insert path races: two replicas can both find no row and both insert. The caller
	///     handles that the way the rest of the codebase does (catch the unique/PK violation and
	///     re-read) — see <see cref="BumpAndSaveAsync" /> for the ready-made version.
	///   </para>
	/// </summary>
	public static async Task BumpAsync(SyncDbContext db, string area, CancellationToken ct)
	{
		DataChange? row = await db.DataChanges
			.FirstOrDefaultAsync(c => c.Key == area, ct).ConfigureAwait(false);
		if (row is null)
		{
			// DbSet.Add is synchronous and local (no I/O) — AddAsync exists only to support
			// async value generators (e.g. HiLo/Cosmos), which this project doesn't use.
#pragma warning disable VSTHRD103
			db.DataChanges.Add(new DataChange
			{
				Key = area, Version = Guid.NewGuid(), UpdatedUtc = DateTime.UtcNow,
			});
#pragma warning restore VSTHRD103
		}
		else
		{
			row.Version = Guid.NewGuid();
			row.UpdatedUtc = DateTime.UtcNow;
		}
	}

	/// <summary>
	///   Bumps the area and saves, tolerating the first-use insert race: when a concurrent
	///   replica created the row between our read and our insert, the primary-key conflict is
	///   caught, the row re-read, and the bump applied as an update. Any other failure keeps its
	///   own diagnostic.
	/// </summary>
	public static async Task BumpAndSaveAsync(SyncDbContext db, string area, CancellationToken ct)
	{
		await BumpAsync(db, area, ct).ConfigureAwait(false);
		try
		{
			await db.SaveChangesAsync(ct).ConfigureAwait(false);
		}
		catch (DbUpdateException ex) when (DbExceptions.IsUniqueViolation(ex))
		{
			DataChange? staged = db.ChangeTracker.Entries<DataChange>()
				.FirstOrDefault(e => e.State == EntityState.Added)?.Entity;
			if (staged is null)
				throw;
			db.Entry(staged).State = EntityState.Detached;
			DataChange? winner = await db.DataChanges
				.FirstOrDefaultAsync(c => c.Key == area, ct).ConfigureAwait(false);
			if (winner is null)
				throw;
			winner.Version = Guid.NewGuid();
			winner.UpdatedUtc = DateTime.UtcNow;
			await db.SaveChangesAsync(ct).ConfigureAwait(false);
		}
	}
}

using ActiveSync.Core.State;
using Microsoft.EntityFrameworkCore;

namespace ActiveSync.Core.Administration;

/// <summary>
///   The single write path over <see cref="SharedCalendarGrant" /> rows (`eas share` and the web
///   Shares page). Both surfaces used to hand-roll the same find-then-add/re-mode EF against the
///   same table — the S3/C18 defect. Identifier shape checks belong to the caller
///   (<see cref="AdminIdentifiers" />) which surfaces the error its own way; this service does the
///   DB work and reports which branch it took so the caller can phrase the result.
/// </summary>
public sealed class ShareAdminService(ISyncDbContextFactory contextFactory)
{
	public sealed record SharePage(int Total, IReadOnlyList<SharedCalendarGrant> Grants);

	public enum UpsertKind { Created, Remoded, Unchanged }

	public sealed record ShareUpsert(UpsertKind Kind, DateTime CreatedUtc);

	/// <summary>
	///   Grants ordered by (user, href). <paramref name="take" /> null lists every match (the CLI,
	///   printing to a terminal); the web page passes a clamped page size and reads <c>Total</c>.
	/// </summary>
	public async Task<SharePage> ListAsync(string? user, int skip, int? take, CancellationToken ct)
	{
		await using SyncDbContext db = contextFactory.CreateDbContext();
		IQueryable<SharedCalendarGrant> query = db.SharedCalendarGrants.AsNoTracking()
			.Where(g => user == null || g.UserName == user);
		int total = await query.CountAsync(ct).ConfigureAwait(false);
		query = query.OrderBy(g => g.UserName).ThenBy(g => g.CollectionHref).Skip(Math.Max(skip, 0));
		if (take is { } t)
			query = query.Take(t);
		List<SharedCalendarGrant> grants = await query.ToListAsync(ct).ConfigureAwait(false);
		return new SharePage(total, grants);
	}

	/// <summary>
	///   Add the grant, or re-mode an existing one to <paramref name="readOnly" />. Adding a grant
	///   that already exists in the requested mode is a no-op (<see cref="UpsertKind.Unchanged" />).
	///   The href is trimmed here so both surfaces store the identical key.
	/// </summary>
	public async Task<ShareUpsert> AddOrUpdateAsync(
		string user, string collectionHref, bool readOnly, CancellationToken ct)
	{
		string href = collectionHref.Trim();
		await using SyncDbContext db = contextFactory.CreateDbContext();
		SharedCalendarGrant? existing = await db.SharedCalendarGrants
			.FirstOrDefaultAsync(g => g.UserName == user && g.CollectionHref == href, ct)
			.ConfigureAwait(false);
		if (existing is not null)
		{
			if (existing.ReadOnly == readOnly)
				return new ShareUpsert(UpsertKind.Unchanged, existing.CreatedUtc);
			existing.ReadOnly = readOnly;
			await db.SaveChangesAsync(ct).ConfigureAwait(false);
			return new ShareUpsert(UpsertKind.Remoded, existing.CreatedUtc);
		}

		DateTime createdUtc = DateTime.UtcNow;
		// DbSet.Add is synchronous and local (no I/O) — AddAsync exists only for async value
		// generators, which this project doesn't use.
#pragma warning disable VSTHRD103
		db.SharedCalendarGrants.Add(new SharedCalendarGrant
		{
			UserName = user,
			CollectionHref = href,
			ReadOnly = readOnly,
			CreatedUtc = createdUtc,
		});
#pragma warning restore VSTHRD103
		await db.SaveChangesAsync(ct).ConfigureAwait(false);
		return new ShareUpsert(UpsertKind.Created, createdUtc);
	}

	/// <summary>Remove a grant; returns false when there was nothing to remove.</summary>
	public async Task<bool> RemoveAsync(string user, string collectionHref, CancellationToken ct)
	{
		string href = collectionHref.Trim();
		await using SyncDbContext db = contextFactory.CreateDbContext();
		SharedCalendarGrant? existing = await db.SharedCalendarGrants
			.FirstOrDefaultAsync(g => g.UserName == user && g.CollectionHref == href, ct)
			.ConfigureAwait(false);
		if (existing is null)
			return false;
		db.SharedCalendarGrants.Remove(existing);
		await db.SaveChangesAsync(ct).ConfigureAwait(false);
		return true;
	}
}

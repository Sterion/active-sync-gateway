using Microsoft.EntityFrameworkCore;

namespace ActiveSync.Core.State;

/// <summary>
///   The DAV href → short-id map: item ServerIds for DAV collections are a short numeric id
///   from the <c>DavItems</c> table rather than the (long, path-shaped) href. One of the
///   collaborators composed by <see cref="SyncStateService" />, sharing its request-scoped
///   <see cref="SyncDbContext" />.
/// </summary>
internal sealed class DavItemMap(SyncDbContext db)
{
	public async Task<string> GetOrAddDavItemIdAsync(UserFolder folder, string href, CancellationToken ct)
	{
		DavItem? item = await db.DavItems
			.FirstOrDefaultAsync(i => i.UserFolderKey == folder.Id && i.Href == href, ct)
			.ConfigureAwait(false);
		if (item is null)
		{
			item = new DavItem { UserFolderKey = folder.Id, Href = href };
			// DbSet.Add false positive for VSTHRD103 — see GetOrCreateDeviceAsync.
#pragma warning disable VSTHRD103
			db.DavItems.Add(item);
#pragma warning restore VSTHRD103
			try
			{
				await db.SaveChangesAsync(ct).ConfigureAwait(false);
			}
			catch (DbUpdateException ex) when (DbExceptions.IsUniqueViolation(ex))
			{
				// A concurrent request mapped the same href first — re-read the winner. Only a
				// unique violation takes this path; any other failure keeps its diagnostic (A9).
				db.Entry(item).State = EntityState.Detached;
				item = await db.DavItems
					.FirstAsync(i => i.UserFolderKey == folder.Id && i.Href == href, ct).ConfigureAwait(false);
			}
		}

		return item.Id.ToString();
	}

	public async Task<string?> ResolveDavHrefAsync(UserFolder folder, string shortId, CancellationToken ct)
	{
		if (!int.TryParse(shortId, out int id))
			return null;
		DavItem? item = await db.DavItems
			.FirstOrDefaultAsync(i => i.Id == id && i.UserFolderKey == folder.Id, ct)
			.ConfigureAwait(false);
		return item?.Href;
	}
}

using Microsoft.EntityFrameworkCore;

namespace ActiveSync.Core.State;

/// <summary>
///   The DAV href → short-id map: item ServerIds for DAV collections are a short numeric id
///   from the <c>DavItems</c> table rather than the (long, path-shaped) href.
///   <para>
///     Unlike its sibling collaborators, this one runs each allocation on its OWN short-lived
///     context (via <see cref="ISyncDbContextFactory" />) rather than the request-scoped one it is
///     composed with. An id allocation happens mid-Sync while the handler holds a half-mutated
///     <see cref="CollectionState" />; committing it on the shared context flushed that state and
///     bumped its ConcurrencyToken before the round was known good (A10). Isolating it also keeps
///     the unique-violation-and-re-read (a self-contained race) from poisoning a larger unit of
///     work. It falls back to the shared context only when no factory is supplied (unit tests).
///   </para>
/// </summary>
internal sealed class DavItemMap(SyncDbContext db, ISyncDbContextFactory? factory = null)
{
	public async Task<string> GetOrAddDavItemIdAsync(UserFolder folder, string href, CancellationToken ct)
	{
		if (factory is null)
			return await GetOrAddAsync(db, folder.Id, href, ct).ConfigureAwait(false);

		await using SyncDbContext own = factory.CreateDbContext();
		return await GetOrAddAsync(own, folder.Id, href, ct).ConfigureAwait(false);
	}

	private static async Task<string> GetOrAddAsync(SyncDbContext ctx, int folderId, string href, CancellationToken ct)
	{
		DavItem? item = await ctx.DavItems
			.FirstOrDefaultAsync(i => i.UserFolderKey == folderId && i.Href == href, ct)
			.ConfigureAwait(false);
		if (item is null)
		{
			item = new DavItem { UserFolderKey = folderId, Href = href };
			// DbSet.Add false positive for VSTHRD103 — see GetOrCreateDeviceAsync.
#pragma warning disable VSTHRD103
			ctx.DavItems.Add(item);
#pragma warning restore VSTHRD103
			try
			{
				await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
			}
			catch (DbUpdateException ex) when (DbExceptions.IsUniqueViolation(ex))
			{
				// A concurrent request mapped the same href first — re-read the winner. Only a
				// unique violation takes this path; any other failure keeps its diagnostic (A9).
				ctx.Entry(item).State = EntityState.Detached;
				item = await ctx.DavItems
					.FirstAsync(i => i.UserFolderKey == folderId && i.Href == href, ct).ConfigureAwait(false);
			}
		}

		return item.Id.ToString();
	}

	public async Task<string?> ResolveDavHrefAsync(UserFolder folder, string shortId, CancellationToken ct)
	{
		// A pure read flushes nothing, so it stays on the shared context — only the writer above
		// needs isolation (A10). Its tracking/N+1 shape is A3's remit (item 23).
		if (!int.TryParse(shortId, out int id))
			return null;
		DavItem? item = await db.DavItems
			.FirstOrDefaultAsync(i => i.Id == id && i.UserFolderKey == folder.Id, ct)
			.ConfigureAwait(false);
		return item?.Href;
	}
}

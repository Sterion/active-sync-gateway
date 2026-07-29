using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Security;
using ActiveSync.Core.State;
using Microsoft.EntityFrameworkCore;

namespace ActiveSync.Backends.Local;

/// <summary>
///   Content store over the gateway's own database (the <see cref="LocalItem" /> table), used
///   when no external DAV backend is configured (and always for Notes). Shaped like the DAV
///   stores with the WebDAV client swapped for EF: stored content is vCard/iCalendar text, item
///   keys are row ids, revisions are a per-row version counter. Data is visible to all of the
///   user's ActiveSync devices and nowhere else. Content is encrypted at rest via
///   <see cref="LocalContentProtector" /> — every read of <see cref="LocalItem.Content" />
///   must go through <see cref="Protector" />. The typed payload ⇄ stored-text mapping is the
///   subclass's (for the payload classes it is the identity — the payload IS the stored text,
///   so what the host hands over is exactly what it gets back).
/// </summary>
/// <typeparam name="TItem">The content class's payload record.</typeparam>
public abstract class LocalStoreBase<TItem>(
	ISyncDbContextFactory dbFactory,
	LocalChangeNotifier notifier,
	int userId,
	LocalContentProtector protector) : IContentStore<TItem> where TItem : class
{
	public const string KeyPrefix = "local:";

	/// <summary>Collection bucket in the LocalItems table ("contacts"/"calendar"/"notes").</summary>
	protected abstract string Collection { get; }

	protected abstract string FolderDisplayName { get; }
	protected abstract FolderType FolderType { get; }

	/// <summary>The owning gateway user — THE identity (DB scoping, AAD, notifier keys).</summary>
	protected int UserId => userId;
	protected ISyncDbContextFactory DbFactory => dbFactory;

	/// <summary>Wakes Ping/Sync waiters after a write on this user's collection.</summary>
	protected void NotifyChanged()
	{
		notifier.NotifyChanged(userId, Collection);
	}

	protected LocalContentProtector Protector => protector;

	public string FolderBackendKey => KeyPrefix + Collection;

	// Local stores share the "local:" prefix, so the single-folder key is matched exactly.
	public bool OwnsKey(FolderKey key)
	{
		return key.Value == FolderBackendKey;
	}

	public Task<IReadOnlyList<BackendFolder>> ListFoldersAsync(CancellationToken ct)
	{
		return Task.FromResult<IReadOnlyList<BackendFolder>>(
		[
			new BackendFolder
			{
				Key = new FolderKey(FolderBackendKey),
				DisplayName = FolderDisplayName,
				Type = FolderType
			}
		]);
	}

	public async Task<IReadOnlyDictionary<ItemKey, ItemRevision>> GetItemRevisionsAsync(
		FolderKey folder, ContentFilter filter, CancellationToken ct)
	{
		await using SyncDbContext db = dbFactory.CreateDbContext();
		// AsNoTracking — a read-only revision listing must not populate the change tracker.
		IQueryable<LocalItem> query = Rows(db).AsNoTracking();
		// Hoisted out of the predicate: the stored column is a UTC DateTime, and a member access
		// on the captured offset inside the expression tree is one more thing for the provider
		// to have to evaluate.
		if (filter.Since is { } since)
		{
			DateTime sinceUtc = since.UtcDateTime;
			query = query.Where(i => i.ItemDateUtc == null || i.ItemDateUtc >= sinceUtc);
		}

		var rows = await query.Select(i => new { i.Id, i.Version }).ToListAsync(ct).ConfigureAwait(false);
		return rows.ToDictionary(
			r => new ItemKey(r.Id.ToString()),
			r => new ItemRevision(r.Version.ToString()));
	}

	public async Task<TItem?> GetItemAsync(FolderKey folder, ItemKey item, CancellationToken ct)
	{
		await using SyncDbContext db = dbFactory.CreateDbContext();
		LocalItem? row = await FindAsync(db, item.Value, ct).ConfigureAwait(false);
		if (row is null)
			return null;
		string content = protector.Unprotect(row.Content, userId, Collection);
		return ParseContent(content);
	}

	public async Task<(ItemKey Key, ItemRevision Revision)> CreateItemAsync(
		FolderKey folder, TItem item, CancellationToken ct)
	{
		// The generated uid is only the fallback: BuildContent may embed it (notes), and
		// ExtractUidCore reads the payload's own UID back out (iCalendar/vCard classes), so the
		// row's Uid column always matches what the stored text actually says.
		string uid = Guid.NewGuid().ToString();
		string content = BuildContent(item, null, uid);
		uid = ExtractUidCore(content) ?? uid;
		await using SyncDbContext db = dbFactory.CreateDbContext();
		LocalItem row = new()
		{
			UserId = userId,
			Collection = Collection,
			Uid = uid,
			Content = protector.Protect(content, userId, Collection),
			Version = 1,
			ItemDateUtc = ExtractItemDate(content),
			LastModifiedUtc = DateTime.UtcNow
		};
		// DbSet.Add is synchronous and local (no I/O) — AddAsync exists only to support
		// async value generators (e.g. HiLo/Cosmos), which this project doesn't use.
#pragma warning disable VSTHRD103
		db.LocalItems.Add(row);
#pragma warning restore VSTHRD103
		await db.SaveChangesAsync(ct).ConfigureAwait(false);
		notifier.NotifyChanged(userId, Collection);
		return (new ItemKey(row.Id.ToString()), new ItemRevision(row.Version.ToString()));
	}

	public async Task<ItemRevision> UpdateItemAsync(
		FolderKey folder, ItemKey item, TItem value, ItemRevision? expected, CancellationToken ct)
	{
		// The host merged the client's partial data already — `value` is the complete payload.
		// This store CAN honour the `expected` precondition (the row version IS the revision), so
		// it does: a mismatch throws the typed precondition failure and the host re-merges onto
		// the fresh payload and retries once. Without `expected` the write is unconditional, with
		// a bounded retry on the row-version race (re-applying the same complete payload).
		const int maxAttempts = 4;
		for (int attempt = 1; ; attempt++)
		{
			await using SyncDbContext db = dbFactory.CreateDbContext();
			LocalItem row = await FindAsync(db, item.Value, ct).ConfigureAwait(false)
			                ?? throw new BackendItemNotFoundException(
				                $"Local {Collection} item {item.Value} no longer exists.");
			if (expected is { } expectedRevision && row.Version.ToString() != expectedRevision.Value)
				throw new BackendPreconditionFailedException(
					$"Local {Collection} item {item.Value} is at version {row.Version}, not the expected {expectedRevision.Value}.");

			// The stored text the payload replaces still feeds the class's merge hook (notes
			// preserve unmapped VJOURNAL properties); ExtractItemDate parses the new plaintext
			// before it is sealed again.
			string existing = protector.Unprotect(row.Content, userId, Collection);
			string content = BuildContent(value, existing, row.Uid);
			row.Content = protector.Protect(content, userId, Collection);
			row.Version++;
			row.ItemDateUtc = ExtractItemDate(content);
			row.LastModifiedUtc = DateTime.UtcNow;
			try
			{
				await db.SaveChangesAsync(ct).ConfigureAwait(false);
			}
			catch (DbUpdateConcurrencyException) when (expected is not null)
			{
				// Another writer bumped the row between our read and save — exactly what the
				// precondition promises to detect. Surface it typed; retrying here would defeat
				// the host's re-merge.
				throw new BackendPreconditionFailedException(
					$"Local {Collection} item {item.Value} was modified concurrently.");
			}
			catch (DbUpdateConcurrencyException) when (attempt < maxAttempts)
			{
				continue;
			}
			catch (DbUpdateConcurrencyException ex)
			{
				// Retries are exhausted — surface the store's own exception type (every
				// other IContentStore failure funnels through BackendException) instead of
				// leaking an EF Core type across the store boundary.
				throw new BackendException($"Local {Collection} item {item.Value} is being modified concurrently; retry.", ex);
			}

			notifier.NotifyChanged(userId, Collection);
			return new ItemRevision(row.Version.ToString());
		}
	}

	public async Task DeleteItemAsync(FolderKey folder, ItemKey item, bool permanent, CancellationToken ct)
	{
		await using SyncDbContext db = dbFactory.CreateDbContext();
		LocalItem? row = await FindAsync(db, item.Value, ct).ConfigureAwait(false);
		if (row is null)
			return;
		db.LocalItems.Remove(row);
		await db.SaveChangesAsync(ct).ConfigureAwait(false);
		notifier.NotifyChanged(userId, Collection);
	}

	// Local stores expose a single fixed folder and cannot move items — so they implement
	// neither IItemMoveOperations nor IFolderOperations rather than carrying throw-stubs. The host
	// answers MoveItems/Folder* with the unsupported status when the capability is absent.

	public async Task<IReadOnlyList<FolderKey>> WaitForChangesAsync(
		IReadOnlyList<FolderKey> folders, TimeSpan timeout, CancellationToken ct)
	{
		// Captured as early as possible — mirrors ImapMailBackend.WaitForChangesAsync's own
		// watchStartUtc — so a write that latched between the caller's entry check (e.g.
		// PingHandler.CheckPendingAsync) and this wait's registration is still observed.
		DateTime watchStartUtc = DateTime.UtcNow;
		return await notifier.WaitAsync(userId, Collection, timeout, watchStartUtc, ct).ConfigureAwait(false)
			? [new FolderKey(FolderBackendKey)]
			: [];
	}

	/// <summary>The typed payload for stored content (null = unparsable; the item is skipped).</summary>
	protected abstract TItem? ParseContent(string content);

	/// <summary>
	///   The stored text for a complete payload. Payload classes return the payload verbatim
	///   (round-trip fidelity — what the host hands over is what it gets back); notes merge the
	///   typed record onto the existing stored VJOURNAL.
	/// </summary>
	/// <param name="item">The complete payload.</param>
	/// <param name="existingContent">The stored text being replaced, or null on create.</param>
	/// <param name="uid">The row's UID (a fresh guid on create).</param>
	protected abstract string BuildContent(TItem item, string? existingContent, string uid);

	/// <summary>The payload's own UID for the row's Uid column; null = keep the generated one.</summary>
	protected virtual string? ExtractUidCore(string content)
	{
		return null;
	}

	/// <summary>Item date used by EAS filter windows (event start); null = always in range.</summary>
	protected virtual DateTime? ExtractItemDate(string content)
	{
		return null;
	}

	protected IQueryable<LocalItem> Rows(SyncDbContext db)
	{
		return db.LocalItems.Where(i => i.UserId == userId && i.Collection == Collection);
	}

	protected async Task<LocalItem?> FindAsync(SyncDbContext db, string itemKey, CancellationToken ct)
	{
		if (!int.TryParse(itemKey, out int id))
			return null;
		return await Rows(db).FirstOrDefaultAsync(i => i.Id == id, ct).ConfigureAwait(false);
	}
}

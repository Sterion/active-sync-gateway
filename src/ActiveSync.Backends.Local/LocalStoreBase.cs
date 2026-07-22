using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Security;
using ActiveSync.Core.State;
using Microsoft.EntityFrameworkCore;

namespace ActiveSync.Backends.Local;

/// <summary>
///   Content store over the gateway's own database (the <see cref="LocalItem" /> table), used
///   when no external DAV backend is configured (and always for Notes). Shaped like the DAV
///   stores with the WebDAV client swapped for EF: content is vCard/iCalendar text, item keys
///   are row ids, revisions are a per-row version counter. Data is visible to all of the
///   user's ActiveSync devices and nowhere else. Content is encrypted at rest via
///   <see cref="LocalContentProtector" /> — every read of <see cref="LocalItem.Content" />
///   must go through <see cref="Protector" />.
/// </summary>
public abstract class LocalStoreBase(
	ISyncDbContextFactory dbFactory,
	LocalChangeNotifier notifier,
	BackendCredentials credentials,
	LocalContentProtector protector) : IContentStore
{
	public const string KeyPrefix = "local:";

	/// <summary>Collection bucket in the LocalItems table ("contacts"/"calendar"/"notes").</summary>
	protected abstract string Collection { get; }

	protected abstract string FolderDisplayName { get; }
	protected abstract int FolderType { get; }

	protected BackendCredentials Credentials => credentials;
	protected ISyncDbContextFactory DbFactory => dbFactory;

	/// <summary>Wakes Ping/Sync waiters after a write on this user's collection.</summary>
	protected void NotifyChanged()
	{
		notifier.NotifyChanged(credentials.UserName, Collection);
	}
	protected LocalContentProtector Protector => protector;

	public string FolderBackendKey => KeyPrefix + Collection;
	public abstract string EasClass { get; }

	// Local stores share the "local:" prefix, so the single-folder key is matched exactly.
	public bool OwnsBackendKey(string backendKey)
	{
		return backendKey == FolderBackendKey;
	}

	public Task<IReadOnlyList<BackendFolder>> ListFoldersAsync(CancellationToken ct)
	{
		return Task.FromResult<IReadOnlyList<BackendFolder>>(
			[new BackendFolder(FolderBackendKey, FolderDisplayName, null, FolderType, EasClass)]);
	}

	public async Task<IReadOnlyDictionary<string, string>> GetItemRevisionsAsync(
		string folderBackendKey, ContentFilter filter, CancellationToken ct)
	{
		await using SyncDbContext db = dbFactory.CreateDbContext();
		IQueryable<LocalItem> query = Rows(db);
		if (filter.SinceUtc is { } since)
			query = query.Where(i => i.ItemDateUtc == null || i.ItemDateUtc >= since);
		var rows = await query.Select(i => new { i.Id, i.Version }).ToListAsync(ct).ConfigureAwait(false);
		return rows.ToDictionary(r => r.Id.ToString(), r => r.Version.ToString(), StringComparer.Ordinal);
	}

	public async Task<BackendItem?> GetItemAsync(
		string folderBackendKey, string itemKey, BodyPreference bodyPreference, CancellationToken ct)
	{
		await using SyncDbContext db = dbFactory.CreateDbContext();
		LocalItem? row = await FindAsync(db, itemKey, ct).ConfigureAwait(false);
		if (row is null)
			return null;
		string content = protector.Unprotect(row.Content, credentials.UserName, Collection);
		IReadOnlyList<XElement>? elements = ToApplicationData(content, bodyPreference);
		return elements is null ? null : new BackendItem(elements);
	}

	public async Task<(string ItemKey, string Revision)> CreateItemAsync(
		string folderBackendKey, XElement applicationData, CancellationToken ct)
	{
		string uid = Guid.NewGuid().ToString();
		string content = BuildContent(applicationData, uid, null);
		await using SyncDbContext db = dbFactory.CreateDbContext();
		LocalItem row = new()
		{
			UserName = credentials.UserName,
			Collection = Collection,
			Uid = uid,
			Content = protector.Protect(content, credentials.UserName, Collection),
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
		notifier.NotifyChanged(credentials.UserName, Collection);
		return (row.Id.ToString(), row.Version.ToString());
	}

	public async Task<string> UpdateItemAsync(
		string folderBackendKey, string itemKey, XElement applicationData, CancellationToken ct)
	{
		// Retry on a concurrent write: another device may bump the same row between our read
		// and save. Each attempt re-reads the latest content, re-merges the client change
		// onto it and re-derives the version, so no update is lost and no revision is reused.
		const int maxAttempts = 4;
		for (int attempt = 1; ; attempt++)
		{
			await using SyncDbContext db = dbFactory.CreateDbContext();
			LocalItem row = await FindAsync(db, itemKey, ct).ConfigureAwait(false)
			                ?? throw new BackendItemNotFoundException(
				                $"Local {Collection} item {itemKey} no longer exists.");
			// Converters merge ApplicationData into the existing content, so it must be decrypted
			// first — and ExtractItemDate parses the new plaintext before it is sealed again.
			string existing = protector.Unprotect(row.Content, credentials.UserName, Collection);
			string content = BuildContent(applicationData, row.Uid, existing);
			row.Content = protector.Protect(content, credentials.UserName, Collection);
			row.Version++;
			row.ItemDateUtc = ExtractItemDate(content);
			row.LastModifiedUtc = DateTime.UtcNow;
			try
			{
				await db.SaveChangesAsync(ct).ConfigureAwait(false);
			}
			catch (DbUpdateConcurrencyException) when (attempt < maxAttempts)
			{
				continue;
			}

			notifier.NotifyChanged(credentials.UserName, Collection);
			return row.Version.ToString();
		}
	}

	public async Task DeleteItemAsync(string folderBackendKey, string itemKey, bool permanent, CancellationToken ct)
	{
		await using SyncDbContext db = dbFactory.CreateDbContext();
		LocalItem? row = await FindAsync(db, itemKey, ct).ConfigureAwait(false);
		if (row is null)
			return;
		db.LocalItems.Remove(row);
		await db.SaveChangesAsync(ct).ConfigureAwait(false);
		notifier.NotifyChanged(credentials.UserName, Collection);
	}

	// K58: local stores expose a single fixed folder and cannot move items — so they implement
	// neither IItemMoveOperations nor IFolderOperations rather than carrying throw-stubs. The host
	// answers MoveItems/Folder* with the unsupported status when the capability is absent.

	public async Task<IReadOnlyList<string>> WaitForChangesAsync(
		IReadOnlyList<string> folderBackendKeys, TimeSpan timeout, CancellationToken ct)
	{
		return await notifier.WaitAsync(credentials.UserName, Collection, timeout, ct).ConfigureAwait(false)
			? [FolderBackendKey]
			: [];
	}

	/// <summary>Converts stored content to EAS ApplicationData (null = unparsable/empty).</summary>
	protected abstract IReadOnlyList<XElement>? ToApplicationData(string content, BodyPreference bodyPreference);

	/// <summary>Builds the stored content from client ApplicationData.</summary>
	protected abstract string BuildContent(XElement applicationData, string uid, string? existingContent);

	/// <summary>Item date used by EAS filter windows (event start); null = always in range.</summary>
	protected virtual DateTime? ExtractItemDate(string content)
	{
		return null;
	}

	protected IQueryable<LocalItem> Rows(SyncDbContext db)
	{
		return db.LocalItems.Where(i => i.UserName == credentials.UserName && i.Collection == Collection);
	}

	protected async Task<LocalItem?> FindAsync(SyncDbContext db, string itemKey, CancellationToken ct)
	{
		if (!int.TryParse(itemKey, out int id))
			return null;
		return await Rows(db).FirstOrDefaultAsync(i => i.Id == id, ct).ConfigureAwait(false);
	}
}

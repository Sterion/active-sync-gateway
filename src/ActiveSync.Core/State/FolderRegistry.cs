using ActiveSync.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace ActiveSync.Core.State;

/// <summary>
///   The per-user folder registry (backend key → EAS ServerId) and the FolderSync hierarchy
///   diff against each device's acknowledged view. One of the collaborators composed by
///   <see cref="SyncStateService" />, sharing its request-scoped <see cref="SyncDbContext" />.
/// </summary>
internal sealed class FolderRegistry(SyncDbContext db)
{
	/// <summary>
	///   Reconciles the per-user folder registry with the folders currently reported by the
	///   backends and returns the live registry (excluding soft-deleted rows).
	/// </summary>
	public async Task<List<UserFolder>> RefreshFolderRegistryAsync(
		string userName, IReadOnlyList<BackendFolder> backendFolders, CancellationToken ct)
	{
		// The registry is per-user and shared across devices, so two devices' first sync can
		// race to insert the same (UserName, BackendKey). On a unique-constraint failure,
		// re-read (the concurrent inserts are now visible, so they reconcile as updates) and
		// retry the whole reconcile.
		const int maxAttempts = 4;
		for (int attempt = 1; ; attempt++)
		{
			List<UserFolder> existing = await db.UserFolders
				.Where(f => f.UserName == userName)
				.ToListAsync(ct).ConfigureAwait(false);
			Dictionary<string, UserFolder> byKey = existing.ToDictionary(f => f.BackendKey, StringComparer.Ordinal);
			HashSet<string> seen = new(StringComparer.Ordinal);

			foreach (BackendFolder bf in backendFolders)
			{
				seen.Add(bf.BackendKey);
				if (byKey.TryGetValue(bf.BackendKey, out UserFolder? row))
				{
					row.DisplayName = bf.DisplayName;
					row.ParentBackendKey = bf.ParentBackendKey;
					row.Type = bf.EasType;
					row.EasClass = bf.EasClass;
					row.Deleted = false;
				}
				else
				{
					// DbSet.Add false positive for VSTHRD103 — see GetOrCreateDeviceAsync.
#pragma warning disable VSTHRD103
					db.UserFolders.Add(new UserFolder
					{
						UserName = userName,
						BackendKey = bf.BackendKey,
						DisplayName = bf.DisplayName,
						ParentBackendKey = bf.ParentBackendKey,
						Type = bf.EasType,
						EasClass = bf.EasClass
					});
#pragma warning restore VSTHRD103
				}
			}

			foreach (UserFolder row in existing)
				if (!seen.Contains(row.BackendKey))
					row.Deleted = true;

			try
			{
				await db.SaveChangesAsync(ct).ConfigureAwait(false);
				break;
			}
			catch (DbUpdateException) when (attempt < maxAttempts)
			{
				foreach (EntityEntry entry in db.ChangeTracker.Entries().ToList())
					entry.State = EntityState.Detached; // discard and re-read on the next attempt
			}
		}

		return await db.UserFolders
			.Where(f => f.UserName == userName && !f.Deleted)
			.ToListAsync(ct).ConfigureAwait(false);
	}

	public Task<UserFolder?> GetFolderByServerIdAsync(string userName, string serverId, CancellationToken ct)
	{
		if (!int.TryParse(serverId, out int id))
			return Task.FromResult<UserFolder?>(null);
		return db.UserFolders
			.FirstOrDefaultAsync(f => f.Id == id && f.UserName == userName && !f.Deleted, ct);
	}

	public Task<List<UserFolder>> GetFoldersAsync(string userName, CancellationToken ct)
	{
		return db.UserFolders.Where(f => f.UserName == userName && !f.Deleted).ToListAsync(ct);
	}

	public async Task<FolderHierarchyDiff> ComputeFolderDiffAsync(
		Device device, IReadOnlyList<UserFolder> registry, CancellationToken ct, bool initial = false)
	{
		// A SyncKey-0 (re)start must emit the COMPLETE hierarchy: a re-provisioned client has
		// lost its local folder state even though our DeviceFolder acknowledgements survive,
		// so diffing against them would return Count=0 and leave the client with no folders.
		List<DeviceFolder> known = initial
			? []
			: await db.DeviceFolders
				.Where(f => f.DeviceKey == device.Id)
				.ToListAsync(ct).ConfigureAwait(false);
		Dictionary<string, DeviceFolder> knownById = known.ToDictionary(f => f.ServerId, StringComparer.Ordinal);
		Dictionary<string, UserFolder> registryByServerId = registry.ToDictionary(f => f.ServerId, StringComparer.Ordinal);
		Dictionary<string, UserFolder> byBackendKey = registry.ToDictionary(f => f.BackendKey, StringComparer.Ordinal);

		string ParentServerId(UserFolder f)
		{
			return f.ParentBackendKey is not null && byBackendKey.TryGetValue(f.ParentBackendKey, out UserFolder? parent)
				? parent.ServerId
				: "0";
		}

		List<FolderChange> adds = new();
		List<FolderChange> updates = new();
		foreach (UserFolder f in registry)
		{
			FolderChange change = new(f.ServerId, f.DisplayName, ParentServerId(f), f.Type);
			if (!knownById.TryGetValue(f.ServerId, out DeviceFolder? k))
				adds.Add(change);
			else if (k.DisplayName != f.DisplayName || (k.ParentServerId ?? "0") != change.ParentServerId)
				updates.Add(change);
		}

		List<string> deletes = known
			.Where(k => !registryByServerId.ContainsKey(k.ServerId))
			.Select(k => k.ServerId)
			.ToList();

		return new FolderHierarchyDiff(adds, updates, deletes);
	}

	/// <summary>Persists the hierarchy as acknowledged by the device and bumps the folder sync key.</summary>
	public async Task<int> CommitFolderHierarchyAsync(
		Device device, IReadOnlyList<UserFolder> registry, CancellationToken ct)
	{
		List<DeviceFolder> known = await db.DeviceFolders
			.Where(f => f.DeviceKey == device.Id)
			.ToListAsync(ct).ConfigureAwait(false);
		db.DeviceFolders.RemoveRange(known);

		Dictionary<string, UserFolder> byBackendKey = registry.ToDictionary(f => f.BackendKey, StringComparer.Ordinal);
		// DbSet.Add false positive for VSTHRD103 — see GetOrCreateDeviceAsync.
#pragma warning disable VSTHRD103
		foreach (UserFolder f in registry)
			db.DeviceFolders.Add(new DeviceFolder
			{
				DeviceKey = device.Id,
				ServerId = f.ServerId,
				DisplayName = f.DisplayName,
				ParentServerId = f.ParentBackendKey is not null &&
				                 byBackendKey.TryGetValue(f.ParentBackendKey, out UserFolder? parent)
					? parent.ServerId
					: "0",
				Type = f.Type
			});
#pragma warning restore VSTHRD103

		device.FolderSyncKey++;
		await db.SaveChangesAsync(ct).ConfigureAwait(false);
		return device.FolderSyncKey;
	}
}

using System.Text.Json;
using ActiveSync.Core.Backend;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace ActiveSync.Core.State;

/// <summary>Result of validating a client-supplied sync key.</summary>
public enum SyncKeyValidation
{
	/// <summary>Key matches the current server state.</summary>
	Current,

	/// <summary>Key matches the previous generation — the client is retrying; state was rolled back.</summary>
	Replay,

	/// <summary>Initial sync (key 0).</summary>
	Initial,

	/// <summary>Unknown/stale key: the client must re-sync from key 0 (EAS status 3).</summary>
	Invalid
}

public sealed record FolderChange(string ServerId, string DisplayName, string? ParentServerId, int Type);

/// <summary>
///   Outcome of one applied client Add, kept for one generation so a retried Sync (lost
///   response) reuses the created item instead of creating a duplicate. A null
///   <see cref="ItemKey" /> marks an Add that stored nothing (16.x draft submitted via
///   email2:Send) — replayed as a bare success without re-sending the mail.
/// </summary>
public sealed record AppliedClientAdd(string? ItemKey, string? Revision);

/// <summary>
///   Outcome of one applied client Change (or occurrence cancel), kept for one generation so
///   a retried Sync (lost response) acknowledges the edit instead of re-applying it — which
///   would re-send iMIP update mails to attendees. A null <see cref="Revision" /> marks a
///   Change that removed the item (16.x draft submitted and deleted via email2:Send).
/// </summary>
public sealed record AppliedClientChange(string? ItemKey, string? Revision);

public sealed record FolderHierarchyDiff(
	IReadOnlyList<FolderChange> Adds,
	IReadOnlyList<FolderChange> Updates,
	IReadOnlyList<string> Deletes);

/// <summary>
///   All persistent sync state: device partnerships, the per-user folder registry (backend key →
///   EAS ServerId), per-collection sync keys with one generation of replay history, and the
///   DAV href → short id map.
/// </summary>
public class SyncStateService(SyncDbContext db)
{
	private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

	/// <summary>
	///   True when an operator block refuses this login: a user-level block (DeviceId null)
	///   matches every device; a device-level block matches only that DeviceId.
	/// </summary>
	public Task<bool> IsLoginBlockedAsync(string userName, string? deviceId, CancellationToken ct)
		=> db.LoginBlocks
			.AnyAsync(b => b.UserName == userName && (b.DeviceId == null || b.DeviceId == deviceId), ct);

	public async Task<Device> GetOrCreateDeviceAsync(
		string userName, string deviceId, string deviceType, CancellationToken ct,
		string? protocolVersion = null)
	{
		Device? device = await db.Devices
			.FirstOrDefaultAsync(d => d.UserName == userName && d.DeviceId == deviceId, ct)
			.ConfigureAwait(false);
		if (device is null)
		{
			device = new Device
			{
				UserName = userName,
				DeviceId = deviceId,
				DeviceType = deviceType,
				CreatedUtc = DateTime.UtcNow
			};
			// DbSet.Add is synchronous and local (no I/O) — AddAsync exists only to support
			// async value generators (e.g. HiLo/Cosmos), which this project doesn't use.
#pragma warning disable VSTHRD103
			db.Devices.Add(device);
#pragma warning restore VSTHRD103
		}

		device.LastSeenUtc = DateTime.UtcNow;
		if (!string.IsNullOrEmpty(deviceType))
			device.DeviceType = deviceType;
		if (!string.IsNullOrEmpty(protocolVersion))
			device.LastProtocolVersion = protocolVersion;
		try
		{
			await db.SaveChangesAsync(ct).ConfigureAwait(false);
		}
		catch (DbUpdateException) when (db.Entry(device).State == EntityState.Added)
		{
			// A concurrent first request for the same (user, device) inserted the row first.
			// Re-read the winner and re-apply the touch as an update.
			db.Entry(device).State = EntityState.Detached;
			device = await db.Devices
				.FirstAsync(d => d.UserName == userName && d.DeviceId == deviceId, ct).ConfigureAwait(false);
			device.LastSeenUtc = DateTime.UtcNow;
			if (!string.IsNullOrEmpty(deviceType))
				device.DeviceType = deviceType;
			if (!string.IsNullOrEmpty(protocolVersion))
				device.LastProtocolVersion = protocolVersion;
			await db.SaveChangesAsync(ct).ConfigureAwait(false);
		}

		return device;
	}

	// ---------- Folder registry ----------

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
					// DbSet.Add false positive for VSTHRD103 — see GetOrCreateDeviceAsync above.
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

	// ---------- Folder hierarchy sync (FolderSync) ----------

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
		// DbSet.Add false positive for VSTHRD103 — see GetOrCreateDeviceAsync above.
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

	// ---------- Collection sync state ----------

	public async Task<(SyncKeyValidation Validation, CollectionState State)> ValidateSyncKeyAsync(
		Device device, string collectionId, string clientSyncKey, CancellationToken ct)
	{
		CollectionState? state = await db.CollectionStates
			.FirstOrDefaultAsync(c => c.DeviceKey == device.Id && c.CollectionId == collectionId, ct)
			.ConfigureAwait(false);

		if (clientSyncKey == "0")
		{
			if (state is null)
			{
				state = new CollectionState { DeviceKey = device.Id, CollectionId = collectionId };
				// DbSet.Add false positive for VSTHRD103 — see GetOrCreateDeviceAsync above.
#pragma warning disable VSTHRD103
				db.CollectionStates.Add(state);
#pragma warning restore VSTHRD103
			}

			state.SyncKey = 0;
			state.SnapshotJson = "{}";
			state.PreviousSnapshotJson = null;
			state.LastClientAddsJson = null;
			state.LastClientChangesJson = null;
			state.UpdatedUtc = DateTime.UtcNow;
			await db.SaveChangesAsync(ct).ConfigureAwait(false);
			return (SyncKeyValidation.Initial, state);
		}

		if (state is null || !int.TryParse(clientSyncKey, out int key))
			return (SyncKeyValidation.Invalid, state ?? new CollectionState
			{
				DeviceKey = device.Id,
				CollectionId = collectionId
			});

		if (key == state.SyncKey)
			return (SyncKeyValidation.Current, state);

		if (key == state.SyncKey - 1 && state.PreviousSnapshotJson is not null)
		{
			// Client never saw our last response: roll back one generation.
			// LastClientAddsJson/LastClientChangesJson are deliberately KEPT — they describe
			// the commands of the discarded generation, which are exactly the ones the client
			// is about to re-send.
			state.SyncKey = key;
			state.SnapshotJson = state.PreviousSnapshotJson;
			state.PreviousSnapshotJson = null;
			await db.SaveChangesAsync(ct).ConfigureAwait(false);
			return (SyncKeyValidation.Replay, state);
		}

		return (SyncKeyValidation.Invalid, state);
	}

	/// <summary>
	///   Read-only classification for GetItemEstimate: returns the verdict plus the snapshot
	///   the estimate should diff against, WITHOUT mutating or persisting collection state
	///   (unlike <see cref="ValidateSyncKeyAsync" />, which is the Sync write path). A query
	///   must never reset a client's SyncKey/snapshot.
	/// </summary>
	public async Task<(SyncKeyValidation Validation, Dictionary<string, string> Snapshot, int FilterType)>
		PeekSyncKeyAsync(Device device, string collectionId, string clientSyncKey, CancellationToken ct)
	{
		CollectionState? state = await db.CollectionStates.AsNoTracking()
			.FirstOrDefaultAsync(c => c.DeviceKey == device.Id && c.CollectionId == collectionId, ct)
			.ConfigureAwait(false);

		if (clientSyncKey == "0")
			return (SyncKeyValidation.Initial, [], state?.FilterType ?? 0);
		if (state is null || !int.TryParse(clientSyncKey, out int key))
			return (SyncKeyValidation.Invalid, [], state?.FilterType ?? 0);
		if (key == state.SyncKey)
			return (SyncKeyValidation.Current, ReadSnapshot(state), state.FilterType);
		if (key == state.SyncKey - 1 && state.PreviousSnapshotJson is not null)
			return (SyncKeyValidation.Replay,
				JsonSerializer.Deserialize<Dictionary<string, string>>(state.PreviousSnapshotJson, JsonOpts) ?? [],
				state.FilterType);
		return (SyncKeyValidation.Invalid, [], state.FilterType);
	}

	public static Dictionary<string, string> ReadSnapshot(CollectionState state)
	{
		return JsonSerializer.Deserialize<Dictionary<string, string>>(state.SnapshotJson, JsonOpts) ?? [];
	}

	/// <summary>The applied-Add map of the generation that produced the current SyncKey.</summary>
	public static Dictionary<string, AppliedClientAdd> ReadAppliedAdds(CollectionState state)
	{
		return state.LastClientAddsJson is null
			? []
			: JsonSerializer.Deserialize<Dictionary<string, AppliedClientAdd>>(state.LastClientAddsJson, JsonOpts) ?? [];
	}

	/// <summary>The applied-Change map of the generation that produced the current SyncKey.</summary>
	public static Dictionary<string, AppliedClientChange> ReadAppliedChanges(CollectionState state)
	{
		return state.LastClientChangesJson is null
			? []
			: JsonSerializer.Deserialize<Dictionary<string, AppliedClientChange>>(state.LastClientChangesJson, JsonOpts)
			  ?? [];
	}

	public async Task<int> CommitCollectionStateAsync(
		CollectionState state, Dictionary<string, string> newSnapshot, int filterType, CancellationToken ct,
		Dictionary<string, AppliedClientAdd>? appliedAdds = null,
		Dictionary<string, AppliedClientChange>? appliedChanges = null)
	{
		state.PreviousSnapshotJson = state.SnapshotJson;
		state.SnapshotJson = JsonSerializer.Serialize(newSnapshot, JsonOpts);
		state.LastClientAddsJson = appliedAdds is { Count: > 0 }
			? JsonSerializer.Serialize(appliedAdds, JsonOpts)
			: null;
		state.LastClientChangesJson = appliedChanges is { Count: > 0 }
			? JsonSerializer.Serialize(appliedChanges, JsonOpts)
			: null;
		state.SyncKey++;
		state.FilterType = filterType;
		state.UpdatedUtc = DateTime.UtcNow;
		try
		{
			await db.SaveChangesAsync(ct).ConfigureAwait(false);
		}
		catch (DbUpdateConcurrencyException ex)
		{
			// A concurrent Sync for the same (device, collection) already advanced the state.
			// This snapshot was diffed against a now-stale base, so it must not overwrite the
			// winner — fail the request and let the client retry; the SyncKey it holds is now
			// one behind, which ValidateSyncKeyAsync recovers via the Replay path.
			throw new BackendException("Concurrent sync for this collection — please retry.", ex);
		}

		return state.SyncKey;
	}

	public Task<CollectionState?> GetCollectionStateAsync(Device device, string collectionId, CancellationToken ct)
	{
		return db.CollectionStates
			.FirstOrDefaultAsync(c => c.DeviceKey == device.Id && c.CollectionId == collectionId, ct);
	}

	// ---------- DAV item id mapping ----------

	public async Task<string> GetOrAddDavItemIdAsync(UserFolder folder, string href, CancellationToken ct)
	{
		DavItem? item = await db.DavItems
			.FirstOrDefaultAsync(i => i.UserFolderKey == folder.Id && i.Href == href, ct)
			.ConfigureAwait(false);
		if (item is null)
		{
			item = new DavItem { UserFolderKey = folder.Id, Href = href };
			// DbSet.Add false positive for VSTHRD103 — see GetOrCreateDeviceAsync above.
#pragma warning disable VSTHRD103
			db.DavItems.Add(item);
#pragma warning restore VSTHRD103
			try
			{
				await db.SaveChangesAsync(ct).ConfigureAwait(false);
			}
			catch (DbUpdateException)
			{
				// A concurrent request mapped the same href first — re-read the winner.
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

	public async Task SaveDeviceInfoAsync(Device device, string deviceInfoJson, CancellationToken ct)
	{
		device.DeviceInfoJson = deviceInfoJson;
		await db.SaveChangesAsync(ct).ConfigureAwait(false);
	}

	/// <summary>Persists pending mutations on tracked entities (device cache fields, snapshots).</summary>
	public Task PersistAsync(CancellationToken ct)
	{
		return db.SaveChangesAsync(ct);
	}

	/// <summary>
	///   Stores the issued policy key and, on the acknowledging phase of the Provision
	///   handshake, the hash of the policy document the device just accepted. Phase 1
	///   passes null — a device mid-handshake has not acknowledged anything yet.
	/// </summary>
	public async Task SetPolicyKeyAsync(Device device, uint policyKey, string? policyDocHash, CancellationToken ct)
	{
		device.PolicyKey = policyKey;
		device.PolicyDocHash = policyDocHash;
		await db.SaveChangesAsync(ct).ConfigureAwait(false);
	}

	/// <summary>Stores the sealed recovery password escrowed via Settings→DevicePassword.</summary>
	public async Task SetRecoveryPasswordAsync(Device device, string? sealedPassword, CancellationToken ct)
	{
		device.RecoveryPasswordProtected = sealedPassword;
		await db.SaveChangesAsync(ct).ConfigureAwait(false);
	}

	/// <summary>
	///   Marks the account-only wipe acknowledged and blocks the partnership, so stolen
	///   credentials stay dead after the account is removed from the device.
	/// </summary>
	public async Task CompleteAccountWipeAsync(Device device, CancellationToken ct)
	{
		device.PendingAccountWipe = false;
		bool alreadyBlocked = await db.LoginBlocks
			.AnyAsync(b => b.UserName == device.UserName && b.DeviceId == device.DeviceId, ct)
			.ConfigureAwait(false);
		if (!alreadyBlocked)
			await db.LoginBlocks.AddAsync(new LoginBlock
			{
				UserName = device.UserName,
				DeviceId = device.DeviceId,
				CreatedUtc = DateTime.UtcNow
			}, ct).ConfigureAwait(false);
		await db.SaveChangesAsync(ct).ConfigureAwait(false);
	}

	/// <summary>The user's out-of-office row, or null when never set.</summary>
	public Task<OofSetting?> GetOofAsync(string userName, CancellationToken ct)
	{
		return db.OofSettings.FirstOrDefaultAsync(o => o.UserName == userName, ct);
	}

	/// <summary>Upserts the out-of-office row (tracked update or insert).</summary>
	public async Task<OofSetting> SaveOofAsync(
		string userName, int state, DateTime? startUtc, DateTime? endUtc,
		string message, string bodyType, string? previousActiveScript, CancellationToken ct)
	{
		OofSetting? row = await db.OofSettings.FirstOrDefaultAsync(o => o.UserName == userName, ct)
			.ConfigureAwait(false);
		if (row is null)
		{
			row = new OofSetting { UserName = userName };
			await db.OofSettings.AddAsync(row, ct).ConfigureAwait(false);
		}

		row.State = state;
		row.StartUtc = startUtc;
		row.EndUtc = endUtc;
		row.Message = message;
		row.BodyType = bodyType;
		row.PreviousActiveScript = previousActiveScript;
		row.UpdatedUtc = DateTime.UtcNow;
		await db.SaveChangesAsync(ct).ConfigureAwait(false);
		return row;
	}
}

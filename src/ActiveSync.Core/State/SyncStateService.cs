using ActiveSync.Contracts;
using Microsoft.EntityFrameworkCore;

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
///   All persistent sync state. Historically one 535-line class; the six responsibilities are now
///   separate single-purpose collaborators — <see cref="DeviceStore" /> (device partnerships,
///   login/session gates, policy/wipe), <see cref="FolderRegistry" /> (folder registry + hierarchy
///   diff), <see cref="CollectionStateStore" /> (sync keys + snapshots), <see cref="DavItemMap" />
///   (DAV href map), plus the out-of-office row and the generic save kept here. This type stays the
///   single scoped entry point (<c>EasContext.State</c>) and delegates to them; because they all
///   share the one request-scoped <see cref="SyncDbContext" />, the change tracker is unified and
///   <see cref="PersistAsync" /> flushes mutations made through any of them.
///   <para>
///     TRANSACTION POLICY (A10): each EAS command commits its collaborators' mutations through the
///     shared context, and each collection commits independently — no single transaction spans a
///     multi-collection Sync. That is deliberate and safe: the SyncKey design already makes
///     per-collection commits atomic units — a collection whose commit does not land keeps its old
///     key, and the client's next Sync (or the N−1 replay) reconciles it on its own. The one thing
///     that must NOT ride the request's context is a self-contained id allocation that commits
///     mid-round: <see cref="DavItemMap" /> therefore takes its own short-lived context (via the
///     injected <see cref="ISyncDbContextFactory" />) so it never flushes — or, on a re-read,
///     poisons — a half-mutated <see cref="CollectionState" />.
///   </para>
/// </summary>
public sealed class SyncStateService(SyncDbContext db, ISyncDbContextFactory? dbContextFactory = null)
{
	private readonly DeviceStore _devices = new(db);
	private readonly FolderRegistry _folders = new(db);
	private readonly CollectionStateStore _collections = new(db);
	private readonly DavItemMap _davItems = new(db, dbContextFactory);

	// ---------- Device partnerships, login/session gates, policy & wipe ----------

	public Task<bool> IsLoginBlockedAsync(string userName, string? deviceId, CancellationToken ct)
		=> _devices.IsLoginBlockedAsync(userName, deviceId, ct);

	public Task<DateTime?> GetSessionsValidAfterAsync(string userName, CancellationToken ct)
		=> _devices.GetSessionsValidAfterAsync(userName, ct);

	public Task RevokeSessionsBeforeAsync(string userName, DateTime whenUtc, CancellationToken ct)
		=> _devices.RevokeSessionsBeforeAsync(userName, whenUtc, ct);

	public Task<Device> GetOrCreateDeviceAsync(
		string userName, string deviceId, string deviceType, CancellationToken ct,
		string? protocolVersion = null)
		=> _devices.GetOrCreateDeviceAsync(userName, deviceId, deviceType, ct, protocolVersion);

	public Task SaveDeviceInfoAsync(Device device, string deviceInfoJson, CancellationToken ct)
		=> _devices.SaveDeviceInfoAsync(device, deviceInfoJson, ct);

	public Task SetPolicyKeyAsync(Device device, uint policyKey, string? policyDocHash, CancellationToken ct)
		=> _devices.SetPolicyKeyAsync(device, policyKey, policyDocHash, ct);

	public Task SetRecoveryPasswordAsync(Device device, string? sealedPassword, CancellationToken ct)
		=> _devices.SetRecoveryPasswordAsync(device, sealedPassword, ct);

	public Task CompleteAccountWipeAsync(Device device, CancellationToken ct)
		=> _devices.CompleteAccountWipeAsync(device, ct);

	// ---------- Folder registry & hierarchy sync ----------

	public Task<List<UserFolder>> RefreshFolderRegistryAsync(
		string userName, IReadOnlyList<BackendFolder> backendFolders, CancellationToken ct)
		=> _folders.RefreshFolderRegistryAsync(userName, backendFolders, ct);

	public Task<UserFolder?> GetFolderByServerIdAsync(string userName, string serverId, CancellationToken ct)
		=> _folders.GetFolderByServerIdAsync(userName, serverId, ct);

	public Task<List<UserFolder>> GetFoldersAsync(string userName, CancellationToken ct)
		=> _folders.GetFoldersAsync(userName, ct);

	public Task<FolderHierarchyDiff> ComputeFolderDiffAsync(
		Device device, IReadOnlyList<UserFolder> registry, CancellationToken ct, bool initial = false)
		=> _folders.ComputeFolderDiffAsync(device, registry, ct, initial);

	public Task<int> CommitFolderHierarchyAsync(
		Device device, IReadOnlyList<UserFolder> registry, CancellationToken ct)
		=> _folders.CommitFolderHierarchyAsync(device, registry, ct);

	// ---------- Collection sync state ----------

	public Task<(SyncKeyValidation Validation, CollectionState? State)> ValidateSyncKeyAsync(
		Device device, string collectionId, string clientSyncKey, CancellationToken ct)
		=> _collections.ValidateSyncKeyAsync(device, collectionId, clientSyncKey, ct);

	public Task<(SyncKeyValidation Validation, Dictionary<string, string> Snapshot, int FilterType)>
		PeekSyncKeyAsync(Device device, string collectionId, string clientSyncKey, CancellationToken ct)
		=> _collections.PeekSyncKeyAsync(device, collectionId, clientSyncKey, ct);

	public Task<int> CommitCollectionStateAsync(
		CollectionState state, Dictionary<string, string> newSnapshot, int filterType, CancellationToken ct,
		Dictionary<string, AppliedClientAdd>? appliedAdds = null,
		Dictionary<string, AppliedClientChange>? appliedChanges = null)
		=> _collections.CommitCollectionStateAsync(state, newSnapshot, filterType, ct, appliedAdds, appliedChanges);

	public Task<CollectionState?> GetCollectionStateAsync(Device device, string collectionId, CancellationToken ct)
		=> _collections.GetCollectionStateAsync(device, collectionId, ct);

	/// <summary>The item snapshot persisted on a <see cref="CollectionState" />.</summary>
	public static Dictionary<string, string> ReadSnapshot(CollectionState state)
		=> CollectionStateStore.ReadSnapshot(state);

	/// <summary>Writes an item snapshot onto a <see cref="CollectionState" /> (stored gzipped).</summary>
	public static void WriteSnapshot(CollectionState state, Dictionary<string, string> snapshot)
		=> CollectionStateStore.WriteSnapshot(state, snapshot);

	/// <summary>The one-generation-old snapshot (SyncKey-1) — empty when there is no replay generation.</summary>
	public static Dictionary<string, string> ReadPreviousSnapshot(CollectionState state)
		=> CollectionStateStore.ReadPreviousSnapshot(state);

	/// <summary>Writes the previous-generation snapshot onto a <see cref="CollectionState" /> (stored gzipped).</summary>
	public static void WritePreviousSnapshot(CollectionState state, Dictionary<string, string> snapshot)
		=> CollectionStateStore.WritePreviousSnapshot(state, snapshot);

	/// <summary>The applied-Add map of the generation that produced the current SyncKey.</summary>
	public static Dictionary<string, AppliedClientAdd> ReadAppliedAdds(CollectionState state)
		=> CollectionStateStore.ReadAppliedAdds(state);

	/// <summary>The applied-Change map of the generation that produced the current SyncKey.</summary>
	public static Dictionary<string, AppliedClientChange> ReadAppliedChanges(CollectionState state)
		=> CollectionStateStore.ReadAppliedChanges(state);

	// ---------- DAV item id mapping ----------

	public Task<string> GetOrAddDavItemIdAsync(UserFolder folder, string href, CancellationToken ct)
		=> _davItems.GetOrAddDavItemIdAsync(folder, href, ct);

	public Task<IReadOnlyDictionary<string, string>> GetOrAddDavItemIdsAsync(
		UserFolder folder, IReadOnlyCollection<string> hrefs, CancellationToken ct)
		=> _davItems.GetOrAddDavItemIdsAsync(folder, hrefs, ct);

	public Task<string?> ResolveDavHrefAsync(UserFolder folder, string shortId, CancellationToken ct)
		=> _davItems.ResolveDavHrefAsync(folder, shortId, ct);

	// ---------- Out-of-office + generic persist (cross-cutting, kept on the facade) ----------

	/// <summary>Persists pending mutations on tracked entities (device cache fields, snapshots).</summary>
	public Task PersistAsync(CancellationToken ct)
	{
		return db.SaveChangesAsync(ct);
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

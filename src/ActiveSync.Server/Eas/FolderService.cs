using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.State;
using ActiveSync.Protocol;

namespace ActiveSync.Server.Eas;

/// <summary>
///   Aggregates folders from every backend store and keeps the per-user folder registry current.
///   Also translates between EAS item ServerIds ("collectionId:sub") and backend item keys.
/// </summary>
public sealed class FolderService(SyncStateService state, ILogger<FolderService> logger)
{
	public async Task<List<UserFolder>> RefreshAsync(IBackendSession session, int userId, CancellationToken ct)
	{
		List<BackendFolder> all = new();
		// The stored registry is fetched lazily and AT MOST ONCE: when several DAV stores
		// are down at the same time — the common correlated case — re-reading the whole registry
		// inside each store's catch was N identical full-table queries during an already-degraded
		// request. The registry does not change while we iterate, so one read serves every fallback.
		List<UserFolder>? existing = null;
		foreach (IContentStore store in session.Stores)
			try
			{
				all.AddRange(await store.ListFoldersAsync(ct));
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				// A dead DAV server must not break mail sync: skip that store's folders
				// (existing registry rows survive because we merge, not replace-all).
				logger.LogWarning(ex, "Listing folders failed for store {Class}", store.EasClass);
				existing ??= await state.GetFoldersAsync(userId, ct);
				all.AddRange(existing
					.Where(f => f.EasClass == store.EasClass)
					.Select(f => new BackendFolder
					{
						BackendKey = f.BackendKey,
						DisplayName = f.DisplayName,
						ParentBackendKey = f.ParentBackendKey,
						// The registry column is the wire integer the store's FolderType was
						// stored as; this is the same value coming back the other way.
						Type = (FolderType)f.Type,
						EasClass = f.EasClass
					}));
			}

		return await state.RefreshFolderRegistryAsync(userId, all, ct);
	}

	public async Task<(UserFolder Folder, IContentStore Store)?> ResolveCollectionAsync(
		IBackendSession session, int userId, string collectionId, CancellationToken ct)
	{
		UserFolder? folder = await state.GetFolderByServerIdAsync(userId, collectionId, ct);
		if (folder is null)
			return null;
		IContentStore? store = session.GetStoreForBackendKey(folder.BackendKey);
		return store is null ? null : (folder, store);
	}

	/// <summary>
	///   Returns the user's whole folder registry indexed by backend key, for resolving a batch of
	///   hits that can span more than one folder (e.g. a mailbox-wide Find) back to their owning
	///   folder in ONE query rather than one lookup per hit.
	/// </summary>
	public async Task<IReadOnlyDictionary<string, UserFolder>> GetFolderMapAsync(int userId, CancellationToken ct)
	{
		List<UserFolder> registry = await state.GetFoldersAsync(userId, ct);
		return registry.ToDictionary(f => f.BackendKey, f => f, StringComparer.Ordinal);
	}

	/// <summary>
	///   Pre-resolves a whole window of DAV item keys to short ids in one query + one flush, so the
	///   render loop can compose ServerIds without a per-item round trip. Returns null for mail
	///   collections (their sub IS the UID — no map) and for an empty window.
	/// </summary>
	public async Task<IReadOnlyDictionary<string, string>?> PreResolveDavItemIdsAsync(
		UserFolder folder, IContentStore store, IReadOnlyCollection<string> itemKeys, CancellationToken ct)
	{
		if (store.EasClass == EasClass.Email || itemKeys.Count == 0)
			return null;
		return await state.GetOrAddDavItemIdsAsync(folder, itemKeys, ct);
	}

	/// <summary>Composes an item ServerId from a backend item key.</summary>
	/// <param name="davIdCache">
	///   Optional href → short-id map from <see cref="PreResolveDavItemIdsAsync" />; when it already
	///   holds the key the composition costs no database round trip.
	/// </param>
	public async Task<string> ComposeServerIdAsync(
		UserFolder folder, IContentStore store, string itemKey, CancellationToken ct,
		IReadOnlyDictionary<string, string>? davIdCache = null)
	{
		string sub = store.EasClass == EasClass.Email
			? itemKey
			: davIdCache is not null && davIdCache.TryGetValue(itemKey, out string? cached)
				? cached
				: await state.GetOrAddDavItemIdAsync(folder, itemKey, ct);
		return $"{folder.ServerId}:{sub}";
	}

	/// <summary>Resolves an item ServerId back to the backend item key.</summary>
	public async Task<string?> ResolveItemKeyAsync(
		UserFolder folder, IContentStore store, string serverId, CancellationToken ct)
	{
		int colon = serverId.IndexOf(':');
		// A ServerId prefix must match the collection it is being applied in — a mismatched
		// "{otherCollection}:{sub}" would otherwise operate on {sub} inside this folder.
		if (colon >= 0 && serverId[..colon] != folder.ServerId)
			return null;
		string sub = colon >= 0 ? serverId[(colon + 1)..] : serverId;
		if (store.EasClass == EasClass.Email)
			return sub;
		return await state.ResolveDavHrefAsync(folder, sub, ct);
	}
}

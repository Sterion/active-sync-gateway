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
	public async Task<List<UserFolder>> RefreshAsync(IBackendSession session, string userName, CancellationToken ct)
	{
		List<BackendFolder> all = new();
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
				List<UserFolder> existing = await state.GetFoldersAsync(userName, ct);
				all.AddRange(existing
					.Where(f => f.EasClass == store.EasClass)
					.Select(f => new BackendFolder(f.BackendKey, f.DisplayName, f.ParentBackendKey, f.Type, f.EasClass)));
			}

		return await state.RefreshFolderRegistryAsync(userName, all, ct);
	}

	public async Task<(UserFolder Folder, IContentStore Store)?> ResolveCollectionAsync(
		IBackendSession session, string userName, string collectionId, CancellationToken ct)
	{
		UserFolder? folder = await state.GetFolderByServerIdAsync(userName, collectionId, ct);
		if (folder is null)
			return null;
		IContentStore? store = session.GetStoreForBackendKey(folder.BackendKey);
		return store is null ? null : (folder, store);
	}

	/// <summary>
	///   Pre-resolves a whole window of DAV item keys to short ids in one query + one flush, so the
	///   render loop can compose ServerIds without a per-item round trip (A3). Returns null for mail
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
	///   holds the key the composition costs no database round trip (A3).
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

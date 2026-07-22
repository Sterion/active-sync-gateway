using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using ActiveSync.Core.State;
using ActiveSync.Protocol.Wbxml;
using Microsoft.Extensions.Options;

namespace ActiveSync.Server.Eas.Handlers;

/// <summary>MoveItems (MS-ASCMD 2.2.1.12).</summary>
public sealed class MoveItemsHandler(
	FolderService folders,
	IOptionsSnapshot<ActiveSyncOptions> options,
	ILogger<MoveItemsHandler> logger) : IEasCommandHandler
{
	private static readonly XNamespace M = EasNamespaces.Move;

	public string Command => "MoveItems";

	public async Task HandleAsync(EasContext context, CancellationToken ct)
	{
		XDocument? request = await context.ReadRequestAsync();
		List<XElement> moves = request?.Root?.Elements(M + "Move").ToList() ?? [];
		List<XElement> responses = new();

		// F22: accumulate snapshot edits per collection and apply them ONCE at the end — a single
		// state load + one PersistAsync per collection, instead of a query + commit per move (a
		// 50-item move was 100 queries and 100 commits). Each edit is also applied to the previous
		// (replay) generation, so an N-1 replay cannot restore the pre-move snapshot and echo the
		// move back to the client that made it.
		Dictionary<string, List<SnapshotEdit>> pendingEdits = new(StringComparer.Ordinal);

		void QueueEdit(string collectionId, string itemKey, bool remove, string? revision)
		{
			if (!pendingEdits.TryGetValue(collectionId, out List<SnapshotEdit>? edits))
				pendingEdits[collectionId] = edits = new List<SnapshotEdit>();
			edits.Add(new SnapshotEdit(itemKey, remove, revision));
		}

		foreach (XElement move in moves)
		{
			string srcMsgId = move.Element(M + "SrcMsgId")?.Value ?? "";
			string srcFldId = move.Element(M + "SrcFldId")?.Value ?? "";
			string dstFldId = move.Element(M + "DstFldId")?.Value ?? "";

			XElement Response(string status, string? dstMsgId = null)
			{
				XElement element = new(M + "Response",
					new XElement(M + "SrcMsgId", srcMsgId),
					new XElement(M + "Status", status));
				if (dstMsgId is not null)
					element.Add(new XElement(M + "DstMsgId", dstMsgId));
				return element;
			}

			try
			{
				(UserFolder Folder, IContentStore Store)? source = await folders.ResolveCollectionAsync(
					context.Session, context.Device.UserName, srcFldId, ct);
				(UserFolder Folder, IContentStore Store)? destination = await folders.ResolveCollectionAsync(
					context.Session, context.Device.UserName, dstFldId, ct);
				if (source is null)
				{
					responses.Add(Response("1"));
					continue;
				}

				if (destination is null || destination.Value.Store != source.Value.Store)
				{
					responses.Add(Response("2"));
					continue;
				}

				if (srcFldId == dstFldId)
				{
					responses.Add(Response("4"));
					continue;
				}

				string? itemKey = await folders.ResolveItemKeyAsync(
					source.Value.Folder, source.Value.Store, srcMsgId, ct);
				if (itemKey is null)
				{
					responses.Add(Response("1"));
					continue;
				}

				// A move touches BOTH folders — a read-only grant on either end blocks it.
				if (WritePermission.IsBlocked(context, options.Value, source.Value.Folder) ||
				    WritePermission.IsBlocked(context, options.Value, destination.Value.Folder))
				{
					// Report failure and forget the item in the source snapshot so it is
					// re-pushed — the client converges even if it already moved it locally.
					logger.LogInformation(
						"Read-only: rejecting move of {SrcMsgId} from \"{Source}\" to \"{Destination}\" for {User}",
						srcMsgId, source.Value.Folder.DisplayName,
						destination.Value.Folder.DisplayName, context.Device.UserName);
					QueueEdit(srcFldId, itemKey, true, null);
					responses.Add(Response("5"));
					continue;
				}

				// K58: item move is an optional capability. A store without it (local, DAV) reports
				// Status 5 (move failed) — the same answer its "not supported" throw used to produce.
				if (source.Value.Store is not IItemMoveOperations mover)
				{
					responses.Add(Response("5"));
					continue;
				}

				string newItemKey = await mover.MoveItemAsync(
					source.Value.Folder.BackendKey, itemKey, destination.Value.Folder.BackendKey, ct);
				string dstMsgId = await folders.ComposeServerIdAsync(
					destination.Value.Folder, destination.Value.Store, newItemKey, ct);

				// Patch snapshots so the move is not echoed back on the next Sync.
				QueueEdit(srcFldId, itemKey, true, null);
				QueueEdit(dstFldId, newItemKey, false, "moved");

				logger.LogInformation("Moved {SrcMsgId} from \"{Source}\" to \"{Destination}\" for {User}",
					srcMsgId, source.Value.Folder.DisplayName,
					destination.Value.Folder.DisplayName, context.Device.UserName);
				responses.Add(Response("3", dstMsgId));
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				logger.LogWarning(ex, "MoveItems failed for {SrcMsgId}", srcMsgId);
				responses.Add(Response("5"));
			}
		}

		// Apply every collection's accumulated edits with a single load each, then one flush total.
		bool anyPatched = false;
		foreach ((string collectionId, List<SnapshotEdit> edits) in pendingEdits)
			anyPatched |= await ApplySnapshotEditsAsync(context, collectionId, edits, ct);
		if (anyPatched)
			await context.State.PersistAsync(ct);

		await context.WriteResponseAsync(new XDocument(new XElement(M + "MoveItems", responses)));
	}

	private static async Task<bool> ApplySnapshotEditsAsync(
		EasContext context, string collectionId, IReadOnlyList<SnapshotEdit> edits, CancellationToken ct)
	{
		CollectionState? state = await context.State.GetCollectionStateAsync(context.Device, collectionId, ct);
		if (state is null || state.SyncKey == 0)
			return false;

		Dictionary<string, string> snapshot = SyncStateService.ReadSnapshot(state);
		Apply(snapshot, edits);
		SyncStateService.WriteSnapshot(state, snapshot);

		// The previous (replay) generation must carry the same edits, or a client resending the
		// N-1 key would replay against a snapshot that still holds the moved item and re-Add it.
		if (state.PreviousSnapshotCompressed is not null)
		{
			Dictionary<string, string> previous = SyncStateService.ReadPreviousSnapshot(state);
			Apply(previous, edits);
			SyncStateService.WritePreviousSnapshot(state, previous);
		}

		return true;
	}

	private static void Apply(Dictionary<string, string> snapshot, IReadOnlyList<SnapshotEdit> edits)
	{
		foreach (SnapshotEdit edit in edits)
			if (edit.Remove)
				snapshot.Remove(edit.ItemKey);
			else
				snapshot[edit.ItemKey] = edit.Revision ?? "";
	}

	/// <summary>One pending edit to a collection's snapshot: remove a key, or set it to a revision.</summary>
	private readonly record struct SnapshotEdit(string ItemKey, bool Remove, string? Revision);
}

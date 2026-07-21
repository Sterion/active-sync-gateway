using System.Text.Json;
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

				if (options.Value.ReadOnly)
				{
					// Report failure and forget the item in the source snapshot so it is
					// re-pushed — the client converges even if it already moved it locally.
					logger.LogInformation(
						"Read-only: rejecting move of {SrcMsgId} from \"{Source}\" to \"{Destination}\" for {User}",
						srcMsgId, source.Value.Folder.DisplayName,
						destination.Value.Folder.DisplayName, context.Device.UserName);
					await PatchSnapshotAsync(context, srcFldId, itemKey, true, null, ct);
					responses.Add(Response("5"));
					continue;
				}

				string newItemKey = await source.Value.Store.MoveItemAsync(
					source.Value.Folder.BackendKey, itemKey, destination.Value.Folder.BackendKey, ct);
				string dstMsgId = await folders.ComposeServerIdAsync(
					destination.Value.Folder, destination.Value.Store, newItemKey, ct);

				// Patch snapshots so the move is not echoed back on the next Sync.
				await PatchSnapshotAsync(context, srcFldId, itemKey, true, null, ct);
				await PatchSnapshotAsync(context, dstFldId, newItemKey, false, "moved", ct);

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

		await context.WriteResponseAsync(new XDocument(new XElement(M + "MoveItems", responses)));
	}

	private static async Task PatchSnapshotAsync(
		EasContext context, string collectionId, string itemKey, bool remove, string? revision, CancellationToken ct)
	{
		CollectionState? state = await context.State.GetCollectionStateAsync(context.Device, collectionId, ct);
		if (state is null || state.SyncKey == 0)
			return;
		Dictionary<string, string> snapshot = SyncStateService.ReadSnapshot(state);
		if (remove)
			snapshot.Remove(itemKey);
		else
			snapshot[itemKey] = revision ?? "";
		state.SnapshotJson = JsonSerializer.Serialize(snapshot);
		await context.State.PersistAsync(ct);
	}
}

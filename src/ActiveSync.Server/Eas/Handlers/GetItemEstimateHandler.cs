using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.State;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Sync;
using ActiveSync.Protocol.Wbxml;
using ActiveSync.Server.Eas.Content;

namespace ActiveSync.Server.Eas.Handlers;

/// <summary>GetItemEstimate (MS-ASCMD 2.2.1.9).</summary>
public sealed class GetItemEstimateHandler(
	FolderService folders,
	ILogger<GetItemEstimateHandler> logger) : IEasCommandHandler
{
	private static readonly XNamespace GIE = EasNamespaces.GetItemEstimate;
	private static readonly XNamespace AS = EasNamespaces.AirSync;

	public string Command => "GetItemEstimate";

	public async Task HandleAsync(EasContext context, CancellationToken ct)
	{
		XDocument? request = await context.ReadRequestAsync();
		List<XElement> collections =
			request?.Root?.Element(GIE + "Collections")?.Elements(GIE + "Collection").ToList() ?? [];
		List<XElement> responses = new();

		foreach (XElement collection in collections)
		{
			// 12.1 uses GIE-namespace CollectionId; 14.x uses airsync:CollectionId/SyncKey inside GIE
			string collectionId = collection.Element(GIE + "CollectionId")?.Value
			                      ?? collection.Element(AS + "CollectionId")?.Value ?? "";
			string syncKey = collection.Element(AS + "SyncKey")?.Value
			                 ?? collection.Element(GIE + "SyncKey")?.Value ?? "0";
			string? filterType = collection.Descendants(AS + "FilterType").FirstOrDefault()?.Value;

			XElement Response(string status, int? estimate, ContentAdapter? resolvedStore = null)
			{
				XElement collectionElement = new(GIE + "Collection",
					new XElement(GIE + "CollectionId", collectionId),
					estimate is null ? null : new XElement(GIE + "Estimate", estimate.ToString()));
				// A 12.1 client identifies a collection by Class + CollectionId (mirroring the
				// deliberate EchoClassIfLegacy handling in SyncHandler.Collection.cs) — only once the
				// store is known, since Class names the collection's EAS class.
				if (context.Version <= EasVersion.V121 && resolvedStore is not null)
					collectionElement.AddFirst(new XElement(GIE + "Class", resolvedStore.EasClass));
				return new XElement(GIE + "Response",
					new XElement(GIE + "Status", status),
					collectionElement);
			}

			(UserFolder Folder, ContentAdapter Store)? resolved = await folders.ResolveCollectionAsync(
				context.Session, context.UserId, collectionId, ct);
			if (resolved is null)
			{
				responses.Add(Response("2", null));
				continue;
			}

			(UserFolder folder, ContentAdapter store) = resolved.Value;
			// GetItemEstimate is a query — peek at the sync key without mutating state
			// (ValidateSyncKeyAsync, used by Sync, would reset the snapshot on key 0).
			(SyncKeyValidation validation, Dictionary<string, SnapshotEntry> snapshot, int stateFilterType) =
				await context.State.PeekSyncKeyAsync(context.Device, collectionId, syncKey, ct);
			// MS-ASCMD's GetItemEstimate Status element is its own table, distinct from Sync's —
			// 3 is SYNCSTATENOTPRIMED (the collection has never completed a Sync round) and 4 is
			// INVALIDSYNCKEY (a stale/mismatched/unparseable key). Initial (key 0) falls through to
			// the estimate below only in valid states (Current/Replay); it must not estimate against
			// an empty baseline, which would report every backend item as "new".
			if (validation == SyncKeyValidation.Initial)
			{
				responses.Add(Response("3", null));
				continue;
			}
			if (validation == SyncKeyValidation.Invalid)
			{
				responses.Add(Response("4", null));
				continue;
			}

			int ft = int.TryParse(filterType, out int f) ? f : stateFilterType;
			ContentFilter filter = ContentFilters.ForClass(store.EasClass, ft);

			IReadOnlyDictionary<ItemKey, ItemRevision> current;
			try
			{
				current = await store.Store.GetItemRevisionsAsync(new FolderKey(folder.BackendKey), filter, ct);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				// A flaky store must not 500 the whole multi-collection request; report status 2
				// for this collection and let the survivors through (as SyncHandler does).
				logger.LogError(ex, "GetItemEstimate revision listing failed for {CollectionId}", collectionId);
				responses.Add(Response("2", null, store));
				continue;
			}

			// Through CollectionSnapshot so an item still owing a read-only revert is counted as the
			// Change the next Sync round will send, exactly as the diff there will see it.
			(CollectionChanges diff, _) = CollectionSnapshot.Diff(snapshot, current, int.MaxValue);
			responses.Add(Response("1", diff.Adds.Count + diff.Changes.Count + diff.Deletes.Count, store));
		}

		await context.WriteResponseAsync(new XDocument(
			new XElement(GIE + "GetItemEstimate", responses)));
	}
}

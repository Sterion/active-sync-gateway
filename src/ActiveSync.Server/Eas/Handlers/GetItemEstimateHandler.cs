using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.State;
using ActiveSync.Core.Sync;
using ActiveSync.Protocol.Wbxml;

namespace ActiveSync.Server.Eas.Handlers;

/// <summary>GetItemEstimate (MS-ASCMD 2.2.1.9).</summary>
public sealed class GetItemEstimateHandler(FolderService folders) : IEasCommandHandler
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

			XElement Response(string status, int? estimate)
			{
				return new XElement(GIE + "Response",
					new XElement(GIE + "Status", status),
					new XElement(GIE + "Collection",
						new XElement(GIE + "CollectionId", collectionId),
						estimate is null ? null : new XElement(GIE + "Estimate", estimate.ToString())));
			}

			(UserFolder Folder, IContentStore Store)? resolved = await folders.ResolveCollectionAsync(
				context.Session, context.Device.UserName, collectionId, ct);
			if (resolved is null)
			{
				responses.Add(Response("2", null));
				continue;
			}

			(UserFolder folder, IContentStore store) = resolved.Value;
			// GetItemEstimate is a query — peek at the sync key without mutating state
			// (ValidateSyncKeyAsync, used by Sync, would reset the snapshot on key 0).
			(SyncKeyValidation validation, Dictionary<string, string> snapshot, int stateFilterType) =
				await context.State.PeekSyncKeyAsync(context.Device, collectionId, syncKey, ct);
			if (validation == SyncKeyValidation.Invalid)
			{
				responses.Add(Response("4", null));
				continue;
			}

			int ft = int.TryParse(filterType, out int f) ? f : stateFilterType;
			ContentFilter filter = ContentFilter.ForClass(store.EasClass, ft);

			IReadOnlyDictionary<string, string> current = await store.GetItemRevisionsAsync(folder.BackendKey, filter, ct);
			CollectionChanges diff = CollectionDiff.Compute(snapshot, current, int.MaxValue);
			responses.Add(Response("1", diff.Adds.Count + diff.Changes.Count + diff.Deletes.Count));
		}

		await context.WriteResponseAsync(new XDocument(
			new XElement(GIE + "GetItemEstimate", responses)));
	}
}

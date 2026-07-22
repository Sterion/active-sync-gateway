using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Core.State;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Sync;

namespace ActiveSync.Server.Eas.Handlers;

// Per-collection processing: validate the sync key, apply the client's commands, diff the
// backend against the stored snapshot, and emit the server→client commands.
public sealed partial class SyncHandler
{
	private async Task<CollectionResult> ProcessCollectionAsync(
		EasContext context, XElement collectionElement, int globalWindow, CancellationToken ct)
	{
		string collectionId = collectionElement.Element(AS + "CollectionId")?.Value ?? "";
		string clientSyncKey = collectionElement.Element(AS + "SyncKey")?.Value ?? "0";

		XElement Error(string status)
		{
			// F4: Status 3 (invalid sync key) must reset the client to an initial sync — echoing
			// the rejected key back makes a trusting client resend it, the resync loop this
			// codebase avoids. Transient/hierarchy errors ("5"/"12") keep the client's key.
			string echoKey = status == "3" ? "0" : clientSyncKey;
			return new XElement(AS + "Collection",
				new XElement(AS + "SyncKey", echoKey),
				new XElement(AS + "CollectionId", collectionId),
				new XElement(AS + "Status", status));
		}

		(UserFolder Folder, IContentStore Store)? resolved = await folders.ResolveCollectionAsync(
			context.Session, context.Device.UserName, collectionId, ct);
		if (resolved is null)
			return new CollectionResult(Error("12"), true, null); // folder hierarchy out of date

		(UserFolder folder, IContentStore store) = resolved.Value;
		(SyncKeyValidation validation, CollectionState? state) = await context.State.ValidateSyncKeyAsync(
			context.Device, collectionId, clientSyncKey, ct);
		if (validation == SyncKeyValidation.Invalid || state is null)
			return new CollectionResult(Error("3"), true, null);

		// Persisted or supplied options
		SyncCollectionOptions collectionOptions = SyncCollectionOptions.Resolve(
			collectionElement.Element(AS + "Options"), state);

		if (validation == SyncKeyValidation.Initial)
		{
			state.OptionsJson = collectionOptions.ToJson();
			int initialKey = await context.State.CommitCollectionStateAsync(
				state, [], collectionOptions.FilterType, ct);
			return new CollectionResult(new XElement(AS + "Collection",
				new XElement(AS + "SyncKey", initialKey.ToString()),
				new XElement(AS + "CollectionId", collectionId),
				new XElement(AS + "Status", "1")), true, null);
		}

		BodyPreference bodyPreference = new(
			collectionOptions.BodyType, collectionOptions.TruncationSize, false,
			context.Version >= EasVersion.V160);
		ContentFilter filter = ContentFilter.ForClass(store.EasClass, collectionOptions.FilterType);
		int windowSize = int.TryParse(collectionElement.Element(AS + "WindowSize")?.Value, out int cw)
			? cw
			: globalWindow;
		windowSize = Math.Clamp(windowSize, 1, options.Value.Eas.MaxWindowSize);
		bool getChanges = collectionElement.Element(AS + "GetChanges")?.Value != "0";

		// MS-ASCMD: DeletesAsMoves defaults to true (move to Trash) when absent; only an
		// explicit "0" requests a permanent delete.
		bool deletesAsMoves = collectionElement.Element(AS + "DeletesAsMoves")?.Value != "0";

		Dictionary<string, string> snapshot = SyncStateService.ReadSnapshot(state);
		List<XElement> clientResponses = new();
		bool snapshotDirty = false;

		// ---- client → server commands ----
		// On a replayed key the client never saw our previous response and re-sends the same
		// commands; the ledger of already-applied Adds/Changes lets us reuse the first attempt's
		// outcome instead of re-executing it (MS-ASCMD retry semantics) — no duplicate items,
		// no re-sent iMIP mails.
		ClientCommandLedger ledger = validation == SyncKeyValidation.Replay
			? ClientCommandLedger.ForReplay(state)
			: ClientCommandLedger.Empty();
		int clientAdds = 0, clientChanges = 0, clientDeletes = 0;
		XElement? commands = collectionElement.Element(AS + "Commands");

		// F7: to detect a concurrent edit (client Change vs a backend that moved on) we need the
		// backend's CURRENT revision of the changed items. Fetch the folder's revision map once,
		// only when the client actually sent Change commands, and only for conflict comparison —
		// NOT for the diff, which must fetch AFTER the client commands land so echo suppression
		// works. A fetch failure degrades to "no conflict detection" (the historical overwrite).
		IReadOnlyDictionary<string, string>? conflictRevisions = null;
		bool hasChangeCommands = commands?.Elements(AS + "Change").Any() == true;
		if (hasChangeCommands && collectionOptions.ServerWinsOnConflict)
			try
			{
				conflictRevisions = await store.GetItemRevisionsAsync(folder.BackendKey, filter, ct);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				logger.LogWarning(ex,
					"Conflict pre-check revision listing failed for {CollectionId}; applying client " +
					"changes without conflict detection", collectionId);
			}

		if (commands is not null)
			foreach (XElement command in commands.Elements())
			{
				switch (command.Name.LocalName)
				{
					case "Add": clientAdds++; break;
					case "Change": clientChanges++; break;
					case "Delete": clientDeletes++; break;
				}

				try
				{
					XElement? handled = await ApplyClientCommandAsync(
						context, folder, store, command, snapshot, bodyPreference, deletesAsMoves, ledger, ct,
						conflictRevisions, collectionOptions.ServerWinsOnConflict);
					if (handled is not null)
						clientResponses.Add(handled);
					snapshotDirty = true;
					Core.Observability.GatewayMetrics.RecordSyncItems(
						context.Device.UserName, store.EasClass, "client_to_server",
						command.Name.LocalName.ToLowerInvariant(), 1);
				}
				catch (BackendItemNotFoundException)
				{
					clientResponses.Add(ClientCommandStatus(command, "8"));
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					logger.LogWarning(ex, "Client {Command} failed in collection {CollectionId}",
						command.Name.LocalName, collectionId);
					clientResponses.Add(ClientCommandStatus(command, "6"));
				}
			}

		// ---- server → client changes ----
		List<XElement> serverCommands = new();
		bool moreAvailable = false;
		Dictionary<string, string> newSnapshot = snapshot;

		if (getChanges)
		{
			IReadOnlyDictionary<string, string> current;
			try
			{
				current = await store.GetItemRevisionsAsync(folder.BackendKey, filter, ct);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				logger.LogError(ex, "Revision listing failed for {CollectionId}", collectionId);
				return new CollectionResult(Error("5"), true, null);
			}

			CollectionChanges diff = CollectionDiff.Compute(snapshot, current, windowSize);
			moreAvailable = diff.MoreAvailable;
			newSnapshot = diff.NewSnapshot;
			Core.Observability.GatewayMetrics.RecordSyncItems(
				context.Device.UserName, store.EasClass, "server_to_client", "add", diff.Adds.Count);
			Core.Observability.GatewayMetrics.RecordSyncItems(
				context.Device.UserName, store.EasClass, "server_to_client", "change", diff.Changes.Count);

			// MS-ASCMD: an item that merely slid out of the FilterType window is still on the
			// server and must be reported as SoftDelete; Delete means "gone for good". The two
			// are indistinguishable from the filtered revision map alone, so ask the store once
			// for the unfiltered map — and only when a *filtered* collection actually produced
			// deletes, so unfiltered classes (contacts/tasks/notes, FilterType 0) pay nothing.
			HashSet<string> agedOut = new(StringComparer.Ordinal);
			if (diff.Deletes.Count > 0 && filter.SinceUtc is not null)
				try
				{
					IReadOnlyDictionary<string, string> unfiltered =
						await store.GetItemRevisionsAsync(folder.BackendKey, ContentFilter.All, ct);
					foreach (string deletedKey in diff.Deletes)
						if (unfiltered.ContainsKey(deletedKey))
							agedOut.Add(deletedKey);
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					// Fall back to a hard Delete: reporting a real deletion as SoftDelete would
					// strand the item on the device forever, which is the worse of the two.
					logger.LogWarning(ex,
						"Unfiltered revision listing failed for {CollectionId}; reporting {Count} " +
						"window departures as hard deletes", collectionId, diff.Deletes.Count);
				}

			Core.Observability.GatewayMetrics.RecordSyncItems(
				context.Device.UserName, store.EasClass, "server_to_client", "delete",
				diff.Deletes.Count - agedOut.Count);
			Core.Observability.GatewayMetrics.RecordSyncItems(
				context.Device.UserName, store.EasClass, "server_to_client", "soft_delete", agedOut.Count);

			// Pre-resolve the whole window's DAV item ids in one query + one flush; without this
			// every Add/Change/Delete composition below did its own SELECT + SaveChanges (A3).
			List<string> windowKeys = new(diff.Adds.Count + diff.Changes.Count + diff.Deletes.Count);
			windowKeys.AddRange(diff.Adds.Select(a => a.ServerId));
			windowKeys.AddRange(diff.Changes.Select(c => c.ServerId));
			windowKeys.AddRange(diff.Deletes);
			IReadOnlyDictionary<string, string>? davIds =
				await folders.PreResolveDavItemIdsAsync(folder, store, windowKeys, ct);

			foreach (ItemChange add in diff.Adds)
			{
				XElement? element = await BuildItemElementAsync(
					AS + "Add", context, folder, store, add.ServerId, bodyPreference, ct, davIds);
				if (element is not null)
					serverCommands.Add(element);
				else
					newSnapshot.Remove(add.ServerId); // vanished mid-sync; retry next round
			}

			foreach (ItemChange change in diff.Changes)
			{
				XElement? element = await BuildItemElementAsync(
					AS + "Change", context, folder, store, change.ServerId, bodyPreference, ct, davIds);
				if (element is not null)
					serverCommands.Add(element);
			}

			foreach (string deletedKey in diff.Deletes)
			{
				string serverId = await folders.ComposeServerIdAsync(folder, store, deletedKey, ct, davIds);
				serverCommands.Add(new XElement(AS + (agedOut.Contains(deletedKey) ? "SoftDelete" : "Delete"),
					new XElement(AS + "ServerId", serverId)));
			}
		}

		// One activity line per collection round; idle polls (all counts zero) stay silent.
		int sentAdds = serverCommands.Count(c => c.Name.LocalName == "Add");
		int sentChanges = serverCommands.Count(c => c.Name.LocalName == "Change");
		int sentDeletes = serverCommands.Count(c => c.Name.LocalName is "Delete" or "SoftDelete");
		if (clientAdds + clientChanges + clientDeletes + sentAdds + sentChanges + sentDeletes > 0)
			logger.LogInformation(
				"Sync \"{Folder}\" for {User}: client {ClientAdds} add/{ClientChanges} change/{ClientDeletes} delete, " +
				"sent {SentAdds} add/{SentChanges} change/{SentDeletes} delete",
				folder.DisplayName, context.Device.UserName,
				clientAdds, clientChanges, clientDeletes, sentAdds, sentChanges, sentDeletes);

		bool hasPayload = clientResponses.Count > 0 || serverCommands.Count > 0;
		if (!hasPayload && !snapshotDirty)
			// Nothing to say for this collection; it is a candidate for the long-poll wait.
			return new CollectionResult(null, false, new WaitableCollection(collectionElement, folder, store));

		state.OptionsJson = collectionOptions.ToJson();
		int newKey = await context.State.CommitCollectionStateAsync(
			state, newSnapshot, collectionOptions.FilterType, ct, ledger.AppliedAdds, ledger.AppliedChanges);

		XElement response = new(AS + "Collection",
			new XElement(AS + "SyncKey", newKey.ToString()),
			new XElement(AS + "CollectionId", collectionId),
			new XElement(AS + "Status", "1"));
		if (clientResponses.Count > 0)
			response.Add(new XElement(AS + "Responses", clientResponses));
		if (serverCommands.Count > 0)
			response.Add(new XElement(AS + "Commands", serverCommands));
		if (moreAvailable)
			response.Add(new XElement(AS + "MoreAvailable"));
		return new CollectionResult(response, true, null);
	}
}

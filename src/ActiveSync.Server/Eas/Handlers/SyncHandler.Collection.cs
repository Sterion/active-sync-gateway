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
	/// <summary>
	///   EAS 12.1 identifies a collection by Class + CollectionId, and the response is expected
	///   to echo the request's Class as the first child of the Collection. 14.0+ dropped it — the
	///   CollectionId alone identifies the collection — so it is emitted only for &lt;= 12.1 to keep
	///   the 14.1 wire form byte-identical.
	/// </summary>
	private static void EchoClassIfLegacy(XElement collection, EasContext context, IContentStore store)
	{
		if (context.Version <= EasVersion.V121)
			collection.AddFirst(new XElement(AS + "Class", store.EasClass));
	}

	private async Task<CollectionResult> ProcessCollectionAsync(
		EasContext context, XElement collectionElement, int globalWindow, CancellationToken ct)
	{
		string collectionId = collectionElement.Element(AS + "CollectionId")?.Value ?? "";
		string clientSyncKey = collectionElement.Element(AS + "SyncKey")?.Value ?? "0";

		XElement Error(string status)
		{
			// Status 3 (invalid sync key) must reset the client to an initial sync — echoing
			// the rejected key back makes a trusting client resend it, the resync loop this
			// codebase avoids. Transient/hierarchy errors ("5"/"12") keep the client's key.
			string echoKey = status == "3" ? "0" : clientSyncKey;
			return new XElement(AS + "Collection",
				new XElement(AS + "SyncKey", echoKey),
				new XElement(AS + "CollectionId", collectionId),
				new XElement(AS + "Status", status));
		}

		(UserFolder Folder, IContentStore Store)? resolved = await folders.ResolveCollectionAsync(
			context.Session, context.UserId, collectionId, ct);
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
				state, [], collectionOptions.FilterType, SyncKeyValidation.Initial, ct);
			XElement initial = new(AS + "Collection",
				new XElement(AS + "SyncKey", initialKey.ToString()),
				new XElement(AS + "CollectionId", collectionId),
				new XElement(AS + "Status", "1"));
			EchoClassIfLegacy(initial, context, store);
			return new CollectionResult(initial, true, null);
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

		// On a Replay the entity is NOT rolled back until this collection's own commit, so the
		// snapshot to diff against is the previous generation, read explicitly here (mirrors
		// PeekSyncKeyAsync). Current diffs against the live snapshot.
		Dictionary<string, string> snapshot = validation == SyncKeyValidation.Replay
			? SyncStateService.ReadPreviousSnapshot(state)
			: SyncStateService.ReadSnapshot(state);
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

		// To detect a concurrent edit (client Change vs a backend that moved on) we need the
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
						context, folder, store, command, snapshot, bodyPreference, deletesAsMoves, ledger,
						state.SyncKey, ct, conflictRevisions, collectionOptions.ServerWinsOnConflict);
					if (handled is not null)
						clientResponses.Add(handled);
					snapshotDirty = true;
					Core.Observability.GatewayMetrics.RecordSyncItems(
						context.UserName, store.EasClass, "client_to_server",
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
		// Set when an Add or Change was skipped this round (its render failed). Backend state
		// genuinely differs from the (rolled-back) persisted snapshot for that item, so offering this
		// collection to the long-poll wait would have the watchdog re-check spin against the same
		// permanently-failing item every interval.
		bool anyItemSkipped = false;

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

			// Pre-resolve the whole window's DAV item ids in one query + one flush; without this
			// every Add/Change/Delete composition below did its own SELECT + SaveChanges.
			List<string> windowKeys = new(diff.Adds.Count + diff.Changes.Count + diff.Deletes.Count);
			windowKeys.AddRange(diff.Adds.Select(a => a.ServerId));
			windowKeys.AddRange(diff.Changes.Select(c => c.ServerId));
			windowKeys.AddRange(diff.Deletes);
			IReadOnlyDictionary<string, string>? davIds =
				await folders.PreResolveDavItemIdsAsync(folder, store, windowKeys, ct);

			// Fetch every Add/Change item's body in ONE batched round instead of one backend
			// round trip (and, for IMAP, one per-user gate acquisition) per item. The default
			// GetItemsAsync loops GetItemAsync so behaviour is unchanged; a store override batches
			// at the protocol level. A batch-level failure degrades to per-item fetch inside
			// BuildItemElementAsync (empty prefetch map), preserving the old resilience.
			IReadOnlyDictionary<string, BackendItem?>? prefetched = null;
			List<string> fetchKeys = new(diff.Adds.Count + diff.Changes.Count);
			fetchKeys.AddRange(diff.Adds.Select(a => a.ServerId));
			fetchKeys.AddRange(diff.Changes.Select(c => c.ServerId));
			if (fetchKeys.Count > 0)
				try
				{
					prefetched = await store.GetItemsAsync(folder.BackendKey, fetchKeys, bodyPreference, ct);
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					logger.LogWarning(ex,
						"Batch item fetch failed for {CollectionId}; falling back to per-item fetch", collectionId);
				}

			foreach (ItemChange add in diff.Adds)
			{
				XElement? element = await BuildItemElementAsync(
					AS + "Add", context, folder, store, add.ServerId, bodyPreference, ct, davIds, prefetched);
				if (element is not null)
					serverCommands.Add(element);
				else
				{
					newSnapshot.Remove(add.ServerId); // vanished mid-sync; retry next round
					anyItemSkipped = true;
				}
			}

			foreach (ItemChange change in diff.Changes)
			{
				XElement? element = await BuildItemElementAsync(
					AS + "Change", context, folder, store, change.ServerId, bodyPreference, ct, davIds, prefetched);
				if (element is not null)
					serverCommands.Add(element);
				else
				{
					// CollectionDiff.Compute already wrote the NEW backend revision into
					// newSnapshot when it charged this item to the window. Mirror the Add loop above:
					// revert to the revision the client last acked (or the sentinel that never matches
					// a real backend revision) so the round does NOT record this Change as delivered —
					// the next diff must re-offer it, exactly as the contract's null convention promises.
					newSnapshot[change.ServerId] = snapshot.TryGetValue(change.ServerId, out string? old)
						? old
						: ReadOnlyRevertRevision;
					anyItemSkipped = true;
				}
			}

			foreach (string deletedKey in diff.Deletes)
			{
				string serverId = await folders.ComposeServerIdAsync(folder, store, deletedKey, ct, davIds);
				serverCommands.Add(new XElement(AS + (agedOut.Contains(deletedKey) ? "SoftDelete" : "Delete"),
					new XElement(AS + "ServerId", serverId)));
			}

			// Record the metric from what was actually SENT — an Add whose fetch returned null
			// (vanished mid-sync) is not in serverCommands and must not be counted as delivered.
			// The same counts feed the activity log below.
			Core.Observability.GatewayMetrics.RecordSyncItems(
				context.UserName, store.EasClass, "server_to_client", "add",
				serverCommands.Count(c => c.Name.LocalName == "Add"));
			Core.Observability.GatewayMetrics.RecordSyncItems(
				context.UserName, store.EasClass, "server_to_client", "change",
				serverCommands.Count(c => c.Name.LocalName == "Change"));
			Core.Observability.GatewayMetrics.RecordSyncItems(
				context.UserName, store.EasClass, "server_to_client", "delete",
				serverCommands.Count(c => c.Name.LocalName == "Delete"));
			Core.Observability.GatewayMetrics.RecordSyncItems(
				context.UserName, store.EasClass, "server_to_client", "soft_delete",
				serverCommands.Count(c => c.Name.LocalName == "SoftDelete"));
		}

		// One activity line per collection round; idle polls (all counts zero) stay silent.
		int sentAdds = serverCommands.Count(c => c.Name.LocalName == "Add");
		int sentChanges = serverCommands.Count(c => c.Name.LocalName == "Change");
		int sentDeletes = serverCommands.Count(c => c.Name.LocalName is "Delete" or "SoftDelete");
		if (clientAdds + clientChanges + clientDeletes + sentAdds + sentChanges + sentDeletes > 0)
			logger.LogInformation(
				"Sync \"{Folder}\" for {User}: client {ClientAdds} add/{ClientChanges} change/{ClientDeletes} delete, " +
				"sent {SentAdds} add/{SentChanges} change/{SentDeletes} delete",
				folder.DisplayName, context.UserName,
				clientAdds, clientChanges, clientDeletes, sentAdds, sentChanges, sentDeletes);

		bool hasPayload = clientResponses.Count > 0 || serverCommands.Count > 0;
		if (!hasPayload && !snapshotDirty)
			// Nothing to say for this collection; it is a candidate for the long-poll wait — UNLESS an
			// item was skipped this round, in which case the backend genuinely still differs from
			// what's stored and re-waiting would just spin the watchdog against the same permanently-
			// failing item on every interval.
			return new CollectionResult(
				null, false, anyItemSkipped ? null : new WaitableCollection(collectionElement, folder, store));

		state.OptionsJson = collectionOptions.ToJson();
		int newKey;
		try
		{
			newKey = await context.State.CommitCollectionStateAsync(
				state, newSnapshot, collectionOptions.FilterType, validation, ct,
				ledger.AppliedAdds, ledger.AppliedChanges);
		}
		catch (BackendException ex)
		{
			// A pipelined sibling request already won the commit race on this collection's
			// CollectionState row. This must not escape as an unhandled exception (an HTTP 500 that
			// discards every OTHER collection's already-computed response in the same request) —
			// FolderModifyHandlerBase and FolderSyncHandler both catch this exact exception and
			// answer a status; Sync did not. The client keeps its current key and retries.
			logger.LogWarning(ex, "Concurrent sync commit for {CollectionId}; asking the client to retry",
				collectionId);
			return new CollectionResult(Error("5"), true, null);
		}

		XElement response = new(AS + "Collection",
			new XElement(AS + "SyncKey", newKey.ToString()),
			new XElement(AS + "CollectionId", collectionId),
			new XElement(AS + "Status", "1"));
		EchoClassIfLegacy(response, context, store);
		// MoreAvailable is emitted immediately after Status — before Responses/Commands — as
		// Exchange and Z-Push do; WBXML is order-sensitive for strict sequence parsers.
		if (moreAvailable)
			response.Add(new XElement(AS + "MoreAvailable"));
		if (clientResponses.Count > 0)
			response.Add(new XElement(AS + "Responses", clientResponses));
		if (serverCommands.Count > 0)
			response.Add(new XElement(AS + "Commands", serverCommands));
		return new CollectionResult(response, true, null);
	}
}

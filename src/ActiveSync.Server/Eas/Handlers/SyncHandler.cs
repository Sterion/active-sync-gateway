using System.Text.Json;
using System.Xml.Linq;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using ActiveSync.Core.State;
using ActiveSync.Core.Sync;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;
using Microsoft.Extensions.Options;

namespace ActiveSync.Server.Eas.Handlers;

/// <summary>Sync (MS-ASCMD 2.2.1.21): item-level synchronization for all content classes.</summary>
public sealed partial class SyncHandler(
	FolderService folders,
	IOptions<ActiveSyncOptions> options,
	IHostApplicationLifetime lifetime,
	ILogger<SyncHandler> logger) : IEasCommandHandler
{
	/// <summary>
	///   Bogus revision written to the snapshot for suppressed read-only writes: it never
	///   matches a real backend revision, so the next diff re-sends the server's version.
	/// </summary>
	private const string ReadOnlyRevertRevision = "!ro";

	private static readonly XNamespace AS = EasNamespaces.AirSync;
	private static readonly XNamespace ASB = EasNamespaces.AirSyncBase;
	private static readonly XNamespace Cal = EasNamespaces.Calendar;
	private static readonly XNamespace E2 = EasNamespaces.Email2;

	public string Command => "Sync";

	public async Task HandleAsync(EasContext context, CancellationToken ct)
	{
		XDocument? request = await context.ReadRequestAsync();
		int? waitSeconds = null;
		int globalWindow = options.Value.Eas.DefaultWindowSize;
		List<XElement> collectionElements;

		if (request?.Root is { } root)
		{
			collectionElements = root.Element(AS + "Collections")?.Elements(AS + "Collection").ToList() ?? [];
			if (collectionElements.Count == 0)
			{
				await WriteStatusAsync(context, "13");
				return;
			}

			if (int.TryParse(root.Element(AS + "Wait")?.Value, out int waitMinutes))
				waitSeconds = waitMinutes * 60;
			else if (int.TryParse(root.Element(AS + "HeartbeatInterval")?.Value, out int heartbeat))
				waitSeconds = heartbeat;
			if (int.TryParse(root.Element(AS + "WindowSize")?.Value, out int gw))
				globalWindow = gw;

			// Cache the replayable request shape for subsequent empty Sync requests (MS-ASCMD:
			// an empty request means "repeat my previous request").
			CachedSyncRequest cache = new(waitSeconds, globalWindow, collectionElements
				.Select(c => new CachedSyncCollection(
					c.Element(AS + "CollectionId")?.Value ?? "",
					c.Element(AS + "GetChanges")?.Value != "0",
					int.TryParse(c.Element(AS + "WindowSize")?.Value, out int cw) ? cw : null))
				.Where(c => c.CollectionId.Length > 0)
				.ToList());
			context.Device.LastSyncRequestJson = JsonSerializer.Serialize(cache);
			await context.State.PersistAsync(ct);
		}
		else
		{
			// Empty Sync: replay the cached request. Without a usable cache, status 13 tells
			// the client to resend the full request.
			List<CachedSyncCollection>? replayed = BuildReplayedCollections(context, out waitSeconds, out globalWindow);
			if (replayed is null)
			{
				await WriteStatusAsync(context, "13");
				return;
			}

			collectionElements = new List<XElement>();
			foreach (CachedSyncCollection cached in replayed)
			{
				CollectionState? state = await context.State.GetCollectionStateAsync(context.Device, cached.CollectionId, ct);
				if (state is null || state.SyncKey == 0)
				{
					// Collection never completed a real sync — the cache is stale.
					await WriteStatusAsync(context, "13");
					return;
				}

				XElement synthetic = new(AS + "Collection",
					new XElement(AS + "SyncKey", state.SyncKey.ToString()),
					new XElement(AS + "CollectionId", cached.CollectionId));
				if (!cached.GetChanges)
					synthetic.Add(new XElement(AS + "GetChanges", "0"));
				if (cached.WindowSize is { } cachedWindow)
					synthetic.Add(new XElement(AS + "WindowSize", cachedWindow.ToString()));
				collectionElements.Add(synthetic);
			}
		}

		List<XElement> responses = new();
		bool anyPayload = false;
		List<(XElement Element, UserFolder Folder, IContentStore Store)> pendingWaitCollections = new();

		foreach (XElement collectionElement in collectionElements)
		{
			(XElement? response, bool hadPayload, (XElement, UserFolder, IContentStore)? waitable) =
				await ProcessCollectionAsync(context, collectionElement, globalWindow, ct);
			if (response is not null)
			{
				responses.Add(response);
				anyPayload |= hadPayload;
			}

			if (waitable is not null)
				pendingWaitCollections.Add(waitable.Value);
		}

		// Long poll: nothing to report and the client asked to wait.
		if (!anyPayload && responses.Count == 0 && waitSeconds is { } wait && pendingWaitCollections.Count > 0)
		{
			wait = Math.Clamp(wait, options.Value.Eas.MinHeartbeatSeconds, options.Value.Eas.MaxHeartbeatSeconds);
			bool changed = await WaitWithWatchdogAsync(
				context, pendingWaitCollections, TimeSpan.FromSeconds(wait), ct);
			if (changed)
				foreach ((XElement element, UserFolder _, IContentStore _) in pendingWaitCollections)
				{
					(XElement? response, bool hadPayload, _) =
						await ProcessCollectionAsync(context, element, globalWindow, ct);
					if (response is not null && hadPayload)
					{
						responses.Add(response);
						anyPayload = true;
					}
				}
		}

		if (responses.Count == 0)
		{
			await context.WriteEmptyAsync(); // canonical "no changes" answer
			return;
		}

		await context.WriteResponseAsync(new XDocument(
			new XElement(AS + "Sync",
				new XElement(AS + "Collections", responses))));
	}

	private static Task WriteStatusAsync(EasContext context, string status)
	{
		return context.WriteResponseAsync(new XDocument(
			new XElement(AS + "Sync", new XElement(AS + "Status", status))));
	}

	private async Task<(XElement? Response, bool HadPayload, (XElement, UserFolder, IContentStore)? Waitable)>
		ProcessCollectionAsync(EasContext context, XElement collectionElement, int globalWindow, CancellationToken ct)
	{
		string collectionId = collectionElement.Element(AS + "CollectionId")?.Value ?? "";
		string clientSyncKey = collectionElement.Element(AS + "SyncKey")?.Value ?? "0";

		XElement Error(string status)
		{
			return new XElement(AS + "Collection",
				new XElement(AS + "SyncKey", clientSyncKey),
				new XElement(AS + "CollectionId", collectionId),
				new XElement(AS + "Status", status));
		}

		(UserFolder Folder, IContentStore Store)? resolved = await folders.ResolveCollectionAsync(
			context.Session, context.Device.UserName, collectionId, ct);
		if (resolved is null)
			return (Error("12"), true, null); // folder hierarchy out of date

		(UserFolder folder, IContentStore store) = resolved.Value;
		(SyncKeyValidation validation, CollectionState state) = await context.State.ValidateSyncKeyAsync(
			context.Device, collectionId, clientSyncKey, ct);
		if (validation == SyncKeyValidation.Invalid)
			return (Error("3"), true, null);

		// Persisted or supplied options
		CollectionOptions? collectionOptions = ParseOptions(collectionElement.Element(AS + "Options"));
		if (collectionOptions is null && state.OptionsJson is not null)
			collectionOptions = JsonSerializer.Deserialize<CollectionOptions>(state.OptionsJson);
		collectionOptions ??= CollectionOptions.Default;

		if (validation == SyncKeyValidation.Initial)
		{
			state.OptionsJson = JsonSerializer.Serialize(collectionOptions);
			int initialKey = await context.State.CommitCollectionStateAsync(
				state, [], collectionOptions.FilterType, ct);
			return (new XElement(AS + "Collection",
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
		int clientAdds = 0, clientChanges = 0, clientDeletes = 0;
		XElement? commands = collectionElement.Element(AS + "Commands");
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
						context, folder, store, command, snapshot, bodyPreference, deletesAsMoves, ct);
					if (handled is not null)
						clientResponses.Add(handled);
					snapshotDirty = true;
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
				return (Error("5"), true, null);
			}

			CollectionChanges diff = CollectionDiff.Compute(snapshot, current, windowSize);
			moreAvailable = diff.MoreAvailable;
			newSnapshot = diff.NewSnapshot;

			foreach (ItemChange add in diff.Adds)
			{
				XElement? element = await BuildItemElementAsync(
					AS + "Add", context, folder, store, add.ServerId, bodyPreference, ct);
				if (element is not null)
					serverCommands.Add(element);
				else
					newSnapshot.Remove(add.ServerId); // vanished mid-sync; retry next round
			}

			foreach (ItemChange change in diff.Changes)
			{
				XElement? element = await BuildItemElementAsync(
					AS + "Change", context, folder, store, change.ServerId, bodyPreference, ct);
				if (element is not null)
					serverCommands.Add(element);
			}

			foreach (string deletedKey in diff.Deletes)
			{
				string serverId = await folders.ComposeServerIdAsync(folder, store, deletedKey, ct);
				serverCommands.Add(new XElement(AS + "Delete",
					new XElement(AS + "ServerId", serverId)));
			}
		}

		// One activity line per collection round; idle polls (all counts zero) stay silent.
		int sentAdds = serverCommands.Count(c => c.Name.LocalName == "Add");
		int sentChanges = serverCommands.Count(c => c.Name.LocalName == "Change");
		int sentDeletes = serverCommands.Count(c => c.Name.LocalName == "Delete");
		if (clientAdds + clientChanges + clientDeletes + sentAdds + sentChanges + sentDeletes > 0)
			logger.LogInformation(
				"Sync \"{Folder}\" for {User}: client {ClientAdds} add/{ClientChanges} change/{ClientDeletes} delete, " +
				"sent {SentAdds} add/{SentChanges} change/{SentDeletes} delete",
				folder.DisplayName, context.Device.UserName,
				clientAdds, clientChanges, clientDeletes, sentAdds, sentChanges, sentDeletes);

		bool hasPayload = clientResponses.Count > 0 || serverCommands.Count > 0;
		if (!hasPayload && !snapshotDirty)
			// Nothing to say for this collection; it is a candidate for the long-poll wait.
			return (null, false, (collectionElement, folder, store));

		state.OptionsJson = JsonSerializer.Serialize(collectionOptions);
		int newKey = await context.State.CommitCollectionStateAsync(
			state, newSnapshot, collectionOptions.FilterType, ct);

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
		return (response, true, null);
	}

	/// <summary>Log-friendly noun for the collection's content class.</summary>
	private static string NounFor(IContentStore store)
	{
		return store.EasClass switch
		{
			EasClass.Email => "email",
			EasClass.Calendar => "event",
			EasClass.Contacts => "contact",
			EasClass.Notes => "note",
			EasClass.Tasks => "task",
			_ => "item"
		};
	}

	/// <summary>Headline for a client Change, derived from the payload (mail flag flips mostly).</summary>
	private static string DescribeChange(IContentStore store, XElement? appData)
	{
		if (store.EasClass == EasClass.Email && appData is not null)
		{
			XNamespace email = EasNamespaces.Email;
			switch (appData.Element(email + "Read")?.Value)
			{
				case "1": return "mark read";
				case "0": return "mark unread";
			}

			if (appData.Element(email + "Flag") is { } flag)
				return flag.HasElements ? "set flag" : "clear flag";
		}

		return "edit";
	}

	/// <summary>Applies one client command; returns a Responses child (null when success is implicit).</summary>
	private async Task<XElement?> ApplyClientCommandAsync(
		EasContext context, UserFolder folder, IContentStore store, XElement command,
		Dictionary<string, string> snapshot, BodyPreference bodyPreference, bool deletesAsMoves, CancellationToken ct)
	{
		bool readOnly = options.Value.ReadOnly;
		XElement? appData = command.Element(AS + "ApplicationData");
		switch (command.Name.LocalName)
		{
			case "Add":
			{
				string clientId = command.Element(AS + "ClientId")?.Value ?? "";
				if (appData is null)
					return ClientCommandStatus(command, "6");
				if (readOnly)
				{
					logger.LogInformation("Read-only: rejecting new {Noun} in \"{Folder}\" for {User}",
						NounFor(store), folder.DisplayName, context.Device.UserName);
					return ClientCommandStatus(command, "6");
				}

				// 16.x: email2:Send on a draft Add means "submit instead of storing" — the
				// message goes out via SMTP + Sent Items and never materializes in Drafts.
				if (HasSendElement(command, appData) &&
				    store.EasClass.Equals(EasClass.Email, StringComparison.OrdinalIgnoreCase))
				{
					await SubmitDraftAsync(context, appData, null, null, ct);
					return new XElement(AS + "Add",
						new XElement(AS + "ClientId", clientId),
						new XElement(AS + "Status", "1"));
				}

				(string itemKey, string revision) = await store.CreateItemAsync(folder.BackendKey, appData, ct);
				snapshot[itemKey] = revision;
				string serverId = await folders.ComposeServerIdAsync(folder, store, itemKey, ct);
				return new XElement(AS + "Add",
					new XElement(AS + "ClientId", clientId),
					new XElement(AS + "ServerId", serverId),
					new XElement(AS + "Status", "1"));
			}
			case "Change":
			{
				string serverId = command.Element(AS + "ServerId")?.Value ?? "";
				string itemKey = await folders.ResolveItemKeyAsync(folder, store, serverId, ct)
				                 ?? throw new BackendItemNotFoundException(serverId);
				if (appData is null)
					return ClientCommandStatus(command, "6");
				if (readOnly)
				{
					// Silent revert: poison the snapshot revision so the next diff pushes
					// the server's version back to the client.
					logger.LogInformation(
						"Read-only: reverting change ({What}) on {Noun} {ServerId} in \"{Folder}\" for {User}",
						DescribeChange(store, appData), NounFor(store), serverId,
						folder.DisplayName, context.Device.UserName);
					snapshot[itemKey] = ReadOnlyRevertRevision;
					return null;
				}

				// 16.x: email2:Send on a draft Change submits the (merged) draft and removes it.
				if (HasSendElement(command, appData) &&
				    store.EasClass.Equals(EasClass.Email, StringComparison.OrdinalIgnoreCase))
				{
					await SubmitDraftAsync(context, appData, folder.BackendKey, itemKey, ct);
					await store.DeleteItemAsync(folder.BackendKey, itemKey, ct, true);
					snapshot.Remove(itemKey);
					return null;
				}

				string revision = await store.UpdateItemAsync(folder.BackendKey, itemKey, appData, ct);
				snapshot[itemKey] = revision;
				return null; // implicit success
			}
			case "Delete":
			{
				string serverId = command.Element(AS + "ServerId")?.Value ?? "";
				string? itemKey = await folders.ResolveItemKeyAsync(folder, store, serverId, ct);

				// 16.x occurrence delete: an InstanceId turns "delete the event" into
				// "cancel this one occurrence". Synthesized as a deleted exception so it
				// rides the normal partial-merge update path (converter appends an EXDATE).
				string? instanceId = command.Element(ASB + "InstanceId")?.Value;
				if (itemKey is not null && instanceId is not null && !readOnly &&
				    store.EasClass.Equals(EasClass.Calendar, StringComparison.OrdinalIgnoreCase))
				{
					DateTime occurrence;
					try
					{
						occurrence = EasDateTime.Parse(instanceId);
					}
					catch (FormatException)
					{
						return ClientCommandStatus(command, "6"); // unparsable InstanceId
					}

					XElement occurrenceDelete = new(AS + "ApplicationData",
						new XElement(Cal + "Exceptions",
							new XElement(Cal + "Exception",
								new XElement(Cal + "Deleted", "1"),
								new XElement(Cal + "ExceptionStartTime",
									EasDateTime.ToCompact(occurrence)))));
					string occurrenceRevision =
						await store.UpdateItemAsync(folder.BackendKey, itemKey, occurrenceDelete, ct);
					snapshot[itemKey] = occurrenceRevision;
					return null;
				}

				if (itemKey is not null)
				{
					if (readOnly)
					{
						// Silent revert: forget the item so the next diff re-Adds it.
						logger.LogInformation(
							"Read-only: reverting delete of {Noun} {ServerId} in \"{Folder}\" for {User}",
							NounFor(store), serverId, folder.DisplayName, context.Device.UserName);
						snapshot.Remove(itemKey);
						return null;
					}

					await store.DeleteItemAsync(folder.BackendKey, itemKey, ct, permanent: !deletesAsMoves);
					snapshot.Remove(itemKey);
				}

				return null; // deletes are only reported on failure
			}
			case "Fetch":
			{
				string serverId = command.Element(AS + "ServerId")?.Value ?? "";
				string itemKey = await folders.ResolveItemKeyAsync(folder, store, serverId, ct)
				                 ?? throw new BackendItemNotFoundException(serverId);
				BodyPreference full = new(bodyPreference.Type, null, false, bodyPreference.Eas16);
				BackendItem item = await store.GetItemAsync(folder.BackendKey, itemKey, full, ct)
				                   ?? throw new BackendItemNotFoundException(serverId);
				return new XElement(AS + "Fetch",
					new XElement(AS + "ServerId", serverId),
					new XElement(AS + "Status", "1"),
					new XElement(AS + "ApplicationData", item.ApplicationData));
			}
			default:
				return ClientCommandStatus(command, "4");
		}
	}

	private static bool HasSendElement(XElement command, XElement appData)
	{
		// MS-ASCMD puts email2:Send as a child of Add/Change; some client generations nest
		// it inside ApplicationData — accept both.
		return command.Element(E2 + "Send") is not null || appData.Element(E2 + "Send") is not null;
	}

	/// <summary>16.x draft submit (email2:Send): merge with the stored draft, SMTP-send, file to Sent.</summary>
	private static async Task SubmitDraftAsync(
		EasContext context, XElement appData, string? folderBackendKey, string? itemKey, CancellationToken ct)
	{
		MimeKit.MimeMessage? original = null;
		if (folderBackendKey is not null && itemKey is not null)
		{
			byte[]? raw = await context.Session.Mail.GetRawMessageAsync(folderBackendKey, itemKey, ct);
			if (raw is not null)
			{
				using MemoryStream rawStream = new(raw);
				original = await MimeKit.MimeMessage.LoadAsync(rawStream, ct);
			}
		}

		MimeKit.MimeMessage message =
			ActiveSync.Backends.Converters.DraftMessageBuilder.Build(appData, original, context.Session.MailAddress);
		using MemoryStream buffer = new();
		await message.WriteToAsync(buffer, ct);
		byte[] mime = buffer.ToArray();
		await context.Session.Mail.SendAsync(mime, ct);
		await context.Session.Mail.SaveToSentAsync(mime, ct);
	}

	private static XElement ClientCommandStatus(XElement command, string status)
	{
		XElement result = new(AS + command.Name.LocalName, new XElement(AS + "Status", status));
		string? serverId = command.Element(AS + "ServerId")?.Value;
		string? clientId = command.Element(AS + "ClientId")?.Value;
		if (clientId is not null)
			result.AddFirst(new XElement(AS + "ClientId", clientId));
		else if (serverId is not null)
			result.AddFirst(new XElement(AS + "ServerId", serverId));
		return result;
	}

	private async Task<XElement?> BuildItemElementAsync(
		XName commandName, EasContext context, UserFolder folder, IContentStore store,
		string itemKey, BodyPreference bodyPreference, CancellationToken ct)
	{
		BackendItem? item;
		try
		{
			item = await store.GetItemAsync(folder.BackendKey, itemKey, bodyPreference, ct);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			logger.LogWarning(ex, "Fetching item {ItemKey} failed", itemKey);
			return null;
		}

		if (item is null)
			return null;
		string serverId = await folders.ComposeServerIdAsync(folder, store, itemKey, ct);
		XElement applicationData = new(AS + "ApplicationData", item.ApplicationData);

		// 16.x drafts: items in the Drafts folder carry email2:IsDraft so the client opens
		// them in the composer instead of the reader.
		if (bodyPreference.Eas16 && folder.Type == EasFolderType.Drafts &&
		    store.EasClass.Equals(EasClass.Email, StringComparison.OrdinalIgnoreCase) &&
		    applicationData.Element(E2 + "IsDraft") is null)
			applicationData.Add(new XElement(E2 + "IsDraft", "1"));

		QualifyCalendarAttachmentReferences(applicationData, serverId);

		return new XElement(commandName,
			new XElement(AS + "ServerId", serverId),
			applicationData);
	}

	/// <summary>
	///   The calendar converter emits attachment FileReferences as "calatt::&lt;index&gt;"
	///   because it cannot know item identity; the full ItemOperations-resolvable shape is
	///   "calatt::&lt;serverId&gt;::&lt;index&gt;", stamped here where the ServerId exists.
	/// </summary>
	private static void QualifyCalendarAttachmentReferences(XElement applicationData, string serverId)
	{
		const string prefix = "calatt::";
		foreach (XElement reference in applicationData.Descendants(ASB + "FileReference"))
		{
			if (!reference.Value.StartsWith(prefix, StringComparison.Ordinal))
				continue;
			string tail = reference.Value[prefix.Length..];
			if (!tail.Contains("::", StringComparison.Ordinal))
				reference.Value = prefix + serverId + "::" + tail;
		}
	}

	/// <summary>
	///   Races the backend watchers against the exact pending-change watchdog. The watchers
	///   give sub-second push when the server cooperates; the watchdog guarantees detection
	///   even when IDLE/STATUS notifications never arrive.
	/// </summary>
	private async Task<bool> WaitWithWatchdogAsync(
		EasContext context,
		List<(XElement Element, UserFolder Folder, IContentStore Store)> collections,
		TimeSpan timeout, CancellationToken ct)
	{
		int watchdogSeconds = options.Value.Eas.WatchdogSeconds;
		DateTime deadline = DateTime.UtcNow + timeout;
		// Also observe host shutdown: an active long-poll must never delay process exit.
		using CancellationTokenSource cts =
			CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.ApplicationStopping);

		// A single watcher task (WaitForAnyChangeAsync already races the per-store waits and
		// drains its own children) versus the optional watchdog re-check.
		Task<bool> watcherTask = WaitForAnyChangeAsync(collections, timeout, cts.Token);
		Task<bool>? watchdogTask = watchdogSeconds > 0 ? WatchdogAsync() : null;

		LongPollWatchdog.Outcome<bool> outcome = await LongPollWatchdog.RaceAsync(
			[watcherTask], watchdogTask, changed => changed, false, cts, ct);

		if (outcome.Result && outcome.FoundByWatchdog)
			logger.LogWarning(
				"Watchdog: pending changes for {User} found by re-check during Sync wait (missed by the backend watcher)",
				context.Device.UserName);
		return outcome.Result;

		async Task<bool> WatchdogAsync()
		{
			TimeSpan interval = TimeSpan.FromSeconds(watchdogSeconds);
			while (true)
			{
				TimeSpan remaining = deadline - DateTime.UtcNow;
				if (remaining <= TimeSpan.Zero)
					return false;
				await Task.Delay(remaining < interval ? remaining : interval, cts.Token);
				foreach ((XElement element, UserFolder folder, IContentStore store) in collections)
				{
					string collectionId = element.Element(AS + "CollectionId")?.Value ?? "";
					if (await PendingChangeDetector.HasPendingChangesAsync(
						    context, collectionId, folder, store, logger, cts.Token))
						return true;
				}
			}
		}
	}

	private static async Task<bool> WaitForAnyChangeAsync(
		List<(XElement Element, UserFolder Folder, IContentStore Store)> collections,
		TimeSpan timeout, CancellationToken ct)
	{
		using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
		List<IGrouping<IContentStore, (XElement Element, UserFolder Folder, IContentStore Store)>> byStore =
			collections.GroupBy(c => c.Store).ToList();
		List<Task<IReadOnlyList<string>>> waits = byStore
			.Select(g => g.Key.WaitForChangesAsync(
				g.Select(c => c.Folder.BackendKey).Distinct().ToList(), timeout, cts.Token))
			.ToList();
		try
		{
			while (waits.Count > 0)
			{
				Task<IReadOnlyList<string>> finished = await Task.WhenAny(waits);
				waits.Remove(finished);
				IReadOnlyList<string> changed = await finished;
				if (changed.Count > 0)
					return true;
			}

			return false;
		}
		finally
		{
			await cts.CancelAsync(); // stop the remaining pollers
		}
	}

	private static CollectionOptions? ParseOptions(XElement? optionsElement)
	{
		if (optionsElement is null)
			return null;
		int filterType = int.TryParse(optionsElement.Element(AS + "FilterType")?.Value, out int ft) ? ft : 0;

		// AirSyncBase body Type codes (MS-ASAIRS): 1 = plain, 2 = HTML, 4 = MIME. When a
		// client offers several, prefer the richest we render well: HTML (2) > plain (1) >
		// whatever else it listed first.
		int bodyType = 2;
		long? truncation = 200 * 1024;
		var preferences = optionsElement.Elements(ASB + "BodyPreference")
			.Select(p => new
			{
				Type = int.TryParse(p.Element(ASB + "Type")?.Value, out int t) ? t : 1,
				Truncation = long.TryParse(p.Element(ASB + "TruncationSize")?.Value, out long tr)
					? (long?)tr
					: null
			})
			.ToList();
		if (preferences.Count > 0)
		{
			var chosen = preferences.FirstOrDefault(p => p.Type == 2)
			             ?? preferences.FirstOrDefault(p => p.Type == 1)
			             ?? preferences[0];
			bodyType = chosen.Type;
			truncation = chosen.Truncation;
		}

		return new CollectionOptions(filterType, bodyType, truncation);
	}

	private sealed record CollectionOptions(int FilterType, int BodyType, long? TruncationSize)
	{
		public static readonly CollectionOptions Default = new(0, 2, 200 * 1024);
	}
}

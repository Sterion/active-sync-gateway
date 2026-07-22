using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Core.State;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;

namespace ActiveSync.Server.Eas.Handlers;

// Client → server command application (Add/Change/Delete/Fetch), including the read-only
// silent-revert path, the 16.x draft-submit shortcut, and the replay ledger reuse.
public sealed partial class SyncHandler
{
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
	internal async Task<XElement?> ApplyClientCommandAsync(
		EasContext context, UserFolder folder, IContentStore store, XElement command,
		Dictionary<string, string> snapshot, BodyPreference bodyPreference, bool deletesAsMoves,
		ClientCommandLedger ledger, CancellationToken ct)
	{
		// Global ReadOnly mode and per-folder read-only shared-calendar grants share the
		// same enforcement: reject Adds, silently revert Changes/Deletes.
		bool readOnly = WritePermission.IsBlocked(context, options.Value, folder);
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

				// Retried Add (the client never saw our response): reuse the outcome of the
				// first attempt — no second backend item, no second iMIP invitation, no
				// second draft submission.
				if (clientId.Length > 0 && ledger.TryReplayAdd(clientId, out AppliedClientAdd? replayed))
				{
					ledger.RecordAdd(clientId, replayed!);
					if (replayed!.ItemKey is null)
						return new XElement(AS + "Add",
							new XElement(AS + "ClientId", clientId),
							new XElement(AS + "Status", "1"));
					snapshot[replayed.ItemKey] = replayed.Revision ?? "";
					string replayedServerId = await folders.ComposeServerIdAsync(folder, store, replayed.ItemKey, ct);
					return new XElement(AS + "Add",
						new XElement(AS + "ClientId", clientId),
						new XElement(AS + "ServerId", replayedServerId),
						new XElement(AS + "Status", "1"));
				}

				// 16.x: email2:Send on a draft Add means "submit instead of storing" — the
				// message goes out via SMTP + Sent Items and never materializes in Drafts.
				if (HasSendElement(command, appData) &&
				    store.EasClass.Equals(EasClass.Email, StringComparison.OrdinalIgnoreCase))
				{
					await SubmitDraftAsync(context, appData, null, null, ct);
					if (clientId.Length > 0)
						ledger.RecordAdd(clientId, new AppliedClientAdd(null, null));
					return new XElement(AS + "Add",
						new XElement(AS + "ClientId", clientId),
						new XElement(AS + "Status", "1"));
				}

				(string itemKey, string revision) = await store.CreateItemAsync(folder.BackendKey, appData, ct);
				snapshot[itemKey] = revision;
				if (clientId.Length > 0)
					ledger.RecordAdd(clientId, new AppliedClientAdd(itemKey, revision));
				if (IsCalendarClass(store))
					await invitations.AfterCreateAsync(context, store, folder.BackendKey, itemKey, ct);
				string serverId = await folders.ComposeServerIdAsync(folder, store, itemKey, ct);
				return new XElement(AS + "Add",
					new XElement(AS + "ClientId", clientId),
					new XElement(AS + "ServerId", serverId),
					new XElement(AS + "Status", "1"));
			}
			case "Change":
			{
				string serverId = command.Element(AS + "ServerId")?.Value ?? "";

				// Retried Change (the client never saw our response): the edit already sits on
				// the backend — acknowledge the recorded outcome instead of re-applying it,
				// which would re-send iMIP update mails (or re-submit a draft). Checked before
				// resolution: a replayed draft-submit Change no longer resolves.
				if (ledger.TryReplayChange(serverId, out AppliedClientChange? replayedChange))
				{
					ledger.RecordChange(serverId, replayedChange!);
					if (replayedChange!.ItemKey is not null)
					{
						if (replayedChange.Revision is not null)
							snapshot[replayedChange.ItemKey] = replayedChange.Revision;
						else
							snapshot.Remove(replayedChange.ItemKey); // draft submitted and removed
					}

					return null; // implicit success, as the lost response reported
				}

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
					// The draft is already sent; deleting it from Drafts is best-effort cleanup. A
					// failure here must not report failure for a sent message (the client would
					// resend and duplicate it) — worst case the draft reappears via the next diff (F10).
					try
					{
						await store.DeleteItemAsync(folder.BackendKey, itemKey, true, ct);
					}
					catch (Exception ex) when (ex is not OperationCanceledException)
					{
						logger.LogWarning(ex,
							"Draft {ServerId} submitted for {User} but removing it from Drafts failed",
							serverId, context.Device.UserName);
					}

					snapshot.Remove(itemKey);
					ledger.RecordChange(serverId, new AppliedClientChange(itemKey, null));
					return null;
				}

				// Meetings: the pre-change ICS feeds the invitation diff (what changed, who
				// was removed) — captured only for the calendar class.
				string? previousIcs = IsCalendarClass(store)
					? await MeetingInvitationService.CaptureIcsAsync(store, folder.BackendKey, itemKey, ct)
					: null;
				string revision = await store.UpdateItemAsync(folder.BackendKey, itemKey, appData, ct);
				snapshot[itemKey] = revision;
				ledger.RecordChange(serverId, new AppliedClientChange(itemKey, revision));
				if (IsCalendarClass(store))
					await invitations.AfterChangeAsync(
						context, store, folder.BackendKey, itemKey, previousIcs, ct);
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
					// Occurrence cancels ride the Change replay map too — re-applying one
					// would re-mail the occurrence CANCEL to attendees.
					string occurrenceKey = serverId + "\n" + instanceId;
					if (ledger.TryReplayChange(occurrenceKey, out AppliedClientChange? replayedCancel))
					{
						ledger.RecordChange(occurrenceKey, replayedCancel!);
						if (replayedCancel!.ItemKey is not null && replayedCancel.Revision is not null)
							snapshot[replayedCancel.ItemKey] = replayedCancel.Revision;
						return null;
					}

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
					ledger.RecordChange(occurrenceKey, new AppliedClientChange(itemKey, occurrenceRevision));
					await invitations.AfterOccurrenceCancelAsync(
						context, store, folder.BackendKey, itemKey, occurrence, ct);
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

					// Meetings: the ICS must be read BEFORE the delete for the CANCEL mail.
					string? deletedIcs = IsCalendarClass(store)
						? await MeetingInvitationService.CaptureIcsAsync(store, folder.BackendKey, itemKey, ct)
						: null;
					await store.DeleteItemAsync(folder.BackendKey, itemKey, !deletesAsMoves, ct);
					snapshot.Remove(itemKey);
					if (deletedIcs is not null)
						await invitations.AfterDeleteAsync(context, store, deletedIcs, ct);
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

	private static bool IsCalendarClass(IContentStore store)
	{
		return store.EasClass.Equals(EasClass.Calendar, StringComparison.OrdinalIgnoreCase);
	}

	private static bool HasSendElement(XElement command, XElement appData)
	{
		// MS-ASCMD puts email2:Send as a child of Add/Change; some client generations nest
		// it inside ApplicationData — accept both.
		return command.Element(E2 + "Send") is not null || appData.Element(E2 + "Send") is not null;
	}

	/// <summary>16.x draft submit (email2:Send): merge with the stored draft, SMTP-send, file to Sent.</summary>
	private async Task SubmitDraftAsync(
		EasContext context, XElement appData, string? folderBackendKey, string? itemKey, CancellationToken ct)
	{
		MimeKit.MimeMessage? original = null;
		if (folderBackendKey is not null && itemKey is not null)
		{
			byte[]? raw = await context.Session.MailStore.GetRawMessageAsync(folderBackendKey, itemKey, ct);
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

		// The SMTP submit is the point of no return: a failure here MUST surface (nothing was sent,
		// so the client is free to resend). Everything after it is best-effort and swallowed —
		// filing to Sent must never turn an already-sent message into a reported failure, or the
		// client resends and the recipient gets it twice (F10).
		await context.Session.MailSubmit.SendAsync(mime, ct);
		Core.Observability.GatewayMetrics.RecordMailSent(context.Device.UserName, "draft_submit");
		try
		{
			await context.Session.MailStore.SaveToSentAsync(mime, ct);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			logger.LogWarning(ex, "Draft submitted for {User} but filing to Sent failed", context.Device.UserName);
		}
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
}

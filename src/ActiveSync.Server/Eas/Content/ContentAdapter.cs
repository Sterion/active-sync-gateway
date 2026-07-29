using System.Xml.Linq;
using ActiveSync.Backends.Common.Converters;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;
using MimeKit;

namespace ActiveSync.Server.Eas.Content;

/// <summary>
///   The host's side of the typed item currency: one adapter per (session, store) that fetches
///   typed payloads, converts them to/from EAS ApplicationData, performs the EAS 16.x ghosting
///   merge (a store only ever receives a COMPLETE payload), and drives the session's
///   revision-keyed payload cache so a partial update usually needs no backend fetch. Handlers
///   trade in the same string keys the persisted state does; the contract's typed keys are
///   wrapped/unwrapped here, at the boundary.
/// </summary>
public sealed class ContentAdapter
{
	/// <summary>
	///   Defensive-parsing bound (contract rule: an oversized payload is treated exactly as an
	///   unparseable one — the item is skipped and retried, never thrown over). Raw messages get
	///   more headroom than text payloads.
	/// </summary>
	private const long MaxMailBytes = 128 * 1024 * 1024;

	private const int MaxTextPayloadChars = 16 * 1024 * 1024;

	private static readonly XNamespace Email = EasNamespaces.Email;
	private static readonly XNamespace Email2 = EasNamespaces.Email2;
	private static readonly XNamespace AirSyncBase = EasNamespaces.AirSyncBase;

	private readonly IBackendSession _session;

	private ContentAdapter(IBackendSession session, IContentStore store)
	{
		_session = session;
		Store = store;
		EasClass = session.EasClassOf(store);
	}

	/// <summary>The wrapped store — still the object to capability-test (`Store is IItemMoveOperations`).</summary>
	public IContentStore Store { get; }

	/// <summary>The store's EAS content class, derived from its alias interface.</summary>
	public string EasClass { get; }

	public static ContentAdapter For(IBackendSession session, IContentStore store)
	{
		return new ContentAdapter(session, store);
	}

	/// <summary>The acting user's scheduling identity (organizer/PARTSTAT matching): mail address, else login.</summary>
	private string ActingIdentity => _session.MailAddress ?? _session.Credentials.UserName;

	// ---------- fetch ----------

	/// <summary>One item's typed payload (boxed for the class-generic render loop); null = not fetched.</summary>
	public async Task<object?> GetItemAsync(string folderKey, string itemKey, CancellationToken ct)
	{
		FolderKey folder = new(folderKey);
		ItemKey item = new(itemKey);
		return Store switch
		{
			IMailStore mail => await mail.GetItemAsync(folder, item, MailFetchOptions.Full, ct),
			ICalendarStore calendar => await calendar.GetItemAsync(folder, item, ct),
			ITaskStore tasks => await tasks.GetItemAsync(folder, item, ct),
			IContactStore contacts => await contacts.GetItemAsync(folder, item, ct),
			INotesStore notes => await notes.GetItemAsync(folder, item, ct),
			_ => null
		};
	}

	/// <summary>
	///   Batch fetch preserving the stores' "null = not fetched, do not advance the snapshot"
	///   contract; the map is string-keyed for the render loop.
	/// </summary>
	public async Task<IReadOnlyDictionary<string, object?>> GetItemsAsync(
		string folderKey, IReadOnlyList<string> itemKeys, CancellationToken ct)
	{
		FolderKey folder = new(folderKey);
		List<ItemKey> items = itemKeys.Select(k => new ItemKey(k)).ToList();
		Dictionary<string, object?> result = new(itemKeys.Count, StringComparer.Ordinal);

		switch (Store)
		{
			case IMailStore mail:
				foreach ((ItemKey key, MailItem? item) in await mail.GetItemsAsync(folder, items, MailFetchOptions.Full, ct))
					result[key.Value] = item;
				break;
			case ICalendarStore calendar:
				foreach ((ItemKey key, CalendarItem? item) in await calendar.GetItemsAsync(folder, items, ct))
					result[key.Value] = item;
				break;
			case ITaskStore tasks:
				foreach ((ItemKey key, TaskItem? item) in await tasks.GetItemsAsync(folder, items, ct))
					result[key.Value] = item;
				break;
			case IContactStore contacts:
				foreach ((ItemKey key, ContactItem? item) in await contacts.GetItemsAsync(folder, items, ct))
					result[key.Value] = item;
				break;
			case INotesStore notes:
				foreach ((ItemKey key, NoteItem? item) in await notes.GetItemsAsync(folder, items, ct))
					result[key.Value] = item;
				break;
		}

		return result;
	}

	/// <summary>Deletes an item (string-keyed convenience; also drops any cached payload).</summary>
	public Task DeleteItemAsync(string folderKey, string itemKey, bool permanent, CancellationToken ct)
	{
		_session.PayloadCache.Remove(new FolderKey(folderKey), new ItemKey(itemKey));
		return Store.DeleteItemAsync(new FolderKey(folderKey), new ItemKey(itemKey), permanent, ct);
	}

	// ---------- render (payload → EAS ApplicationData) ----------

	/// <summary>
	///   EAS ApplicationData children for a typed payload. Null = the payload is malformed or
	///   over the size bound — the contract's defensive rule says treat it exactly as a fetch
	///   failure (skip, do not advance the snapshot, retry next round).
	/// </summary>
	public List<XElement>? Render(object item, BodyPreference bodyPreference, string folderKey, string itemKey)
	{
		try
		{
			switch (item)
			{
				case MailItem mail:
				{
					if (mail.Rfc822.Length > MaxMailBytes)
						return null;
					using MemoryStream stream = new(mail.Rfc822.ToArray());
					MimeMessage message = MimeMessage.Load(stream);
					return MailConverter.ToApplicationData(
						message, mail.Flags, mail.Categories, bodyPreference,
						index => MailFileReference.Encode(folderKey, itemKey, index), mail.Received);
				}
				case CalendarItem calendar:
					return OverTextBound(calendar.ICalendar)
						? null
						: CalendarConverter.ToApplicationData(calendar.ICalendar, bodyPreference, ActingIdentity);
				case TaskItem task:
					return OverTextBound(task.ICalendar)
						? null
						: TasksConverter.ToApplicationData(task.ICalendar, bodyPreference);
				case ContactItem contact:
					return OverTextBound(contact.VCard)
						? null
						: ContactConverter.ToApplicationData(contact.VCard, bodyPreference);
				case NoteItem note:
					return NotesXml.ToApplicationData(note, bodyPreference);
				default:
					return null;
			}
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			// A malformed payload behaves exactly as a fetch failure (contract § defensive rule).
			return null;
		}
	}

	/// <summary>
	///   Records the payload the client is about to be sent at this revision, so its next partial
	///   update can merge with no backend fetch. Only the payload-text classes cache — mail's
	///   Change never needs its payload (flags live outside the RFC822) and notes merge against
	///   the store's typed row directly.
	/// </summary>
	public void CacheRendered(string folderKey, string itemKey, string revision, object item)
	{
		string? payload = item switch
		{
			CalendarItem calendar => calendar.ICalendar,
			TaskItem task => task.ICalendar,
			ContactItem contact => contact.VCard,
			_ => null
		};
		if (payload is not null)
			_session.PayloadCache.Set(new FolderKey(folderKey), new ItemKey(itemKey), new ItemRevision(revision), payload);
	}

	// ---------- client writes (EAS ApplicationData → payload) ----------

	/// <summary>Applies a client Add: builds the complete payload and creates it.</summary>
	public async Task<(string ItemKey, string Revision)> CreateItemAsync(
		string folderKey, XElement applicationData, CancellationToken ct)
	{
		FolderKey folder = new(folderKey);
		(ItemKey key, ItemRevision revision) = Store switch
		{
			// EAS 16.x drafts — the only mail create a client can Sync; the store re-refuses
			// anything but Drafts.
			IMailStore mail => await mail.CreateDraftAsync(folder, new MailItem
			{
				Rfc822 = await BuildDraftBytesAsync(applicationData, null, ct),
				Flags = new MailFlags { Draft = true }
			}, ct),
			ICalendarStore calendar => await calendar.CreateItemAsync(
				folder, new CalendarItem { ICalendar = MergeCalendar(applicationData, null) }, ct),
			ITaskStore tasks => await tasks.CreateItemAsync(
				folder, new TaskItem { ICalendar = MergeTask(applicationData, null) }, ct),
			IContactStore contacts => await contacts.CreateItemAsync(
				folder, new ContactItem { VCard = MergeContact(applicationData, null) }, ct),
			INotesStore notes => await notes.CreateItemAsync(
				folder, NotesXml.FromApplicationData(applicationData, null), ct),
			_ => throw new BackendException($"The {EasClass} store cannot create items.")
		};
		return (key.Value, revision.Value);
	}

	/// <summary>
	///   Applies a client Change: merges the (possibly partial) ApplicationData onto the current
	///   payload host-side and replaces it, using the § 6.3 flow — cached payload at the acked
	///   revision first (conditional write, no fetch), then on a miss or a failed precondition a
	///   fresh fetch + re-merge + one unconditional write. Mail dispatches to its two write
	///   shapes instead: the flags patch, or (Drafts only) the draft rewrite.
	/// </summary>
	public async Task<string> UpdateItemAsync(
		string folderKey, string itemKey, XElement applicationData, string? ackedRevision,
		bool isDraftsFolder, CancellationToken ct)
	{
		if (Store is IMailStore mail)
			return await UpdateMailAsync(mail, folderKey, itemKey, applicationData, ackedRevision, isDraftsFolder, ct);

		FolderKey folder = new(folderKey);
		ItemKey item = new(itemKey);

		if (Store is INotesStore notes)
		{
			// Notes are typed and row-backed — read the current note and merge directly
			// (cheaper than a cache + Optional<T> machinery for the simplest class).
			NoteItem? existingNote = await notes.GetItemAsync(folder, item, ct)
			                         ?? throw new BackendItemNotFoundException(itemKey);
			NoteItem mergedNote = NotesXml.FromApplicationData(applicationData, existingNote);
			ItemRevision noteRevision = await notes.UpdateItemAsync(folder, item, mergedNote, null, ct);
			return noteRevision.Value;
		}

		// Payload-text classes: try the revision-keyed cache first — a client can only edit an
		// item it was sent, and if the cached payload is still at the acked revision the merge
		// needs no fetch and the write can be conditioned on exactly that revision.
		if (ackedRevision is { Length: > 0 } acked &&
		    _session.PayloadCache.TryGet(folder, item, new ItemRevision(acked), out string cached))
			try
			{
				string merged = MergePayload(applicationData, cached);
				ItemRevision revision = await UpdatePayloadAsync(folder, item, merged, new ItemRevision(acked), ct);
				_session.PayloadCache.Set(folder, item, revision, merged);
				return revision.Value;
			}
			catch (BackendPreconditionFailedException)
			{
				// The item moved underneath the cached basis — drop it and fall through to the
				// fresh fetch + re-merge below (the single bounded retry § 6.3 specifies).
				_session.PayloadCache.Remove(folder, item);
			}

		// Fresh path: merge onto the payload as the backend holds it NOW. The write is
		// unconditional — the contract has no revision-returning fetch, so there is no newer
		// revision to condition on, and merging onto the freshest payload is already the § 6.3
		// conflict resolution (recorded as a Phase 3 deviation in the design document).
		string? existing = await GetPayloadTextAsync(folder, item, ct)
		                   ?? throw new BackendItemNotFoundException(itemKey);
		string freshMerged = MergePayload(applicationData, existing);
		ItemRevision freshRevision = await UpdatePayloadAsync(folder, item, freshMerged, null, ct);
		_session.PayloadCache.Set(folder, item, freshRevision, freshMerged);
		return freshRevision.Value;
	}

	/// <summary>
	///   Draft-content elements, as opposed to a pure flag change (Read/Flag/Categories). EAS XML
	///   knowledge, so it lives host-side now — SyncHandler consults it to route a mail Change.
	/// </summary>
	public static bool HasDraftContent(XElement applicationData)
	{
		return applicationData.Element(Email + "To") is not null ||
		       applicationData.Element(Email + "Cc") is not null ||
		       applicationData.Element(Email2 + "Bcc") is not null ||
		       applicationData.Element(Email + "Subject") is not null ||
		       applicationData.Element(AirSyncBase + "Body") is not null ||
		       applicationData.Element(AirSyncBase + "Attachments") is not null;
	}

	/// <summary>
	///   Builds the complete draft MIME for a client's ApplicationData, merged onto the stored
	///   original when one is provided (16.x rewrite) — DraftMessageBuilder is the EAS half, so
	///   it runs here, host-side; the store receives finished bytes.
	/// </summary>
	private async Task<byte[]> BuildDraftBytesAsync(
		XElement applicationData, MimeMessage? original, CancellationToken ct)
	{
		MimeMessage draft = DraftMessageBuilder.Build(applicationData, original, _session.MailAddress);
		using MemoryStream buffer = new();
		await draft.WriteToAsync(buffer, ct);
		return buffer.ToArray();
	}

	private async Task<string> UpdateMailAsync(
		IMailStore mail, string folderKey, string itemKey, XElement applicationData,
		string? ackedRevision, bool isDraftsFolder, CancellationToken ct)
	{
		FolderKey folder = new(folderKey);
		ItemKey item = new(itemKey);

		if (HasDraftContent(applicationData))
		{
			// A content-bearing Change outside Drafts must be refused (AGENTS.md: Sync Add/Change
			// of Email is allowed ONLY in the Drafts folder) — falling through to the flags patch
			// would silently discard the edit while still reporting Status 1 to the client.
			if (!isDraftsFolder)
				throw new BackendException("Changing mail content via Sync is only supported in the Drafts folder.");

			// 16.x draft rewrite: merge onto the stored original (gone = merge-from-nothing, so a
			// retry after the store's delete-first fault window still converges), then hand the
			// store the finished replacement. The returned key is informational — the caller keeps
			// the snapshot keyed on the OLD item so the next diff re-identifies as Delete+Add.
			MimeMessage? original = null;
			ReadOnlyMemory<byte>? raw = await _session.Mailbox.GetRawMessageAsync(folder, item, ct);
			if (raw is { } rawBytes)
			{
				using MemoryStream stream = new(rawBytes.ToArray());
				original = await MimeMessage.LoadAsync(stream, ct);
			}

			(ItemKey _, ItemRevision revision) = await mail.ReplaceDraftAsync(folder, item, new MailItem
			{
				Rfc822 = await BuildDraftBytesAsync(applicationData, original, ct),
				Flags = new MailFlags { Draft = true }
			}, ct);
			return revision.Value;
		}

		// The everyday mail Change: a presence-carried flags/categories patch — each field
		// applies only when the client actually sent its element.
		MailFlagsPatch patch = new();
		string? read = applicationData.Element(Email + "Read")?.Value;
		if (read is not null)
			patch = patch with { Read = read == "1" };
		if (applicationData.Element(Email + "Flag") is { } flag)
			// MS-ASEMAIL Flag: Status 2 = flagged; 1 (complete) and 0/empty (cleared) both clear —
			// today's deliberately lossy bool mapping.
			patch = patch with { Flagged = flag.Element(Email + "Status")?.Value == "2" };
		if (applicationData.Element(Email + "Categories") is { } categories)
			patch = patch with
			{
				Categories = Optional<IReadOnlyList<string>>.Of(
					categories.Elements(Email + "Category").Select(c => c.Value).ToList())
			};

		ItemRevision? expected = ackedRevision is { Length: > 0 } acked ? new ItemRevision(acked) : null;
		ItemRevision newRevision = await mail.UpdateFlagsAsync(folder, item, patch, expected, ct);
		return newRevision.Value;
	}

	// ---------- payload-text plumbing ----------

	/// <summary>The current payload text as the store holds it; null when the item vanished.</summary>
	private async Task<string?> GetPayloadTextAsync(FolderKey folder, ItemKey item, CancellationToken ct)
	{
		return Store switch
		{
			ICalendarStore calendar => (await calendar.GetItemAsync(folder, item, ct))?.ICalendar,
			ITaskStore tasks => (await tasks.GetItemAsync(folder, item, ct))?.ICalendar,
			IContactStore contacts => (await contacts.GetItemAsync(folder, item, ct))?.VCard,
			_ => throw new BackendException($"The {EasClass} store carries no payload text.")
		};
	}

	/// <summary>
	///   The ghosting merge for the payload-text classes: absent elements keep the stored
	///   payload's values (the converters preserve unmanaged properties), and the result is
	///   always a COMPLETE payload.
	/// </summary>
	private string MergePayload(XElement applicationData, string? existing)
	{
		return Store switch
		{
			ICalendarStore => MergeCalendar(applicationData, existing),
			ITaskStore => MergeTask(applicationData, existing),
			IContactStore => MergeContact(applicationData, existing),
			_ => throw new BackendException($"The {EasClass} store carries no payload text.")
		};
	}

	private Task<ItemRevision> UpdatePayloadAsync(
		FolderKey folder, ItemKey item, string merged, ItemRevision? expected, CancellationToken ct)
	{
		return Store switch
		{
			ICalendarStore calendar => calendar.UpdateItemAsync(
				folder, item, new CalendarItem { ICalendar = merged }, expected, ct),
			ITaskStore tasks => tasks.UpdateItemAsync(
				folder, item, new TaskItem { ICalendar = merged }, expected, ct),
			IContactStore contacts => contacts.UpdateItemAsync(
				folder, item, new ContactItem { VCard = merged }, expected, ct),
			_ => throw new BackendException($"The {EasClass} store carries no payload text.")
		};
	}

	private string MergeCalendar(XElement applicationData, string? existingIcs)
	{
		string uid = TryExtractUid(existingIcs, CalendarConverter.ExtractUid) ?? Guid.NewGuid().ToString();
		// Attachment cap: Auto semantics (1 MiB) for every backend while conversion is host-side —
		// the provider-owned CalendarAttachments knob is Phase 4's knob-inventory item (recorded
		// as a Phase 3 deviation in the design document).
		return CalendarConverter.FromApplicationData(
			applicationData, uid, existingIcs, CalendarAttachmentPolicy.CapBytes(null), ActingIdentity);
	}

	private static string MergeTask(XElement applicationData, string? existingIcs)
	{
		string uid = TryExtractUid(existingIcs, TasksConverter.ExtractUid) ?? Guid.NewGuid().ToString();
		return TasksConverter.FromApplicationData(applicationData, uid, existingIcs);
	}

	private static string MergeContact(XElement applicationData, string? existingVcard)
	{
		string uid = TryExtractUid(existingVcard, ContactConverter.ExtractUid) ?? Guid.NewGuid().ToString();
		return ContactConverter.FromApplicationData(applicationData, uid, existingVcard);
	}

	private static string? TryExtractUid(string? payload, Func<string, string?> extract)
	{
		if (payload is null)
			return null;
		try
		{
			return extract(payload);
		}
		catch (Exception)
		{
			return null; // malformed stored payload — mint a fresh uid rather than fail the write
		}
	}

	private static bool OverTextBound(string payload)
	{
		return payload.Length > MaxTextPayloadChars;
	}
}

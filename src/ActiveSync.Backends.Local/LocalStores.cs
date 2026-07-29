using ActiveSync.Backends.Common.Converters;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Security;
using ActiveSync.Core.State;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ActiveSync.Backends.Local;

/// <summary>Contacts served from the gateway database (vCard rows); GAL search included.</summary>
public sealed class LocalContactStore(
	ISyncDbContextFactory dbFactory,
	LocalChangeNotifier notifier,
	int userId,
	LocalContentProtector protector,
	ILogger logger)
	: LocalStoreBase<ContactItem>(dbFactory, notifier, userId, protector), IContactStore, IDirectoryOperations
{
	public const string BackendKey = KeyPrefix + "contacts";

	protected override string Collection => "contacts";
	protected override string FolderDisplayName => "Contacts";
	protected override FolderType FolderType => FolderType.Contacts;

	public async Task<IReadOnlyList<GalEntry>> SearchGalAsync(
		string query, int maxResults, GalPhotoRequest? photos, CancellationToken ct)
	{
		await using SyncDbContext db = DbFactory.CreateDbContext();
		List<GalEntry> results = new();
		int photosGranted = 0;
		// Stream the rows (AsAsyncEnumerable) so the maxResults break stops pulling and
		// decrypting rows once enough matches are found, instead of ToListAsync materializing the
		// entire collection up front; AsNoTracking because this is a pure read; and parse each card
		// ONCE via BuildGalEntry (match, fields and photo in a single pass).
		await foreach (string stored in Rows(db).AsNoTracking().Select(i => i.Content)
			               .AsAsyncEnumerable().WithCancellation(ct).ConfigureAwait(false))
		{
			string vcf;
			try
			{
				vcf = Protector.Unprotect(stored, UserId, Collection);
			}
			catch (BackendException ex)
			{
				// One row written under a rotated key (or otherwise undecryptable) must not
				// fail the ENTIRE GAL search — skip it and keep going, the way a malformed vCard
				// already costs one contact instead of the whole result set.
				logger.LogWarning(ex, "Skipping an undecryptable contact row during GAL search for user {UserId}", UserId);
				continue;
			}

			GalEntry? gal = ContactPayload.BuildGalEntry(
				vcf, query, photos is not null, photos?.MaxSizeBytes,
				photosGranted >= (photos?.MaxCount ?? int.MaxValue), out bool granted);
			if (gal is null)
				continue;
			if (granted)
				photosGranted++;
			results.Add(gal);
			// Checked AFTER adding — the await foreach above has already pulled/materialized
			// the CURRENT row by the time the loop body runs, so testing this BEFORE the add (the
			// original check-before-add shape) let the enumerator advance one row past what maxResults needs.
			if (results.Count >= maxResults)
				break;
		}

		return results;
	}

	// The stored text IS the payload (round-trip fidelity: what the host hands over is what it
	// gets back), so both directions are the identity.
	protected override ContactItem ParseContent(string content)
	{
		return new ContactItem { VCard = content };
	}

	protected override string BuildContent(ContactItem item, string? existingContent, string uid)
	{
		return item.VCard;
	}

	protected override string? ExtractUidCore(string content)
	{
		try
		{
			return ContactPayload.ExtractUid(content);
		}
		catch (Exception)
		{
			return null; // unparsable → keep the generated uid
		}
	}
}

/// <summary>Calendar served from the gateway database (iCalendar VEVENT rows).</summary>
public sealed class LocalCalendarStore(
	ISyncDbContextFactory dbFactory,
	LocalChangeNotifier notifier,
	int userId,
	LocalContentProtector protector,
	string gatewayLogin,
	string partStatIdentity,
	ILogger logger)
	: LocalStoreBase<CalendarItem>(dbFactory, notifier, userId, protector),
		ICalendarStore, IMeetingOperations, ICalendarAttachmentSource, IFreeBusySource
{
	public const string BackendKey = KeyPrefix + "calendar";

	protected override string Collection => "calendar";
	protected override string FolderDisplayName => "Calendar";
	protected override FolderType FolderType => FolderType.Calendar;

	public async Task<ItemKey?> RespondToMeetingAsync(
		FolderKey calendar, string eventUid, MeetingResponseKind response, CancellationToken ct)
	{
		// Bounded retry mirroring LocalStoreBase.UpdateItemAsync — another device may bump
		// the same row between our read and save, so each attempt re-reads the latest content
		// and re-applies the response instead of losing it to a lost concurrency race.
		const int maxAttempts = 4;
		for (int attempt = 1; ; attempt++)
		{
			await using SyncDbContext db = DbFactory.CreateDbContext();
			LocalItem? row = await Rows(db).FirstOrDefaultAsync(i => i.Uid == eventUid, ct).ConfigureAwait(false);
			if (row is null)
				return null;
			string plain = Protector.Unprotect(row.Content, UserId, Collection);
			// partStatIdentity = mail address ?? gateway login; the row scope and encryption AAD
			// above stay on the gateway UserId.
			string? updated = CalendarPayload.SetPartStat(plain, (int)response, partStatIdentity);
			if (updated is null)
				return new ItemKey(row.Id.ToString());

			row.Content = Protector.Protect(updated, UserId, Collection);
			row.Version++;
			row.LastModifiedUtc = DateTime.UtcNow;
			try
			{
				await db.SaveChangesAsync(ct).ConfigureAwait(false);
			}
			catch (DbUpdateConcurrencyException) when (attempt < maxAttempts)
			{
				continue;
			}
			catch (DbUpdateConcurrencyException ex)
			{
				throw new BackendException(
					$"Local calendar item {eventUid} is being modified concurrently; retry.", ex);
			}

			NotifyChanged(); // wake waiting Pings, like every other local write path
			return new ItemKey(row.Id.ToString());
		}
	}

	public Task<bool> ShouldSendInvitationsAsync(CancellationToken ct)
	{
		// Local storage has no server-side scheduling — the gateway is the only party that
		// can invite anyone, so it always does (there is no knob for the local store).
		return Task.FromResult(true);
	}

	// The stored text IS the payload — identity in both directions (round-trip fidelity).
	protected override CalendarItem ParseContent(string content)
	{
		return new CalendarItem { ICalendar = content };
	}

	protected override string BuildContent(CalendarItem item, string? existingContent, string uid)
	{
		return item.ICalendar;
	}

	protected override string? ExtractUidCore(string content)
	{
		try
		{
			return CalendarPayload.ExtractUid(content);
		}
		catch (Exception)
		{
			return null; // unparsable → keep the generated uid
		}
	}

	/// <summary>ItemOperations fetch of an inline event attachment (the Nth ATTACH of the stored event).</summary>
	public async Task<BackendAttachment?> GetEventAttachmentAsync(
		FolderKey folder, ItemKey item, int index, CancellationToken ct)
	{
		await using SyncDbContext db = DbFactory.CreateDbContext();
		LocalItem? row = await FindAsync(db, item.Value, ct).ConfigureAwait(false);
		if (row is null)
			return null;
		string ics = Protector.Unprotect(row.Content, UserId, Collection);
		return CalendarPayload.ExtractAttachment(ics, index);
	}

	/// <summary>
	///   Free/busy from the stored events — the local store only ever holds the requesting
	///   user's own calendar, so any other target has no data here (status 163 upstream).
	/// </summary>
	public async Task<IReadOnlyList<BusyPeriod>?> GetBusyPeriodsAsync(
		string targetAddress, DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
	{
		if (!targetAddress.Equals(partStatIdentity, StringComparison.OrdinalIgnoreCase) &&
		    !targetAddress.Equals(gatewayLogin, StringComparison.OrdinalIgnoreCase))
			return null;

		await using SyncDbContext db = DbFactory.CreateDbContext();
		// AsNoTracking — a pure read; no need to snapshot every event into the change tracker.
		List<string> contents = await Rows(db).AsNoTracking().Select(i => i.Content).ToListAsync(ct)
			.ConfigureAwait(false);
		List<string> plaintext = new(contents.Count);
		foreach (string stored in contents)
			try
			{
				plaintext.Add(Protector.Unprotect(stored, UserId, Collection));
			}
			catch (BackendException ex)
			{
				// AGENTS.md: "a free/busy failure must never fail the whole
				// ResolveRecipients" — one undecryptable event must not sink the whole answer.
				logger.LogWarning(ex, "Skipping an undecryptable calendar row during free/busy for user {UserId}", UserId);
			}

		return CalendarPayload.BusyPeriodsFromEvents(plaintext, start.UtcDateTime, end.UtcDateTime);
	}

	protected override DateTime? ExtractItemDate(string content)
	{
		try
		{
			// Same obsolete-surface rationale as CalendarConverter: EAS 14.1 carries at
			// most one recurrence rule, so the single-value members match the protocol.
#pragma warning disable CS0618
			Calendar? calendar = Calendar.Load(content);
			CalendarEvent? master = calendar?.Events.FirstOrDefault(e => e.RecurrenceId is null)
			                        ?? calendar?.Events.FirstOrDefault();
			if (master is null)
				return null;
			// Recurring events must always stay in the filter window.
			if (master.RecurrenceRules is { Count: > 0 })
				return null;
			return master.Start?.AsUtc;
#pragma warning restore CS0618
		}
		catch (Exception)
		{
			return null; // unparsable → never filter it out
		}
	}
}

/// <summary>
///   Tasks served from the gateway database (iCalendar VTODO rows); used when no
///   CalDAV tasks collection is configured/available.
/// </summary>
public sealed class LocalTaskStore(
	ISyncDbContextFactory dbFactory,
	LocalChangeNotifier notifier,
	int userId,
	LocalContentProtector protector)
	: LocalStoreBase<TaskItem>(dbFactory, notifier, userId, protector), ITaskStore
{
	public const string BackendKey = KeyPrefix + "tasks";

	protected override string Collection => "tasks";
	protected override string FolderDisplayName => "Tasks";
	protected override FolderType FolderType => FolderType.Tasks;

	// The stored text IS the payload — identity in both directions (round-trip fidelity).
	protected override TaskItem ParseContent(string content)
	{
		return new TaskItem { ICalendar = content };
	}

	protected override string BuildContent(TaskItem item, string? existingContent, string uid)
	{
		return item.ICalendar;
	}

	protected override string? ExtractUidCore(string content)
	{
		try
		{
			return TaskPayload.ExtractUid(content);
		}
		catch (Exception)
		{
			return null; // unparsable → keep the generated uid
		}
	}
}

/// <summary>
///   Notes served from the gateway database. The payload is the typed <see cref="NoteItem" />;
///   at rest it is stored as iCalendar VJOURNAL text — a PRIVATE storage convention
///   (<see cref="NoteJournalMapper" />), kept so existing sealed rows need no migration.
///   Always local — the DAV backends carry no notes.
/// </summary>
public sealed class LocalNotesStore(
	ISyncDbContextFactory dbFactory,
	LocalChangeNotifier notifier,
	int userId,
	LocalContentProtector protector)
	: LocalStoreBase<NoteItem>(dbFactory, notifier, userId, protector), INotesStore
{
	public const string BackendKey = KeyPrefix + "notes";

	protected override string Collection => "notes";
	protected override string FolderDisplayName => "Notes";
	protected override FolderType FolderType => FolderType.Notes;

	protected override NoteItem? ParseContent(string content)
	{
		return NoteJournalMapper.ToNote(content);
	}

	protected override string BuildContent(NoteItem item, string? existingContent, string uid)
	{
		// Merging onto the existing journal preserves unmapped VJOURNAL properties (DTSTART,
		// custom props) across an edit.
		return NoteJournalMapper.ToJournal(item, existingContent, uid);
	}

	protected override string? ExtractUidCore(string content)
	{
		return NoteJournalMapper.ExtractUid(content);
	}
}

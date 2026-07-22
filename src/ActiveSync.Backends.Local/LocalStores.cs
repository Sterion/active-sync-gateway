using System.Xml.Linq;
using ActiveSync.Backends.Converters;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Security;
using ActiveSync.Core.State;
using ActiveSync.Protocol;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Microsoft.EntityFrameworkCore;

namespace ActiveSync.Backends.Local;

/// <summary>Contacts served from the gateway database (vCard rows); GAL search included.</summary>
public sealed class LocalContactStore(
	ISyncDbContextFactory dbFactory,
	LocalChangeNotifier notifier,
	BackendCredentials credentials,
	LocalContentProtector protector)
	: LocalStoreBase(dbFactory, notifier, credentials, protector), IContactOperations
{
	public const string BackendKey = KeyPrefix + "contacts";

	protected override string Collection => "contacts";
	protected override string FolderDisplayName => "Contacts";
	protected override int FolderType => EasFolderType.Contacts;
	public override string EasClass => Protocol.EasClass.Contacts;

	public async Task<IReadOnlyList<IReadOnlyList<XElement>>> SearchGalAsync(
		string query, int maxResults, GalPhotoRequest? photos, CancellationToken ct)
	{
		await using SyncDbContext db = DbFactory.CreateDbContext();
		List<IReadOnlyList<XElement>> results = new();
		int photosGranted = 0;
		// D19: stream the rows (AsAsyncEnumerable) so the maxResults break stops pulling and
		// decrypting rows once enough matches are found, instead of ToListAsync materializing the
		// entire collection up front; AsNoTracking because this is a pure read; and parse each card
		// ONCE via BuildGalEntry rather than three times (ToGalEntry + AppendGalPicture re-parsed).
		await foreach (string stored in Rows(db).AsNoTracking().Select(i => i.Content)
			               .AsAsyncEnumerable().WithCancellation(ct).ConfigureAwait(false))
		{
			if (results.Count >= maxResults)
				break;
			string vcf = Protector.Unprotect(stored, Credentials.UserName, "contacts");
			List<XElement>? gal = ContactConverter.BuildGalEntry(
				vcf, query, photos is not null, photos?.MaxSizeBytes,
				photosGranted >= (photos?.MaxCount ?? int.MaxValue), out bool granted);
			if (gal is null)
				continue;
			if (granted)
				photosGranted++;
			results.Add(gal);
		}

		return results;
	}

	protected override IReadOnlyList<XElement>? ToApplicationData(string content, BodyPreference bodyPreference)
	{
		return ContactConverter.ToApplicationData(content, bodyPreference);
	}

	protected override string BuildContent(XElement applicationData, string uid, string? existingContent)
	{
		return ContactConverter.FromApplicationData(applicationData, uid, existingContent);
	}
}

/// <summary>Calendar served from the gateway database (iCalendar VEVENT rows).</summary>
public sealed class LocalCalendarStore(
	ISyncDbContextFactory dbFactory,
	LocalChangeNotifier notifier,
	BackendCredentials credentials,
	LocalContentProtector protector,
	string partStatIdentity)
	: LocalStoreBase(dbFactory, notifier, credentials, protector),
		ICalendarOperations, ICalendarAttachmentSource, IFreeBusySource
{
	public const string BackendKey = KeyPrefix + "calendar";

	protected override string Collection => "calendar";
	protected override string FolderDisplayName => "Calendar";
	protected override int FolderType => EasFolderType.Calendar;
	public override string EasClass => Protocol.EasClass.Calendar;

	public async Task<string?> RespondToMeetingAsync(
		string calendarFolderBackendKey, string eventUid, int userResponse, CancellationToken ct)
	{
		await using SyncDbContext db = DbFactory.CreateDbContext();
		LocalItem? row = await Rows(db).FirstOrDefaultAsync(i => i.Uid == eventUid, ct).ConfigureAwait(false);
		if (row is null)
			return null;
		string plain = Protector.Unprotect(row.Content, Credentials.UserName, "calendar");
		// partStatIdentity = mail address ?? gateway login; the row scope and encryption AAD
		// above stay on the gateway login (Credentials).
		string? updated = CalendarConverter.SetPartStat(plain, userResponse, partStatIdentity);
		if (updated is not null)
		{
			row.Content = Protector.Protect(updated, Credentials.UserName, "calendar");
			row.Version++;
			row.LastModifiedUtc = DateTime.UtcNow;
			await db.SaveChangesAsync(ct).ConfigureAwait(false);
			NotifyChanged(); // wake waiting Pings, like every other local write path
		}

		return row.Id.ToString();
	}

	public async Task<string?> GetRawEventAsync(string folderBackendKey, string itemKey, CancellationToken ct)
	{
		await using SyncDbContext db = DbFactory.CreateDbContext();
		LocalItem? row = await FindAsync(db, itemKey, ct).ConfigureAwait(false);
		return row is null ? null : Protector.Unprotect(row.Content, Credentials.UserName, "calendar");
	}

	public Task<bool> ShouldSendInvitationsAsync(CancellationToken ct)
	{
		// Local storage has no server-side scheduling — the gateway is the only party that
		// can invite anyone, so it always does (there is no knob for the local store).
		return Task.FromResult(true);
	}

	protected override IReadOnlyList<XElement>? ToApplicationData(string content, BodyPreference bodyPreference)
	{
		return CalendarConverter.ToApplicationData(content, bodyPreference);
	}

	protected override string BuildContent(XElement applicationData, string uid, string? existingContent)
	{
		// The local store has no DAV server to protect — Auto semantics (1 MiB cap).
		return CalendarConverter.FromApplicationData(applicationData, uid, existingContent,
			CalendarAttachmentPolicy.CapBytes(null), partStatIdentity);
	}

	/// <summary>ItemOperations fetch of an inline event attachment (calatt:: FileReference).</summary>
	public async Task<BackendAttachment?> GetEventAttachmentAsync(
		string folderBackendKey, string itemKey, int index, CancellationToken ct)
	{
		await using SyncDbContext db = DbFactory.CreateDbContext();
		LocalItem? row = await FindAsync(db, itemKey, ct).ConfigureAwait(false);
		if (row is null)
			return null;
		string ics = Protector.Unprotect(row.Content, Credentials.UserName, "calendar");
		return CalendarConverter.ExtractAttachment(ics, index);
	}

	/// <summary>
	///   Free/busy from the stored events — the local store only ever holds the requesting
	///   user's own calendar, so any other target has no data here (status 163 upstream).
	/// </summary>
	public async Task<IReadOnlyList<BusyPeriod>?> GetBusyPeriodsAsync(
		string targetAddress, DateTime startUtc, DateTime endUtc, CancellationToken ct)
	{
		if (!targetAddress.Equals(partStatIdentity, StringComparison.OrdinalIgnoreCase) &&
		    !targetAddress.Equals(Credentials.UserName, StringComparison.OrdinalIgnoreCase))
			return null;

		await using SyncDbContext db = DbFactory.CreateDbContext();
		// D19: AsNoTracking — a pure read; no need to snapshot every event into the change tracker.
		List<string> contents = await Rows(db).AsNoTracking().Select(i => i.Content).ToListAsync(ct)
			.ConfigureAwait(false);
		return CalendarConverter.BusyPeriodsFromEvents(
			contents.Select(c => Protector.Unprotect(c, Credentials.UserName, "calendar")),
			startUtc, endUtc);
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
	BackendCredentials credentials,
	LocalContentProtector protector)
	: LocalStoreBase(dbFactory, notifier, credentials, protector)
{
	public const string BackendKey = KeyPrefix + "tasks";

	protected override string Collection => "tasks";
	protected override string FolderDisplayName => "Tasks";
	protected override int FolderType => EasFolderType.Tasks;
	public override string EasClass => Protocol.EasClass.Tasks;

	protected override IReadOnlyList<XElement>? ToApplicationData(string content, BodyPreference bodyPreference)
	{
		return TasksConverter.ToApplicationData(content, bodyPreference);
	}

	protected override string BuildContent(XElement applicationData, string uid, string? existingContent)
	{
		return TasksConverter.FromApplicationData(applicationData, uid, existingContent);
	}
}

/// <summary>
///   Notes served from the gateway database (iCalendar VJOURNAL rows). Always local —
///   the DAV backends carry no notes.
/// </summary>
public sealed class LocalNotesStore(
	ISyncDbContextFactory dbFactory,
	LocalChangeNotifier notifier,
	BackendCredentials credentials,
	LocalContentProtector protector)
	: LocalStoreBase(dbFactory, notifier, credentials, protector)
{
	public const string BackendKey = KeyPrefix + "notes";

	protected override string Collection => "notes";
	protected override string FolderDisplayName => "Notes";
	protected override int FolderType => EasFolderType.Notes;
	public override string EasClass => Protocol.EasClass.Notes;

	protected override IReadOnlyList<XElement>? ToApplicationData(string content, BodyPreference bodyPreference)
	{
		return NotesConverter.ToApplicationData(content, bodyPreference);
	}

	protected override string BuildContent(XElement applicationData, string uid, string? existingContent)
	{
		return NotesConverter.FromApplicationData(applicationData, uid, existingContent);
	}
}

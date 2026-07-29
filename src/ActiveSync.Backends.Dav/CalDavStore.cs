using System.Xml.Linq;
using ActiveSync.Backends.Common.Converters;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Protocol;
using Microsoft.Extensions.Logging;

namespace ActiveSync.Backends.Dav;

/// <summary>
///   Calendar content store over CalDAV. Item keys are server hrefs; revisions are ETags. The
///   payload is the stored iCalendar document verbatim — the store neither reads nor writes EAS
///   XML (the host owns that conversion and hands over complete documents).
/// </summary>
public sealed class CalDavStore(
	WebDavClient dav,
	DavServerOptions options,
	BackendCredentials credentials,
	string partStatIdentity,
	ILogger logger,
	int pollSeconds,
	IReadOnlyList<SharedCollection>? sharedCollections = null)
	: DavStoreBase<CalendarItem>(dav, options, credentials, logger, pollSeconds),
		ICalendarStore, IMeetingOperations, ICalendarAttachmentSource, IFreeBusySource, IReadOnlyCollectionSource
{
	public const string KeyPrefix = "caldav:";

	private readonly IReadOnlyList<SharedCollection> _sharedCollections = sharedCollections ?? [];

	/// <summary>Whether a folder maps to a shared collection granted read-only.</summary>
	/// <param name="folder">The folder to test.</param>
	/// <returns><c>true</c> when the folder is granted read-only.</returns>
	public bool IsReadOnlyCollection(FolderKey folder)
	{
		string href = FromBackendKey(folder.Value);
		return _sharedCollections.Any(c => c.ReadOnly && SharedHrefEquals(c.Href, href));
	}

	/// <summary>
	///   Grant-vs-server href comparison: servers canonicalize hrefs (percent-encoding,
	///   case), while grants hold whatever the operator typed — compare leniently.
	/// </summary>
	private static bool SharedHrefEquals(string a, string b)
	{
		return Uri.UnescapeDataString(a).TrimEnd('/')
			.Equals(Uri.UnescapeDataString(b).TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
	}

	protected override string Prefix => KeyPrefix;
	protected override string MediaType => "text/calendar";
	protected override string FileExtension => ".ics";
	protected override string WellKnownPath => "/.well-known/caldav";
	protected override XName HomeSetProperty => DavNs.CalDav + "calendar-home-set";
	protected override string? HomeSetDiscoveryLogLabel => "CalDAV";
	protected override string ProtocolLabel => "CalDAV";
	protected override string ItemNoun => "event";
	protected override string ItemNounPlural => "events";
	protected override string CollectionKindPlural => "calendars";
	protected override string CtagLabel => "CalDAV";

	// ---------- IMeetingOperations ----------

	/// <summary>
	///   Updates the acting user's PARTSTAT on the stored event and reports the calendar item that
	///   holds it (the gateway mails the iTIP reply itself).
	/// </summary>
	/// <param name="calendar">The calendar folder holding the event.</param>
	/// <param name="eventUid">The event's iCalendar UID.</param>
	/// <param name="response">The user's answer.</param>
	/// <param name="ct">Cancellation token for the backend round-trip.</param>
	/// <returns>The calendar item holding the event, or <c>null</c> when none was found.</returns>
	public async Task<ItemKey?> RespondToMeetingAsync(
		FolderKey calendar, string eventUid, MeetingResponseKind response, CancellationToken ct)
	{
		// Locate the event by UID in the calendar collection.
		string collection = FromBackendKey(calendar.Value);
		string? href = (await FindByUidAsync(collection, eventUid, ct).ConfigureAwait(false))?.Href;
		if (href is null)
			return null;

		(string Content, string? ETag)? existing = await Dav.GetAsync(href, ct).ConfigureAwait(false);
		if (existing is null)
			return null;

		// partStatIdentity = the user's mail address (falls back to the gateway login) —
		// Credentials.UserName is the DAV backend login, which need not match any attendee.
		// MeetingResponseKind's values are the MS-ASCMD UserResponse wire values the converter takes.
		string? updated = CalendarConverter.SetPartStat(existing.Value.Content, (int)response, partStatIdentity);
		if (updated is not null)
			await Dav.PutAsync(href, updated, "text/calendar", existing.Value.ETag, false, ct)
				.ConfigureAwait(false);
		return new ItemKey(href);
	}

	private bool? _serverSchedules;

	/// <summary>
	///   Whether the gateway should mail iMIP invitations itself: Auto probes the server for
	///   RFC 6638 implicit scheduling (a scheduling server invites on its own, and double invites
	///   are worse than none).
	/// </summary>
	/// <param name="ct">Cancellation token for the backend round-trip.</param>
	/// <returns><c>true</c> when the gateway should send invitation mail itself.</returns>
	public async Task<bool> ShouldSendInvitationsAsync(CancellationToken ct)
	{
		switch (Options.SendInvitations.ToLowerInvariant())
		{
			case "off":
				return false;
			case "on":
				return true;
		}

		// Auto: probe once per store whether the server schedules on its own — a
		// scheduling server sends its own invitations, and double invites are worse than
		// none. Probe failures mean "server does not schedule" (the gateway mails).
		if (_serverSchedules is { } cached)
			return !cached;
		bool schedules = await ProbeServerSchedulingAsync(ct).ConfigureAwait(false);
		_serverSchedules = schedules;
		Logger.LogInformation(
			"CalDAV schedule-outbox probe: {Result} — gateway iMIP invitations {State} (SendInvitations=Auto)",
			schedules ? "present" : "absent", schedules ? "disabled" : "enabled");
		return !schedules;
	}

	private async Task<bool> ProbeServerSchedulingAsync(CancellationToken ct)
	{
		try
		{
			// Primary signal: the "calendar-auto-schedule" compliance class (RFC 6638 §8.1)
			// on the user's home set — servers doing implicit scheduling mail invitations on
			// every PUT (verified live: Stalwart and Axigen both do, and Stalwart exposes NO
			// schedule-outbox-URL, which is why the outbox probe is only the fallback).
			string capabilities = await Dav
				.GetDavCapabilitiesAsync(await GetHomeSetAsync(ct).ConfigureAwait(false), ct)
				.ConfigureAwait(false);
			if (capabilities.Contains("calendar-auto-schedule", StringComparison.Ordinal))
				return true;

			string? principal = await Dav.GetPropertyAsync("/", DavNs.D + "current-user-principal", ct)
				.ConfigureAwait(false);
			if (string.IsNullOrWhiteSpace(principal))
				return false;
			string? outbox = await Dav.GetPropertyAsync(principal, DavNs.CalDav + "schedule-outbox-URL", ct)
				.ConfigureAwait(false);
			return !string.IsNullOrWhiteSpace(outbox);
		}
		catch (BackendException)
		{
			return false;
		}
	}

	// The stored document IS the payload — identity in both directions (round-trip fidelity).
	protected override CalendarItem ToItem(string content)
	{
		return new CalendarItem { ICalendar = content };
	}

	protected override string PayloadOf(CalendarItem item)
	{
		return item.ICalendar;
	}

	/// <summary>ItemOperations fetch of an inline event attachment (the Nth ATTACH of the stored event).</summary>
	/// <param name="folder">The calendar folder holding the event.</param>
	/// <param name="item">The event holding the attachment.</param>
	/// <param name="index">The attachment's position among the event's ATTACH properties.</param>
	/// <param name="ct">Cancellation token for the backend round-trip.</param>
	/// <returns>The attachment, or <c>null</c> when the event or attachment no longer exists.</returns>
	public async Task<BackendAttachment?> GetEventAttachmentAsync(
		FolderKey folder, ItemKey item, int index, CancellationToken ct)
	{
		(string Content, string? ETag)? fetched = await Dav.GetAsync(item.Value, ct).ConfigureAwait(false);
		return fetched is null ? null : CalendarConverter.ExtractAttachment(fetched.Value.Content, index);
	}

	/// <summary>
	///   Free/busy via CALDAV:free-busy-query. The requesting user's own availability always
	///   works; another principal's only when a HomeSetPath template can address their
	///   collections AND the server grants read access (Stalwart answers 403 → null →
	///   per-recipient Availability status 163). RFC 6638 scheduling is not implemented —
	///   neither supported test backend offers a schedule-outbox.
	/// </summary>
	/// <param name="targetAddress">The recipient's address to query.</param>
	/// <param name="start">Start of the queried range.</param>
	/// <param name="end">End of the queried range.</param>
	/// <param name="ct">Cancellation token for the backend round-trip.</param>
	/// <returns>The recipient's busy periods, an empty list if free, or <c>null</c> if no data is available.</returns>
	public async Task<IReadOnlyList<BusyPeriod>?> GetBusyPeriodsAsync(
		string targetAddress, DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
	{
		bool self = targetAddress.Equals(partStatIdentity, StringComparison.OrdinalIgnoreCase) ||
		            targetAddress.Equals(Credentials.UserName, StringComparison.OrdinalIgnoreCase);
		List<string> collections = new();
		if (self)
		{
			// A folder that is itself a share (granted to this user, not owned by them) must
			// not be folded into the user's OWN availability — ResolveRecipients would otherwise
			// report the user busy whenever a colleague's/team calendar shared to them is busy.
			foreach (BackendFolder folder in await ListFoldersAsync(ct).ConfigureAwait(false))
			{
				string href = FromBackendKey(folder.Key.Value);
				if (_sharedCollections.Any(c => SharedHrefEquals(c.Href, href)))
					continue;
				collections.Add(href);
			}
		}
		else if (!string.IsNullOrEmpty(Options.HomeSetPath))
		{
			string home = DavDiscovery.ExpandTemplate(Options.HomeSetPath, targetAddress);
			try
			{
				XElement body = new(DavNs.D + "propfind",
					new XElement(DavNs.D + "prop", new XElement(DavNs.D + "resourcetype")));
				foreach (DavResource resource in await Dav.PropfindAsync(home, 1, body, ct).ConfigureAwait(false))
					if (resource.Propstat.Descendants(DavNs.D + "resourcetype").FirstOrDefault()?
						    .Element(DavNs.CalDav + "calendar") is not null)
						collections.Add(resource.Href);
			}
			catch (BackendException)
			{
				return null; // the other principal's collections are not ours to read
			}
		}
		else
		{
			return null;
		}

		XElement query = new(DavNs.CalDav + "free-busy-query",
			new XElement(DavNs.CalDav + "time-range",
				new XAttribute("start", EasDateTime.ToCompact(start.UtcDateTime)),
				new XAttribute("end", EasDateTime.ToCompact(end.UtcDateTime))));
		List<BusyPeriod> result = new();
		bool anyData = false;
		foreach (string collection in collections)
		{
			string? ics = await Dav.ReportRawAsync(collection, 1, query, ct).ConfigureAwait(false);
			if (ics is null)
				continue;
			anyData = true;
			result.AddRange(CalendarConverter.ParseFreeBusy(ics));
		}

		return anyData ? result : null;
	}

	protected override string? ExtractUid(string content)
	{
		return CalendarConverter.ExtractUid(content);
	}

	protected override XElement BuildUidQueryBody(string uid)
	{
		return new XElement(DavNs.CalDav + "calendar-query",
			new XElement(DavNs.D + "prop", new XElement(DavNs.D + "getetag")),
			new XElement(DavNs.CalDav + "filter",
				new XElement(DavNs.CalDav + "comp-filter", new XAttribute("name", "VCALENDAR"),
					new XElement(DavNs.CalDav + "comp-filter", new XAttribute("name", "VEVENT"),
						new XElement(DavNs.CalDav + "prop-filter", new XAttribute("name", "UID"),
							new XElement(DavNs.CalDav + "text-match", uid))))));
	}

	/// <inheritdoc />
	public override async Task<IReadOnlyList<BackendFolder>> ListFoldersAsync(CancellationToken ct)
	{
		string home = await GetHomeSetAsync(ct).ConfigureAwait(false);
		XElement body = new(DavNs.D + "propfind",
			new XElement(DavNs.D + "prop",
				new XElement(DavNs.D + "resourcetype"),
				new XElement(DavNs.D + "displayname"),
				new XElement(DavNs.CalDav + "supported-calendar-component-set")));
		List<DavResource> resources = await Dav.PropfindAsync(home, 1, body, ct).ConfigureAwait(false);

		List<BackendFolder> folders = new();
		bool first = true;
		// Multistatus order is server whim, and the first VEVENT collection below becomes THE
		// default calendar — sort so the pick is stable across sessions and servers.
		foreach (DavResource resource in resources.OrderBy(r => r.Href, StringComparer.OrdinalIgnoreCase))
		{
			XElement? type = resource.Propstat.Descendants(DavNs.D + "resourcetype").FirstOrDefault();
			if (type?.Element(DavNs.CalDav + "calendar") is null)
				continue;
			List<string?> components = resource.Propstat
				.Descendants(DavNs.CalDav + "supported-calendar-component-set")
				.Descendants(DavNs.CalDav + "comp")
				.Select(c => c.Attribute("name")?.Value)
				.Where(n => n is not null)
				.ToList();
			if (components.Count > 0 && !components.Contains("VEVENT"))
				continue; // e.g. VTODO-only collections
			string? name = resource.Propstat.Descendants(DavNs.D + "displayname").FirstOrDefault()?.Value;
			if (string.IsNullOrWhiteSpace(name))
				name = resource.Href.TrimEnd('/').Split('/').LastOrDefault() ?? "Calendar";
			// A collection the user also holds a share entry for is a share, not their primary
			// calendar — it must never claim the default-calendar slot.
			bool granted = _sharedCollections.Any(s => SharedHrefEquals(s.Href, resource.Href));
			folders.Add(new BackendFolder
			{
				Key = new FolderKey(ToBackendKey(resource.Href)),
				DisplayName = name,
				Type = first && !granted ? FolderType.Calendar : FolderType.UserCalendar
			});
			if (!granted)
				first = false;
		}

		// "A share never claims the default slot" is deliberate (AGENTS.md), but it has no
		// floor — a delegate account whose home set contains only granted collections would
		// otherwise get ZERO folders of type 8 (Calendar), and iOS in particular expects a default
		// calendar folder to exist. If nothing was promoted above, promote the first (already
		// href-sorted) calendar folder, preferring one that is not itself a share.
		if (folders.Count > 0 && folders.TrueForAll(f => f.Type != FolderType.Calendar))
		{
			int promoteIndex = folders.FindIndex(f =>
				!_sharedCollections.Any(s => SharedHrefEquals(s.Href, FromBackendKey(f.Key.Value))));
			if (promoteIndex < 0)
				promoteIndex = 0; // every home-set calendar is a share — fall back to the first anyway
			folders[promoteIndex] = folders[promoteIndex] with { Type = FolderType.Calendar };
		}

		// Shared collections (config + `eas share` grants): each is probed individually and
		// SKIPPED on any failure — an unreachable/revoked share must never break folder sync.
		foreach (SharedCollection shared in _sharedCollections)
		{
			if (folders.Any(f => SharedHrefEquals(FromBackendKey(f.Key.Value), shared.Href)))
				continue; // already in the user's own home set
			try
			{
				List<DavResource> probe = await Dav.PropfindAsync(shared.Href, 0, body, ct)
					.ConfigureAwait(false);
				DavResource? resource = probe.FirstOrDefault();
				XElement? type = resource?.Propstat.Descendants(DavNs.D + "resourcetype").FirstOrDefault();
				if (type?.Element(DavNs.CalDav + "calendar") is null)
				{
					Logger.LogWarning("Shared collection {Href} is not a calendar collection; skipped",
						shared.Href);
					continue;
				}

				List<string?> components = resource!.Propstat
					.Descendants(DavNs.CalDav + "supported-calendar-component-set")
					.Descendants(DavNs.CalDav + "comp")
					.Select(c => c.Attribute("name")?.Value)
					.Where(n => n is not null)
					.ToList();
				if (components.Count > 0 && !components.Contains("VEVENT"))
				{
					Logger.LogWarning("Shared collection {Href} does not carry events; skipped", shared.Href);
					continue;
				}

				// Dedupe AGAIN on the server's canonical href: the configured entry and the
				// home-set listing may spell the same collection differently (encoding, case).
				if (folders.Any(f => SharedHrefEquals(FromBackendKey(f.Key.Value), resource.Href)))
					continue;
				string? name = resource.Propstat.Descendants(DavNs.D + "displayname").FirstOrDefault()?.Value;
				if (string.IsNullOrWhiteSpace(name))
					name = shared.Href.TrimEnd('/').Split('/').LastOrDefault() ?? "Shared";
				folders.Add(new BackendFolder
				{
					Key = new FolderKey(ToBackendKey(resource.Href)),
					DisplayName = name,
					Type = FolderType.UserCalendar
				});
			}
			catch (BackendException ex)
			{
				Logger.LogWarning("Shared collection {Href} is not accessible ({Reason}); skipped",
					shared.Href, ex.Message);
			}
		}

		return folders;
	}

	/// <inheritdoc />
	public override async Task<IReadOnlyDictionary<ItemKey, ItemRevision>> GetItemRevisionsAsync(
		FolderKey folder, ContentFilter filter, CancellationToken ct)
	{
		string collection = FromBackendKey(folder.Value);
		XElement filterElement = new(DavNs.CalDav + "filter",
			new XElement(DavNs.CalDav + "comp-filter", new XAttribute("name", "VCALENDAR"),
				BuildEventFilter(filter)));
		XElement body = new(DavNs.CalDav + "calendar-query",
			new XElement(DavNs.D + "prop", new XElement(DavNs.D + "getetag")),
			filterElement);

		List<DavResource> resources = await Dav.ReportAsync(collection, 1, body, ct).ConfigureAwait(false);
		Dictionary<ItemKey, ItemRevision> map = new();
		foreach (DavResource resource in resources)
		{
			if (PathsEqual(resource.Href, collection))
				continue;
			string? etag = resource.Propstat.Descendants(DavNs.D + "getetag").FirstOrDefault()?.Value;
			if (etag is not null)
				map[new ItemKey(resource.Href)] = new ItemRevision(etag);
		}

		return map;
	}

	internal static XElement BuildEventFilter(ContentFilter filter)
	{
		// Always send a time-range: Axigen's calendar-query omits recurring events when the
		// VEVENT comp-filter carries no time-range (verified live 2026-07-17). An epoch start
		// is semantically "everything" — every event overlaps [1970, ∞) — so unfiltered syncs
		// keep their meaning on well-behaved servers too.
		DateTimeOffset since = filter.Since ?? DateTimeOffset.UnixEpoch;
		return new XElement(DavNs.CalDav + "comp-filter", new XAttribute("name", "VEVENT"),
			new XElement(DavNs.CalDav + "time-range",
				new XAttribute("start", EasDateTime.ToCompact(since.UtcDateTime))));
	}
}

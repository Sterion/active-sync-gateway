using System.Xml.Linq;
using ActiveSync.Backends.Converters;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Protocol;
using Microsoft.Extensions.Logging;

namespace ActiveSync.Backends.Dav;

/// <summary>Calendar content store over CalDAV. Item keys are server hrefs; revisions are ETags.</summary>
public sealed class CalDavStore(
	WebDavClient dav,
	DavServerOptions options,
	BackendCredentials credentials,
	string partStatIdentity,
	ILogger logger,
	IReadOnlyList<SharedCollection>? sharedCollections = null)
	: DavStoreBase(dav, options, credentials, logger),
		ICalendarOperations, ICalendarAttachmentSource, IFreeBusySource, IReadOnlyCollectionSource
{
	public const string KeyPrefix = "caldav:";

	private readonly IReadOnlyList<SharedCollection> _sharedCollections = sharedCollections ?? [];

	/// <summary>Whether a folder maps to a shared collection granted read-only.</summary>
	public bool IsReadOnlyCollection(string folderBackendKey)
	{
		string href = FromBackendKey(folderBackendKey);
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
	public override string EasClass => Protocol.EasClass.Calendar;
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

	// ---------- ICalendarOperations ----------

	public async Task<string?> RespondToMeetingAsync(
		string calendarFolderBackendKey, string eventUid, int userResponse, CancellationToken ct)
	{
		// Locate the event by UID in the calendar collection.
		string collection = FromBackendKey(calendarFolderBackendKey);
		string? href = (await FindByUidAsync(collection, eventUid, ct).ConfigureAwait(false))?.Href;
		if (href is null)
			return null;

		(string Content, string? ETag)? existing = await Dav.GetAsync(href, ct).ConfigureAwait(false);
		if (existing is null)
			return null;

		// partStatIdentity = the user's mail address (falls back to the gateway login) —
		// Credentials.UserName is the DAV backend login, which need not match any attendee.
		string? updated = CalendarConverter.SetPartStat(existing.Value.Content, userResponse, partStatIdentity);
		if (updated is not null)
			await Dav.PutAsync(href, updated, "text/calendar", existing.Value.ETag, false, ct)
				.ConfigureAwait(false);
		return href;
	}

	public async Task<string?> GetRawEventAsync(string folderBackendKey, string itemKey, CancellationToken ct)
	{
		return (await Dav.GetAsync(itemKey, ct).ConfigureAwait(false))?.Content;
	}

	private bool? _serverSchedules;

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

	protected override IReadOnlyList<XElement>? ToApplicationData(string content, BodyPreference bodyPreference)
	{
		return CalendarConverter.ToApplicationData(content, bodyPreference);
	}

	protected override string FromApplicationData(XElement applicationData, string uid, string? existingContent)
	{
		return CalendarConverter.FromApplicationData(applicationData, uid, existingContent,
			CalendarAttachmentPolicy.CapBytes(Options.CalendarAttachments), partStatIdentity);
	}

	/// <summary>ItemOperations fetch of an inline event attachment (calatt:: FileReference).</summary>
	public async Task<BackendAttachment?> GetEventAttachmentAsync(
		string folderBackendKey, string itemKey, int index, CancellationToken ct)
	{
		(string Content, string? ETag)? item = await Dav.GetAsync(itemKey, ct).ConfigureAwait(false);
		return item is null ? null : CalendarConverter.ExtractAttachment(item.Value.Content, index);
	}

	/// <summary>
	///   Free/busy via CALDAV:free-busy-query. The requesting user's own availability always
	///   works; another principal's only when a HomeSetPath template can address their
	///   collections AND the server grants read access (Stalwart answers 403 → null →
	///   per-recipient Availability status 163). RFC 6638 scheduling is not implemented —
	///   neither supported test backend offers a schedule-outbox.
	/// </summary>
	public async Task<IReadOnlyList<BusyPeriod>?> GetBusyPeriodsAsync(
		string targetAddress, DateTime startUtc, DateTime endUtc, CancellationToken ct)
	{
		bool self = targetAddress.Equals(partStatIdentity, StringComparison.OrdinalIgnoreCase) ||
		            targetAddress.Equals(Credentials.UserName, StringComparison.OrdinalIgnoreCase);
		List<string> collections = new();
		if (self)
		{
			foreach (BackendFolder folder in await ListFoldersAsync(ct).ConfigureAwait(false))
				collections.Add(FromBackendKey(folder.BackendKey));
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
				new XAttribute("start", EasDateTime.ToCompact(startUtc)),
				new XAttribute("end", EasDateTime.ToCompact(endUtc))));
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
			folders.Add(new BackendFolder(
				ToBackendKey(resource.Href),
				name,
				null,
				first && !granted ? EasFolderType.Calendar : EasFolderType.UserCalendar,
				Protocol.EasClass.Calendar));
			if (!granted)
				first = false;
		}

		// Shared collections (config + `eas share` grants): each is probed individually and
		// SKIPPED on any failure — an unreachable/revoked share must never break folder sync.
		foreach (SharedCollection shared in _sharedCollections)
		{
			if (folders.Any(f => SharedHrefEquals(FromBackendKey(f.BackendKey), shared.Href)))
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
				if (folders.Any(f => SharedHrefEquals(FromBackendKey(f.BackendKey), resource.Href)))
					continue;
				string? name = resource.Propstat.Descendants(DavNs.D + "displayname").FirstOrDefault()?.Value;
				if (string.IsNullOrWhiteSpace(name))
					name = shared.Href.TrimEnd('/').Split('/').LastOrDefault() ?? "Shared";
				folders.Add(new BackendFolder(
					ToBackendKey(resource.Href),
					name,
					null,
					EasFolderType.UserCalendar,
					Protocol.EasClass.Calendar));
			}
			catch (BackendException ex)
			{
				Logger.LogWarning("Shared collection {Href} is not accessible ({Reason}); skipped",
					shared.Href, ex.Message);
			}
		}

		return folders;
	}

	public override async Task<IReadOnlyDictionary<string, string>> GetItemRevisionsAsync(
		string folderBackendKey, ContentFilter filter, CancellationToken ct)
	{
		string collection = FromBackendKey(folderBackendKey);
		XElement filterElement = new(DavNs.CalDav + "filter",
			new XElement(DavNs.CalDav + "comp-filter", new XAttribute("name", "VCALENDAR"),
				BuildEventFilter(filter)));
		XElement body = new(DavNs.CalDav + "calendar-query",
			new XElement(DavNs.D + "prop", new XElement(DavNs.D + "getetag")),
			filterElement);

		List<DavResource> resources = await Dav.ReportAsync(collection, 1, body, ct).ConfigureAwait(false);
		Dictionary<string, string> map = new(StringComparer.Ordinal);
		foreach (DavResource resource in resources)
		{
			if (PathsEqual(resource.Href, collection))
				continue;
			string? etag = resource.Propstat.Descendants(DavNs.D + "getetag").FirstOrDefault()?.Value;
			if (etag is not null)
				map[resource.Href] = etag;
		}

		return map;
	}

	internal static XElement BuildEventFilter(ContentFilter filter)
	{
		// Always send a time-range: Axigen's calendar-query omits recurring events when the
		// VEVENT comp-filter carries no time-range (verified live 2026-07-17). An epoch start
		// is semantically "everything" — every event overlaps [1970, ∞) — so unfiltered syncs
		// keep their meaning on well-behaved servers too.
		DateTime since = filter.SinceUtc ?? DateTime.UnixEpoch;
		return new XElement(DavNs.CalDav + "comp-filter", new XAttribute("name", "VEVENT"),
			new XElement(DavNs.CalDav + "time-range",
				new XAttribute("start", EasDateTime.ToCompact(since))));
	}
}

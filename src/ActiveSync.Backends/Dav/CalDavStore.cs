using System.Xml.Linq;
using ActiveSync.Backends.Converters;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using ActiveSync.Protocol;
using Microsoft.Extensions.Logging;

namespace ActiveSync.Backends.Dav;

/// <summary>Calendar content store over CalDAV. Item keys are server hrefs; revisions are ETags.</summary>
public sealed class CalDavStore(
	WebDavClient dav,
	DavServerOptions options,
	BackendCredentials credentials,
	string partStatIdentity,
	ILogger logger)
	: DavStoreBase(dav, options, credentials, logger),
		ICalendarOperations, ICalendarAttachmentSource, IFreeBusySource
{
	public const string KeyPrefix = "caldav:";

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

	protected override IReadOnlyList<XElement>? ToApplicationData(string content, BodyPreference bodyPreference)
	{
		return CalendarConverter.ToApplicationData(content, bodyPreference);
	}

	protected override string FromApplicationData(XElement applicationData, string uid, string? existingContent)
	{
		return CalendarConverter.FromApplicationData(applicationData, uid, existingContent,
			CalendarAttachmentPolicy.CapBytes(options.CalendarAttachments));
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
		            targetAddress.Equals(credentials.UserName, StringComparison.OrdinalIgnoreCase);
		List<string> collections = new();
		if (self)
		{
			foreach (BackendFolder folder in await ListFoldersAsync(ct).ConfigureAwait(false))
				collections.Add(FromBackendKey(folder.BackendKey));
		}
		else if (!string.IsNullOrEmpty(options.HomeSetPath))
		{
			string home = DavDiscovery.ExpandTemplate(options.HomeSetPath, targetAddress);
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
		foreach (DavResource resource in resources)
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
			folders.Add(new BackendFolder(
				ToBackendKey(resource.Href),
				name,
				null,
				first ? EasFolderType.Calendar : EasFolderType.UserCalendar,
				Protocol.EasClass.Calendar));
			first = false;
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

	private static XElement BuildEventFilter(ContentFilter filter)
	{
		// Always send a time-range: Axigen's calendar-query omits recurring events when the
		// VEVENT comp-filter carries no time-range (verified live 2026-07-17). An epoch start
		// is semantically "everything" — every event overlaps [1970, ∞) — so unfiltered syncs
		// keep their meaning on well-behaved servers too.
		DateTime since = filter.SinceUtc ?? DateTime.UnixEpoch;
		return new XElement(DavNs.CalDav + "comp-filter", new XAttribute("name", "VEVENT"),
			new XElement(DavNs.CalDav + "time-range",
				new XAttribute("start", since.ToString("yyyyMMdd'T'HHmmss'Z'"))));
	}
}

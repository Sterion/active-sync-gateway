using System.Xml.Linq;
using ActiveSync.Backends.Converters;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Protocol;
using Microsoft.Extensions.Logging;

namespace ActiveSync.Backends.Dav;

/// <summary>
///   Tasks content store over CalDAV: VTODO items in the calendar-home-set collection named
///   by <see cref="DavServerOptions.TaskFolder" /> (Axigen layout: a "Tasks" calendar
///   collection whose supported-calendar-component-set is VTODO). Item keys are server
///   hrefs; revisions are ETags.
/// </summary>
public sealed class CalDavTaskStore(
	WebDavClient dav,
	DavServerOptions options,
	BackendCredentials credentials,
	ILogger logger,
	int pollSeconds)
	: DavStoreBase(dav, options, credentials, logger, pollSeconds)
{
	public const string KeyPrefix = "caldav-tasks:";

	protected override string Prefix => KeyPrefix;
	public override string EasClass => Protocol.EasClass.Tasks;
	protected override string MediaType => "text/calendar";
	protected override string FileExtension => ".ics";
	protected override string WellKnownPath => "/.well-known/caldav";

	protected override XName HomeSetProperty => DavNs.CalDav + "calendar-home-set";

	// Task discovery stays quiet (the calendar store already logs the shared home set).
	protected override string ProtocolLabel => "CalDAV tasks";
	protected override string ItemNoun => "task";
	protected override string ItemNounPlural => "tasks";
	protected override string CollectionKindPlural => "task collections";
	protected override string CtagLabel => "CalDAV-Tasks";

	protected override IReadOnlyList<XElement>? ToApplicationData(string content, BodyPreference bodyPreference)
	{
		return TasksConverter.ToApplicationData(content, bodyPreference);
	}

	protected override string FromApplicationData(XElement applicationData, string uid, string? existingContent)
	{
		return TasksConverter.FromApplicationData(applicationData, uid, existingContent);
	}

	protected override string? ExtractUid(string content)
	{
		return TasksConverter.ExtractUid(content);
	}

	protected override XElement BuildUidQueryBody(string uid)
	{
		return new XElement(DavNs.CalDav + "calendar-query",
			new XElement(DavNs.D + "prop", new XElement(DavNs.D + "getetag")),
			new XElement(DavNs.CalDav + "filter",
				new XElement(DavNs.CalDav + "comp-filter", new XAttribute("name", "VCALENDAR"),
					new XElement(DavNs.CalDav + "comp-filter", new XAttribute("name", "VTODO"),
						new XElement(DavNs.CalDav + "prop-filter", new XAttribute("name", "UID"),
							new XElement(DavNs.CalDav + "text-match", uid))))));
	}

	public override async Task<IReadOnlyList<BackendFolder>> ListFoldersAsync(CancellationToken ct)
	{
		string? taskFolderName = Options.TaskFolder;
		if (string.IsNullOrWhiteSpace(taskFolderName))
			return [];

		string home = await GetHomeSetAsync(ct).ConfigureAwait(false);
		XElement body = new(DavNs.D + "propfind",
			new XElement(DavNs.D + "prop",
				new XElement(DavNs.D + "resourcetype"),
				new XElement(DavNs.D + "displayname"),
				new XElement(DavNs.CalDav + "supported-calendar-component-set")));
		List<DavResource> resources = await Dav.PropfindAsync(home, 1, body, ct).ConfigureAwait(false);

		List<BackendFolder> folders = new();
		foreach (DavResource resource in resources)
		{
			XElement? type = resource.Propstat.Descendants(DavNs.D + "resourcetype").FirstOrDefault();
			if (type?.Element(DavNs.CalDav + "calendar") is null)
				continue;
			// Only collections that can hold VTODOs (an absent comp set means "anything").
			List<string?> components = resource.Propstat
				.Descendants(DavNs.CalDav + "supported-calendar-component-set")
				.Descendants(DavNs.CalDav + "comp")
				.Select(c => c.Attribute("name")?.Value)
				.Where(n => n is not null)
				.ToList();
			if (components.Count > 0 && !components.Contains("VTODO"))
				continue;

			string? name = resource.Propstat.Descendants(DavNs.D + "displayname").FirstOrDefault()?.Value;
			string segment = resource.Href.TrimEnd('/').Split('/').LastOrDefault() ?? "";
			if (!string.Equals(name, taskFolderName, StringComparison.OrdinalIgnoreCase) &&
			    !string.Equals(segment, taskFolderName, StringComparison.OrdinalIgnoreCase))
				continue;

			folders.Add(new BackendFolder(
				ToBackendKey(resource.Href),
				string.IsNullOrWhiteSpace(name) ? taskFolderName : name!,
				null,
				folders.Count == 0 ? EasFolderType.Tasks : EasFolderType.UserTasks,
				Protocol.EasClass.Tasks));
		}

		if (folders.Count == 0)
			Logger.LogDebug("CalDAV: no VTODO collection named \"{TaskFolder}\" found for {User}",
				taskFolderName, Credentials.UserName);
		return folders;
	}

	public override async Task<IReadOnlyDictionary<string, string>> GetItemRevisionsAsync(
		string folderBackendKey, ContentFilter filter, CancellationToken ct)
	{
		string collection = FromBackendKey(folderBackendKey);
		XElement body = new(DavNs.CalDav + "calendar-query",
			new XElement(DavNs.D + "prop", new XElement(DavNs.D + "getetag")),
			new XElement(DavNs.CalDav + "filter",
				new XElement(DavNs.CalDav + "comp-filter", new XAttribute("name", "VCALENDAR"),
					new XElement(DavNs.CalDav + "comp-filter", new XAttribute("name", "VTODO")))));

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
}

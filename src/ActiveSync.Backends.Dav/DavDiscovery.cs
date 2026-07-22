using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using Microsoft.Extensions.Logging;

namespace ActiveSync.Backends.Dav;

/// <summary>Shared CalDAV/CardDAV plumbing: principal discovery and ctag polling.</summary>
public static class DavDiscovery
{
	public static string ExpandTemplate(string template, string userName)
	{
		string localPart = userName.Contains('@') ? userName[..userName.IndexOf('@')] : userName;
		return template
			.Replace("{user}", Uri.EscapeDataString(userName), StringComparison.OrdinalIgnoreCase)
			.Replace("{localpart}", Uri.EscapeDataString(localPart), StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	///   RFC 6764 discovery: well-known → current-user-principal → home set property.
	/// </summary>
	public static async Task<string> DiscoverHomeSetAsync(
		WebDavClient dav, string wellKnownPath, XName homeSetProperty, CancellationToken ct)
	{
		string principal;
		XElement principalProp = new(DavNs.D + "propfind",
			new XElement(DavNs.D + "prop", new XElement(DavNs.D + "current-user-principal")));

		string start = await TryPropfindAsync(dav, wellKnownPath, principalProp, ct).ConfigureAwait(false)
		               ?? await TryPropfindAsync(dav, "/", principalProp, ct).ConfigureAwait(false)
		               ?? throw new BackendException("Could not locate DAV current-user-principal.");
		principal = start;

		XElement homeProp = new(DavNs.D + "propfind",
			new XElement(DavNs.D + "prop", new XElement(homeSetProperty)));
		List<DavResource> resources = await dav.PropfindAsync(principal, 0, homeProp, ct).ConfigureAwait(false);
		string? home = resources
			.SelectMany(r => r.Propstat.Descendants(homeSetProperty))
			.Select(e => e.Element(DavNs.D + "href")?.Value)
			.FirstOrDefault(v => !string.IsNullOrEmpty(v));
		return home ?? throw new BackendException($"DAV server did not report {homeSetProperty.LocalName}.");
	}

	private static async Task<string?> TryPropfindAsync(
		WebDavClient dav, string path, XElement body, CancellationToken ct)
	{
		try
		{
			List<DavResource> resources = await dav.PropfindAsync(path, 0, body, ct).ConfigureAwait(false);
			return resources
				.SelectMany(r => r.Propstat.Descendants(DavNs.D + "current-user-principal"))
				.Select(e => e.Element(DavNs.D + "href")?.Value)
				.FirstOrDefault(v => !string.IsNullOrEmpty(v));
		}
		catch (BackendException)
		{
			return null;
		}
	}

	/// <summary>Polls collection ctags/sync-tokens until one changes or the timeout elapses.</summary>
	public static async Task<IReadOnlyList<string>> PollCtagsAsync(
		WebDavClient dav,
		IReadOnlyList<string> folderBackendKeys,
		Func<string, string> hrefFromKey,
		TimeSpan timeout,
		ILogger logger,
		string protocol,
		string userName,
		CancellationToken ct)
	{
		DateTime deadline = DateTime.UtcNow + timeout;
		(Dictionary<string, string?> Map, HashSet<string> Failed) baseline =
			await SnapshotAsync().ConfigureAwait(false);

		while (DateTime.UtcNow < deadline)
		{
			TimeSpan remaining = deadline - DateTime.UtcNow;
			await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, Math.Max(1, remaining.TotalSeconds))), ct)
				.ConfigureAwait(false);
			(Dictionary<string, string?> Map, HashSet<string> Failed) current =
				await SnapshotAsync().ConfigureAwait(false);
			List<string> changed = DetectChanges(
				folderBackendKeys, baseline.Map, current.Map, baseline.Failed, current.Failed);
			if (changed.Count > 0)
			{
				foreach (string key in changed)
					logger.LogInformation(
						"{Protocol} poll: {Collection} changed for {User} (ctag {Baseline} -> {Current})",
						protocol, key, userName,
						baseline.Map.GetValueOrDefault(key) ?? "?", current.Map.GetValueOrDefault(key) ?? "?");
				return changed;
			}
		}

		logger.LogDebug("{Protocol} poll: no changes in {Collections} for {User} within the heartbeat",
			protocol, string.Join(", ", folderBackendKeys), userName);
		return [];

		async Task<(Dictionary<string, string?> Map, HashSet<string> Failed)> SnapshotAsync()
		{
			Dictionary<string, string?> map = new(StringComparer.Ordinal);
			HashSet<string> failed = new(StringComparer.Ordinal);
			foreach (string key in folderBackendKeys)
				try
				{
					map[key] =
						await dav.GetPropertyAsync(hrefFromKey(key), DavNs.CalendarServer + "getctag", ct)
							.ConfigureAwait(false)
						?? await dav.GetPropertyAsync(hrefFromKey(key), DavNs.D + "sync-token", ct)
							.ConfigureAwait(false);
				}
				catch (BackendException)
				{
					// H12: a transient read failure is "unknown", NOT "changed". Track it so the diff
					// skips this key — the old code stuffed the sentinel "error" into the map, which
					// then compared unequal to the real baseline ctag and forced a full re-sync on
					// every DAV hiccup.
					failed.Add(key);
				}

			return (map, failed);
		}
	}

	/// <summary>
	///   Collections whose ctag/sync-token changed between two poll snapshots. A key that failed to
	///   read in either snapshot is treated as unknown (never changed): the Ping entry check and the
	///   watchdog re-check are the correctness guarantee, so a transient DAV error must not masquerade
	///   as a change and trigger a full re-sync (H12).
	/// </summary>
	internal static List<string> DetectChanges(
		IReadOnlyList<string> folderBackendKeys,
		IReadOnlyDictionary<string, string?> baseline,
		IReadOnlyDictionary<string, string?> current,
		IReadOnlySet<string> baselineFailed,
		IReadOnlySet<string> currentFailed)
	{
		return folderBackendKeys
			.Where(k => !baselineFailed.Contains(k) && !currentFailed.Contains(k)
			            && baseline.GetValueOrDefault(k) != current.GetValueOrDefault(k))
			.ToList();
	}
}

using System.Globalization;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using ActiveSync.Backends.Converters;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;

namespace ActiveSync.Backends.Jmap;

/// <summary>
///   Calendar content store over JMAP (JMAP for Calendars / JSCalendar). Folder keys are
///   <c>jmap-cal:{calendarId}</c>; item keys are CalendarEvent ids. Events bridge JSCalendar ⇄
///   iCalendar (<see cref="JsCalendarConverter" />) and then reuse the mature iCalendar ⇄ EAS
///   converter. Listing uses CalendarEvent/get ids:null (CalendarEvent/query is FTS-backed and
///   eventually-consistent). Scheduling is left to the JMAP server, so the gateway does not
///   also mail iMIP (<see cref="ShouldSendInvitationsAsync" /> is false).
/// </summary>
public sealed class JmapCalendarStore(JmapClient client, string? mailAddress, int pollSeconds)
	: IContentStore, ICalendarOperations, IReadOnlyCollectionSource, IItemMoveOperations
{
	public const string KeyPrefix = "jmap-cal:";

	private static readonly string[] Cap = [JmapCapabilities.Core, JmapCapabilities.Calendars];
	private static readonly XNamespace Cal = EasNamespaces.Calendar;

	private string? _account;

	public string EasClass => Protocol.EasClass.Calendar;

	public bool OwnsBackendKey(string backendKey) => backendKey.StartsWith(KeyPrefix, StringComparison.Ordinal);

	public bool IsReadOnlyCollection(string folderBackendKey) => false;

	public async Task<IReadOnlyList<BackendFolder>> ListFoldersAsync(CancellationToken ct)
	{
		string account = await AccountAsync(ct).ConfigureAwait(false);
		using JmapResponse response = await client.CallAsync(Cap, "Calendar/get", new Dictionary<string, object?>
		{
			["accountId"] = account,
			["ids"] = null,
			["properties"] = new[] { "id", "name", "isDefault" }
		}, ct).ConfigureAwait(false);

		List<BackendFolder> result = new();
		foreach (JsonElement cal in response.Arguments("0").GetProperty("list").EnumerateArray())
		{
			string id = cal.GetProperty("id").GetString()!;
			bool isDefault = cal.TryGetProperty("isDefault", out JsonElement d) && d.ValueKind == JsonValueKind.True;
			result.Add(new BackendFolder(
				KeyPrefix + id,
				cal.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? id : id,
				null,
				isDefault ? EasFolderType.Calendar : EasFolderType.UserCalendar,
				Protocol.EasClass.Calendar));
		}

		return result;
	}

	public async Task<IReadOnlyDictionary<string, string>> GetItemRevisionsAsync(
		string folderBackendKey, ContentFilter filter, CancellationToken ct)
	{
		string account = await AccountAsync(ct).ConfigureAwait(false);
		string calId = FromKey(folderBackendKey);
		List<JsonElement> events = await GetAllEventsAsync(account, ct).ConfigureAwait(false);
		// H29: honor the client's calendar FilterType window instead of ignoring it (CalDavStore
		// applies a time-range; the JMAP store enumerates all events, so it filters in memory).
		return events.Where(e => InCalendar(e, calId))
			.Where(e => WithinFilter(e, filter))
			.ToDictionary(e => e.GetProperty("id").GetString()!, Revision, StringComparer.Ordinal);
	}

	public async Task<BackendItem?> GetItemAsync(
		string folderBackendKey, string itemKey, BodyPreference bodyPreference, CancellationToken ct)
	{
		JsonElement? jsEvent = await GetEventAsync(itemKey, ct).ConfigureAwait(false);
		if (jsEvent is not { } value)
			return null;
		string ics = JsCalendarConverter.ToICalendar(value);
		List<XElement>? data = CalendarConverter.ToApplicationData(ics, bodyPreference);
		return data is null ? null : new BackendItem(data);
	}

	public async Task<(string ItemKey, string Revision)> CreateItemAsync(
		string folderBackendKey, XElement applicationData, CancellationToken ct)
	{
		string account = await AccountAsync(ct).ConfigureAwait(false);
		string uid = applicationData.Element(Cal + "UID")?.Value ?? Guid.NewGuid().ToString();
		string ics = CalendarConverter.FromApplicationData(applicationData, uid, null, defaultOrganizer: mailAddress);
		Dictionary<string, object?> jsEvent = JsCalendarConverter.FromICalendar(ics, null);
		jsEvent["calendarIds"] = new Dictionary<string, object?> { [FromKey(folderBackendKey)] = true };

		using JmapResponse response = await client.CallAsync(Cap, "CalendarEvent/set", new Dictionary<string, object?>
		{
			["accountId"] = account,
			["create"] = new Dictionary<string, object?> { ["e"] = jsEvent }
		}, ct).ConfigureAwait(false);
		JsonElement args = response.Arguments("0");
		if (!args.TryGetProperty("created", out JsonElement created) || !created.TryGetProperty("e", out JsonElement made))
		{
			string type = args.TryGetProperty("notCreated", out JsonElement nc) && nc.TryGetProperty("e", out JsonElement err)
				? err.TryGetProperty("type", out JsonElement t) ? t.GetString() ?? "unknown" : "unknown"
				: "unknown";
			throw new BackendException($"JMAP CalendarEvent/set create failed: {type}.");
		}

		string id = made.GetProperty("id").GetString()!;
		JsonElement? full = await GetEventAsync(id, ct).ConfigureAwait(false);
		return (id, full is { } f ? Revision(f) : "0");
	}

	public async Task<string> UpdateItemAsync(
		string folderBackendKey, string itemKey, XElement applicationData, CancellationToken ct)
	{
		string account = await AccountAsync(ct).ConfigureAwait(false);
		JsonElement? existing = await GetEventAsync(itemKey, ct).ConfigureAwait(false);
		string uid = existing is { } e && e.TryGetProperty("uid", out JsonElement u) ? u.GetString() ?? itemKey : itemKey;
		string? existingIcs = existing is { } ev ? JsCalendarConverter.ToICalendar(ev) : null;
		string ics = CalendarConverter.FromApplicationData(applicationData, uid, existingIcs, defaultOrganizer: mailAddress);
		Dictionary<string, object?> jsEvent = JsCalendarConverter.FromICalendar(ics, existing);
		// uid is immutable on update (server rejects it as invalidProperties); calendarIds and
		// id are managed by move/create, not a content update.
		jsEvent.Remove("uid");
		jsEvent.Remove("calendarIds");
		jsEvent.Remove("id");

		using JmapResponse response = await client.CallAsync(Cap, "CalendarEvent/set", new Dictionary<string, object?>
		{
			["accountId"] = account,
			["update"] = new Dictionary<string, object?> { [itemKey] = jsEvent }
		}, ct).ConfigureAwait(false);
		EnsureNotIn(response.Arguments("0"), "notUpdated", itemKey);
		JsonElement? full = await GetEventAsync(itemKey, ct).ConfigureAwait(false);
		return full is { } f ? Revision(f) : "0";
	}

	public async Task DeleteItemAsync(
		string folderBackendKey, string itemKey, bool permanent, CancellationToken ct)
	{
		string account = await AccountAsync(ct).ConfigureAwait(false);
		using JmapResponse response = await client.CallAsync(Cap, "CalendarEvent/set", new Dictionary<string, object?>
		{
			["accountId"] = account,
			["destroy"] = new[] { itemKey }
		}, ct).ConfigureAwait(false);
		EnsureNotIn(response.Arguments("0"), "notDestroyed", itemKey);
	}

	public async Task<string> MoveItemAsync(
		string sourceFolderBackendKey, string itemKey, string destinationFolderBackendKey, CancellationToken ct)
	{
		string account = await AccountAsync(ct).ConfigureAwait(false);
		using JmapResponse response = await client.CallAsync(Cap, "CalendarEvent/set", new Dictionary<string, object?>
		{
			["accountId"] = account,
			["update"] = new Dictionary<string, object?>
			{
				[itemKey] = new Dictionary<string, object?>
				{
					["calendarIds"] = new Dictionary<string, object?> { [FromKey(destinationFolderBackendKey)] = true }
				}
			}
		}, ct).ConfigureAwait(false);
		EnsureNotIn(response.Arguments("0"), "notUpdated", itemKey);
		return itemKey;
	}

	// K58: JMAP calendar folder mutation over ActiveSync is not supported, so this store does not
	// implement IFolderOperations (it does support item move — IItemMoveOperations above).

	public async Task<IReadOnlyList<string>> WaitForChangesAsync(
		IReadOnlyList<string> folderBackendKeys, TimeSpan timeout, CancellationToken ct)
	{
		string account = await AccountAsync(ct).ConfigureAwait(false);
		Dictionary<string, string> baseline = await TokensAsync(account, folderBackendKeys, ct).ConfigureAwait(false);
		DateTime deadline = DateTime.UtcNow + timeout;
		int delaySeconds = 1;
		int ceiling = Math.Max(1, pollSeconds);
		while (DateTime.UtcNow < deadline)
		{
			TimeSpan remaining = deadline - DateTime.UtcNow;
			TimeSpan delay = TimeSpan.FromSeconds(Math.Min(delaySeconds, ceiling));
			if (delay > remaining) delay = remaining;
			if (delay > TimeSpan.Zero) await Task.Delay(delay, ct).ConfigureAwait(false);
			delaySeconds = Math.Min(delaySeconds * 2, ceiling);

			Dictionary<string, string> current = await TokensAsync(account, folderBackendKeys, ct).ConfigureAwait(false);
			List<string> changed = folderBackendKeys
				.Where(k => baseline.GetValueOrDefault(k) != current.GetValueOrDefault(k))
				.ToList();
			if (changed.Count > 0)
				return changed;
		}

		return [];
	}

	// ---------- ICalendarOperations ----------

	public async Task<string?> RespondToMeetingAsync(
		string calendarFolderBackendKey, string eventUid, int userResponse, CancellationToken ct)
	{
		string account = await AccountAsync(ct).ConfigureAwait(false);
		List<JsonElement> events = await GetAllEventsAsync(account, ct).ConfigureAwait(false);
		JsonElement match = events.FirstOrDefault(e =>
			e.TryGetProperty("uid", out JsonElement u) && u.GetString() == eventUid);
		if (match.ValueKind != JsonValueKind.Object)
			return null;
		string itemKey = match.GetProperty("id").GetString()!;

		// EAS userResponse: 1=Accept, 2=Tentative, 3=Decline.
		string status = userResponse switch { 2 => "tentative", 3 => "declined", _ => "accepted" };
		if (mailAddress is not null && FindParticipantId(match, mailAddress) is { } participantId)
		{
			// H10: dispose the response and surface a failed participation-status update instead of
			// leaking the document and reporting a meeting response that never took.
			using JmapResponse response = await client.CallAsync(Cap, "CalendarEvent/set", new Dictionary<string, object?>
			{
				["accountId"] = account,
				["update"] = new Dictionary<string, object?>
				{
					[itemKey] = new Dictionary<string, object?>
					{
						[$"participants/{participantId}/participationStatus"] = status
					}
				}
			}, ct).ConfigureAwait(false);
			EnsureNotIn(response.Arguments("0"), "notUpdated", itemKey);
		}

		return itemKey;
	}

	public async Task<string?> GetRawEventAsync(string folderBackendKey, string itemKey, CancellationToken ct)
	{
		JsonElement? jsEvent = await GetEventAsync(itemKey, ct).ConfigureAwait(false);
		return jsEvent is { } value ? JsCalendarConverter.ToICalendar(value) : null;
	}

	/// <summary>The JMAP server schedules meetings itself, so the gateway never also mails iMIP.</summary>
	public Task<bool> ShouldSendInvitationsAsync(CancellationToken ct) => Task.FromResult(false);

	// ---------- helpers ----------

	public static string FromKey(string backendKey) =>
		backendKey.StartsWith(KeyPrefix, StringComparison.Ordinal)
			? backendKey[KeyPrefix.Length..]
			: throw new BackendException($"Not a JMAP calendar folder key: {backendKey}");

	private async Task<JsonElement?> GetEventAsync(string itemKey, CancellationToken ct)
	{
		string account = await AccountAsync(ct).ConfigureAwait(false);
		using JmapResponse response = await client.CallAsync(Cap, "CalendarEvent/get", new Dictionary<string, object?>
		{
			["accountId"] = account,
			["ids"] = new[] { itemKey }
		}, ct).ConfigureAwait(false);
		JsonElement list = response.Arguments("0").GetProperty("list");
		return list.GetArrayLength() == 0 ? null : list[0].Clone();
	}

	private async Task<List<JsonElement>> GetAllEventsAsync(string account, CancellationToken ct)
	{
		using JmapResponse response = await client.CallAsync(Cap, "CalendarEvent/get", new Dictionary<string, object?>
		{
			["accountId"] = account,
			["ids"] = null
		}, ct).ConfigureAwait(false);
		return response.Arguments("0").GetProperty("list").EnumerateArray().Select(e => e.Clone()).ToList();
	}

	private async Task<Dictionary<string, string>> TokensAsync(
		string account, IReadOnlyList<string> folderBackendKeys, CancellationToken ct)
	{
		// H15: the wait token is the account-level CalendarEvent state instead of a SHA-256 over the
		// full JSCalendar body of every event, which used to be re-downloaded on every poll tick for
		// the whole heartbeat. CalendarEvent/get with an empty id list returns just the current state;
		// it advances on ANY event create/update/destroy. The state is account-wide, so a change in
		// one calendar shifts every watched calendar's token — the wait over-notifies rather than
		// misses (the safe direction; the client resyncs and finds nothing new). Mirrors the mail
		// store's H19 token.
		using JmapResponse response = await client.CallAsync(Cap, "CalendarEvent/get", new Dictionary<string, object?>
		{
			["accountId"] = account,
			["ids"] = Array.Empty<string>()
		}, ct).ConfigureAwait(false);
		JsonElement args = response.Arguments("0");
		string state = args.TryGetProperty("state", out JsonElement s) ? s.GetString() ?? "" : "";
		Dictionary<string, string> tokens = new(StringComparer.Ordinal);
		foreach (string folderKey in folderBackendKeys)
			tokens[folderKey] = state;
		return tokens;
	}

	private static string? FindParticipantId(JsonElement jsEvent, string email)
	{
		if (!jsEvent.TryGetProperty("participants", out JsonElement participants) ||
		    participants.ValueKind != JsonValueKind.Object)
			return null;
		foreach (JsonProperty p in participants.EnumerateObject())
		{
			if (p.Value.TryGetProperty("sendTo", out JsonElement sendTo) && sendTo.ValueKind == JsonValueKind.Object &&
			    sendTo.TryGetProperty("imip", out JsonElement imip) &&
			    imip.GetString()?.EndsWith(email, StringComparison.OrdinalIgnoreCase) == true)
				return p.Name;
			if (p.Value.TryGetProperty("email", out JsonElement e) &&
			    string.Equals(e.GetString(), email, StringComparison.OrdinalIgnoreCase))
				return p.Name;
		}

		return null;
	}

	/// <summary>
	///   Whether an event falls inside the client's calendar filter window. A recurring event may
	///   still have current occurrences, so it is never dropped on a date filter; a single event is
	///   kept when its end (start + duration) is at or after the window start. When the start cannot
	///   be parsed the event is kept — over-including is harmless (the client just sees a few old
	///   events); silently dropping one is not.
	/// </summary>
	private static bool WithinFilter(JsonElement jsEvent, ContentFilter filter)
	{
		if (filter.SinceUtc is not { } since)
			return true;
		if (jsEvent.TryGetProperty("recurrenceRules", out _) ||
		    jsEvent.TryGetProperty("recurrenceRule", out _) ||
		    jsEvent.TryGetProperty("recurrenceOverrides", out _))
			return true;
		if (!jsEvent.TryGetProperty("start", out JsonElement startEl) || startEl.GetString() is not { } startStr ||
		    !DateTime.TryParse(startStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime start))
			return true;
		TimeSpan duration = TimeSpan.Zero;
		if (jsEvent.TryGetProperty("duration", out JsonElement durEl) && durEl.GetString() is { } durStr)
			try { duration = XmlConvert.ToTimeSpan(durStr); }
			catch (FormatException) { /* malformed duration — treat as instantaneous */ }
		// start is a local/floating wall time and `since` is UTC; the ≤ tz-offset slop is
		// acceptable for a coarse day-granularity window (CalDAV's time-range is no finer).
		return start + duration >= since;
	}

	private static bool InCalendar(JsonElement jsEvent, string calId)
	{
		return jsEvent.TryGetProperty("calendarIds", out JsonElement cals) && cals.ValueKind == JsonValueKind.Object &&
		       cals.TryGetProperty(calId, out JsonElement v) && v.ValueKind == JsonValueKind.True;
	}

	private async Task<string> AccountAsync(CancellationToken ct)
	{
		if (_account is not null)
			return _account;
		JmapSessionResource session = await client.GetSessionAsync(ct).ConfigureAwait(false);
		// H9: a server without the calendars capability gets a clear error, not an opaque 400 from
		// a request built with using:[…calendars] it never advertised support for.
		session.RequireCapability(JmapCapabilities.Calendars);
		return _account = session.PrimaryAccount(JmapCapabilities.Calendars);
	}

	// H5: hash a canonical form (members sorted), not the raw text, so a server re-ordering the same
	// event JSON does not flip the revision and re-sync the whole calendar.
	private static string Revision(JsonElement jsEvent) => JmapRevision.Compute(jsEvent);

	private static void EnsureNotIn(JsonElement setResult, string bucket, string id)
	{
		if (setResult.TryGetProperty(bucket, out JsonElement failures) &&
		    failures.ValueKind == JsonValueKind.Object && failures.TryGetProperty(id, out JsonElement error))
		{
			string type = error.TryGetProperty("type", out JsonElement t) ? t.GetString() ?? "unknown" : "unknown";
			// H20: a notFound SetError means the event is gone; surface it as not-found so the host
			// reconciles (re-add/delete) rather than treating the update/delete as a transient error.
			throw string.Equals(type, "notFound", StringComparison.Ordinal)
				? new BackendItemNotFoundException($"JMAP CalendarEvent {id} no longer exists.")
				: new BackendException($"JMAP CalendarEvent/set failed for '{id}': {type}.");
		}
	}
}

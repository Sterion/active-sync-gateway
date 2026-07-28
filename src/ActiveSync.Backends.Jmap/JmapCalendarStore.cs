using System.Globalization;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using ActiveSync.Backends.Common.Converters;
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
	: IContentStore, ICalendarOperations, IItemMoveOperations
{
	public const string KeyPrefix = "jmap-cal:";

	private static readonly string[] Cap = [JmapCapabilities.Core, JmapCapabilities.Calendars];
	private static readonly XNamespace Cal = EasNamespaces.Calendar;

	private string? _account;

	// H7: the full account listing (state + events) cached on the store instance. GetItemRevisionsAsync
	// is invoked once PER CALENDAR within one Sync round, and RespondToMeetingAsync adds another
	// caller — without this, M calendars cost M full downloads of the same N events. A cheap
	// state-only check (StateAsync) decides whether the cached list is still current before paying
	// for a real download.
	private List<JsonElement>? _cachedEvents;
	private string? _cachedEventsState;

	public string EasClass => Protocol.EasClass.Calendar;

	public bool OwnsBackendKey(string backendKey) => backendKey.StartsWith(KeyPrefix, StringComparison.Ordinal);

	// H21: this store used to declare IReadOnlyCollectionSource with IsReadOnlyCollection hard-
	// coded to `false` — behaviourally identical to not implementing the interface (shared JMAP
	// calendars are never reverted here), but it made the store LOOK share-aware to
	// IBackendSession.IsReadOnlyFolder's OR and to anyone reading the type list. Dropped rather
	// than left as a capability that was never real; see docs/backends.md.

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
		List<JsonElement> events = await AllEventsAsync(account, ct).ConfigureAwait(false);
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
		// D7: mailAddress is the acting user's mail address, so MeetingStatus can tell
		// "I am the organizer" apart from "I am an invitee".
		List<XElement>? data = CalendarConverter.ToApplicationData(ics, bodyPreference, mailAddress);
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

	public async Task<(string ItemKey, string Revision)> MoveItemAsync(
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
		// F5: report the item's REAL revision at the destination, not a placeholder the caller
		// would otherwise have to invent (see UpdateItemAsync above for the identical shape).
		JsonElement? full = await GetEventAsync(itemKey, ct).ConfigureAwait(false);
		return (itemKey, full is { } f ? Revision(f) : "0");
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
		List<JsonElement> events = await AllEventsAsync(account, ct).ConfigureAwait(false);
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

	private async Task<Dictionary<string, string>> TokensAsync(
		string account, IReadOnlyList<string> folderBackendKeys, CancellationToken ct)
	{
		// H15: the wait token is the account-level CalendarEvent state instead of a SHA-256 over the
		// full JSCalendar body of every event, which used to be re-downloaded on every poll tick for
		// the whole heartbeat. The state is account-wide, so a change in one calendar shifts every
		// watched calendar's token — the wait over-notifies rather than misses (the safe direction;
		// the client resyncs and finds nothing new). Mirrors the mail store's H19 token.
		string state = await StateAsync(account, ct).ConfigureAwait(false);
		Dictionary<string, string> tokens = new(StringComparer.Ordinal);
		foreach (string folderKey in folderBackendKeys)
			tokens[folderKey] = state;
		return tokens;
	}

	// H7: CalendarEvent/get with an empty id list returns just the current account-level state —
	// no event bodies — so this is cheap enough to call before every full download to decide
	// whether the cache is still current.
	private async Task<string> StateAsync(string account, CancellationToken ct)
	{
		using JmapResponse response = await client.CallAsync(Cap, "CalendarEvent/get", new Dictionary<string, object?>
		{
			["accountId"] = account,
			["ids"] = Array.Empty<string>()
		}, ct).ConfigureAwait(false);
		JsonElement args = response.Arguments("0");
		return args.TryGetProperty("state", out JsonElement s) ? s.GetString() ?? "" : "";
	}

	// H7: caches the full account listing on the store instance, keyed by the CalendarEvent state,
	// so a Sync round with M calendars (GetItemRevisionsAsync is invoked once per calendar, and
	// RespondToMeetingAsync adds another caller) costs at most one real download, not M.
	private async Task<List<JsonElement>> AllEventsAsync(string account, CancellationToken ct)
	{
		string state = await StateAsync(account, ct).ConfigureAwait(false);
		if (_cachedEvents is not null && string.Equals(_cachedEventsState, state, StringComparison.Ordinal))
			return _cachedEvents;

		List<JsonElement> events = await FetchAllEventsAsync(account, ct).ConfigureAwait(false);
		_cachedEvents = events;
		_cachedEventsState = state;
		return events;
	}

	private async Task<List<JsonElement>> FetchAllEventsAsync(string account, CancellationToken ct)
	{
		JmapSessionResource session = await client.GetSessionAsync(ct).ConfigureAwait(false);
		if (session.CoreLimits.MaxObjectsInGet == int.MaxValue)
			return await FetchAllEventsUnboundedAsync(account, ct).ConfigureAwait(false);

		try
		{
			// H7: a server that declares a finite maxObjectsInGet answers requestTooLarge to a
			// blind "ids:null" over a large calendar — page the ids through CalendarEvent/query
			// (position-based, restarting on a queryState shift, same H3 protection the mail
			// store's Email/query paging uses) and fetch each page's bodies in maxObjectsInGet
			// batches.
			return await FetchAllEventsPagedAsync(account, session, ct).ConfigureAwait(false);
		}
		catch (BackendException)
		{
			// CalendarEvent/query is FTS-backed and eventually-consistent on some servers (the
			// contact store's ContactCard/query is documented as answering serverUnavailable right
			// after a write) — fall back to the simple, always-consistent ids:null get rather than
			// failing the whole calendar sync over the paging optimization.
			return await FetchAllEventsUnboundedAsync(account, ct).ConfigureAwait(false);
		}
	}

	private async Task<List<JsonElement>> FetchAllEventsUnboundedAsync(string account, CancellationToken ct)
	{
		using JmapResponse response = await client.CallAsync(Cap, "CalendarEvent/get", new Dictionary<string, object?>
		{
			["accountId"] = account,
			["ids"] = null
		}, ct).ConfigureAwait(false);
		return response.Arguments("0").GetProperty("list").EnumerateArray().Select(e => e.Clone()).ToList();
	}

	private async Task<List<JsonElement>> FetchAllEventsPagedAsync(
		string account, JmapSessionResource session, CancellationToken ct)
	{
		int page = Math.Max(1, Math.Min(500, session.CoreLimits.MaxObjectsInGet));
		List<JsonElement> events = new();
		int position = 0;
		int previousPosition = -1;
		string? queryState = null;
		int restartsRemaining = 3;
		while (true)
		{
			JmapCall query = new("CalendarEvent/query", new Dictionary<string, object?>
			{
				["accountId"] = account,
				["position"] = position,
				["limit"] = page
			}, "0");
			JmapCall get = new("CalendarEvent/get", new Dictionary<string, object?>
			{
				["accountId"] = account,
				["#ids"] = ResultRef("0", "CalendarEvent/query", "/ids")
			}, "1");

			using JmapResponse response = await client.InvokeAsync(Cap, [query, get], ct).ConfigureAwait(false);
			JsonElement queryArgs = response.Arguments("0");

			// Same defence as the mail store's H3 fix: a concurrent write can shift the (unsorted,
			// server-defined) result order between pages, so a queryState change restarts the whole
			// enumeration from position 0 instead of risking a dropped or duplicated event.
			string? currentState =
				queryArgs.TryGetProperty("queryState", out JsonElement qsEl) ? qsEl.GetString() : null;
			if (queryState is not null &&
			    !string.Equals(queryState, currentState, StringComparison.Ordinal) &&
			    restartsRemaining > 0)
			{
				restartsRemaining--;
				events.Clear();
				position = 0;
				previousPosition = -1;
				queryState = null;
				continue;
			}

			queryState = currentState;
			foreach (JsonElement jsEvent in response.Arguments("1").GetProperty("list").EnumerateArray())
				events.Add(jsEvent.Clone());

			int returned = queryArgs.GetProperty("ids").GetArrayLength();
			int reported = queryArgs.TryGetProperty("position", out JsonElement pos) && pos.TryGetInt32(out int pv)
				? pv
				: position;
			if (reported <= previousPosition)
				break; // a server that never advances position must not spin this loop forever
			previousPosition = reported;
			position = reported + returned;
			if (returned == 0)
				break;
			if (queryArgs.TryGetProperty("total", out JsonElement tot) && tot.TryGetInt64(out long total) &&
			    position >= total)
				break;
		}

		return events;
	}

	private static Dictionary<string, object?> ResultRef(string resultOf, string name, string path)
	{
		return new Dictionary<string, object?> { ["resultOf"] = resultOf, ["name"] = name, ["path"] = path };
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

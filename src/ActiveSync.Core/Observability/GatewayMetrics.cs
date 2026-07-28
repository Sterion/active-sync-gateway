using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Http;

namespace ActiveSync.Core.Observability;

/// <summary>
///   The gateway's Meter and instruments ("metric everything"). Static on purpose: the
///   increments happen deep in handlers and backends where DI plumbing would be pure
///   ceremony — the OpenTelemetry provider in the Server project subscribes by meter name.
///   Per-user labels are on by default and gated by <see cref="PerUserLabels" />; when off,
///   the user tag value collapses to "-". Metrics:PerUser is a LIVE-tier setting, so
///   <see cref="PerUserLabels" /> reads through a provider wired by
///   <see cref="SetPerUserLabelsProvider" /> — the same live <c>IOptionsMonitor</c> snapshot
///   every other live setting reads, rather than a value captured once at startup.
/// </summary>
public static class GatewayMetrics
{
	public const string MeterName = "ActiveSync.Gateway";

	// Every instrument shares one Prometheus namespace prefix so the gateway's series are
	// grep-able and don't collide with a co-scraped app's generic names (sync_items, mail_sent, …).
	private const string Prefix = "activesync_";

	private static readonly Meter Meter = new(MeterName);

	private static readonly Counter<long> EasRequests = Meter.CreateCounter<long>(
		Prefix + "eas_requests", null, "EAS requests by command, HTTP status and user.");

	private static readonly Histogram<double> EasRequestDuration = Meter.CreateHistogram<double>(
		Prefix + "eas_request_duration_seconds", "s",
		"EAS request duration by command and HTTP status (long-poll commands dominate their own label).");

	private static readonly Counter<long> SyncItems = Meter.CreateCounter<long>(
		Prefix + "sync_items", null,
		"Items synced by user, content class, direction and operation.");

	private static readonly Counter<long> MailSent = Meter.CreateCounter<long>(
		Prefix + "mail_sent", null, "Outbound mail submissions by user and kind.");

	private static readonly Counter<long> BackendErrors = Meter.CreateCounter<long>(
		Prefix + "backend_errors", null, "Backend operation failures by protocol.");

	private static readonly Counter<long> BackendRetries = Meter.CreateCounter<long>(
		Prefix + "backend_retries", null, "Backend operations replayed after a transient failure, by protocol.");

	private static readonly Counter<long> ThrottleRejections = Meter.CreateCounter<long>(
		Prefix + "auth_throttle_rejections", null,
		"Authentication attempts rejected by the brute-force throttle, by source (eas|webui).");

	// Auth outcomes, so a dashboard can see the success/failure/throttle mix per source rather
	// than only counting rejections.
	private static readonly Counter<long> AuthOutcomes = Meter.CreateCounter<long>(
		Prefix + "auth_outcomes", null, "Authentication outcomes by source, outcome and user.");

	private static readonly ConcurrentDictionary<string, int> ActiveLongPolls =
		new(StringComparer.OrdinalIgnoreCase);

	// Observer slots are read (from the gauge callback on the metrics-collection thread) and
	// written (from startup / provider registration) on different threads, so they are volatile to
	// publish the reference safely. Last-write-wins: the per-process single owner plugs its live
	// counts in; test hosts overwrite each other harmlessly.
	private static volatile Func<IEnumerable<Measurement<long>>>? _sessionsObserver;
	private static volatile Func<IEnumerable<Measurement<long>>>? _idleWatchersObserver;
	private static volatile Func<DateTimeOffset?>? _certificateExpiryObserver;

	static GatewayMetrics()
	{
		Meter.CreateObservableGauge(Prefix + "backend_sessions_active",
			() => _sessionsObserver?.Invoke() ?? [], null, "Live backend sessions by user.");
		Meter.CreateObservableGauge(Prefix + "imap_idle_watchers_active",
			() => _idleWatchersObserver?.Invoke() ?? [], null, "Live IMAP IDLE watchers by user.");
		Meter.CreateObservableGauge(Prefix + "eas_longpolls_active",
			() => ActiveLongPolls
				.Where(pair => pair.Value > 0)
				.Select(pair => new Measurement<long>(pair.Value, new KeyValuePair<string, object?>("user", pair.Key))),
			null, "EAS long-polls (Ping/Sync waits) currently parked, by user.");
		// Seconds until the serving TLS certificate expires (negative once expired). Emits nothing
		// until an observer is wired (plaintext / no cert), like the other gauges.
		Meter.CreateObservableGauge(Prefix + "tls_certificate_expiry_seconds",
			() =>
			{
				DateTimeOffset? notAfter = _certificateExpiryObserver?.Invoke();
				return notAfter is { } expiry
					? new Measurement<double>[] { new((expiry - DateTimeOffset.UtcNow).TotalSeconds) }
					: [];
			},
			"s", "Seconds until the serving TLS certificate expires (negative if already expired).");
	}

	// Last-write-wins, same rationale as the observer slots above — a fresh test host's
	// wiring must win over a previous one's, and the two only ever race benignly.
	private static volatile Func<bool>? _perUserLabelsProvider;

	/// <summary>
	///   Whether metric label values include the per-account identity, or collapse to "-".
	///   Reads through <see cref="_perUserLabelsProvider" /> so a live <c>Metrics:PerUser</c>
	///   change (`eas config set`, the admin Settings page) takes effect on the very next
	///   metric emission — no restart, matching the setting's catalogued live tier. Defaults to
	///   true when nothing has wired a provider yet (unit tests that call GatewayMetrics
	///   directly, before ProgramServer's startup wiring runs).
	/// </summary>
	public static bool PerUserLabels => _perUserLabelsProvider?.Invoke() ?? true;

	/// <summary>
	///   Wires <see cref="PerUserLabels" /> to a live source — call once at startup with a
	///   delegate that re-reads <c>IOptionsMonitor&lt;ActiveSyncOptions&gt;.CurrentValue</c> (or
	///   equivalent) rather than a value snapshotted once.
	/// </summary>
	public static void SetPerUserLabelsProvider(Func<bool> read)
	{
		_perUserLabelsProvider = read;
	}

	// Bounds any future call site that hands User() an unauthenticated, attacker-controlled
	// value directly — the same 128-char budget EndpointAuth's LogText.Clean uses for the same
	// field.
	private const int MaxUserLabelLength = 128;

	private static string User(string user)
	{
		return PerUserLabels ? Sanitize(user) : "-";
	}

	/// <summary>
	///   Clamps length and neutralizes control characters (incl. the bidi-override smuggling
	///   <see cref="WireLog.IsUnsafe" /> also guards against) — defence in depth for a Prometheus
	///   label value, independent of whichever call site decided a raw value was safe to pass in.
	/// </summary>
	private static string Sanitize(string value)
	{
		string text = value.Length > MaxUserLabelLength ? value[..MaxUserLabelLength] : value;
		bool unsafeFound = false;
		foreach (char c in text)
			if (WireLog.IsUnsafe(c, allowLineStructure: false))
			{
				unsafeFound = true;
				break;
			}
		if (!unsafeFound)
			return text;
		return string.Create(text.Length, text, static (dest, source) =>
		{
			for (int i = 0; i < source.Length; i++)
				dest[i] = WireLog.IsUnsafe(source[i], allowLineStructure: false) ? '?' : source[i];
		});
	}

	/// <summary>
	///   Label value for the EAS command. The command is client-controlled query text and every
	///   distinct value becomes its own time series, so anything outside the MS-ASHTTP command
	///   set collapses to "other" and known commands are folded to one canonical casing.
	/// </summary>
	private static string Command(string command)
	{
		return EasRequestParameters.CanonicalCommand(command) ?? "other";
	}

	public static void RecordEasRequest(string command, int statusCode, string user, double seconds)
	{
		string label = Command(command);
		EasRequests.Add(1,
			new KeyValuePair<string, object?>("command", label),
			new KeyValuePair<string, object?>("status", statusCode),
			new KeyValuePair<string, object?>("user", User(user)));
		// The duration histogram carries the same status dimension as the counter, so
		// latency can be sliced by outcome (e.g. 401s are cheap, 200 Syncs are not).
		EasRequestDuration.Record(seconds,
			new KeyValuePair<string, object?>("command", label),
			new KeyValuePair<string, object?>("status", statusCode));
	}

	/// <summary>
	///   One authentication outcome. source: eas | webui; outcome: success | failure | throttled | error.
	/// </summary>
	public static void RecordAuthOutcome(string source, string outcome, string user)
	{
		// `user` on every non-success path is the raw HTTP Basic username — an unauthenticated
		// caller controls it outright (a throttled/rejected/errored request never proved the
		// identity), so it becomes its own Prometheus series for any distinct string a sprayer
		// cares to send. Only "success" has actually verified the login; every other outcome
		// collapses to the same sentinel PerUserLabels=false uses, independent of that switch.
		string label = outcome == "success" ? User(user) : "-";
		AuthOutcomes.Add(1,
			new KeyValuePair<string, object?>("source", source),
			new KeyValuePair<string, object?>("outcome", outcome),
			new KeyValuePair<string, object?>("user", label));
	}

	/// <summary>direction: client_to_server | server_to_client; operation: add | change | delete | fetch.</summary>
	public static void RecordSyncItems(string user, string easClass, string direction, string operation, int count)
	{
		if (count <= 0)
			return;
		SyncItems.Add(count,
			new KeyValuePair<string, object?>("user", User(user)),
			new KeyValuePair<string, object?>("class", easClass),
			new KeyValuePair<string, object?>("direction", direction),
			new KeyValuePair<string, object?>("operation", operation));
	}

	/// <summary>kind: send | smart_reply | smart_forward | draft_submit | imip.</summary>
	public static void RecordMailSent(string user, string kind)
	{
		MailSent.Add(1,
			new KeyValuePair<string, object?>("user", User(user)),
			new KeyValuePair<string, object?>("kind", kind));
	}

	public static void RecordBackendError(string protocol)
	{
		BackendErrors.Add(1, new KeyValuePair<string, object?>("protocol", protocol));
	}

	/// <summary>One transient backend failure that was replayed (not a final error).</summary>
	public static void RecordBackendRetry(string protocol)
	{
		BackendRetries.Add(1, new KeyValuePair<string, object?>("protocol", protocol));
	}

	/// <summary>One throttle rejection, tagged by source (eas | webui) so the two paths are distinct.</summary>
	public static void RecordThrottleRejection(string source)
	{
		ThrottleRejections.Add(1, new KeyValuePair<string, object?>("source", source));
	}

	/// <summary>Currently-parked long-polls per user (for the admin dashboard).</summary>
	public static IReadOnlyList<KeyValuePair<string, int>> SnapshotLongPolls()
	{
		return ActiveLongPolls.Where(pair => pair.Value > 0).ToList();
	}

	public static void SetSessionsObserver(Func<IEnumerable<Measurement<long>>> observe)
	{
		_sessionsObserver = observe;
	}

	/// <summary>
	///   Removes the sessions observer, but only if it is still the EXACT delegate passed in
	///   — a disposed <c>BackendSessionFactory</c> must not clear a later factory's observer
	///   (e.g. a fresh <c>WebApplicationFactory</c> in a test host) out from under it; it may only
	///   ever clear its own. Benign race with a concurrent <see cref="SetSessionsObserver" />, same
	///   as the rest of this last-write-wins slot.
	/// </summary>
	public static void ClearSessionsObserver(Func<IEnumerable<Measurement<long>>> observe)
	{
		if (_sessionsObserver == observe)
			_sessionsObserver = null;
	}

	public static void SetIdleWatchersObserver(Func<IEnumerable<Measurement<long>>> observe)
	{
		_idleWatchersObserver = observe;
	}

	/// <summary>Publishes the serving TLS certificate's expiry for the expiry gauge.</summary>
	public static void SetCertificateExpiryObserver(Func<DateTimeOffset?> observe)
	{
		_certificateExpiryObserver = observe;
	}

	/// <summary>Marks one long-poll as parked until the returned scope is disposed.</summary>
	public static IDisposable TrackLongPoll(string user)
	{
		string key = User(user);
		ActiveLongPolls.AddOrUpdate(key, 1, (_, current) => current + 1);
		return new LongPollScope(key);
	}

	private sealed class LongPollScope(string key) : IDisposable
	{
		private bool _disposed;

		public void Dispose()
		{
			if (_disposed)
				return;
			_disposed = true;
			// Decrement, and drop the entry once it hits zero — otherwise the dictionary keeps one
			// dead slot per distinct user that ever parked a long-poll, for the process lifetime. The
			// KeyValuePair Remove overload deletes only if the value is still the one we read, so a
			// concurrent increment cannot lose its slot.
			while (ActiveLongPolls.TryGetValue(key, out int current))
			{
				int next = current - 1;
				if (next > 0)
				{
					if (ActiveLongPolls.TryUpdate(key, next, current))
						break;
				}
				else if (((ICollection<KeyValuePair<string, int>>)ActiveLongPolls)
				         .Remove(new KeyValuePair<string, int>(key, current)))
				{
					break;
				}
			}
		}
	}
}

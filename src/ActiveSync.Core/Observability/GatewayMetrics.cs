using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using ActiveSync.Protocol.Http;

namespace ActiveSync.Core.Observability;

/// <summary>
///   The gateway's Meter and instruments ("metric everything"). Static on purpose: the
///   increments happen deep in handlers and backends where DI plumbing would be pure
///   ceremony — the OpenTelemetry provider in the Server project subscribes by meter name.
///   Per-user labels are on by default and gated by <see cref="PerUserLabels" /> (set once
///   at startup from Metrics:PerUser); when off, the user tag value collapses to "-".
/// </summary>
public static class GatewayMetrics
{
	public const string MeterName = "ActiveSync.Gateway";

	// K4: every instrument shares one Prometheus namespace prefix so the gateway's series are
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

	// K5: auth outcomes, so a dashboard can see the success/failure/throttle mix per source rather
	// than only counting rejections.
	private static readonly Counter<long> AuthOutcomes = Meter.CreateCounter<long>(
		Prefix + "auth_outcomes", null, "Authentication outcomes by source, outcome and user.");

	private static readonly ConcurrentDictionary<string, int> ActiveLongPolls =
		new(StringComparer.OrdinalIgnoreCase);

	// K3: observer slots are read (from the gauge callback on the metrics-collection thread) and
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
		// K5: seconds until the serving TLS certificate expires (negative once expired). Emits nothing
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

	public static bool PerUserLabels { get; set; } = true;

	private static string User(string user)
	{
		return PerUserLabels ? user : "-";
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
		// K4: the duration histogram carries the same status dimension as the counter, so
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
		AuthOutcomes.Add(1,
			new KeyValuePair<string, object?>("source", source),
			new KeyValuePair<string, object?>("outcome", outcome),
			new KeyValuePair<string, object?>("user", User(user)));
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

	public static void SetIdleWatchersObserver(Func<IEnumerable<Measurement<long>>> observe)
	{
		_idleWatchersObserver = observe;
	}

	/// <summary>Publishes the serving TLS certificate's expiry for the expiry gauge (K5).</summary>
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
			// K2: decrement, and drop the entry once it hits zero — otherwise the dictionary keeps one
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

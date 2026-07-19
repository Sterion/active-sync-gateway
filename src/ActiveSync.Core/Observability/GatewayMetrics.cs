using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

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

	private static readonly Meter Meter = new(MeterName);

	private static readonly Counter<long> EasRequests = Meter.CreateCounter<long>(
		"eas_requests", null, "EAS requests by command, HTTP status and user.");

	private static readonly Histogram<double> EasRequestDuration = Meter.CreateHistogram<double>(
		"eas_request_duration_seconds", "s",
		"EAS request duration by command (long-poll commands dominate their own label).");

	private static readonly Counter<long> SyncItems = Meter.CreateCounter<long>(
		"sync_items", null,
		"Items synced by user, content class, direction and operation.");

	private static readonly Counter<long> MailSent = Meter.CreateCounter<long>(
		"mail_sent", null, "Outbound mail submissions by user and kind.");

	private static readonly Counter<long> BackendErrors = Meter.CreateCounter<long>(
		"backend_errors", null, "Backend operation failures by protocol.");

	private static readonly Counter<long> BackendRetries = Meter.CreateCounter<long>(
		"backend_retries", null, "Backend operations replayed after a transient failure, by protocol.");

	private static readonly Counter<long> ThrottleRejections = Meter.CreateCounter<long>(
		"auth_throttle_rejections", null, "Authentication attempts rejected by the brute-force throttle.");

	private static readonly ConcurrentDictionary<string, int> ActiveLongPolls =
		new(StringComparer.OrdinalIgnoreCase);

	// Last-write-wins observer slots: the (per-process single) BackendSessionFactory plugs
	// its live counts in; test hosts overwrite each other harmlessly.
	private static Func<IEnumerable<Measurement<long>>>? _sessionsObserver;
	private static Func<IEnumerable<Measurement<long>>>? _idleWatchersObserver;

	static GatewayMetrics()
	{
		Meter.CreateObservableGauge("backend_sessions_active",
			() => _sessionsObserver?.Invoke() ?? [], null, "Live backend sessions by user.");
		Meter.CreateObservableGauge("imap_idle_watchers_active",
			() => _idleWatchersObserver?.Invoke() ?? [], null, "Live IMAP IDLE watchers by user.");
		Meter.CreateObservableGauge("eas_longpolls_active",
			() => ActiveLongPolls
				.Where(pair => pair.Value > 0)
				.Select(pair => new Measurement<long>(pair.Value, new KeyValuePair<string, object?>("user", pair.Key))),
			null, "EAS long-polls (Ping/Sync waits) currently parked, by user.");
	}

	public static bool PerUserLabels { get; set; } = true;

	private static string User(string user)
	{
		return PerUserLabels ? user : "-";
	}

	public static void RecordEasRequest(string command, int statusCode, string user, double seconds)
	{
		EasRequests.Add(1,
			new KeyValuePair<string, object?>("command", command),
			new KeyValuePair<string, object?>("status", statusCode),
			new KeyValuePair<string, object?>("user", User(user)));
		EasRequestDuration.Record(seconds, new KeyValuePair<string, object?>("command", command));
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

	public static void RecordThrottleRejection()
	{
		ThrottleRejections.Add(1);
	}

	public static void SetSessionsObserver(Func<IEnumerable<Measurement<long>>> observe)
	{
		_sessionsObserver = observe;
	}

	public static void SetIdleWatchersObserver(Func<IEnumerable<Measurement<long>>> observe)
	{
		_idleWatchersObserver = observe;
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
			ActiveLongPolls.AddOrUpdate(key, 0, (_, current) => Math.Max(0, current - 1));
		}
	}
}

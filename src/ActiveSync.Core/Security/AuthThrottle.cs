using System.Collections.Concurrent;
using ActiveSync.Core.Options;
using Microsoft.Extensions.Options;

namespace ActiveSync.Core.Security;

/// <summary>
///   Brute-force throttle for the authenticated endpoints (EAS Basic auth and the web UI
///   login form — callers namespace their keys). Counts failed attempts in a fixed window and
///   answers 429 once a key reaches its limit — without touching the mail backend. Callers
///   key the granular counter by (address, username) so a valid login for one account cannot
///   clear another account's counter, and keep a looser per-address ceiling
///   (<see cref="IpWideLimit" />) so username-rotation from one address is still bounded.
///   Sized for the gateway's audience (a handful of mailbox owners), not as a general-purpose
///   WAF. Both halves of a key are unauthenticated input, so the table is hard-capped at
///   <see cref="MaxTrackedKeys" /> and cleaned at most once every <see cref="PruneIntervalSeconds" />.
/// </summary>
public sealed class AuthThrottle(IOptionsMonitor<ActiveSyncOptions> options)
{
	/// <summary>The per-address ceiling is this many times the per-(address, user) limit.</summary>
	private const int IpWideFactor = 5;

	/// <summary>
	///   Hard ceiling on tracked keys. Failure keys are minted from the client's address and the
	///   username it presented, both unauthenticated input, so nothing else bounds the table.
	/// </summary>
	private const int MaxTrackedKeys = 10_000;

	/// <summary>Minimum spacing between full-table cleanup scans.</summary>
	private const int PruneIntervalSeconds = 30;

	private readonly ConcurrentDictionary<string, Entry> _failures = new();

	private long _pruneScans;
	private DateTime _nextPruneUtc = DateTime.MinValue;

	private AuthOptions Options => options.CurrentValue.Auth;

	/// <summary>Keys currently tracked. Test seam for the table-growth bound.</summary>
	internal int TrackedKeys => _failures.Count;

	/// <summary>Full-table cleanup scans performed so far. Test seam for the per-failure O(n) scan.</summary>
	internal long PruneScans => Interlocked.Read(ref _pruneScans);

	/// <summary>Per-address failure ceiling, or 0 when throttling is disabled.</summary>
	public int IpWideLimit => Options.MaxFailures <= 0 ? 0 : Options.MaxFailures * IpWideFactor;

	/// <summary>Seconds until the key may retry, or null when not blocked.</summary>
	public int? BlockedForSeconds(string key)
	{
		return BlockedForSeconds(key, Options.MaxFailures);
	}

	/// <summary>Seconds until the key may retry against a specific limit, or null when not blocked.</summary>
	public int? BlockedForSeconds(string key, int limit)
	{
		if (limit <= 0)
			return null;
		if (!_failures.TryGetValue(key, out Entry? entry))
			return null;
		lock (entry)
		{
			DateTime windowEnd = entry.WindowStartUtc.AddSeconds(Options.FailureWindowSeconds);
			if (windowEnd <= DateTime.UtcNow || entry.Count < limit)
				return null;
			return Math.Max(1, (int)(windowEnd - DateTime.UtcNow).TotalSeconds);
		}
	}

	public void RecordFailure(string key)
	{
		if (Options.MaxFailures <= 0)
			return;

		if (!_failures.TryGetValue(key, out Entry? entry))
		{
			// Only a key we have never seen can grow the table, so this is the only path that
			// pays for cleanup — the old code scanned on EVERY failure once the table was large,
			// which handed a username-rotating attacker an O(n) cost per request. Beyond the cap
			// new keys are dropped rather than tracked: the attacker's own per-address counter was
			// minted long before the table filled and keeps blocking them, whereas growing without
			// bound is the denial of service itself.
			Prune();
			if (_failures.Count >= MaxTrackedKeys)
				return;
			entry = _failures.GetOrAdd(key, _ => new Entry { WindowStartUtc = DateTime.UtcNow });
		}

		lock (entry)
		{
			if (entry.WindowStartUtc.AddSeconds(Options.FailureWindowSeconds) <= DateTime.UtcNow)
			{
				entry.WindowStartUtc = DateTime.UtcNow;
				entry.Count = 0;
			}

			entry.Count++;
		}
	}

	public void RecordSuccess(string key)
	{
		_failures.TryRemove(key, out _);
	}

	/// <summary>
	///   Reclaims keys whose window has expired — they carry no state worth keeping. Rate-limited
	///   to one scan per <see cref="PruneIntervalSeconds" /> so the O(n) walk cannot be driven once
	///   per request, and <see cref="MaxTrackedKeys" /> bounds the n it walks. The interval stamp is
	///   deliberately unsynchronized: the worst a race costs is a second concurrent scan.
	/// </summary>
	private void Prune()
	{
		DateTime now = DateTime.UtcNow;
		if (now < _nextPruneUtc)
			return;
		_nextPruneUtc = now.AddSeconds(PruneIntervalSeconds);
		Interlocked.Increment(ref _pruneScans);
		DateTime cutoff = now.AddSeconds(-Options.FailureWindowSeconds);
		foreach ((string key, Entry entry) in _failures)
			if (entry.WindowStartUtc <= cutoff)
				_failures.TryRemove(key, out _);
	}

	private sealed class Entry
	{
		public int Count;
		public DateTime WindowStartUtc;
	}
}

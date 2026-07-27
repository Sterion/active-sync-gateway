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
public sealed class AuthThrottle(IOptionsMonitor<ActiveSyncOptions> options, TimeProvider timeProvider)
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

	/// <summary>
	///   Number of candidates inspected by <see cref="EvictOldestToMakeRoom" /> (K6). A full O(n)
	///   scan over up to <see cref="MaxTrackedKeys" /> entries on every over-capacity insert would
	///   itself become an O(n)-per-request cost once an attack keeps the table pinned at the cap
	///   (the exact problem the prune rate-limit exists to avoid) — a small bounded sample is
	///   enough to always make room without that cost.
	/// </summary>
	private const int EvictionSampleSize = 32;

	private readonly ConcurrentDictionary<string, Entry> _failures = new();

	private long _pruneScans;

	/// <summary>
	///   UTC ticks of the next allowed prune scan, 0 (== <see cref="DateTime.MinValue" />) until
	///   the first call. K8: a plain <c>DateTime</c> field here was mutated by a bare
	///   check-then-set under concurrent <see cref="RecordFailure" /> calls — not just a benign
	///   double-scan race, but a torn 8-byte read/write with no atomicity guarantee, able to
	///   observe a garbage timestamp (far-future → pruning wedges off; far-past → the O(n) scan
	///   the cap exists to avoid runs on every failure). Stored as ticks and accessed only via
	///   <see cref="Interlocked" /> so both the read and the write are atomic.
	/// </summary>
	private long _nextPruneTicks;

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
		DateTime now = timeProvider.GetUtcNow().UtcDateTime;
		lock (entry)
		{
			DateTime windowEnd = entry.WindowStartUtc.AddSeconds(Options.FailureWindowSeconds);
			if (windowEnd <= now || entry.Count < limit)
				return null;
			return Math.Max(1, (int)(windowEnd - now).TotalSeconds);
		}
	}

	public void RecordFailure(string key)
	{
		if (Options.MaxFailures <= 0)
			return;

		DateTime now = timeProvider.GetUtcNow().UtcDateTime;
		if (!_failures.TryGetValue(key, out Entry? entry))
		{
			// Only a key we have never seen can grow the table, so this is the only path that
			// pays for cleanup — the old code scanned on EVERY failure once the table was large,
			// which handed a username-rotating attacker an O(n) cost per request.
			Prune();
			// K6: beyond the cap, EVICT rather than refuse. Refusing meant one address that fills
			// the table by rotating usernames permanently starves every OTHER address of its own
			// per-address ceiling key for the rest of the failure window — a two-host attacker
			// (host A floods and accepts its own throttling, host B then brute-forces) faced no
			// throttling at all, because host B's key could never be minted. Dropping the
			// oldest-windowed entry keeps a new key always mintable; it is also the entry closest
			// to expiring anyway, so this tends to reclaim exactly what Prune() would have taken
			// next.
			if (_failures.Count >= MaxTrackedKeys)
				EvictOldestToMakeRoom();
			entry = _failures.GetOrAdd(key, _ => new Entry { WindowStartUtc = now });
		}

		lock (entry)
		{
			if (entry.WindowStartUtc.AddSeconds(Options.FailureWindowSeconds) <= now)
			{
				entry.WindowStartUtc = now;
				entry.Count = 0;
			}

			entry.Count++;
		}
	}

	/// <summary>
	///   Clears a key on a successful login. Pass <paramref name="isAddressKey" /> = true ONLY for
	///   the shared per-address ceiling key (<see cref="IpWideLimit" />) — the call is then a
	///   deliberate no-op (K7): a valid login for one account must never reset the address-wide
	///   ceiling, or an attacker holding any one valid credential could rotate usernames
	///   indefinitely from that address. Only a granular (address, username) key may ever be
	///   cleared here.
	/// </summary>
	public void RecordSuccess(string key, bool isAddressKey = false)
	{
		if (isAddressKey)
			return;
		_failures.TryRemove(key, out _);
	}

	/// <summary>
	///   Reclaims keys whose window has expired — they carry no state worth keeping. Rate-limited
	///   to one scan per <see cref="PruneIntervalSeconds" /> so the O(n) walk cannot be driven once
	///   per request, and <see cref="MaxTrackedKeys" /> bounds the n it walks. The interval stamp
	///   is read and written only through <see cref="Interlocked" /> (see
	///   <see cref="_nextPruneTicks" />); the check-then-set as a whole is still racy by design —
	///   the worst that costs is a second concurrent scan, not a torn/garbage timestamp.
	/// </summary>
	private void Prune()
	{
		DateTime now = timeProvider.GetUtcNow().UtcDateTime;
		if (now.Ticks < Interlocked.Read(ref _nextPruneTicks))
			return;
		Interlocked.Exchange(ref _nextPruneTicks, now.AddSeconds(PruneIntervalSeconds).Ticks);
		Interlocked.Increment(ref _pruneScans);
		DateTime cutoff = now.AddSeconds(-Options.FailureWindowSeconds);
		foreach ((string key, Entry entry) in _failures)
			if (entry.WindowStartUtc <= cutoff)
				_failures.TryRemove(key, out _);
	}

	/// <summary>
	///   Makes room for one more key by dropping the oldest-windowed entry among a bounded sample
	///   (K6) — an approximate LRU eviction, not an exact global-oldest scan, so the cost per
	///   over-capacity insert stays O(<see cref="EvictionSampleSize" />) instead of O(table size).
	/// </summary>
	private void EvictOldestToMakeRoom()
	{
		string? victim = null;
		DateTime oldest = DateTime.MaxValue;
		int scanned = 0;
		foreach ((string candidateKey, Entry candidate) in _failures)
		{
			DateTime windowStart;
			lock (candidate)
				windowStart = candidate.WindowStartUtc;
			if (windowStart < oldest)
			{
				oldest = windowStart;
				victim = candidateKey;
			}

			if (++scanned >= EvictionSampleSize)
				break;
		}

		if (victim is not null)
			_failures.TryRemove(victim, out _);
	}

	private sealed class Entry
	{
		public int Count;
		public DateTime WindowStartUtc;
	}
}

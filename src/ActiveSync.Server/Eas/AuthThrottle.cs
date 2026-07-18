using System.Collections.Concurrent;
using ActiveSync.Core.Options;
using Microsoft.Extensions.Options;

namespace ActiveSync.Server.Eas;

/// <summary>
///   Brute-force throttle for the authenticated endpoints. Counts failed Basic-auth attempts
///   in a fixed window and answers 429 once a key reaches its limit — without touching the
///   IMAP backend. Callers key the granular counter by (address, username) so a valid login
///   for one account cannot clear another account's counter, and keep a looser per-address
///   ceiling (<see cref="IpWideLimit" />) so username-rotation from one address is still
///   bounded. Sized for the gateway's audience (a handful of mailbox owners), not as a
///   general-purpose WAF.
/// </summary>
public sealed class AuthThrottle(IOptions<ActiveSyncOptions> options)
{
	/// <summary>The per-address ceiling is this many times the per-(address, user) limit.</summary>
	private const int IpWideFactor = 5;

	private readonly ConcurrentDictionary<string, Entry> _failures = new();

	private AuthOptions Options => options.Value.Auth;

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
		PruneIfLarge();
		Entry entry = _failures.GetOrAdd(key, _ => new Entry { WindowStartUtc = DateTime.UtcNow });
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
	///   Opportunistic cleanup so an address-rotating attacker cannot grow the table without
	///   bound; expired windows carry no state worth keeping.
	/// </summary>
	private void PruneIfLarge()
	{
		if (_failures.Count < 10_000)
			return;
		DateTime cutoff = DateTime.UtcNow.AddSeconds(-Options.FailureWindowSeconds);
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

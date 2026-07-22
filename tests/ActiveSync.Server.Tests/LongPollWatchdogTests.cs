using System.Diagnostics;
using ActiveSync.Server.Eas;

namespace ActiveSync.Server.Tests;

/// <summary>
///   The shared long-poll race coordinator (<see cref="LongPollWatchdog.RaceAsync{T}" />) used by
///   both Ping and Sync waits. These pin its two reliability contracts:
///   E7 — a watcher that completes "no change" early must not collapse the heartbeat into an
///   immediate return (a tight client re-poll loop); and E8 — a single faulting watcher must not
///   abort the whole long-poll (a 500 to the client).
/// </summary>
public sealed class LongPollWatchdogTests
{
	// E7: with the watchdog disabled and every watcher completing "no change" the instant it is
	// awaited, the race must still honour the heartbeat window (idle to the deadline) rather than
	// returning immediately — otherwise the client re-Pings back-to-back in a tight loop.
	[Fact]
	public async Task RaceAsync_WhenAllWatchersCompleteEarly_HonoursTheDeadline()
	{
		using CancellationTokenSource linked = new();
		DateTime deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(400);
		Stopwatch sw = Stopwatch.StartNew();

		LongPollWatchdog.Outcome<bool> outcome = await LongPollWatchdog.RaceAsync(
			[Task.FromResult(false)], // a degraded watcher: gives up immediately with "no change"
			watchdog: null, // WatchdogSeconds=0 — nothing else keeps the poll alive
			isPositive: changed => changed,
			none: false,
			deadline,
			linked,
			CancellationToken.None);

		sw.Stop();
		Assert.False(outcome.Result); // no change, as expected
		// It must have waited out (most of) the heartbeat, not returned the instant the watcher gave up.
		Assert.True(sw.ElapsedMilliseconds >= 300,
			$"expected the race to idle to the deadline (~400ms), returned after {sw.ElapsedMilliseconds}ms");
	}
}

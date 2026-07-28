using ActiveSync.Backends.Local;

namespace ActiveSync.Core.Tests;

/// <summary>
///   G21: <see cref="LocalChangeNotifier" /> had no latch, so a write landing between a caller's
///   entry check (e.g. PingHandler's CheckPendingAsync) and its wait's own registration was
///   silently dropped — invisible until the next watchdog re-check. Mirrors ImapIdleWatcher's
///   <c>LastChangeUtc</c> latch ("events are latched so a change that fires while no wait is
///   registered is still seen by the next wait").
/// </summary>
public sealed class LocalChangeNotifierTests
{
	[Fact]
	public async Task NotifyChanged_BeforeWaitRegisters_MustNotBeLost()
	{
		LocalChangeNotifier notifier = new();

		// Mirrors LocalStoreBase.WaitForChangesAsync capturing "now" right after PingHandler's
		// entry check (CheckPendingAsync) found nothing pending...
		DateTime sinceUtc = DateTime.UtcNow;

		// ...and a write on another device commits (and notifies) in the gap before this wait
		// has had a chance to register a listener.
		notifier.NotifyChanged(1, "contacts");

		bool changed = await notifier.WaitAsync(
			1, "contacts", TimeSpan.FromMilliseconds(200), sinceUtc, CancellationToken.None);

		Assert.True(changed); // must not sit invisible until the watchdog re-check
	}

	[Fact]
	public async Task WaitAsync_IgnoresAChangeThatLatchedBeforeSinceUtc()
	{
		// A change that already happened before the caller's own "since" reference must not
		// cause an immediate (spurious) return — catching it is the entry check's job; the latch
		// exists only for the gap AFTER that reference point.
		LocalChangeNotifier notifier = new();
		notifier.NotifyChanged(1, "contacts");
		DateTime sinceUtc = DateTime.UtcNow;

		bool changed = await notifier.WaitAsync(
			1, "contacts", TimeSpan.FromMilliseconds(100), sinceUtc, CancellationToken.None);

		Assert.False(changed); // correctly times out — not latched
	}

	[Fact]
	public async Task WaitAsync_StillWakesOnALiveNotification()
	{
		LocalChangeNotifier notifier = new();
		Task<bool> wait = notifier.WaitAsync(
			1, "contacts", TimeSpan.FromSeconds(5), DateTime.UtcNow, CancellationToken.None);
		notifier.NotifyChanged(1, "contacts");
		Assert.True(await wait);
	}
}

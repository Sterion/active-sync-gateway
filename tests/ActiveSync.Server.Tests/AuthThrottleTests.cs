using ActiveSync.Core.Options;
using ActiveSync.Core.Security;
using Microsoft.Extensions.Options;

namespace ActiveSync.Server.Tests;

public class AuthThrottleTests
{
	private static AuthThrottle Create(int maxFailures = 3, int windowSeconds = 300)
	{
		return new AuthThrottle(TestOptionsMonitor.Of(new ActiveSyncOptions
		{
			Auth = new AuthOptions { MaxFailures = maxFailures, FailureWindowSeconds = windowSeconds }
		}), TimeProvider.System);
	}

	[Fact]
	public void BlocksAfterLimit_AndReportsRetryAfter()
	{
		AuthThrottle throttle = Create();
		Assert.Null(throttle.BlockedForSeconds("1.2.3.4"));
		throttle.RecordFailure("1.2.3.4");
		throttle.RecordFailure("1.2.3.4");
		Assert.Null(throttle.BlockedForSeconds("1.2.3.4"));
		throttle.RecordFailure("1.2.3.4");
		int? retryAfter = throttle.BlockedForSeconds("1.2.3.4");
		Assert.NotNull(retryAfter);
		Assert.InRange(retryAfter.Value, 1, 300);
	}

	[Fact]
	public void OtherAddresses_AreNotAffected()
	{
		AuthThrottle throttle = Create(1);
		throttle.RecordFailure("1.2.3.4");
		Assert.NotNull(throttle.BlockedForSeconds("1.2.3.4"));
		Assert.Null(throttle.BlockedForSeconds("5.6.7.8"));
	}

	[Fact]
	public void SuccessClearsTheCounter()
	{
		AuthThrottle throttle = Create(2);
		throttle.RecordFailure("1.2.3.4");
		throttle.RecordSuccess("1.2.3.4");
		throttle.RecordFailure("1.2.3.4");
		Assert.Null(throttle.BlockedForSeconds("1.2.3.4"));
	}

	[Fact]
	public void MaxFailures_AppliesLive_WithoutReconstruction()
	{
		// The throttle must read Auth from IOptionsMonitor.CurrentValue on each call, so a live
		// settings change takes effect without rebuilding the singleton (the Phase 3 contract).
		TestOptionsMonitor.Mutable<ActiveSyncOptions> monitor =
			new(new ActiveSyncOptions { Auth = new AuthOptions { MaxFailures = 0 } });
		AuthThrottle throttle = new(monitor, TimeProvider.System);

		for (int i = 0; i < 10; i++)
			throttle.RecordFailure("1.2.3.4");
		Assert.Null(throttle.BlockedForSeconds("1.2.3.4")); // disabled

		monitor.CurrentValue = new ActiveSyncOptions { Auth = new AuthOptions { MaxFailures = 1 } };
		throttle.RecordFailure("1.2.3.4");
		Assert.NotNull(throttle.BlockedForSeconds("1.2.3.4")); // live change applied
	}

	[Fact]
	public void ZeroMaxFailures_DisablesTheThrottle()
	{
		AuthThrottle throttle = Create(0);
		for (int i = 0; i < 50; i++)
			throttle.RecordFailure("1.2.3.4");
		Assert.Null(throttle.BlockedForSeconds("1.2.3.4"));
	}

	[Fact]
	public void PerUserCounters_AreIndependent_SuccessDoesNotClearAnotherUser()
	{
		AuthThrottle throttle = Create(2);
		const string ip = "203.0.113.9";
		string alice = $"{ip}\nalice", bob = $"{ip}\nbob";

		throttle.RecordFailure(bob);
		throttle.RecordFailure(bob);
		Assert.NotNull(throttle.BlockedForSeconds(bob));

		// A valid login for alice must not reset bob's counter (the reported weakness).
		throttle.RecordSuccess(alice);
		Assert.NotNull(throttle.BlockedForSeconds(bob));
	}

	[Fact]
	public void FailureTable_IsBounded_UnderUsernameRotation()
	{
		// K26: every distinct (address, username) pair mints a row, and the cleanup only removes
		// EXPIRED windows — so an attacker rotating usernames inside one window grows the table
		// without bound. 60k unauthenticated requests must not leave 60k rows behind.
		AuthThrottle throttle = Create(3, 3600);
		for (int i = 0; i < 60_000; i++)
			throttle.RecordFailure($"203.0.113.9\nuser{i}@example.com");

		Assert.InRange(throttle.TrackedKeys, 1, 20_000);
	}

	[Fact]
	public void FailureTable_DoesNotRescanItself_OnEveryFailure()
	{
		// K26, second half: once the table is over the cleanup threshold the scan runs on EVERY
		// subsequent failure, so the attack costs the gateway O(n) per request it sends.
		AuthThrottle throttle = Create(3, 3600);
		for (int i = 0; i < 60_000; i++)
			throttle.RecordFailure($"203.0.113.9\nuser{i}@example.com");

		Assert.InRange(throttle.PruneScans, 0, 10);
	}

	[Fact]
	public void AtCapacity_ExistingCountersStillBite()
	{
		// The cap must not become an escape hatch: the per-address key exists before the table
		// fills, so the IP-wide ceiling still blocks the rotating attacker that filled it.
		AuthThrottle throttle = Create(2, 3600);
		const string ip = "203.0.113.9";
		throttle.RecordFailure(ip);
		for (int i = 0; i < 60_000; i++)
			throttle.RecordFailure($"{ip}\nuser{i}@example.com");
		for (int i = 0; i < throttle.IpWideLimit; i++)
			throttle.RecordFailure(ip);

		Assert.NotNull(throttle.BlockedForSeconds(ip, throttle.IpWideLimit));
	}

	[Fact]
	public void IpWideCeiling_IsFiveTimesThePerUserLimit_AndBoundsUsernameRotation()
	{
		AuthThrottle throttle = Create(2);
		const string ip = "203.0.113.9";
		Assert.Equal(10, throttle.IpWideLimit);

		for (int i = 0; i < 10; i++)
			throttle.RecordFailure(ip); // rotation feeds the shared per-address counter
		Assert.NotNull(throttle.BlockedForSeconds(ip, throttle.IpWideLimit));
		Assert.Null(throttle.BlockedForSeconds($"{ip}\nfresh")); // a new user has no block yet
	}

	[Fact]
	public void WindowExpiry_IsDrivenByInjectedTimeProvider_NotTheWallClock()
	{
		// K9: AuthThrottle read DateTime.UtcNow directly, so window-expiry/retry-after/prune
		// cadence could only be exercised by waiting on the real clock (untestable
		// deterministically). Injecting TimeProvider lets a fake clock prove expiry without
		// any Thread.Sleep — this constructor overload does not exist on unmodified code.
		FakeTimeProvider clock = new(DateTimeOffset.UtcNow);
		AuthThrottle throttle = new(TestOptionsMonitor.Of(new ActiveSyncOptions
		{
			Auth = new AuthOptions { MaxFailures = 1, FailureWindowSeconds = 60 }
		}), clock);

		throttle.RecordFailure("1.2.3.4");
		Assert.NotNull(throttle.BlockedForSeconds("1.2.3.4"));

		clock.Advance(TimeSpan.FromSeconds(61));
		Assert.Null(throttle.BlockedForSeconds("1.2.3.4")); // window expired per the fake clock
	}

	[Fact]
	public void TableAtCapacity_StillAdmitsANewAddressFromADifferentSource()
	{
		// K6: once the table hits MaxTrackedKeys, RecordFailure just returns without creating a
		// new entry — so a single attacking address that fills the table by rotating usernames
		// (10,000 distinct (address, username) keys, well within one failure window) permanently
		// starves every OTHER address of its own per-address ceiling key until the window drains.
		// A two-host attacker (host A floods and accepts its own block, host B then brute-forces)
		// faces no throttling at all: BlockedForSeconds simply finds nothing in the table for B.
		AuthThrottle throttle = Create(2, 3600);
		for (int i = 0; i < 10_000; i++)
			throttle.RecordFailure($"203.0.113.9\nuser{i}@example.com");

		const string otherAddress = "198.51.100.7";
		for (int i = 0; i < 5; i++)
			throttle.RecordFailure(otherAddress);

		Assert.NotNull(throttle.BlockedForSeconds(otherAddress));
	}

	[Fact]
	public void RecordSuccess_AddressKeyGuard_NeverClearsTheCeiling()
	{
		// K7: RecordSuccess cleared whichever key it was handed, so a caller could wipe the
		// shared per-address ceiling with one valid login. The WebUi login endpoint did exactly
		// that (RecordSuccess(userKey) AND RecordSuccess(addressKey)), voiding the class's own
		// documented guarantee: "a valid login for one account cannot clear another account's
		// counter, and keep a looser per-address ceiling ... so username-rotation from one
		// address is still bounded." This overload does not exist on unmodified code — nothing in
		// the type distinguished a granular key from a ceiling key.
		AuthThrottle throttle = Create(2, 3600);
		const string address = "203.0.113.5";
		for (int i = 0; i < throttle.IpWideLimit; i++)
			throttle.RecordFailure(address);
		Assert.NotNull(throttle.BlockedForSeconds(address, throttle.IpWideLimit));

		// A caller that correctly marks this as the address ceiling key must not be able to clear
		// it via a success on some other account.
		throttle.RecordSuccess(address, isAddressKey: true);
		Assert.NotNull(throttle.BlockedForSeconds(address, throttle.IpWideLimit));
	}

	[Fact]
	public void Prune_UnderConcurrentAccess_StaysConsistent()
	{
		// K8 (COVERAGE, not red-first): `_nextPruneUtc` was a plain, non-atomic `DateTime`
		// mutated under a bare check-then-set race in Prune(). A genuine torn read of an 8-byte
		// field needs a 32-bit process to trigger deterministically — this suite runs 64-bit, so
		// the original symptom cannot be exhibited here. This exercises the fixed
		// Interlocked-backed cadence under real concurrency: many threads driving Prune()
		// concurrently must neither throw nor blow the scan cadence out to "every failure".
		FakeTimeProvider clock = new(DateTimeOffset.UtcNow);
		AuthThrottle throttle = new(TestOptionsMonitor.Of(new ActiveSyncOptions
		{
			Auth = new AuthOptions { MaxFailures = 3, FailureWindowSeconds = 3600 }
		}), clock);

		Parallel.For(0, 32, i =>
		{
			for (int j = 0; j < 500; j++)
				throttle.RecordFailure($"203.0.113.9\nuser{i}-{j}@example.com");
		});

		Assert.InRange(throttle.PruneScans, 0, 20);
	}
}

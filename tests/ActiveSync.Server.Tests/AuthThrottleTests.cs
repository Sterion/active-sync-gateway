using ActiveSync.Core.Options;
using ActiveSync.Server.Eas;
using Microsoft.Extensions.Options;

namespace ActiveSync.Server.Tests;

public class AuthThrottleTests
{
	private static AuthThrottle Create(int maxFailures = 3, int windowSeconds = 300)
	{
		return new AuthThrottle(Options.Create(new ActiveSyncOptions
		{
			Auth = new AuthOptions { MaxFailures = maxFailures, FailureWindowSeconds = windowSeconds }
		}));
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
}

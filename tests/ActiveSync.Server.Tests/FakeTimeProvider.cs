namespace ActiveSync.Server.Tests;

/// <summary>Controllable <see cref="TimeProvider" /> for deterministic time-based tests (Lets
/// AuthThrottle's window-expiry, retry-after and prune-interval be tested without a real clock).</summary>
internal sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
{
	private DateTimeOffset _utcNow = start;

	public override DateTimeOffset GetUtcNow() => _utcNow;

	public void Advance(TimeSpan delta) => _utcNow += delta;
}

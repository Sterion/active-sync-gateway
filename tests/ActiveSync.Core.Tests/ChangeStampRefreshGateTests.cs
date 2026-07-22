using ActiveSync.Core.Settings;

namespace ActiveSync.Core.Tests;

/// <summary>
///   B11 — the shared change-stamp poll gate (used by both SettingsRefresher and AccountResolver).
///   A negative or non-finite cadence must never permanently disable refresh: it clamps to
///   "check on the next request" so a mistaken value is self-repairing rather than needing a restart.
/// </summary>
public sealed class ChangeStampRefreshGateTests
{
	[Fact]
	public void NegativeCadence_DoesNotPermanentlyDisable_ChecksAgainNextTime()
	{
		ChangeStampRefreshGate gate = new();

		// First check always allowed; scheduling with a negative cadence must NOT push the next
		// check out to "never" — the previous code returned before ever reading the stamp again.
		Assert.True(gate.ShouldCheck(force: false));
		gate.ScheduleNext(-1);
		Assert.True(gate.ShouldCheck(force: false));

		gate.ScheduleNext(double.NegativeInfinity);
		Assert.True(gate.ShouldCheck(force: false));

		gate.ScheduleNext(double.NaN);
		Assert.True(gate.ShouldCheck(force: false));
	}

	[Fact]
	public void PositiveCadence_SkipsUntilTheIntervalElapses()
	{
		ChangeStampRefreshGate gate = new();
		gate.ScheduleNext(3600); // an hour out
		Assert.False(gate.ShouldCheck(force: false));
		// force always bypasses the interval (the startup path).
		Assert.True(gate.ShouldCheck(force: true));
	}

	[Fact]
	public void ZeroCadence_ChecksEveryTime()
	{
		ChangeStampRefreshGate gate = new();
		gate.ScheduleNext(0);
		Assert.True(gate.ShouldCheck(force: false));
		gate.ScheduleNext(0);
		Assert.True(gate.ShouldCheck(force: false));
	}
}

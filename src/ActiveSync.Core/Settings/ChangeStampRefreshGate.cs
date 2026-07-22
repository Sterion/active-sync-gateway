namespace ActiveSync.Core.Settings;

/// <summary>
///   The shared "should I poll the change stamp now?" interval gate used by BOTH change-stamp
///   refreshers — <see cref="SettingsRefresher" /> (global settings) and
///   <see cref="ActiveSync.Core.Accounts.AccountResolver" /> (declared accounts). They previously
///   inlined the same logic with subtly different rules (one guarded a negative cadence with a
///   "have I loaded yet" flag, the other did not), and a negative cadence PERMANENTLY disabled live
///   refresh — including the pickup of an operator setting it back to positive, so recovery needed a
///   restart (B11).
///
///   Unified here: the cadence is always clamped to a finite, non-negative number of seconds, so a
///   negative or non-finite value degrades to "check on the next request" rather than "never check
///   again". Live refresh can therefore never lock itself out.
/// </summary>
internal sealed class ChangeStampRefreshGate
{
	private long _nextCheckTicks;

	/// <summary>
	///   True when the caller should proceed to read the change stamp. <paramref name="force" />
	///   (used once at startup) bypasses the interval.
	/// </summary>
	public bool ShouldCheck(bool force) =>
		force || Environment.TickCount64 >= Volatile.Read(ref _nextCheckTicks);

	/// <summary>
	///   Records that a check just ran and schedules the earliest next one. A negative or non-finite
	///   cadence clamps to zero (check on the next request), so it can never disable refresh forever.
	/// </summary>
	public void ScheduleNext(double refreshSeconds)
	{
		double seconds = double.IsFinite(refreshSeconds) ? Math.Max(refreshSeconds, 0) : 0;
		Volatile.Write(ref _nextCheckTicks, Environment.TickCount64 + (long)(seconds * 1000));
	}
}

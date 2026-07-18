namespace ActiveSync.Server.Eas;

/// <summary>
///   Shared long-poll coordinator for the Ping and Sync heartbeat waits. It races the
///   backend-change "watcher" tasks (IMAP IDLE / STATUS / DAV ctag / local notifier) against
///   an optional periodic "watchdog" exact re-check, and:
///   - swallows the host-shutdown cancellation so an in-flight long-poll ends promptly and
///   the caller answers the canonical empty/heartbeat response instead of erroring;
///   - drains every task before returning — the watchdog borrows the request-scoped
///   DbContext and must never outlive the request; the watchers self-cancel promptly.
///   The caller owns the linked <see cref="CancellationTokenSource" /> (usually linking the
///   request token with <c>IHostApplicationLifetime.ApplicationStopping</c>) and starts every
///   task with its token before calling in. It then interprets the returned result and whether
///   the watchdog — rather than a watcher — produced it (which drives the log level: a watchdog
///   hit means the backend watchers missed a change, and is logged as a warning).
/// </summary>
internal static class LongPollWatchdog
{
	/// <param name="watchers">Backend-change waiters, already started with the linked token.</param>
	/// <param name="watchdog">Periodic exact re-check task, or null when disabled.</param>
	/// <param name="isPositive">Decides whether a task's result counts as "changed".</param>
	/// <param name="none">The result meaning "no change" (returned on timeout/shutdown).</param>
	/// <param name="linkedCts">The caller's cancellation source; cancelled here in the finally.</param>
	/// <param name="requestAborted">The bare request token, to tell a client disconnect from host shutdown.</param>
	public static async Task<Outcome<T>> RaceAsync<T>(
		IReadOnlyList<Task<T>> watchers,
		Task<T>? watchdog,
		Func<T, bool> isPositive,
		T none,
		CancellationTokenSource linkedCts,
		CancellationToken requestAborted)
	{
		List<Task<T>> pending = new(watchers);
		if (watchdog is not null)
			pending.Add(watchdog);

		T result = none;
		bool foundByWatchdog = false;
		try
		{
			while (pending.Count > 0 && !isPositive(result))
			{
				Task<T> finished = await Task.WhenAny(pending);
				pending.Remove(finished);
				T value = await finished;
				if (isPositive(value))
				{
					result = value;
					foundByWatchdog = finished == watchdog;
				}
			}
		}
		catch (OperationCanceledException) when (!requestAborted.IsCancellationRequested)
		{
			// Host is shutting down: report "no change" so the caller ends the long-poll
			// gracefully (the client treats it as a heartbeat expiry and re-polls).
		}
		finally
		{
			await linkedCts.CancelAsync();
			// Drain whatever is still running: the removed tasks were already awaited above.
			foreach (Task<T> task in pending)
				try
				{
					await task;
				}
				catch
				{
					// cancelled or already-logged failure
				}
		}

		return new Outcome<T>(result, foundByWatchdog);
	}

	public readonly record struct Outcome<T>(T Result, bool FoundByWatchdog);
}

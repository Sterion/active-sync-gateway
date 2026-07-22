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
	/// <summary>
	///   Upper bound on the post-cancellation drain. Watchers self-cancel within milliseconds of
	///   the linked token firing, so this is only ever hit by a misbehaving one — which must not
	///   pin the request (and its connection) open indefinitely.
	/// </summary>
	private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);

	/// <param name="watchers">Backend-change waiters, already started with the linked token.</param>
	/// <param name="watchdog">Periodic exact re-check task, or null when disabled.</param>
	/// <param name="isPositive">Decides whether a task's result counts as "changed".</param>
	/// <param name="none">The result meaning "no change" (returned on timeout/shutdown).</param>
	/// <param name="deadline">
	///   End of the heartbeat window. Watchers are expected to wait it out, but a degraded one can
	///   complete "no change" early; the race honours the deadline rather than returning immediately
	///   (which would collapse the heartbeat into a tight client re-poll loop — <c>E7</c>).
	/// </param>
	/// <param name="linkedCts">The caller's cancellation source; cancelled here in the finally.</param>
	/// <param name="requestAborted">The bare request token, to tell a client disconnect from host shutdown.</param>
	public static async Task<Outcome<T>> RaceAsync<T>(
		IReadOnlyList<Task<T>> watchers,
		Task<T>? watchdog,
		Func<T, bool> isPositive,
		T none,
		DateTime deadline,
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
				T value;
				try
				{
					value = await finished;
				}
				catch (OperationCanceledException) when (!requestAborted.IsCancellationRequested)
				{
					throw; // host shutdown — the outer handler ends the poll gracefully
				}
				catch
				{
					// E8: a single watcher faulting must not abort the whole long-poll into a 500.
					// The watcher self-logs its failure; treat it as "no change from this watcher"
					// and keep racing the rest (and the watchdog, the real correctness backstop).
					continue;
				}

				if (isPositive(value))
				{
					result = value;
					foundByWatchdog = finished == watchdog;
				}
			}

			// E7: a watcher that completes "no change" before its timeout is spent (a degraded or
			// unavailable backend watcher) is dropped above — and when the watchdog is disabled
			// (WatchdogSeconds=0) nothing else keeps the poll alive, so the loop would drain and
			// return "no change" the instant the last watcher gives up, turning the client's
			// heartbeat into a tight re-poll loop. Honour the heartbeat: idle out the remaining
			// window so the wait lasts as long as the client asked for.
			if (!isPositive(result))
			{
				TimeSpan remaining = deadline - DateTime.UtcNow;
				if (remaining > TimeSpan.Zero)
					await Task.Delay(remaining, linkedCts.Token);
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
			// Drain whatever is still running (the removed tasks were already awaited above),
			// but bound it: a well-behaved watcher self-cancels within milliseconds of the token
			// firing — a misbehaving one must not hang the request indefinitely (E8).
			try
			{
				await Task.WhenAll(pending).WaitAsync(DrainTimeout);
			}
			catch
			{
				// cancelled, timed out, or an already-logged watcher failure
			}
		}

		return new Outcome<T>(result, foundByWatchdog);
	}

	public readonly record struct Outcome<T>(T Result, bool FoundByWatchdog);
}

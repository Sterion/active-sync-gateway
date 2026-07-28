namespace ActiveSync.Backends.Local;

/// <summary>
///   In-process change signal for the local content stores: a write on one device wakes the
///   Ping/Sync waits of the user's other devices instantly. Notifications do not cross
///   process boundaries — multi-instance deployments fall back to the watchdog re-check.
/// </summary>
public sealed class LocalChangeNotifier
{
	private readonly Lock _lock = new();
	private readonly Dictionary<string, List<TaskCompletionSource>> _waiters = new(StringComparer.Ordinal);

	// The last time each key changed, updated on EVERY NotifyChanged call — including when
	// nobody is currently waiting. Mirrors ImapIdleWatcher.LastChangeUtc: a write that lands
	// between a caller's entry check and this wait's registration must still be visible to that
	// wait instead of being silently dropped for lack of a registered listener.
	private readonly Dictionary<string, DateTime> _lastChangeUtc = new(StringComparer.Ordinal);

	private static string Key(int userId, string collection)
	{
		return $"{userId}\n{collection}";
	}

	public void NotifyChanged(int userId, string collection)
	{
		string key = Key(userId, collection);
		List<TaskCompletionSource>? waiters;
		lock (_lock)
		{
			_lastChangeUtc[key] = DateTime.UtcNow;
			if (!_waiters.Remove(key, out waiters))
				return;
		}

		foreach (TaskCompletionSource waiter in waiters)
			waiter.TrySetResult();
	}

	/// <summary>
	///   Waits for a change signal, returning immediately (true, no listener registered) when the
	///   latch already shows a change strictly after <paramref name="sinceUtc" /> — the caller's
	///   own "as of" reference, captured as early as possible (e.g. right after an entry check
	///   found nothing pending). Otherwise behaves like the timeout-only overload. Returns false
	///   on timeout.
	/// </summary>
	public async Task<bool> WaitAsync(
		int userId, string collection, TimeSpan timeout, DateTime sinceUtc, CancellationToken ct)
	{
		string key = Key(userId, collection);
		TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
		lock (_lock)
		{
			if (_lastChangeUtc.TryGetValue(key, out DateTime last) && last > sinceUtc)
				return true; // latched: the change landed before this wait registered

			if (!_waiters.TryGetValue(key, out List<TaskCompletionSource>? list))
				_waiters[key] = list = [];
			list.Add(tcs);
		}

		try
		{
			await tcs.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
			return true;
		}
		catch (TimeoutException)
		{
			return false;
		}
		finally
		{
			lock (_lock)
			{
				if (_waiters.TryGetValue(key, out List<TaskCompletionSource>? list))
				{
					list.Remove(tcs);
					if (list.Count == 0)
						_waiters.Remove(key);
				}
			}
		}
	}
}

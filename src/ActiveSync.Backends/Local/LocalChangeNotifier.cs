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

	private static string Key(string userName, string collection)
	{
		return $"{userName}\n{collection}";
	}

	public void NotifyChanged(string userName, string collection)
	{
		List<TaskCompletionSource>? waiters;
		lock (_lock)
		{
			if (!_waiters.Remove(Key(userName, collection), out waiters))
				return;
		}

		foreach (TaskCompletionSource waiter in waiters)
			waiter.TrySetResult();
	}

	/// <summary>Waits for a change signal; returns false on timeout.</summary>
	public async Task<bool> WaitAsync(
		string userName, string collection, TimeSpan timeout, CancellationToken ct)
	{
		TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
		string key = Key(userName, collection);
		lock (_lock)
		{
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

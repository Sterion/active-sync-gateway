namespace ActiveSync.Plugin.Local;

/// <summary>
///   The push signal for one account's store root: a recursive <see cref="FileSystemWatcher" />
///   raced against a periodic re-stat of the watched directories, with the same change LATCH the
///   in-repo backends use (<c>LocalChangeNotifier</c>, <c>ImapIdleWatcher.LastChangeUtc</c>).
/// </summary>
/// <remarks>
///   <para>
///     The latch is what closes the race between the host's "anything pending?" entry check and the
///     wait registering: every change stamps its directory even when nobody is waiting, and a wait
///     starting with an earlier <c>sinceUtc</c> returns immediately.
///   </para>
///   <para>
///     The poll is not redundant with the watcher. <see cref="FileSystemWatcher" /> reports nothing
///     at all on many bind mounts, SMB/NFS shares and container filesystems, and drops events
///     wholesale on buffer overflow; the watcher is the latency optimization and the poll is the
///     correctness guarantee — exactly how the IMAP backend treats IDLE versus STATUS polling.
///   </para>
///   <para>
///     Owned by the PROVIDER, one per (login, root), never by a store: a connection's disposal
///     would otherwise tear the watcher down under a long-poll that is still parked on it.
///   </para>
/// </remarks>
internal sealed class FileTreeWatcher : IDisposable
{
	/// <summary>Directory paths are compared the way the host filesystem compares them.</summary>
	private static readonly StringComparer PathComparer =
		OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
			? StringComparer.OrdinalIgnoreCase
			: StringComparer.Ordinal;

	private readonly Lock _gate = new();
	private readonly Dictionary<string, DateTime> _lastChangeUtc = new(PathComparer);
	private readonly List<Waiter> _waiters = [];
	private readonly TimeSpan _pollInterval;
	private readonly string _root;
	private FileSystemWatcher? _watcher;
	private DateTime _lastGlobalChangeUtc = DateTime.MinValue;
	private bool _disposed;

	public FileTreeWatcher(string root, TimeSpan pollInterval)
	{
		_root = root;
		_pollInterval = pollInterval < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : pollInterval;
		TryStartWatcher();
	}

	/// <summary>The account root this watcher covers — the resource name for the admin dashboard.</summary>
	public string Root => _root;

	/// <summary>Whether the OS watcher is live; false means the wait runs on the poll alone.</summary>
	public bool WatcherActive
	{
		get
		{
			lock (_gate)
				return _watcher is not null;
		}
	}

	/// <summary>
	///   Stamps a directory as changed and wakes anyone waiting on it. Called both by the OS
	///   watcher and by the stores after their own writes, so a change is visible to another
	///   device's parked Ping without waiting for the filesystem to report it.
	/// </summary>
	public void NotifyChanged(string directory)
	{
		string key = Normalize(directory);
		lock (_gate)
		{
			_lastChangeUtc[key] = DateTime.UtcNow;
			foreach (Waiter waiter in _waiters)
				if (waiter.Directories.Contains(key))
					waiter.Signal();
		}
	}

	/// <summary>
	///   Waits until one of <paramref name="directories" /> changes, or the timeout elapses.
	///   Returns the changed directories (empty on timeout).
	/// </summary>
	/// <param name="directories">Absolute directory paths to watch.</param>
	/// <param name="timeout">How long to wait.</param>
	/// <param name="sinceUtc">
	///   Captured by the caller BEFORE it looked for pending changes; a change stamped after this
	///   instant satisfies the wait immediately, which is what makes a change landing between two
	///   requests reach the next one.
	/// </param>
	/// <param name="ct">Cancellation token aborting the wait.</param>
	public async Task<IReadOnlyList<string>> WaitAsync(
		IReadOnlyList<string> directories, TimeSpan timeout, DateTime sinceUtc, CancellationToken ct)
	{
		if (directories.Count == 0)
			return [];

		HashSet<string> watched = new(directories.Select(Normalize), PathComparer);
		List<string> latched = Latched(watched, sinceUtc);
		if (latched.Count > 0)
			return latched;

		Dictionary<string, string> baseline = new(PathComparer);
		foreach (string directory in watched)
			baseline[directory] = Signature(directory);

		Waiter waiter = new(watched);
		lock (_gate)
			_waiters.Add(waiter);
		try
		{
			DateTime deadline = DateTime.UtcNow + timeout;
			while (true)
			{
				TimeSpan remaining = deadline - DateTime.UtcNow;
				if (remaining <= TimeSpan.Zero)
					return [];

				await waiter.WaitAsync(remaining < _pollInterval ? remaining : _pollInterval, ct)
					.ConfigureAwait(false);

				latched = Latched(watched, sinceUtc);
				foreach (string directory in watched)
					if (!latched.Contains(directory, PathComparer)
					    && !string.Equals(Signature(directory), baseline[directory], StringComparison.Ordinal))
						latched.Add(directory);

				if (latched.Count > 0)
					return latched;
			}
		}
		finally
		{
			lock (_gate)
				_waiters.Remove(waiter);
			waiter.Dispose();
		}
	}

	public void Dispose()
	{
		FileSystemWatcher? watcher;
		lock (_gate)
		{
			if (_disposed)
				return;
			_disposed = true;
			watcher = _watcher;
			_watcher = null;
		}

		watcher?.Dispose();
	}

	/// <summary>Every directory stamped after <paramref name="sinceUtc" />, plus all of them after an overflow.</summary>
	private List<string> Latched(HashSet<string> watched, DateTime sinceUtc)
	{
		List<string> changed = [];
		lock (_gate)
		{
			if (_lastGlobalChangeUtc > sinceUtc)
				return [.. watched];
			foreach (string directory in watched)
				if (_lastChangeUtc.TryGetValue(directory, out DateTime last) && last > sinceUtc)
					changed.Add(directory);
		}

		return changed;
	}

	/// <summary>
	///   A directory's cheap content signature: item count, newest write and total size. This is
	///   the poll's equivalent of an IMAP STATUS — it detects any add, delete, rename or rewrite
	///   without reading a single file.
	/// </summary>
	internal static string Signature(string directory)
	{
		DirectoryInfo info = new(directory);
		if (!info.Exists)
			return "-";

		int count = 0;
		long newest = 0;
		long total = 0;
		try
		{
			foreach (FileInfo file in info.EnumerateFiles("*", SearchOption.TopDirectoryOnly))
			{
				if (!AtomicFile.IsItemFileName(file.Name))
					continue;
				count++;
				total += file.Length;
				long ticks = file.LastWriteTimeUtc.Ticks;
				if (ticks > newest)
					newest = ticks;
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// A directory that cannot be read right now must not end the wait with a bogus
			// "changed" — report a constant so the comparison stays stable until it can.
			return "?";
		}

		return $"{count}|{newest}|{total}";
	}

	private void TryStartWatcher()
	{
		try
		{
			Directory.CreateDirectory(_root);
			FileSystemWatcher watcher = new(_root)
			{
				IncludeSubdirectories = true,
				InternalBufferSize = 64 * 1024,
				NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
				                                      | NotifyFilters.LastWrite | NotifyFilters.Size
			};
			watcher.Created += OnChanged;
			watcher.Changed += OnChanged;
			watcher.Deleted += OnChanged;
			watcher.Renamed += OnRenamed;
			watcher.Error += OnError;
			watcher.EnableRaisingEvents = true;
			lock (_gate)
			{
				if (_disposed)
				{
					watcher.Dispose();
					return;
				}

				_watcher?.Dispose();
				_watcher = watcher;
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
		                           or ArgumentException or PlatformNotSupportedException)
		{
			// No OS watcher here (a filesystem that cannot watch, a root that vanished). The poll
			// still makes every wait correct, just at poll latency — the same degradation the IMAP
			// backend accepts when a server offers no IDLE.
			lock (_gate)
				_watcher = null;
		}
	}

	private void OnChanged(object sender, FileSystemEventArgs e)
	{
		if (AtomicFile.IsItemFileName(Path.GetFileName(e.FullPath)))
			NotifyChanged(Path.GetDirectoryName(e.FullPath) ?? _root);
	}

	private void OnRenamed(object sender, RenamedEventArgs e)
	{
		// A flag change IS a rename, and a move between folders touches two directories.
		NotifyChanged(Path.GetDirectoryName(e.FullPath) ?? _root);
		NotifyChanged(Path.GetDirectoryName(e.OldFullPath) ?? _root);
	}

	private void OnError(object sender, ErrorEventArgs e)
	{
		// Buffer overflow: the events we lost are unknowable, so latch EVERYTHING and rebuild the
		// watcher. Losing this would strand every parked wait until its poll tick.
		lock (_gate)
			_lastGlobalChangeUtc = DateTime.UtcNow;
		WakeAll();
		TryStartWatcher();
	}

	private void WakeAll()
	{
		lock (_gate)
			foreach (Waiter waiter in _waiters)
				waiter.Signal();
	}

	private static string Normalize(string directory)
	{
		return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
	}

	/// <summary>One parked wait: the directories it watches and the gate that wakes it.</summary>
	private sealed class Waiter(HashSet<string> directories) : IDisposable
	{
		private readonly SemaphoreSlim _signal = new(0, 1);

		public HashSet<string> Directories => directories;

		/// <summary>Wakes the wait. Called under the watcher's gate, so the count test is safe.</summary>
		public void Signal()
		{
			if (_signal.CurrentCount == 0)
				_signal.Release();
		}

		public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
		{
			await _signal.WaitAsync(timeout, ct).ConfigureAwait(false);
		}

		public void Dispose()
		{
			_signal.Dispose();
		}
	}
}

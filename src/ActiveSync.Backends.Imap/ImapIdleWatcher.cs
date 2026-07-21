using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using Microsoft.Extensions.Logging;

namespace ActiveSync.Backends.Imap;

/// <summary>
///   Long-lived IMAP IDLE watcher, one per (user, folder), shared by all of the user's
///   devices and decoupled from the request lifecycle: the dedicated connection keeps
///   idling the folder across Pings, and events are latched (<see cref="LastChangeUtc" />)
///   so a change that fires while no wait is registered is still seen by the next wait.
///   This is a latency optimization only — the exact entry check and the watchdog re-check
///   remain the correctness guarantees.
/// </summary>
public sealed class ImapIdleWatcher(
	ImapOptions options,
	BackendCredentials credentials,
	string folderFullName,
	ILogger logger,
	ILogger? wireLogger = null) : IAsyncDisposable
{
	/// <summary>MailKit guidance: re-issue IDLE well before the RFC 2177 29-minute server timeout.</summary>
	private static readonly TimeSpan IdleSlice = TimeSpan.FromMinutes(9);

	private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(5);
	private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

	private readonly Lock _lock = new();
	private readonly CancellationTokenSource _stopCts = new();
	private readonly List<TaskCompletionSource> _waiters = [];
	private long _lastChangeTicks;
	private Task? _loop;
	private volatile bool _unavailable;

	public BackendCredentials Credentials { get; } = credentials;

	/// <summary>UTC time of the most recent folder event (the latch); MinValue when none yet.</summary>
	public DateTime LastChangeUtc => new(Interlocked.Read(ref _lastChangeTicks), DateTimeKind.Utc);

	public async ValueTask DisposeAsync()
	{
		await _stopCts.CancelAsync().ConfigureAwait(false);
		MarkUnavailable();
		if (_loop is not null)
			try
			{
				// Joining our own background loop after cancelling it — no foreign
				// context involved, so the VSTHRD003 deadlock concern does not apply.
#pragma warning disable VSTHRD003
				await _loop.ConfigureAwait(false);
#pragma warning restore VSTHRD003
			}
			catch
			{
				// loop exceptions were already logged
			}

		_stopCts.Dispose();
	}

	/// <summary>
	///   Waits for a change in the watched folder. Returns true when a change occurred after
	///   <paramref name="sinceUtc" /> (immediately for latched events), false on timeout, and
	///   null when IDLE is unavailable so the caller falls back to STATUS polling.
	/// </summary>
	public async Task<bool?> WaitForChangeAsync(DateTime sinceUtc, TimeSpan timeout, CancellationToken ct)
	{
		if (_unavailable)
			return null;
		EnsureStarted();

		if (LastChangeUtc > sinceUtc)
			return true; // latched: the event fired before this wait registered

		TaskCompletionSource waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
		lock (_lock)
		{
			_waiters.Add(waiter);
		}

		try
		{
			// Re-check after registration: an event may have slipped in between.
			if (LastChangeUtc > sinceUtc)
				return true;
			await waiter.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
			return true;
		}
		catch (TimeoutException)
		{
			return false;
		}
		catch (OperationCanceledException) when (!ct.IsCancellationRequested)
		{
			return null; // watcher stopped or became unavailable mid-wait
		}
		finally
		{
			lock (_lock)
			{
				_waiters.Remove(waiter);
			}
		}
	}

	private void EnsureStarted()
	{
		if (_loop is not null)
			return;
		lock (_lock)
		{
			_loop ??= Task.Run(() => RunAsync(_stopCts.Token));
		}
	}

	private async Task RunAsync(CancellationToken ct)
	{
		logger.LogInformation("IMAP IDLE watcher started for {User} on \"{Folder}\"",
			Credentials.UserName, folderFullName);
		TimeSpan backoff = InitialBackoff;
		bool connectionLost = false;
		while (!ct.IsCancellationRequested)
			try
			{
				using ImapClient client = await ImapConnectionFactory.ConnectAsync(options, Credentials, ct, wireLogger)
					.ConfigureAwait(false);

				if (!client.Capabilities.HasFlag(ImapCapabilities.Idle))
				{
					logger.LogInformation(
						"IMAP server lacks IDLE; watcher for {User} disabled (STATUS polling covers push)",
						Credentials.UserName);
					MarkUnavailable();
					await client.DisconnectAsync(true, CancellationToken.None).ConfigureAwait(false);
					return;
				}

				IMailFolder folder = await client.GetFolderAsync(folderFullName, ct).ConfigureAwait(false);
				await folder.OpenAsync(FolderAccess.ReadOnly, ct).ConfigureAwait(false);
				if (connectionLost)
				{
					logger.LogInformation("IMAP IDLE watcher reconnected for {User} on \"{Folder}\"",
						Credentials.UserName, folderFullName);
					connectionLost = false;
				}

				backoff = InitialBackoff;

				void OnCountChanged(object? sender, EventArgs e)
				{
					OnEvent("message count changed");
				}

				void OnExpunged(object? sender, EventArgs e)
				{
					OnEvent("message expunged");
				}

				void OnFlagsChanged(object? sender, EventArgs e)
				{
					OnEvent("message flags changed");
				}

				folder.CountChanged += OnCountChanged;
				folder.MessageExpunged += OnExpunged;
				folder.MessageFlagsChanged += OnFlagsChanged;
				try
				{
					while (!ct.IsCancellationRequested)
					{
						// Events are raised during IdleAsync and do NOT end it — the
						// connection keeps watching; slices only refresh the IDLE command.
						using CancellationTokenSource slice = CancellationTokenSource.CreateLinkedTokenSource(ct);
						slice.CancelAfter(IdleSlice);
						await client.IdleAsync(slice.Token, ct).ConfigureAwait(false);
					}
				}
				finally
				{
					folder.CountChanged -= OnCountChanged;
					folder.MessageExpunged -= OnExpunged;
					folder.MessageFlagsChanged -= OnFlagsChanged;
					try
					{
						await client.DisconnectAsync(true, CancellationToken.None).ConfigureAwait(false);
					}
					catch
					{
						// best effort
					}
				}
			}
			catch (OperationCanceledException) when (ct.IsCancellationRequested)
			{
				break;
			}
			catch (AuthenticationException ex)
			{
				// Stale credentials (rotation) — stop; the session factory rebuilds the
				// watcher when it sees a new password for this user.
				logger.LogWarning(ex, "IMAP IDLE watcher for {User}: authentication failed; watcher stopped",
					Credentials.UserName);
				MarkUnavailable();
				return;
			}
			catch (Exception ex)
			{
				if (!connectionLost)
				{
					logger.LogWarning(ex,
						"IMAP IDLE watcher for {User} on \"{Folder}\" lost its connection; reconnecting",
						Credentials.UserName, folderFullName);
					connectionLost = true;
				}

				try
				{
					await Task.Delay(backoff, ct).ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					break;
				}

				backoff = backoff * 2 > MaxBackoff ? MaxBackoff : backoff * 2;
			}

		logger.LogInformation("IMAP IDLE watcher stopped for {User} on \"{Folder}\"",
			Credentials.UserName, folderFullName);
	}

	private void OnEvent(string reason)
	{
		Interlocked.Exchange(ref _lastChangeTicks, DateTime.UtcNow.Ticks);
		logger.LogInformation("IMAP IDLE: {Reason} in \"{Folder}\" for {User}",
			reason, folderFullName, Credentials.UserName);
		ReleaseWaiters(false);
	}

	private void MarkUnavailable()
	{
		_unavailable = true;
		ReleaseWaiters(true); // cancelled waiters report null → callers fall back to polling
	}

	private void ReleaseWaiters(bool cancel)
	{
		List<TaskCompletionSource> waiters;
		lock (_lock)
		{
			waiters = [.. _waiters];
			_waiters.Clear();
		}

		foreach (TaskCompletionSource waiter in waiters)
			if (cancel)
				waiter.TrySetCanceled();
			else
				waiter.TrySetResult();
	}
}

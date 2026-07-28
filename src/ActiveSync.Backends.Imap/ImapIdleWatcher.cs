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

	/// <summary>
	///   G6: MailKit raises <see cref="AuthenticationException" /> for ANY negative LOGIN/
	///   AUTHENTICATE reply, not only a genuinely wrong password — including a transient one
	///   (Dovecot's "NO [UNAVAILABLE] Maximum number of connections from user+IP exceeded", which
	///   this watcher's dedicated-connection-per-(user,folder) shape provokes). Retry with the
	///   normal capped backoff for this many CONSECUTIVE auth failures before treating it as a
	///   real credential rejection and latching the watcher unavailable.
	/// </summary>
	private const int MaxTransientAuthFailures = 3;

	private readonly Lock _lock = new();
	private readonly CancellationTokenSource _stopCts = new();
	private readonly List<TaskCompletionSource> _waiters = [];
	private long _lastChangeTicks;
	private Task? _loop;
	private volatile bool _unavailable;

	public BackendCredentials Credentials { get; } = credentials;

	/// <summary>
	///   The resolved connection options this watcher was built with (G7) — compared alongside
	///   <see cref="Credentials" /> by <see cref="ImapBackendProvider.GetOrCreateWatcher" /> so a
	///   per-user host/port/security change rebuilds the watcher instead of reusing one still
	///   pointed at the old server.
	/// </summary>
	internal ImapOptions Options { get; } = options;

	/// <summary>UTC time of the most recent folder event (the latch); MinValue when none yet.</summary>
	public DateTime LastChangeUtc => new(Interlocked.Read(ref _lastChangeTicks), DateTimeKind.Utc);

	/// <summary>
	///   G27: whether the background IDLE loop has actually been started (a connection either is or
	///   is being established) — as opposed to merely CONSTRUCTED. <see cref="ImapBackendProvider" />
	///   dereferences its cached <c>Lazy&lt;ImapIdleWatcher&gt;</c> on every
	///   <c>GetOrCreateWatcher</c> call just to compare credentials/options, so the watcher object
	///   always exists well before the first real wait; only <see cref="EnsureStarted" /> (called
	///   from <see cref="WaitForChangeAsync" />) means a connection was ever attempted.
	/// </summary>
	internal bool IsStarted => _loop is not null;

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
		int consecutiveAuthFailures = 0;
		while (!ct.IsCancellationRequested)
			try
			{
				using ImapClient client = await ImapConnectionFactory.ConnectAsync(Options, Credentials, ct, wireLogger)
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
				consecutiveAuthFailures = 0;

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
			catch (AuthenticationException ex) when (++consecutiveAuthFailures < MaxTransientAuthFailures)
			{
				// Within the transient-failure budget — retry with the normal capped backoff
				// instead of latching unavailable on what may be a momentary rejection (e.g. a
				// per-user-IP connection cap) rather than a genuine credential problem.
				logger.LogWarning(ex,
					"IMAP IDLE watcher for {User}: authentication failed (attempt {Attempt}/{Max}); retrying",
					Credentials.UserName, consecutiveAuthFailures, MaxTransientAuthFailures);
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
			catch (AuthenticationException ex)
			{
				// Exhausted the transient-failure budget: MaxTransientAuthFailures consecutive
				// rejections without a single successful connect in between — a genuine
				// credential problem (or a sustained block), not a momentary connection-cap hit.
				// Stop; the session factory rebuilds the watcher when it sees a new password for
				// this user.
				logger.LogWarning(ex,
					"IMAP IDLE watcher for {User}: authentication failed {Attempts} times in a row; watcher stopped",
					Credentials.UserName, consecutiveAuthFailures);
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

using System.Net.Sockets;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using MailKit;
using MailKit.Net.Imap;
using Microsoft.Extensions.Logging;

namespace ActiveSync.Backends.Imap;

/// <summary>
///   The dedicated connection the Ping/Sync STATUS poll runs on. One per gateway user,
///   shared by all of that user's devices and folders (exactly like <see cref="ImapIdleWatcher" />),
///   owned by <see cref="ImapBackendProvider" /> and evicted when the user's last session goes.
///   <para>
///     The defect it fixes is a shared GATE, not a shared connection: <c>SnapshotStatusAsync</c>
///     used to run through <see cref="ImapSession.RunAsync{T}" />, the same per-session semaphore
///     <c>GetItemRevisionsAsync</c> holds for its (deliberately unpaged) whole-mailbox FETCH, so
///     one device's Sync round stalled every other device's push detection behind it. So this type
///     is a SECOND gate over a PERSISTENT client — never a connection per poll. A poll runs every
///     30 s for the whole heartbeat, on every long-poll (<c>WaitForChangesAsync</c> races the poll
///     against IDLE rather than using it only as an IDLE fallback), so reconnecting per call would
///     be ~118 logins per device per hour and would aggravate the very per-user connection cap
///     <see cref="ImapIdleWatcher" />'s transient-auth retry budget exists to survive. Steady state is three
///     connections per user: session + IDLE + poll.
///   </para>
/// </summary>
public sealed class ImapStatusPoller(
	ImapOptions options,
	BackendCredentials credentials,
	ILogger logger,
	ILogger? wireLogger = null) : IAsyncDisposable
{
	private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(5);
	private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

	private readonly SemaphoreSlim _gate = new(1, 1);
	private TimeSpan _backoff = InitialBackoff;
	private ImapClient? _client;
	private int _disposed;
	private DateTime _retryNotBeforeUtc = DateTime.MinValue;

	/// <summary>The credentials this poll connection authenticates with (rotation rebuilds it).</summary>
	public BackendCredentials Credentials { get; } = credentials;

	/// <summary>
	///   The resolved connection options this poller was built with — compared alongside
	///   <see cref="Credentials" /> by <see cref="ImapBackendProvider.GetOrCreatePoller" /> so a
	///   per-user host/port/security change rebuilds it instead of leaving a live connection
	///   pointed at the old server (the same rule the IDLE watcher's connection options follow).
	/// </summary>
	internal ImapOptions Options { get; } = options;

	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) == 1)
			return;

		// Bounded: a wedged in-flight STATUS must not block eviction forever. The gate itself is
		// deliberately NOT disposed — a SemaphoreSlim with no WaitHandle use needs no disposal,
		// and disposing it would race an in-flight Release() into an ObjectDisposedException.
		bool acquired = await _gate.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
		try
		{
			await DisposeClientAsync().ConfigureAwait(false);
		}
		finally
		{
			if (acquired)
				_gate.Release();
		}
	}

	/// <summary>
	///   STATUS fingerprints for the given folder backend keys, over this poller's own persistent
	///   connection. Folders that no longer exist map to "gone" rather than throwing.
	/// </summary>
	public async Task<Dictionary<string, string>> StatusAsync(
		IReadOnlyList<string> folderBackendKeys, CancellationToken ct)
	{
		if (Volatile.Read(ref _disposed) == 1)
			throw new BackendException("IMAP status poller has been disposed.");

		bool IsTransient(Exception ex)
		{
			if (ct.IsCancellationRequested)
				return false;
			if (ex is OperationCanceledException)
				return true; // a MailKit per-op Timeout cancels an INTERNAL token, not ours
			return ex is IOException or ImapProtocolException or ServiceNotConnectedException or SocketException;
		}

		await _gate.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			return await TransientRetry.RunAsync(async () =>
			{
				ImapClient client = await EnsureConnectedAsync(ct).ConfigureAwait(false);
				try
				{
					return await ReadStatusAsync(client, folderBackendKeys, ct).ConfigureAwait(false);
				}
				catch (Exception ex) when (IsTransient(ex))
				{
					await DisposeClientAsync().ConfigureAwait(false); // next attempt reconnects clean
					throw;
				}
			}, IsTransient, ct, idempotent: true, onRetry: (ex, attempt) =>
			{
				Core.Observability.GatewayMetrics.RecordBackendRetry("imap");
				logger.LogWarning(ex, "IMAP status poll transient failure for {User}; reconnecting (retry {Attempt}/{Max})",
					Credentials.UserName, attempt, TransientRetry.DelaysMs.Length);
			}).ConfigureAwait(false);
		}
		finally
		{
			_gate.Release();
		}
	}

	/// <summary>
	///   The STATUS fingerprint read itself, shared with <c>ImapMailBackend</c>'s session-based
	///   fallback (used when no provider-owned poller is available, e.g. direct construction).
	/// </summary>
	internal static async Task<Dictionary<string, string>> ReadStatusAsync(
		ImapClient client, IReadOnlyList<string> folderBackendKeys, CancellationToken ct)
	{
		Dictionary<string, string> map = new(StringComparer.Ordinal);
		foreach (string key in folderBackendKeys)
			try
			{
				IMailFolder folder = await client.GetFolderAsync(ImapSession.FromBackendKey(key), ct)
					.ConfigureAwait(false);
				// UIDVALIDITY leads the fingerprint so a reset (mailbox recreated, restored,
				// migrated) always reads as a change even when count/uidnext/unread happen to
				// land identically — that is the moment every stored item key goes stale.
				await folder.StatusAsync(
						StatusItems.Count | StatusItems.UidNext | StatusItems.Unread | StatusItems.UidValidity, ct)
					.ConfigureAwait(false);
				map[key] = $"{folder.UidValidity}:{folder.Count}:{folder.UidNext}:{folder.Unread}";
			}
			catch (FolderNotFoundException)
			{
				map[key] = "gone";
			}

		return map;
	}

	/// <summary>
	///   Lazy start plus capped-backoff reconnect: the connection is opened on the first poll and
	///   then reused. After a failed connect the next attempt is refused (cheaply, without a socket)
	///   until the backoff elapses, so a poll loop cannot hammer a refusing server every 30 s across
	///   every device of the user — the same InitialBackoff/MaxBackoff schedule the IDLE watcher uses.
	/// </summary>
	private async Task<ImapClient> EnsureConnectedAsync(CancellationToken ct)
	{
		if (_client is { IsConnected: true, IsAuthenticated: true })
			return _client;

		await DisposeClientAsync().ConfigureAwait(false);
		if (DateTime.UtcNow < _retryNotBeforeUtc)
			throw new BackendException(
				$"IMAP status poll connection for {Credentials.UserName} is backing off after a failed connect.");

		try
		{
			_client = await ImapConnectionFactory.ConnectAsync(Options, Credentials, ct, wireLogger)
				.ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
		{
			_retryNotBeforeUtc = DateTime.UtcNow + _backoff;
			_backoff = _backoff * 2 > MaxBackoff ? MaxBackoff : _backoff * 2;
			logger.LogWarning(ex,
				"IMAP status poll connection for {User} failed; next attempt in {Backoff}",
				Credentials.UserName, _retryNotBeforeUtc - DateTime.UtcNow);
			throw;
		}

		_backoff = InitialBackoff;
		_retryNotBeforeUtc = DateTime.MinValue;
		// Per-op inactivity timeout, tighter than MailKit's 120 s default, so a hung STATUS fails
		// fast enough to retry inside a short heartbeat (the session client is set the same way).
		_client.Timeout = 30_000;
		return _client;
	}

	private async Task DisposeClientAsync()
	{
		if (_client is null)
			return;
		try
		{
			if (_client.IsConnected)
				await _client.DisconnectAsync(true).ConfigureAwait(false);
		}
		catch
		{
			// best effort
		}

		_client.Dispose();
		_client = null;
	}
}

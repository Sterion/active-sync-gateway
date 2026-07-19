using System.Net.Sockets;
using ActiveSync.Core.Backend;
using MailKit;
using MailKit.Net.Imap;
using Microsoft.Extensions.Logging;

namespace ActiveSync.Backends.Imap;

/// <summary>
///   Owns one MailKit <see cref="ImapClient" /> for a backend session. MailKit clients are not
///   thread-safe, so every use goes through <see cref="RunAsync{T}" /> which serializes access and
///   transparently reconnects dropped connections.
/// </summary>
public sealed class ImapSession(
	ImapOptions options, BackendCredentials credentials, ILogger logger,
	ILogger? wireLogger = null) : IAsyncDisposable
{
	public const string KeyPrefix = "imap:";
	private readonly SemaphoreSlim _gate = new(1, 1);
	private ImapClient? _client;

	/// <summary>The IMAP login this session authenticates as (for log lines).</summary>
	public string UserName => credentials.UserName;

	public async ValueTask DisposeAsync()
	{
		await _gate.WaitAsync().ConfigureAwait(false);
		try
		{
			await DisposeClientAsync().ConfigureAwait(false);
		}
		finally
		{
			_gate.Release();
			_gate.Dispose();
		}
	}

	public static string ToBackendKey(string fullName)
	{
		return KeyPrefix + fullName;
	}

	public static string FromBackendKey(string backendKey)
	{
		return backendKey.StartsWith(KeyPrefix, StringComparison.Ordinal)
			? backendKey[KeyPrefix.Length..]
			: throw new BackendException($"Not an IMAP folder key: {backendKey}");
	}

	public async Task<T> RunAsync<T>(
		Func<ImapClient, Task<T>> action, CancellationToken ct, bool idempotent = true)
	{
		// Idempotent ops (reads, flag stores, moves, deletes, folder ops) replay on any transient
		// drop. A non-idempotent op (APPEND: draft create, content-bearing draft edit, save-to-Sent)
		// replays ONLY on a clean "not connected" — the command never left the client (e.g. the
		// pooled connection idled out between requests). A mid-flight IOException/timeout might have
		// reached the server, so it is surfaced rather than risk a duplicate message.
		bool IsTransient(Exception ex)
		{
			if (ct.IsCancellationRequested)
				return false;
			if (ex is OperationCanceledException)
				return true; // a MailKit per-op Timeout cancels an INTERNAL token, not ours
			return ex is IOException or ImapProtocolException or ServiceNotConnectedException or SocketException;
		}

		bool IsCleanNotConnected(Exception ex)
		{
			return !ct.IsCancellationRequested && ex is ServiceNotConnectedException;
		}

		Func<Exception, bool> transient = idempotent ? IsTransient : IsCleanNotConnected;

		await _gate.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			// idempotent:true here always — the safety choice is already baked into `transient`
			// (a non-idempotent op only ever matches the clean not-connected case).
			return await TransientRetry.RunAsync(async () =>
			{
				ImapClient client = await EnsureConnectedAsync(ct).ConfigureAwait(false);
				try
				{
					return await action(client).ConfigureAwait(false);
				}
				catch (Exception ex) when (transient(ex))
				{
					await DisposeClientAsync().ConfigureAwait(false); // next attempt reconnects clean
					throw;
				}
			}, transient, ct, idempotent: true, onRetry: (ex, attempt) =>
			{
				Core.Observability.GatewayMetrics.RecordBackendRetry("imap");
				logger.LogWarning(ex, "IMAP transient failure for {User}; reconnecting (retry {Attempt}/{Max})",
					credentials.UserName, attempt, TransientRetry.DelaysMs.Length);
			}).ConfigureAwait(false);
		}
		finally
		{
			_gate.Release();
		}
	}

	public Task RunAsync(Func<ImapClient, Task> action, CancellationToken ct, bool idempotent = true)
	{
		return RunAsync(async client =>
		{
			await action(client).ConfigureAwait(false);
			return true;
		}, ct, idempotent);
	}

	private async Task<ImapClient> EnsureConnectedAsync(CancellationToken ct)
	{
		if (_client is { IsConnected: true, IsAuthenticated: true })
			return _client;

		await DisposeClientAsync().ConfigureAwait(false);
		_client = await ImapConnectionFactory.ConnectAsync(options, credentials, ct, wireLogger).ConfigureAwait(false);
		// Per-op inactivity timeout, tighter than MailKit's 120 s default, so a hung command fails
		// fast enough to retry within a short heartbeat. Set on the session client only — the IMAP
		// IDLE watcher builds its own client via the factory and keeps the long default (its slices
		// are minutes long). MailKit resets this on socket activity, so streaming FETCHes are fine.
		_client.Timeout = 30_000;
		return _client;
	}

	/// <summary>Opens the folder identified by a backend key (read-write unless specified).</summary>
	public static async Task<IMailFolder> OpenFolderAsync(
		ImapClient client, string backendKey, FolderAccess access, CancellationToken ct)
	{
		IMailFolder folder = await client.GetFolderAsync(FromBackendKey(backendKey), ct).ConfigureAwait(false);
		if (!folder.IsOpen || folder.Access < access)
			await folder.OpenAsync(access, ct).ConfigureAwait(false);
		return folder;
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

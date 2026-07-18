using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
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

	public async Task<T> RunAsync<T>(Func<ImapClient, Task<T>> action, CancellationToken ct)
	{
		await _gate.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			ImapClient client = await EnsureConnectedAsync(ct).ConfigureAwait(false);
			try
			{
				return await action(client).ConfigureAwait(false);
			}
			catch (Exception ex) when (ex is IOException or ImapProtocolException or ServiceNotConnectedException)
			{
				Core.Observability.GatewayMetrics.RecordBackendError("imap");
				logger.LogWarning(ex, "IMAP connection dropped for {User}; reconnecting", credentials.UserName);
				await DisposeClientAsync().ConfigureAwait(false);
				client = await EnsureConnectedAsync(ct).ConfigureAwait(false);
				return await action(client).ConfigureAwait(false);
			}
		}
		finally
		{
			_gate.Release();
		}
	}

	public Task RunAsync(Func<ImapClient, Task> action, CancellationToken ct)
	{
		return RunAsync(async client =>
		{
			await action(client).ConfigureAwait(false);
			return true;
		}, ct);
	}

	private async Task<ImapClient> EnsureConnectedAsync(CancellationToken ct)
	{
		if (_client is { IsConnected: true, IsAuthenticated: true })
			return _client;

		await DisposeClientAsync().ConfigureAwait(false);
		_client = await ImapConnectionFactory.ConnectAsync(options, credentials, ct, wireLogger).ConfigureAwait(false);
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

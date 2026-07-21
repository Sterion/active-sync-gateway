using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using Microsoft.Extensions.Logging;

namespace ActiveSync.Backends.Jmap;

/// <summary>
///   A long-lived per-user JMAP EventSource (SSE) consumer: it holds the stream open and
///   latches a signal whenever the server pushes a <c>StateChange</c>, so Ping/Sync waits wake
///   within a fraction of a second instead of on the poll interval. It is an accelerator only —
///   callers still poll as the correctness backstop (a missed or unavailable push just falls
///   back to the poll). Modelled on the IMAP IDLE watcher: dedicated connection, reconnect with
///   backoff, and a latch so a change firing between waits is not lost.
/// </summary>
public sealed class JmapEventSourceWatcher : IAsyncDisposable
{
	private readonly JmapClient _client;
	private readonly ILogger _logger;
	private readonly CancellationTokenSource _cts = new();
	private readonly Task _loop;
	private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private long _lastChangeTicks = DateTime.UtcNow.Ticks;

	public JmapEventSourceWatcher(JmapClient client, BackendCredentials credentials, ILogger logger)
	{
		_client = client;
		Credentials = credentials;
		_logger = logger;
		_loop = Task.Run(RunAsync);
	}

	/// <summary>The credentials this watcher authenticates with (so the provider can rotate on change).</summary>
	public BackendCredentials Credentials { get; }

	/// <summary>
	///   Completes when a change is pushed after <paramref name="afterUtc" />. If one already
	///   arrived since then, returns immediately (the latch), so a push between waits is not lost.
	/// </summary>
	public Task WaitForChangeAsync(DateTime afterUtc, CancellationToken ct)
	{
		if (new DateTime(Interlocked.Read(ref _lastChangeTicks), DateTimeKind.Utc) > afterUtc)
			return Task.CompletedTask;
		// The signal TCS is completed by the background SSE loop — the shared-latch pattern the
		// IMAP IDLE watcher uses too.
#pragma warning disable VSTHRD003
		return _signal.Task.WaitAsync(ct);
#pragma warning restore VSTHRD003
	}

	public async ValueTask DisposeAsync()
	{
		await _cts.CancelAsync().ConfigureAwait(false);
		try
		{
			// Awaiting the ctor-started background loop to finish before disposing the client.
#pragma warning disable VSTHRD003
			await _loop.ConfigureAwait(false);
#pragma warning restore VSTHRD003
		}
		catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
		{
			// expected on shutdown
		}

		_client.Dispose();
		_cts.Dispose();
	}

	private async Task RunAsync()
	{
		int backoffSeconds = 1;
		while (!_cts.IsCancellationRequested)
		{
			try
			{
				using HttpResponseMessage? response =
					await _client.OpenEventSourceAsync(30, _cts.Token).ConfigureAwait(false);
				if (response is null)
					return; // server advertises no EventSource — nothing to watch
				backoffSeconds = 1;
				await using Stream stream = await response.Content.ReadAsStreamAsync(_cts.Token).ConfigureAwait(false);
				using StreamReader reader = new(stream);
				string currentEvent = "";
				while (!_cts.IsCancellationRequested)
				{
					string? line = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
					if (line is null)
						break; // stream closed — reconnect
					if (line.Length == 0)
					{
						currentEvent = "";
						continue;
					}

					if (line.StartsWith("event:", StringComparison.Ordinal))
						currentEvent = line[6..].Trim();
					else if (line.StartsWith("data:", StringComparison.Ordinal) && currentEvent != "ping")
						Signal(); // a state change (anything that is not the keep-alive ping)
				}
			}
			catch (OperationCanceledException)
			{
				return;
			}
			catch (Exception ex)
			{
				_logger.LogDebug(ex, "JMAP EventSource dropped for {User}; reconnecting", Credentials.UserName);
			}

			try
			{
				await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), _cts.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				return;
			}

			backoffSeconds = Math.Min(backoffSeconds * 2, 60);
		}
	}

	private void Signal()
	{
		Interlocked.Exchange(ref _lastChangeTicks, DateTime.UtcNow.Ticks);
		TaskCompletionSource previous = Interlocked.Exchange(
			ref _signal, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
		previous.TrySetResult();
	}
}

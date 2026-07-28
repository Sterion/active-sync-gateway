using System.Text;
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
				// H12: BackendHttpClientFactory documents this stream (opened with ResponseHeadersRead)
				// as exempt from MaxResponseContentBufferSize — a plain StreamReader.ReadLineAsync here
				// grows its internal buffer without bound if the server ever emits a line with no '\n'.
				// CappedLineReader abandons the connection (throws, caught by the reconnect logic below)
				// once a single record exceeds the cap instead.
				CappedLineReader reader = new(stream);
				// H6: dispatch once at the record boundary (a blank line — SSE fields have no fixed
				// order within a record, so a ping's "data:" line can legally arrive before its
				// "event: ping" line, and signalling as soon as "data:" is seen mis-latches on that).
				string currentEvent = "";
				bool sawData = false;
				while (!_cts.IsCancellationRequested)
				{
					string? line = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
					if (line is null)
						break; // stream closed — reconnect
					if (line.Length == 0)
					{
						if (sawData && currentEvent != "ping")
							Signal(); // a state change (anything that is not the keep-alive ping)
						currentEvent = "";
						sawData = false;
						continue;
					}

					if (line.StartsWith("event:", StringComparison.Ordinal))
						currentEvent = line[6..].Trim();
					else if (line.StartsWith("data:", StringComparison.Ordinal))
						sawData = true;
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

	/// <summary>
	///   H12: a forward-only, size-capped line reader over the raw SSE byte stream. Unlike
	///   <see cref="StreamReader" />.ReadLineAsync, which grows its internal buffer without bound
	///   when a line never terminates, this throws once a single unterminated record exceeds
	///   <see cref="MaxLineBytes" /> — an SSE record here (a tiny StateChange payload or a ping) is
	///   always a few hundred bytes, so the cap is generous while still being finite. The thrown
	///   <see cref="BackendException" /> is caught by <see cref="RunAsync" />'s existing reconnect
	///   logic, same as any other dropped connection.
	/// </summary>
	private sealed class CappedLineReader(Stream inner)
	{
		private const int MaxLineBytes = 64 * 1024;

		private readonly byte[] _buffer = new byte[4096];
		private int _position;
		private int _length;

		public async Task<string?> ReadLineAsync(CancellationToken ct)
		{
			using MemoryStream line = new();
			while (true)
			{
				if (_position >= _length)
				{
					_length = await inner.ReadAsync(_buffer, ct).ConfigureAwait(false);
					_position = 0;
					if (_length == 0)
						return line.Length > 0 ? Decode(line) : null; // EOF — reconnect
				}

				byte b = _buffer[_position++];
				if (b == (byte)'\n')
					return Decode(line);
				line.WriteByte(b);
				if (line.Length > MaxLineBytes)
					throw new BackendException(
						$"JMAP EventSource record exceeded the {MaxLineBytes}-byte line cap.");
			}
		}

		private static string Decode(MemoryStream line)
		{
			byte[] bytes = line.ToArray();
			int length = bytes.Length;
			if (length > 0 && bytes[length - 1] == (byte)'\r') // StreamReader.ReadLine parity: trim a lone CR before LF
				length--;
			return Encoding.UTF8.GetString(bytes, 0, length);
		}
	}
}

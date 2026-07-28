using System.Net;
using System.Reflection;
using System.Text;
using ActiveSync.Backends.Jmap;
using ActiveSync.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Core.Tests;

/// <summary>
///   H6: the SSE watcher signalled on any non-ping <c>data:</c> line without buffering a full
///   record, so a ping whose <c>data:</c> line happens to precede its <c>event:</c> line (both
///   legal per the SSE spec — field order within a record is not fixed) spuriously woke every
///   wait, defeating the accelerator (over-signalling is otherwise harmless — the poll backstop
///   still governs correctness, but it burns a wakeup every keep-alive interval for nothing).
/// </summary>
public sealed class JmapEventSourceWatcherTests
{
	private static readonly Uri Base = new("http://localhost:5232");

	private const string SessionJson = """
	{
	  "capabilities": { "urn:ietf:params:jmap:core": {} },
	  "primaryAccounts": { "urn:ietf:params:jmap:core": "c" },
	  "apiUrl": "http://localhost:5232/jmap/",
	  "downloadUrl": "http://localhost:5232/jmap/download/{accountId}/{blobId}/{name}?accept={type}",
	  "uploadUrl": "http://localhost:5232/jmap/upload/{accountId}/",
	  "eventSourceUrl": "http://localhost:5232/jmap/eventsource/?types={types}&closeafter={closeafter}&ping={ping}",
	  "state": "abc"
	}
	""";

	[Fact]
	public async Task PingRecord_WithDataLineBeforeEventLine_DoesNotSignal()
	{
		// One SSE record whose "data:" line precedes its "event: ping" line, then the stream ends.
		const string sse = "data: {}\r\nevent: ping\r\n\r\n";

		StubHandler stub = new(request => request.RequestUri!.AbsolutePath.StartsWith("/jmap/eventsource")
			? Sse(sse)
			: Json(SessionJson));

		// Captured right after construction, before the background loop has had a chance to reach
		// the stream (it still has to fetch the session, then the eventsource response) — a Signal()
		// stamps _lastChangeTicks strictly after this, so WaitForChangeAsync(start) genuinely waits
		// on the latch instead of short-circuiting on the constructor's own initial timestamp.
		await using JmapEventSourceWatcher watcher = new(
			new JmapClient(Base, new HttpClient(stub)), new BackendCredentials("u", "p"), NullLogger.Instance);
		DateTime start = DateTime.UtcNow;

		Task wait = watcher.WaitForChangeAsync(start, CancellationToken.None);
		Task winner = await Task.WhenAny(wait, Task.Delay(TimeSpan.FromMilliseconds(500)));

		Assert.NotSame(wait, winner); // must still be waiting — the ping must not have signalled
	}

	[Fact]
	public async Task StateChangeRecord_DoesSignal()
	{
		// A genuine push (RFC 8620 §7.3 names the event type "state") must still wake the wait,
		// so the H6 fix (only dispatching at the record boundary) doesn't accidentally swallow
		// real changes along with the ping fix.
		const string sse = "event: state\r\ndata: {\"changed\":{}}\r\n\r\n";

		StubHandler stub = new(request => request.RequestUri!.AbsolutePath.StartsWith("/jmap/eventsource")
			? Sse(sse)
			: Json(SessionJson));

		await using JmapEventSourceWatcher watcher = new(
			new JmapClient(Base, new HttpClient(stub)), new BackendCredentials("u", "p"), NullLogger.Instance);
		DateTime start = DateTime.UtcNow;

		Task wait = watcher.WaitForChangeAsync(start, CancellationToken.None);
		Task winner = await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(2)));

		Assert.Same(wait, winner); // the state-change record must have signalled
	}

	// H14 (coverage, not red-first proof — see fix-review.md's "genuinely cannot be reproduced"
	// clause). The defect was a single-VM-instruction gap in WaitForChangeAsync between reading
	// the latch and capturing the signal TCS: nothing between those two statements ever yields, so
	// no external caller can suspend execution in that exact gap to force the interleaving without
	// an invasive test seam — an attempted stress-test version of this test (many concurrent
	// Signal() calls racing many waits) was tried and discarded: it passed identically on the
	// unfixed code, because a tight, continuous stream of Signal() calls completes whatever TCS is
	// current within a cycle or two either way, masking the one-signal loss the fix closes. What
	// IS verifiable from outside the class is that the refactor (capturing `pending` before the
	// latch check, instead of reading `_signal` after it) did not change the two behaviours the
	// method contracts to: an already-latched call returns synchronously-completed, and a genuine
	// signal still wakes an in-flight wait.
	[Fact]
	public async Task WaitForChangeAsync_AlreadyLatched_ReturnsSynchronously()
	{
		StubHandler stub = new(request => request.RequestUri!.AbsolutePath.StartsWith("/jmap/eventsource")
			? new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent("", Encoding.UTF8, "text/event-stream")
			}
			: Json(SessionJson));

		await using JmapEventSourceWatcher watcher = new(
			new JmapClient(Base, new HttpClient(stub)), new BackendCredentials("u", "p"), NullLogger.Instance);
		MethodInfo signal = typeof(JmapEventSourceWatcher).GetMethod(
			"Signal", BindingFlags.NonPublic | BindingFlags.Instance)!;
		DateTime beforeSignal = DateTime.UtcNow;
		signal.Invoke(watcher, null);

		Task wait = watcher.WaitForChangeAsync(beforeSignal, CancellationToken.None);

		Assert.Equal(TaskStatus.RanToCompletion, wait.Status); // no await needed — already latched
	}

	// H12: the SSE stream read through a plain StreamReader.ReadLineAsync with no size cap, on a
	// path BackendHttpClientFactory documents as exempt from the response ceiling (it opens the
	// stream with ResponseHeadersRead, so MaxResponseContentBufferSize never applies). A server
	// that emits an endless line with no '\n' grows the reader's internal buffer without bound.
	// Proven deterministically (not timing-dependent): an in-memory stream that never returns 0
	// bytes and never emits a newline is read for a fixed, short wall-clock window; unmodified
	// code has no reason to ever stop reading within that window, so it consumes many times the
	// intended cap. The fix must abandon the connection once a single record exceeds the cap, so
	// total bytes read stays close to it.
	[Fact]
	public async Task UnboundedSseLine_DoesNotGrowTheReadBufferWithoutLimit()
	{
		InfiniteNoNewlineStream garbage = new();
		StubHandler stub = new(request => request.RequestUri!.AbsolutePath.StartsWith("/jmap/eventsource")
			? new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StreamContent(garbage) { Headers = { ContentType = new("text/event-stream") } }
			}
			: Json(SessionJson));

		await using JmapEventSourceWatcher watcher = new(
			new JmapClient(Base, new HttpClient(stub)), new BackendCredentials("u", "p"), NullLogger.Instance);

		// Give the background loop a generous window to read as much as it will. Real network I/O
		// would throttle this; here the stream is fully in-memory, so the only thing that can bound
		// the byte count is the watcher itself abandoning the connection.
		await Task.Delay(TimeSpan.FromMilliseconds(500));

		Assert.True(garbage.TotalRead < 2_000_000,
			$"the SSE line reader must cap a single unterminated record instead of growing without " +
			$"bound; read {garbage.TotalRead} bytes with no line terminator in 500ms");
	}

	/// <summary>An endless byte stream (no EOF, no '\n') that counts everything read from it.</summary>
	private sealed class InfiniteNoNewlineStream : Stream
	{
		public long TotalRead;

		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => throw new NotSupportedException();
		public override long Position { get => TotalRead; set => throw new NotSupportedException(); }

		public override int Read(byte[] buffer, int offset, int count)
		{
			// Never called by the watcher (it reads asynchronously) — this test double only needs
			// to satisfy the abstract Stream surface without itself sync-over-async'ing.
			throw new NotSupportedException();
		}

		public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
			ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

		public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
		{
			// Honor cancellation so the watcher's dispose (which cancels and awaits the background
			// loop) can actually terminate — an infinite stream that ignored ct would hang the test
			// itself rather than exercise the size cap under test.
			ct.ThrowIfCancellationRequested();
			int n = Math.Min(buffer.Length, 4096);
			buffer.Span[..n].Fill((byte)'a'); // never '\n' or '\r' — one endless "line"
			TotalRead += n;
			return ValueTask.FromResult(n);
		}

		public override void Flush() { }
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
	}

	private static HttpResponseMessage Sse(string body)
	{
		return new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
		};
	}

	private static HttpResponseMessage Json(string body)
	{
		return new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent(body, Encoding.UTF8, "application/json")
		};
	}

	private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request, CancellationToken cancellationToken)
		{
			return Task.FromResult(responder(request));
		}
	}
}

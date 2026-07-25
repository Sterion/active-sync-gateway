using System.Net;
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

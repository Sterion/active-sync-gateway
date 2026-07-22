using System.Net;
using System.Text;
using System.Xml.Linq;
using ActiveSync.Backends.Dav;
using Microsoft.Extensions.Logging;

namespace ActiveSync.Core.Tests;

/// <summary>
///   WebDAV client request-shaping. H2: hrefs from a multistatus were percent-decoded and then
///   re-resolved as URIs, so a resource whose name contains <c>#</c>/<c>?</c>/<c>%</c> was fetched
///   at the wrong path. H3: an <c>If-Match</c> ETag that is not RFC-quoted was silently dropped,
///   turning a conditional update into an unconditional PUT (lost update).
/// </summary>
public sealed class WebDavClientTests
{
	private static readonly Uri Base = new("https://dav.example.com/");

	// H2: the address book/calendar contains a resource literally named "a#b.ics"; the server
	// reports its href percent-encoded as ".../a%23b.ics". Fetching it must hit that exact path,
	// not "/dav/cal/a" (with "#b.ics" swallowed as a URI fragment).
	[Fact]
	public async Task Href_WithEncodedSpecialCharacter_IsFetchedVerbatim()
	{
		string multistatus =
			"""
			<D:multistatus xmlns:D="DAV:">
			  <D:response>
			    <D:href>/dav/cal/a%23b.ics</D:href>
			    <D:propstat><D:status>HTTP/1.1 200 OK</D:status>
			      <D:prop><D:getetag>"e1"</D:getetag></D:prop>
			    </D:propstat>
			  </D:response>
			</D:multistatus>
			""";
		Uri? getUri = null;
		RecordingHandler stub = new(request =>
		{
			if (request.Method.Method == "PROPFIND")
				return Xml(multistatus);
			getUri = request.RequestUri;
			return Ok("BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n");
		});
		using WebDavClient client = new(Base, new HttpClient(stub));

		List<DavResource> resources = await client.PropfindAsync("/dav/cal/", 1,
			new XElement(XName.Get("propfind", "DAV:")), CancellationToken.None);
		string href = Assert.Single(resources).Href;
		await client.GetAsync(href, CancellationToken.None);

		Assert.NotNull(getUri);
		Assert.Equal("/dav/cal/a%23b.ics", getUri!.AbsolutePath);
	}

	// H3: many servers (Stalwart among them) hand back a bare, unquoted ETag. EntityTagHeaderValue
	// .TryParse rejects it, so the old code omitted If-Match entirely — an unconditional PUT that
	// clobbers a concurrent update. The header must be present and carry the quoted ETag.
	[Fact]
	public async Task UpdatePut_WithBareEtag_StillSendsIfMatch()
	{
		string? ifMatch = null;
		RecordingHandler stub = new(request =>
		{
			ifMatch = request.Headers.IfMatch.ToString();
			return Ok("");
		});
		using WebDavClient client = new(Base, new HttpClient(stub));

		await client.PutAsync("/dav/cal/x.ics", "BODY", "text/calendar", etag: "12345", ifNoneMatch: false,
			CancellationToken.None);

		Assert.Equal("\"12345\"", ifMatch);
	}

	// H27: a per-resource failure inside an otherwise-207 multistatus (here a 403 on b.ics) used to
	// vanish without a trace, hiding a permission problem behind a short listing. It must be logged;
	// the successful sibling is still returned.
	[Fact]
	public async Task Multistatus_PartialFailure_IsLoggedNotSilentlyDropped()
	{
		string multistatus =
			"""
			<D:multistatus xmlns:D="DAV:">
			  <D:response>
			    <D:href>/dav/cal/a.ics</D:href>
			    <D:propstat><D:status>HTTP/1.1 200 OK</D:status>
			      <D:prop><D:getetag>"e1"</D:getetag></D:prop>
			    </D:propstat>
			  </D:response>
			  <D:response>
			    <D:href>/dav/cal/b.ics</D:href>
			    <D:propstat><D:status>HTTP/1.1 403 Forbidden</D:status>
			      <D:prop><D:getetag/></D:prop>
			    </D:propstat>
			  </D:response>
			</D:multistatus>
			""";
		RecordingHandler stub = new(_ => Xml(multistatus));
		CapturingLogger logger = new();
		using WebDavClient client = new(Base, new HttpClient(stub), logger);

		List<DavResource> resources = await client.PropfindAsync("/dav/cal/", 1,
			new XElement(XName.Get("propfind", "DAV:")), CancellationToken.None);

		Assert.Equal("/dav/cal/a.ics", Assert.Single(resources).Href);
		Assert.Contains(logger.Messages, m => m.Contains("failure response"));
	}

	// H27: the old Contains("200") match dropped any legitimate 2xx that was not literally "200"
	// (e.g. 204). The status code is now parsed as a number in the 2xx range.
	[Fact]
	public async Task Multistatus_Non200SuccessStatus_IsAccepted()
	{
		string multistatus =
			"""
			<D:multistatus xmlns:D="DAV:">
			  <D:response>
			    <D:href>/dav/cal/c.ics</D:href>
			    <D:propstat><D:status>HTTP/1.1 204 No Content</D:status>
			      <D:prop><D:getetag>"e2"</D:getetag></D:prop>
			    </D:propstat>
			  </D:response>
			</D:multistatus>
			""";
		RecordingHandler stub = new(_ => Xml(multistatus));
		using WebDavClient client = new(Base, new HttpClient(stub));

		List<DavResource> resources = await client.PropfindAsync("/dav/cal/", 1,
			new XElement(XName.Get("propfind", "DAV:")), CancellationToken.None);

		Assert.Equal("/dav/cal/c.ics", Assert.Single(resources).Href);
	}

	// H28: a hostile/compromised DAV server must never get the client to resolve an external
	// entity. This is COVERAGE, not a red-first reproducer: XDocument.Parse already prohibits DTDs
	// by default, so the multistatus is rejected before and after the fix; the test pins the
	// hardening so a future refactor to a DTD-permitting reader is caught.
	[Fact]
	public async Task Multistatus_WithDtdEntity_IsRejected_NotResolved()
	{
		string xxe =
			"""
			<?xml version="1.0"?>
			<!DOCTYPE multistatus [ <!ENTITY x "boom"> ]>
			<D:multistatus xmlns:D="DAV:">
			  <D:response><D:href>/dav/cal/&x;.ics</D:href>
			    <D:propstat><D:status>HTTP/1.1 200 OK</D:status>
			      <D:prop><D:getetag>"e1"</D:getetag></D:prop></D:propstat>
			  </D:response>
			</D:multistatus>
			""";
		RecordingHandler stub = new(_ => Xml(xxe));
		using WebDavClient client = new(Base, new HttpClient(stub));

		await Assert.ThrowsAsync<ActiveSync.Contracts.BackendException>(() =>
			client.PropfindAsync("/dav/cal/", 1, new XElement(XName.Get("propfind", "DAV:")), CancellationToken.None));
	}

	// H18: a create-PUT (If-None-Match:*) whose response was lost gets replayed by the transient
	// retry and lands on the resource it just created. Stalwart signals "already exists" with 412;
	// Axigen signals the identical condition with 409 Conflict. Both must be treated as success (the
	// create landed) so a replay never tells the client the item was not stored. The 409 arm was the
	// gap that failed the Axigen CI leg (DavCreatePutReplayTests) while Stalwart stayed green.
	[Theory]
	[InlineData(HttpStatusCode.PreconditionFailed)] // 412 — Stalwart
	[InlineData(HttpStatusCode.Conflict)]           // 409 — Axigen
	public async Task CreatePut_WhenServerReportsAlreadyExists_IsTreatedAsSuccess(HttpStatusCode status)
	{
		RecordingHandler stub = new(_ => new HttpResponseMessage(status));
		using WebDavClient client = new(Base, new HttpClient(stub));

		string? etag = await client.PutAsync(
			"/dav/cal/replayed.ics", "BODY", "text/calendar", etag: null, ifNoneMatch: true,
			CancellationToken.None);

		Assert.Null(etag); // success sentinel — caller re-resolves the stored href/ETag
	}

	// The boundary the 409 widening must NOT cross: an UPDATE-PUT (If-Match) 409 is a genuine
	// conflict / lost update and must still surface. Only the create-PUT (If-None-Match:*) 409 is
	// reinterpreted as "already exists"; a 409 on a conditional update stays an error.
	[Fact]
	public async Task UpdatePut_WhenServerReturns409Conflict_StillThrows()
	{
		RecordingHandler stub = new(_ => new HttpResponseMessage(HttpStatusCode.Conflict));
		using WebDavClient client = new(Base, new HttpClient(stub));

		await Assert.ThrowsAsync<ActiveSync.Contracts.BackendException>(() =>
			client.PutAsync("/dav/cal/x.ics", "BODY", "text/calendar", etag: "12345", ifNoneMatch: false,
				CancellationToken.None));
	}

	// H24: multistatus/GET/REPORT bodies were read with ReadAsStringAsync — fully buffered with no
	// size ceiling, so a malicious or malfunctioning server could stream an unbounded body into
	// memory and OOM the gateway. A response whose declared Content-Length exceeds the ceiling must
	// be refused before it is buffered.
	[Fact]
	public async Task Propfind_ResponseExceedingCeiling_IsRefused_NotBuffered()
	{
		string multistatus =
			"""
			<D:multistatus xmlns:D="DAV:">
			  <D:response>
			    <D:href>/dav/cal/a.ics</D:href>
			    <D:propstat><D:status>HTTP/1.1 200 OK</D:status>
			      <D:prop><D:getetag>"e1"</D:getetag></D:prop>
			    </D:propstat>
			  </D:response>
			</D:multistatus>
			""";
		RecordingHandler stub = new(_ => Xml(multistatus));
		using WebDavClient client = new(Base, new HttpClient(stub)) { MaxResponseBytes = 16 };

		await Assert.ThrowsAsync<ActiveSync.Contracts.BackendException>(() =>
			client.PropfindAsync("/dav/cal/", 1, new XElement(XName.Get("propfind", "DAV:")), CancellationToken.None));
	}

	// H24: the ceiling must also stop a body with NO declared Content-Length (chunked) — the read
	// itself is capped, not just the header check — while a body under the ceiling still parses.
	[Fact]
	public async Task Propfind_ChunkedBodyOverCeiling_IsCappedMidRead()
	{
		string multistatus =
			"<D:multistatus xmlns:D=\"DAV:\"><D:response><D:href>/dav/cal/a.ics</D:href>" +
			"<D:propstat><D:status>HTTP/1.1 200 OK</D:status><D:prop><D:getetag>\"" +
			new string('x', 4096) + "\"</D:getetag></D:prop></D:propstat></D:response></D:multistatus>";
		RecordingHandler stub = new(_ => ChunkedXml(multistatus));
		using WebDavClient client = new(Base, new HttpClient(stub)) { MaxResponseBytes = 256 };

		await Assert.ThrowsAsync<ActiveSync.Contracts.BackendException>(() =>
			client.PropfindAsync("/dav/cal/", 1, new XElement(XName.Get("propfind", "DAV:")), CancellationToken.None));
	}

	private static HttpResponseMessage Ok(string body)
	{
		return new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent(body, Encoding.UTF8, "text/calendar")
		};
	}

	// A 207 whose content reports no Content-Length (a non-seekable stream), forcing the reader to
	// consume the body without knowing its size up front.
	private static HttpResponseMessage ChunkedXml(string body)
	{
		return new HttpResponseMessage((HttpStatusCode)207)
		{
			Content = new StreamContent(new ForwardOnlyStream(Encoding.UTF8.GetBytes(body)))
		};
	}

	/// <summary>A read-only, non-seekable stream so StreamContent cannot report a Content-Length.</summary>
	private sealed class ForwardOnlyStream(byte[] data) : Stream
	{
		private int _pos;
		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => throw new NotSupportedException();
		public override long Position { get => _pos; set => throw new NotSupportedException(); }
		public override void Flush() { }
		public override int Read(byte[] buffer, int offset, int count)
		{
			int n = Math.Min(count, data.Length - _pos);
			Array.Copy(data, _pos, buffer, offset, n);
			_pos += n;
			return n;
		}
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
	}

	private static HttpResponseMessage Xml(string body)
	{
		return new HttpResponseMessage((HttpStatusCode)207)
		{
			Content = new StringContent(body, Encoding.UTF8, "application/xml")
		};
	}

	private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
		: HttpMessageHandler
	{
		protected override async Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request, CancellationToken cancellationToken)
		{
			// Read the body before responding so If-Match/headers are materialized.
			if (request.Content is not null)
				await request.Content.ReadAsStringAsync(cancellationToken);
			return responder(request);
		}
	}

	private sealed class CapturingLogger : ILogger
	{
		public List<string> Messages { get; } = new();

		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
		public bool IsEnabled(LogLevel logLevel) => true;

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
			Func<TState, Exception?, string> formatter)
		{
			Messages.Add(formatter(state, exception));
		}
	}
}

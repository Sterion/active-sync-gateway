using System.Net;
using System.Text;
using System.Xml.Linq;
using ActiveSync.Backends.Dav;
using Microsoft.Extensions.Logging;

namespace ActiveSync.Core.Tests;

/// <summary>
///   WebDAV client request-shaping. hrefs from a multistatus were percent-decoded and then
///   re-resolved as URIs, so a resource whose name contains <c>#</c>/<c>?</c>/<c>%</c> was fetched
///   at the wrong path. Also, an <c>If-Match</c> ETag that is not RFC-quoted was silently dropped,
///   turning a conditional update into an unconditional PUT (lost update).
/// </summary>
public sealed class WebDavClientTests
{
	private static readonly Uri Base = new("https://dav.example.com/");

	// The address book/calendar contains a resource literally named "a#b.ics"; the server
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

	// Many servers (Stalwart among them) hand back a bare, unquoted ETag. EntityTagHeaderValue
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

	// A per-resource failure inside an otherwise-207 multistatus (here a 403 on b.ics) used to
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

	// The old Contains("200") match dropped any legitimate 2xx that was not literally "200"
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

	// A hostile/compromised DAV server must never get the client to resolve an external
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

	// A create-PUT (If-None-Match:*) whose response was lost gets replayed by the transient
	// retry and lands on the resource it just created. Stalwart signals "already exists" with 412 —
	// RFC 7232 ties 412 unambiguously to the If-* precondition itself, so it is treated as success
	// with no further check needed (unlike 409 below, which is a genuine RFC 4918 conflict code and
	// gets narrowed by an extra presence check).
	[Fact]
	public async Task CreatePut_WhenServerReports412AlreadyExists_IsTreatedAsSuccess()
	{
		RecordingHandler stub = new(_ => new HttpResponseMessage(HttpStatusCode.PreconditionFailed));
		using WebDavClient client = new(Base, new HttpClient(stub));

		string? etag = await client.PutAsync(
			"/dav/cal/replayed.ics", "BODY", "text/calendar", etag: null, ifNoneMatch: true,
			CancellationToken.None);

		Assert.Null(etag); // success sentinel — caller re-resolves the stored href/ETag
	}

	// RFC 4918 §9.7.1 defines 409 Conflict on PUT as "the parent collection does not exist" —
	// NOT "already exists" the way 412 does. Axigen answers a replayed create-PUT with 409 rather
	// than 412 (the case above widened for), but blindly treating EVERY create-PUT 409 as
	// success (the old, unnarrowed fix) hid a genuine failure: a collection deleted/renamed
	// server-side while the session was cached. The gateway would report Sync Add Status 1,
	// ResolveStoredHrefAsync could never find the item, and the snapshot's phantom entry gets
	// deleted by the very next diff — the create the client was told succeeded silently vanishes. A
	// 409 must only be treated as success once the target is confirmed actually present.
	[Fact]
	public async Task CreatePut_When409AndTargetNotPresent_ThrowsInsteadOfSilentlySucceeding()
	{
		RecordingHandler stub = new(request => request.Method == HttpMethod.Put
			? new HttpResponseMessage(HttpStatusCode.Conflict)
			: new HttpResponseMessage(HttpStatusCode.NotFound)); // the verifying GET: truly not there
		using WebDavClient client = new(Base, new HttpClient(stub));

		await Assert.ThrowsAsync<ActiveSync.Contracts.BackendException>(() =>
			client.PutAsync("/dav/cal/gone.ics", "BODY", "text/calendar", etag: null, ifNoneMatch: true,
				CancellationToken.None));
	}

	// Continued from the case above: when the verifying GET confirms the target really is present,
	// the 409 IS a safe replay (Axigen's genuine "already exists" signal) and must still be treated
	// as success — this is the legitimate case the presence check must preserve.
	[Fact]
	public async Task CreatePut_When409AndTargetPresent_IsTreatedAsSuccess()
	{
		RecordingHandler stub = new(request => request.Method == HttpMethod.Put
			? new HttpResponseMessage(HttpStatusCode.Conflict)
			: Ok("BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n")); // the verifying GET: it's really there
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

	// Every DAV verb funnels through the same fast transient retry (SendAsync), PUT included.
	// If the server applies an update-PUT and the response is then lost (a reset, or a 503 from a
	// load balancer after the write landed), the retry replays the SAME If-Match header against a
	// resource whose ETag the replay's own first attempt already moved — a genuine-looking 412 for
	// a write the server in fact accepted. The client saw a failed Sync Change on a change that
	// succeeded. Re-GETting on 412 and finding the stored content already matches what we just sent
	// distinguishes this lost-response replay from a real concurrent conflict.
	[Fact]
	public async Task UpdatePut_WhenReplayed412MatchesOwnContent_IsTreatedAsSuccess()
	{
		const string body = "BEGIN:VCALENDAR\r\nSUMMARY:same\r\nEND:VCALENDAR\r\n";
		RecordingHandler stub = new(request =>
		{
			if (request.Method == HttpMethod.Put)
				return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
			HttpResponseMessage get = new(HttpStatusCode.OK) { Content = new StringContent(body) };
			get.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"new-etag\"");
			return get;
		});
		using WebDavClient client = new(Base, new HttpClient(stub));

		string? etag = await client.PutAsync(
			"/dav/cal/x.ics", body, "text/calendar", etag: "\"old-etag\"", ifNoneMatch: false,
			CancellationToken.None);

		Assert.Equal("\"new-etag\"", etag);
	}

	// Continued from the case above: a REAL concurrent conflict — the stored content is someone else's edit, not
	// ours — must keep surfacing as a failure. Only a content match proves it was our own replay.
	[Fact]
	public async Task UpdatePut_When412AndStoredContentDiffers_StillThrows()
	{
		RecordingHandler stub = new(request => request.Method == HttpMethod.Put
			? new HttpResponseMessage(HttpStatusCode.PreconditionFailed)
			: Ok("BEGIN:VCALENDAR\r\nSUMMARY:someone-elses-edit\r\nEND:VCALENDAR\r\n"));
		using WebDavClient client = new(Base, new HttpClient(stub));

		await Assert.ThrowsAsync<ActiveSync.Contracts.BackendException>(() =>
			client.PutAsync("/dav/cal/x.ics", "BEGIN:VCALENDAR\r\nSUMMARY:my-edit\r\nEND:VCALENDAR\r\n",
				"text/calendar", etag: "\"old-etag\"", ifNoneMatch: false, CancellationToken.None));
	}

	// RFC 4918 permits a multistatus <D:href> to be an absolute URI, and every href fed to
	// Resolve() is server-controlled (current-user-principal, home-set, every multistatus <href>,
	// schedule-outbox-URL). A malicious/compromised DAV server can therefore hand back an absolute
	// off-origin href; the Basic Authorization header lives on the shared HttpClient and rides
	// whatever URI the caller builds, so the gateway would send the user's mail password to that
	// foreign host. The request must never reach the handler.
	[Fact]
	public async Task GetAsync_WithOffOriginAbsoluteHref_IsRefused_NotSent()
	{
		bool requestReachedHandler = false;
		RecordingHandler stub = new(_ =>
		{
			requestReachedHandler = true;
			return Ok("");
		});
		using WebDavClient client = new(Base, new HttpClient(stub));

		await Assert.ThrowsAsync<ActiveSync.Contracts.BackendException>(() =>
			client.GetAsync("https://evil.example.net/x", CancellationToken.None));

		Assert.False(requestReachedHandler, "credentials must never be attached to an off-origin URL");
	}

	// Multistatus/GET/REPORT bodies were read with ReadAsStringAsync — fully buffered with no
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

	// The ceiling must also stop a body with NO declared Content-Length (chunked) — the read
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

	// TransientRetry.SendHttpAsync rethrows the ORIGINAL transport exception once its retry
	// budget is spent (see TransientRetry.IsTransientHttpException). Every DAV call site funnels
	// through WebDavClient.SendAsync and only ever catches BackendException — a raw
	// HttpRequestException therefore escaped four "never break folder sync / never treat a hiccup as
	// a change" guards (CalDavStore's shared-collection probe, DavDiscovery's ctag poll,
	// DavStoreBase.FindByUidAsync, CardDavStore's GAL fallback). Every other DAV failure mode (HTTP
	// status, XML parse) already surfaces as BackendException; a transport failure must too.
	[Fact]
	public async Task TransportFailure_SurfacesAsBackendException()
	{
		ThrowingHandler stub = new(() => new HttpRequestException("connection reset"));
		using WebDavClient client = new(Base, new HttpClient(stub));

		await Assert.ThrowsAsync<ActiveSync.Contracts.BackendException>(() =>
			client.PropfindAsync(
				"/dav/cal/", 1, new XElement(XName.Get("propfind", "DAV:")), CancellationToken.None));
	}

	private sealed class ThrowingHandler(Func<Exception> makeException) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request, CancellationToken cancellationToken)
		{
			throw makeException();
		}
	}

	// WebDavClient.DefaultMaxResponseBytes shared the SAME 128 MiB ceiling as JMAP's blob
	// downloads (BackendHttpClientFactory.MaxBackendResponseBytes) — but a DAV multistatus is legitimately
	// several MB, never remotely close to a legitimate JMAP attachment, and XDocument materializes
	// roughly 5-10x the wire size (an XElement/XAttribute/XText per node) on top of the already-buffered
	// HTTP response — so a hostile 128 MB multistatus amplifies to ~1 GB of managed heap. The DAV ceiling
	// must be decoupled from and lower than JMAP's, which stays 128 MiB for its own legitimate use.
	[Fact]
	public void DefaultMaxResponseBytes_IsDecoupledFromAndLowerThanTheJmapBlobCeiling()
	{
		Assert.NotEqual(ActiveSync.Backends.Common.BackendHttpClientFactory.MaxBackendResponseBytes,
			WebDavClient.DefaultMaxResponseBytes);
		Assert.True(WebDavClient.DefaultMaxResponseBytes <= 32L * 1024 * 1024,
			$"DAV's default response ceiling ({WebDavClient.DefaultMaxResponseBytes} bytes) should be " +
			"proportionate to a multistatus listing, not JMAP's much larger blob-download ceiling");
	}

	// There was no MaxCharactersInDocument ceiling at all on the XmlReaderSettings used to parse
	// a multistatus — only the byte-level MaxResponseBytes cap existed. A response entirely within the
	// byte ceiling can still amplify into an excessive character count during parse; the ceiling below
	// must be independently enforced regardless of how generous the byte cap is.
	[Fact]
	public async Task Propfind_ResponseOverCharacterCeiling_IsRejected()
	{
		string multistatus =
			"<D:multistatus xmlns:D=\"DAV:\"><D:response><D:href>/dav/cal/a.ics</D:href>" +
			"<D:propstat><D:status>HTTP/1.1 200 OK</D:status><D:prop><D:getetag>\"" +
			new string('x', 500) + "\"</D:getetag></D:prop></D:propstat></D:response></D:multistatus>";
		RecordingHandler stub = new(_ => Xml(multistatus));
		using WebDavClient client = new(Base, new HttpClient(stub))
		{
			MaxResponseBytes = 1_000_000, // well within the byte ceiling — this must not be what blocks it
			MaxCharactersInDocument = 50  // but the parse itself is capped far below the document's length
		};

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

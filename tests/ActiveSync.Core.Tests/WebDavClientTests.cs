using System.Net;
using System.Text;
using System.Xml.Linq;
using ActiveSync.Backends.Dav;

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

	private static HttpResponseMessage Ok(string body)
	{
		return new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent(body, Encoding.UTF8, "text/calendar")
		};
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
}

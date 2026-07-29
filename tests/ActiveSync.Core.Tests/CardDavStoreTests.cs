using System.Net;
using System.Text;
using System.Xml.Linq;
using ActiveSync.Backends.Dav;
using ActiveSync.Contracts;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Core.Tests;

/// <summary>
///   The default contacts folder (EAS type Contacts) was whichever address book the server
///   happened to list first in the multistatus — unstable across sessions and servers.
///   <c>CalDavStore</c> already sorts the home set before crowning the default calendar; CardDAV
///   must do the same so the pick is deterministic.
/// </summary>
public sealed class CardDavStoreTests
{
	private static readonly Uri Base = new("https://dav.example.com/");

	[Fact]
	public async Task ListFolders_DefaultContacts_IsChosenDeterministically()
	{
		// The server lists "zeta" before "alpha"; the default must still be the href-sorted first.
		string multistatus =
			"""
			<D:multistatus xmlns:D="DAV:" xmlns:C="urn:ietf:params:jmap:contacts" xmlns:CR="urn:ietf:params:xml:ns:carddav">
			  <D:response>
			    <D:href>/dav/ab/</D:href>
			    <D:propstat><D:status>HTTP/1.1 200 OK</D:status>
			      <D:prop><D:resourcetype><D:collection/></D:resourcetype></D:prop>
			    </D:propstat>
			  </D:response>
			  <D:response>
			    <D:href>/dav/ab/zeta/</D:href>
			    <D:propstat><D:status>HTTP/1.1 200 OK</D:status>
			      <D:prop>
			        <D:resourcetype><D:collection/><CR:addressbook/></D:resourcetype>
			        <D:displayname>Zeta</D:displayname>
			      </D:prop>
			    </D:propstat>
			  </D:response>
			  <D:response>
			    <D:href>/dav/ab/alpha/</D:href>
			    <D:propstat><D:status>HTTP/1.1 200 OK</D:status>
			      <D:prop>
			        <D:resourcetype><D:collection/><CR:addressbook/></D:resourcetype>
			        <D:displayname>Alpha</D:displayname>
			      </D:prop>
			    </D:propstat>
			  </D:response>
			</D:multistatus>
			""";
		StubHandler stub = new(_ => Xml(multistatus));
		using WebDavClient dav = new(Base, new HttpClient(stub));
		DavServerOptions options = new() { BaseUrl = Base.ToString(), HomeSetPath = "/dav/ab/" };
		CardDavStore store = new(dav, options, new BackendCredentials { UserName = "user", Password = "pass" }, NullLogger.Instance, pollSeconds: 60);

		IReadOnlyList<BackendFolder> folders = await store.ListFoldersAsync(CancellationToken.None);

		BackendFolder def = Assert.Single(folders, f => f.Type == FolderType.Contacts);
		Assert.Equal("Alpha", def.DisplayName);
	}

	// A server can accept the addressbook-query REPORT and return a well-formed 207 whose
	// propstats carry getetag but no address-data (unsupported or silently dropped) — that yields an
	// EMPTY-BUT-NON-NULL card list, indistinguishable from "the REPORT threw" only in that
	// QueryGalCardsAsync's caller treats it as "genuinely zero matches" instead of falling back to
	// the per-contact enumeration path. GAL search then permanently returns nothing, silently, for
	// every query on that server. Proven by making the REPORT return a response with getetag but no
	// address-data: unmodified code returns 0 results and issues 0 GETs; the fix must fall back to
	// enumeration (GetCardsByEnumerationAsync) and find the contact via a per-contact GET.
	[Fact]
	public async Task SearchGal_ReportOmitsAddressData_FallsBackToEnumeration()
	{
		const string aliceCard =
			"BEGIN:VCARD\nVERSION:3.0\nFN:Alice Example\nN:Example;Alice;;;\nEMAIL:alice@example.com\nEND:VCARD\n";

		string homeSet =
			"""
			<D:multistatus xmlns:D="DAV:" xmlns:CR="urn:ietf:params:xml:ns:carddav">
			  <D:response>
			    <D:href>/dav/ab/default/</D:href>
			    <D:propstat><D:status>HTTP/1.1 200 OK</D:status>
			      <D:prop>
			        <D:resourcetype><D:collection/><CR:addressbook/></D:resourcetype>
			        <D:displayname>Default</D:displayname>
			      </D:prop>
			    </D:propstat>
			  </D:response>
			</D:multistatus>
			""";
		string etagList =
			"""
			<D:multistatus xmlns:D="DAV:">
			  <D:response><D:href>/dav/ab/default/a.vcf</D:href>
			    <D:propstat><D:status>HTTP/1.1 200 OK</D:status><D:prop><D:getetag>"e1"</D:getetag></D:prop></D:propstat>
			  </D:response>
			</D:multistatus>
			""";
		// Well-formed 207: getetag present, address-data ABSENT (a server that accepted the REPORT
		// but does not honour address-data) — must be told apart from "the REPORT threw".
		string queryResultNoAddressData =
			"""
			<D:multistatus xmlns:D="DAV:" xmlns:CR="urn:ietf:params:xml:ns:carddav">
			  <D:response><D:href>/dav/ab/default/a.vcf</D:href>
			    <D:propstat><D:status>HTTP/1.1 200 OK</D:status><D:prop><D:getetag>"e1"</D:getetag></D:prop></D:propstat>
			  </D:response>
			</D:multistatus>
			""";

		int getCount = 0;
		StubHandler stub = new(request =>
		{
			string method = request.Method.Method;
			string path = request.RequestUri!.AbsolutePath;
			if (method == "REPORT")
				return Xml(queryResultNoAddressData);
			if (method == "GET")
			{
				getCount++;
				return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(aliceCard) };
			}
			return Xml(path == "/dav/ab/" ? homeSet : etagList);
		});
		using WebDavClient dav = new(Base, new HttpClient(stub));
		DavServerOptions options = new() { BaseUrl = Base.ToString(), HomeSetPath = "/dav/ab/" };
		CardDavStore store = new(dav, options, new BackendCredentials { UserName = "user", Password = "pass" }, NullLogger.Instance, pollSeconds: 60);

		IReadOnlyList<IReadOnlyList<XElement>> results =
			await store.SearchGalAsync("Alice", 25, null, CancellationToken.None);

		Assert.Single(results); // found via the enumeration fallback, not silently empty
		Assert.Equal(1, getCount); // fell back to a per-contact GET
	}

	// GAL search issued one HTTP GET per contact (a 5000-contact book = 5000 serial round
	// trips per keystroke). A single addressbook-query REPORT returns the matching vCards inline, so
	// no per-contact GET is needed. Proven by counting GETs: unmodified code fetches every card,
	// the fixed store fetches none.
	[Fact]
	public async Task SearchGal_UsesAddressbookQuery_NotAGetPerContact()
	{
		const string aliceCard =
			"BEGIN:VCARD\nVERSION:3.0\nFN:Alice Example\nN:Example;Alice;;;\nEMAIL:alice@example.com\nEND:VCARD\n";
		const string bobCard =
			"BEGIN:VCARD\nVERSION:3.0\nFN:Alice Partner\nN:Partner;Alice;;;\nEMAIL:alice.p@example.com\nEND:VCARD\n";

		string homeSet =
			"""
			<D:multistatus xmlns:D="DAV:" xmlns:CR="urn:ietf:params:xml:ns:carddav">
			  <D:response>
			    <D:href>/dav/ab/default/</D:href>
			    <D:propstat><D:status>HTTP/1.1 200 OK</D:status>
			      <D:prop>
			        <D:resourcetype><D:collection/><CR:addressbook/></D:resourcetype>
			        <D:displayname>Default</D:displayname>
			      </D:prop>
			    </D:propstat>
			  </D:response>
			</D:multistatus>
			""";
		// etag-only listing, the shape GetItemRevisions (the fallback path) expects.
		string etagList =
			"""
			<D:multistatus xmlns:D="DAV:">
			  <D:response><D:href>/dav/ab/default/a.vcf</D:href>
			    <D:propstat><D:status>HTTP/1.1 200 OK</D:status><D:prop><D:getetag>"e1"</D:getetag></D:prop></D:propstat>
			  </D:response>
			  <D:response><D:href>/dav/ab/default/b.vcf</D:href>
			    <D:propstat><D:status>HTTP/1.1 200 OK</D:status><D:prop><D:getetag>"e2"</D:getetag></D:prop></D:propstat>
			  </D:response>
			</D:multistatus>
			""";
		string queryResult =
			$"""
			<D:multistatus xmlns:D="DAV:" xmlns:CR="urn:ietf:params:xml:ns:carddav">
			  <D:response><D:href>/dav/ab/default/a.vcf</D:href>
			    <D:propstat><D:status>HTTP/1.1 200 OK</D:status>
			      <D:prop><D:getetag>"e1"</D:getetag><CR:address-data>{aliceCard}</CR:address-data></D:prop></D:propstat>
			  </D:response>
			  <D:response><D:href>/dav/ab/default/b.vcf</D:href>
			    <D:propstat><D:status>HTTP/1.1 200 OK</D:status>
			      <D:prop><D:getetag>"e2"</D:getetag><CR:address-data>{bobCard}</CR:address-data></D:prop></D:propstat>
			  </D:response>
			</D:multistatus>
			""";

		int getCount = 0;
		int reportCount = 0;
		StubHandler stub = new(request =>
		{
			string method = request.Method.Method;
			string path = request.RequestUri!.AbsolutePath;
			if (method == "REPORT")
			{
				reportCount++;
				return Xml(queryResult);
			}
			if (method == "GET")
			{
				getCount++;
				return new HttpResponseMessage(HttpStatusCode.OK)
				{
					Content = new StringContent(path.EndsWith("a.vcf") ? aliceCard : bobCard)
				};
			}
			// PROPFIND: home set vs collection listing.
			return Xml(path == "/dav/ab/" ? homeSet : etagList);
		});
		using WebDavClient dav = new(Base, new HttpClient(stub));
		DavServerOptions options = new() { BaseUrl = Base.ToString(), HomeSetPath = "/dav/ab/" };
		CardDavStore store = new(dav, options, new BackendCredentials { UserName = "user", Password = "pass" }, NullLogger.Instance, pollSeconds: 60);

		IReadOnlyList<IReadOnlyList<XElement>> results =
			await store.SearchGalAsync("Alice", 25, null, CancellationToken.None);

		Assert.Equal(2, results.Count);
		Assert.Equal(0, getCount);       // no per-contact GET
		Assert.True(reportCount >= 1);   // one addressbook-query REPORT instead
	}

	// CreateItemAsync fetched a full pre-PUT collection listing (PROPFIND) unconditionally,
	// even when the UID-query REPORT already located the stored item at the exact PUT href —
	// wasting a full enumeration on every single create against a well-behaved server (the fix
	// is meant to defer it to the listing-diff fallback only). Counting PROPFIND calls
	// distinguishes "always fetched" (unmodified) from "never needed on this path" (fixed).
	[Fact]
	public async Task CreateItem_WhenUidQueryMatchesPutHref_SkipsTheFullEnumeration()
	{
		int propfindCount = 0;
		string? createdHref = null;
		StubHandler stub = new(request =>
		{
			string method = request.Method.Method;
			if (method == "PUT")
			{
				createdHref = request.RequestUri!.AbsolutePath;
				HttpResponseMessage put = new(HttpStatusCode.Created);
				put.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"etag1\"");
				return put;
			}
			if (method == "REPORT")
			{
				string href = createdHref!;
				return Xml($"""
				<D:multistatus xmlns:D="DAV:">
				  <D:response><D:href>{href}</D:href>
				    <D:propstat><D:status>HTTP/1.1 200 OK</D:status><D:prop><D:getetag>"etag1"</D:getetag></D:prop></D:propstat>
				  </D:response>
				</D:multistatus>
				""");
			}
			// PROPFIND — the pre-PUT/post-PUT full listing this test asserts is skipped when the
			// UID query already resolved the canonical href.
			propfindCount++;
			return Xml("""<D:multistatus xmlns:D="DAV:"></D:multistatus>""");
		});
		using WebDavClient dav = new(Base, new HttpClient(stub));
		DavServerOptions options = new() { BaseUrl = Base.ToString(), HomeSetPath = "/dav/ab/" };
		CardDavStore store = new(dav, options, new BackendCredentials { UserName = "user", Password = "pass" }, NullLogger.Instance, pollSeconds: 60);

		XElement app = new("ApplicationData",
			new XElement(EasNamespaces.Contacts + "FirstName", "Ada"));
		(string itemKey, string revision) = await store.CreateItemAsync(
			CardDavStore.KeyPrefix + "/dav/ab/default/", app, CancellationToken.None);

		Assert.Equal(0, propfindCount);
		Assert.False(string.IsNullOrEmpty(itemKey));
		Assert.False(string.IsNullOrEmpty(revision));
	}

	// The eager pre-PUT listing was turned into a lazy Func consulted only inside
	// ResolveStoredHrefAsync, which runs AFTER the PUT — so whenever it IS invoked, it enumerates a
	// collection that already contains the just-created resource. `before` is documented as the
	// listing "from before the PUT" but can no longer be that. On a server that stores the resource
	// under a canonical href different from the PUT target (Axigen-style rewrite), the UID-query hit
	// is wrongly rejected (`!before().ContainsKey(hit.Href)` is now false because the post-PUT
	// listing DOES contain it), and the listing-diff fallback's `appeared` set is always empty
	// (both "before" and "after" are the same post-PUT snapshot) — so the method falls through to
	// its warning and returns the WRONG href (the naive PUT target, not where the item actually is).
	[Fact]
	public async Task CreateItem_WhenServerCanonicalizesHref_AdoptsTheCanonicalHref()
	{
		const string canonicalHref = "/dav/ab/default/canonical-server-id.vcf";
		string? putContent = null;
		StubHandler stub = new(request =>
		{
			string method = request.Method.Method;
			if (method == "PUT")
			{
				putContent = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
				HttpResponseMessage put = new(HttpStatusCode.Created);
				put.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"put-etag\"");
				return put;
			}
			if (method == "REPORT")
			{
				// UID query: the server reports the item under ITS OWN canonical href, never the
				// naive PUT target.
				return Xml($"""
				<D:multistatus xmlns:D="DAV:">
				  <D:response><D:href>{canonicalHref}</D:href>
				    <D:propstat><D:status>HTTP/1.1 200 OK</D:status><D:prop><D:getetag>"canon-etag"</D:getetag></D:prop></D:propstat>
				  </D:response>
				</D:multistatus>
				""");
			}
			if (method == "GET")
			{
				// Content-verification GET: the server serves back the exact content it stored,
				// under the canonical href.
				return new HttpResponseMessage(HttpStatusCode.OK)
				{
					Content = new StringContent(putContent ?? string.Empty)
				};
			}
			// PROPFIND: the listing only ever shows the canonical href — the naive PUT target was
			// never actually used by the server.
			return Xml($"""
			<D:multistatus xmlns:D="DAV:">
			  <D:response><D:href>{canonicalHref}</D:href>
			    <D:propstat><D:status>HTTP/1.1 200 OK</D:status><D:prop><D:getetag>"canon-etag"</D:getetag></D:prop></D:propstat>
			  </D:response>
			</D:multistatus>
			""");
		});
		using WebDavClient dav = new(Base, new HttpClient(stub));
		DavServerOptions options = new() { BaseUrl = Base.ToString(), HomeSetPath = "/dav/ab/" };
		CardDavStore store = new(dav, options, new BackendCredentials { UserName = "user", Password = "pass" }, NullLogger.Instance, pollSeconds: 60);

		XElement app = new("ApplicationData",
			new XElement(EasNamespaces.Contacts + "FirstName", "Ada"));
		(string itemKey, string revision) = await store.CreateItemAsync(
			CardDavStore.KeyPrefix + "/dav/ab/default/", app, CancellationToken.None);

		Assert.Equal(canonicalHref, itemKey);
		Assert.Equal("canon-etag", revision.Trim('"'));
	}

	// On a server whose listings AND UID-query index lag a PUT (Axigen — AGENTS.md: "listings
	// can lag a PUT by up to ~a minute"), neither FindByUidAsync (REPORT) nor GetItemRevisionsAsync
	// (PROPFIND) sees the just-created item yet. The old code fell straight from there into a
	// content scan of the (stale) listing it just fetched — fetching every PRE-EXISTING item in the
	// collection to compare UIDs that can never match, because the listing never contained the new
	// item to begin with. A direct GET of the PUT target does not depend on either index, so trying
	// it first resolves a server that honoured the PUT target (Axigen included) in exactly one GET,
	// with zero GETs spent on the collection's other contents.
	[Fact]
	public async Task CreateItem_WhenListingLagsThePut_VerifiesPutHrefDirectly_WithoutScanningExistingItems()
	{
		string? putHref = null;
		string? putContent = null;
		int putHrefGetCount = 0;
		int otherItemGetCount = 0;
		StubHandler stub = new(request =>
		{
			string method = request.Method.Method;
			string path = request.RequestUri!.AbsolutePath;
			if (method == "PUT")
			{
				putHref = path;
				putContent = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
				return new HttpResponseMessage(HttpStatusCode.Created); // Axigen: no ETag on the PUT response
			}
			if (method == "REPORT")
				// UID-query index lags the PUT: no match yet.
				return Xml("""<D:multistatus xmlns:D="DAV:"></D:multistatus>""");
			if (method == "GET")
			{
				if (path == putHref)
				{
					putHrefGetCount++;
					HttpResponseMessage ok = new(HttpStatusCode.OK)
					{
						Content = new StringContent(putContent ?? string.Empty)
					};
					ok.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"put-etag\"");
					return ok;
				}
				// A pre-existing item: its content can never match the new item's UID. Must never be
				// fetched once the direct PUT-href GET already resolved the create.
				otherItemGetCount++;
				return new HttpResponseMessage(HttpStatusCode.OK)
				{
					Content = new StringContent("BEGIN:VCARD\nVERSION:3.0\nUID:someone-else\nEND:VCARD\n")
				};
			}
			// PROPFIND: the listing also lags the PUT — three pre-existing items, none of them ours.
			return Xml("""
			<D:multistatus xmlns:D="DAV:">
			  <D:response><D:href>/dav/ab/default/x1.vcf</D:href>
			    <D:propstat><D:status>HTTP/1.1 200 OK</D:status><D:prop><D:getetag>"e1"</D:getetag></D:prop></D:propstat>
			  </D:response>
			  <D:response><D:href>/dav/ab/default/x2.vcf</D:href>
			    <D:propstat><D:status>HTTP/1.1 200 OK</D:status><D:prop><D:getetag>"e2"</D:getetag></D:prop></D:propstat>
			  </D:response>
			  <D:response><D:href>/dav/ab/default/x3.vcf</D:href>
			    <D:propstat><D:status>HTTP/1.1 200 OK</D:status><D:prop><D:getetag>"e3"</D:getetag></D:prop></D:propstat>
			  </D:response>
			</D:multistatus>
			""");
		});
		using WebDavClient dav = new(Base, new HttpClient(stub));
		DavServerOptions options = new() { BaseUrl = Base.ToString(), HomeSetPath = "/dav/ab/" };
		CardDavStore store = new(dav, options, new BackendCredentials { UserName = "user", Password = "pass" }, NullLogger.Instance, pollSeconds: 60);

		XElement app = new("ApplicationData",
			new XElement(EasNamespaces.Contacts + "FirstName", "Ada"));
		(string itemKey, string revision) = await store.CreateItemAsync(
			CardDavStore.KeyPrefix + "/dav/ab/default/", app, CancellationToken.None);

		Assert.Equal(0, otherItemGetCount); // the three pre-existing items must never be fetched
		Assert.Equal(1, putHrefGetCount);   // resolved via one direct GET of the PUT target
		Assert.Equal(putHref, itemKey);
		Assert.Equal("put-etag", revision.Trim('"'));
	}

	// When the server exposes no ETag anywhere for a newly created item -- not on the PUT
	// response, not via a direct getetag PROPFIND -- the old code fell back to a fresh
	// Guid.NewGuid() on every call: a value indistinguishable from a genuine opaque ETag that can
	// never equal what a later listing reports, so the very next diff treats the item as
	// changed/deleted even though nothing changed (defeating the echo suppression AGENTS.md
	// describes: "patch the snapshot in place so the same change is not sent back"). The revision
	// must instead be a fixed, self-documenting "unknown" placeholder -- proven here by checking it
	// does not look like a random GUID and is stable across calls (a fresh GUID would differ every
	// time; a fixed sentinel would not).
	[Fact]
	public async Task CreateItem_WhenServerExposesNoEtagAnywhere_ReturnsAStableNonGuidSentinel()
	{
		string? putHref = null;
		string? putContent = null;
		StubHandler stub = new(request =>
		{
			string method = request.Method.Method;
			if (method == "PUT")
			{
				putHref = request.RequestUri!.AbsolutePath;
				putContent = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
				return new HttpResponseMessage(HttpStatusCode.Created); // no ETag header anywhere
			}
			if (method == "REPORT")
				return Xml("""<D:multistatus xmlns:D="DAV:"></D:multistatus>"""); // no UID-query hit
			if (method == "GET")
				// Verifies the content at putHref, but the server carries no ETag on GET either.
				return new HttpResponseMessage(HttpStatusCode.OK)
				{
					Content = new StringContent(putContent ?? string.Empty)
				};
			// PROPFIND (the direct getetag probe): the resource exists but carries no getetag
			// property at all (not merely an empty one -- absent, so the lookup finds nothing).
			return Xml($"""
			<D:multistatus xmlns:D="DAV:">
			  <D:response><D:href>{putHref}</D:href>
			    <D:propstat><D:status>HTTP/1.1 200 OK</D:status><D:prop></D:prop></D:propstat>
			  </D:response>
			</D:multistatus>
			""");
		});
		using WebDavClient dav = new(Base, new HttpClient(stub));
		DavServerOptions options = new() { BaseUrl = Base.ToString(), HomeSetPath = "/dav/ab/" };
		CardDavStore store = new(dav, options, new BackendCredentials { UserName = "user", Password = "pass" }, NullLogger.Instance, pollSeconds: 60);
		XElement app = new("ApplicationData", new XElement(EasNamespaces.Contacts + "FirstName", "Ada"));

		(_, string revision1) = await store.CreateItemAsync(
			CardDavStore.KeyPrefix + "/dav/ab/default/", app, CancellationToken.None);
		(_, string revision2) = await store.CreateItemAsync(
			CardDavStore.KeyPrefix + "/dav/ab/default/", app, CancellationToken.None);

		Assert.False(Guid.TryParse(revision1, out _),
			$"'{revision1}' looks like a random GUID, not a self-documenting sentinel");
		Assert.Equal(revision1, revision2); // stable placeholder, not a fresh random value per call
	}

	private static HttpResponseMessage Xml(string body)
	{
		return new HttpResponseMessage((HttpStatusCode)207)
		{
			Content = new StringContent(body, Encoding.UTF8, "application/xml")
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

using System.Net;
using System.Text;
using System.Xml.Linq;
using ActiveSync.Backends.Dav;
using ActiveSync.Contracts;
using ActiveSync.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Core.Tests;

/// <summary>
///   H22: the default contacts folder (EAS type Contacts) was whichever address book the server
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
		CardDavStore store = new(dav, options, new BackendCredentials("user", "pass"), NullLogger.Instance);

		IReadOnlyList<BackendFolder> folders = await store.ListFoldersAsync(CancellationToken.None);

		BackendFolder def = Assert.Single(folders, f => f.EasType == EasFolderType.Contacts);
		Assert.Equal("Alpha", def.DisplayName);
	}

	// H14: GAL search issued one HTTP GET per contact (a 5000-contact book = 5000 serial round
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
		CardDavStore store = new(dav, options, new BackendCredentials("user", "pass"), NullLogger.Instance);

		IReadOnlyList<IReadOnlyList<XElement>> results =
			await store.SearchGalAsync("Alice", 25, null, CancellationToken.None);

		Assert.Equal(2, results.Count);
		Assert.Equal(0, getCount);       // no per-contact GET
		Assert.True(reportCount >= 1);   // one addressbook-query REPORT instead
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

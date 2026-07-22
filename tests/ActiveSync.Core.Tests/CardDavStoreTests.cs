using System.Net;
using System.Text;
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

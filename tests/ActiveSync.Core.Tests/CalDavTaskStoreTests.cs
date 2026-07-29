using System.Net;
using System.Text;
using System.Xml.Linq;
using ActiveSync.Backends.Dav;
using ActiveSync.Contracts;
using ActiveSync.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Core.Tests;

/// <summary>
///   The tasks folder pick was not sorted, unlike the calendar and contacts picks it
///   mirrors. Both <c>CalDavStore</c> and <c>CardDavStore</c> href-sort their home-set listing
///   before crowning the default folder, precisely because "multistatus order is server whim"
///   (AGENTS.md). <c>CalDavTaskStore</c> did not, so when two VTODO collections both match
///   <c>TaskFolder</c> by display name or trailing path segment, which one becomes EAS folder
///   type 7 (Tasks, the default) flips between sessions and servers.
/// </summary>
public sealed class CalDavTaskStoreTests
{
	private static readonly Uri Base = new("https://dav.example.com/");

	[Fact]
	public async Task ListFolders_DefaultTasks_IsChosenDeterministically()
	{
		// The server lists the "z" collection before the "a" one; the default must still be the
		// href-sorted first, regardless of raw multistatus order.
		string multistatus =
			"""
			<D:multistatus xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">
			  <D:response>
			    <D:href>/dav/cal/</D:href>
			    <D:propstat><D:status>HTTP/1.1 200 OK</D:status>
			      <D:prop><D:resourcetype><D:collection/></D:resourcetype></D:prop>
			    </D:propstat>
			  </D:response>
			  <D:response>
			    <D:href>/dav/cal/z/Tasks/</D:href>
			    <D:propstat><D:status>HTTP/1.1 200 OK</D:status>
			      <D:prop>
			        <D:resourcetype><D:collection/><C:calendar/></D:resourcetype>
			        <D:displayname>Tasks</D:displayname>
			        <C:supported-calendar-component-set><C:comp name="VTODO"/></C:supported-calendar-component-set>
			      </D:prop>
			    </D:propstat>
			  </D:response>
			  <D:response>
			    <D:href>/dav/cal/a/Tasks/</D:href>
			    <D:propstat><D:status>HTTP/1.1 200 OK</D:status>
			      <D:prop>
			        <D:resourcetype><D:collection/><C:calendar/></D:resourcetype>
			        <D:displayname>Tasks</D:displayname>
			        <C:supported-calendar-component-set><C:comp name="VTODO"/></C:supported-calendar-component-set>
			      </D:prop>
			    </D:propstat>
			  </D:response>
			</D:multistatus>
			""";
		StubHandler stub = new(_ => Xml(multistatus));
		using WebDavClient dav = new(Base, new HttpClient(stub));
		DavServerOptions options = new() { BaseUrl = Base.ToString(), HomeSetPath = "/dav/cal/", TaskFolder = "Tasks" };
		CalDavTaskStore store = new(dav, options, new BackendCredentials { UserName = "user", Password = "pass" }, NullLogger.Instance, pollSeconds: 60);

		IReadOnlyList<BackendFolder> folders = await store.ListFoldersAsync(CancellationToken.None);

		BackendFolder def = Assert.Single(folders, f => f.Type == FolderType.Tasks);
		Assert.Equal("/dav/cal/a/Tasks/", def.Key.Value[CalDavTaskStore.KeyPrefix.Length..]);
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

using System.Net;
using System.Text;
using System.Xml.Linq;
using ActiveSync.Backends.Dav;
using ActiveSync.Contracts;
using ActiveSync.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Core.Tests;

/// <summary>
///   H22: self free/busy folded shared read-only calendars into the user's own availability — a
///   colleague's or team calendar shared TO the user made ResolveRecipients report the user busy
///   whenever the shared calendar was busy, degrading meeting scheduling against them.
///   H23: the "a share never claims the default slot" rule (deliberate, per AGENTS.md) had no
///   floor — a delegate account whose home set contains only granted collections got zero folders
///   of EAS type 8 (Calendar), though iOS expects exactly one default calendar folder to exist.
/// </summary>
public sealed class CalDavStoreTests
{
	private static readonly Uri Base = new("https://dav.example.com/");

	[Fact]
	public async Task GetBusyPeriods_Self_ExcludesSharedCalendars()
	{
		string homeSet =
			"""
			<D:multistatus xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">
			  <D:response>
			    <D:href>/dav/cal/own/</D:href>
			    <D:propstat><D:status>HTTP/1.1 200 OK</D:status>
			      <D:prop>
			        <D:resourcetype><D:collection/><C:calendar/></D:resourcetype>
			        <D:displayname>Own</D:displayname>
			        <C:supported-calendar-component-set><C:comp name="VEVENT"/></C:supported-calendar-component-set>
			      </D:prop>
			    </D:propstat>
			  </D:response>
			  <D:response>
			    <D:href>/dav/cal/team/</D:href>
			    <D:propstat><D:status>HTTP/1.1 200 OK</D:status>
			      <D:prop>
			        <D:resourcetype><D:collection/><C:calendar/></D:resourcetype>
			        <D:displayname>Team</D:displayname>
			        <C:supported-calendar-component-set><C:comp name="VEVENT"/></C:supported-calendar-component-set>
			      </D:prop>
			    </D:propstat>
			  </D:response>
			</D:multistatus>
			""";
		const string freeBusyIcs =
			"BEGIN:VCALENDAR\r\nBEGIN:VFREEBUSY\r\nFREEBUSY:20260101T000000Z/20260101T010000Z\r\nEND:VFREEBUSY\r\nEND:VCALENDAR\r\n";

		List<string> reportPaths = new();
		StubHandler stub = new(request =>
		{
			if (request.Method.Method == "REPORT")
			{
				reportPaths.Add(request.RequestUri!.AbsolutePath);
				return new HttpResponseMessage(HttpStatusCode.OK)
				{
					Content = new StringContent(freeBusyIcs, Encoding.UTF8, "text/calendar")
				};
			}
			return Xml(homeSet); // PROPFIND (home set)
		});
		using WebDavClient dav = new(Base, new HttpClient(stub));
		DavServerOptions options = new() { BaseUrl = Base.ToString(), HomeSetPath = "/dav/cal/" };
		// "team" is shared TO this user (read-only) — must not be folded into the user's OWN
		// availability. It also happens to appear inside the user's own home-set listing above,
		// which ListFoldersAsync already anticipates via its own "granted" check.
		SharedCollection[] shared = [new SharedCollection("/dav/cal/team/", ReadOnly: true)];
		CalDavStore store = new(dav, options, new BackendCredentials("user", "pass"), "user@example.com",
			NullLogger.Instance, pollSeconds: 60, shared);

		await store.GetBusyPeriodsAsync("user@example.com",
			new DateTime(2026, 1, 1), new DateTime(2026, 1, 2), CancellationToken.None);

		Assert.DoesNotContain("/dav/cal/team/", reportPaths);
		Assert.Contains("/dav/cal/own/", reportPaths);
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

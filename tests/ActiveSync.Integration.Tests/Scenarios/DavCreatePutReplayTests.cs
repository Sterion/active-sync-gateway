using System.Xml.Linq;
using ActiveSync.Backends.Dav;
using ActiveSync.Contracts;
using ActiveSync.Integration.Tests.Infrastructure;

namespace ActiveSync.Integration.Tests.Scenarios;

/// <summary>
///   H18: the transient retry replays create-PUTs. A create carries If-None-Match:*, so if the
///   first attempt reached the server and only its response was lost, the replay lands on the
///   resource it just created and comes back 412 — which used to surface as a "precondition failed"
///   BackendException, telling the client the create failed though the item exists. Reproduced
///   deterministically here by issuing the identical create-PUT twice against a real DAV server.
/// </summary>
[Collection("gateway")]
[Trait("Category", "Integration")]
public class DavCreatePutReplayTests
{
	private static readonly XNamespace D = "DAV:";
	private static readonly XNamespace CalDav = "urn:ietf:params:xml:ns:caldav";

	[DavBackendFact]
	public async Task ReplayedCreatePut_OntoTheResourceItCreated_IsTreatedAsSuccess()
	{
		CancellationToken ct = CancellationToken.None;
		using WebDavClient dav = new(
			new Uri(TestBackend.DavUrl!), // DavBackendFact guarantees a DAV backend
			new BackendCredentials(TestBackend.User1, TestBackend.Password),
			allowInvalidCertificates: true);

		// Locate a writable calendar collection (RFC 6764 discovery, or the stack's home-set template).
		string homeSet = string.IsNullOrEmpty(TestBackend.DavHomeSetPath)
			? await DavDiscovery.DiscoverHomeSetAsync(dav, "/.well-known/caldav", CalDav + "calendar-home-set", ct)
			: DavDiscovery.ExpandTemplate(TestBackend.DavHomeSetPath, TestBackend.User1);

		List<DavResource> collections = await dav.PropfindAsync(homeSet, 1,
			new XElement(D + "propfind", new XElement(D + "prop", new XElement(D + "resourcetype"))), ct);
		string? collection = collections
			.FirstOrDefault(r => r.Propstat.Descendants(CalDav + "calendar").Any())?.Href;
		if (collection is null)
			return; // stack exposes no writable calendar collection — nothing to test

		string uid = $"it-h18-{Guid.NewGuid():N}";
		string href = $"{collection.TrimEnd('/')}/{uid}.ics";
		string ics =
			"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//eas//test//EN\r\n" +
			$"BEGIN:VEVENT\r\nUID:{uid}\r\nDTSTAMP:20260722T120000Z\r\n" +
			"DTSTART:20260722T130000Z\r\nDTEND:20260722T140000Z\r\nSUMMARY:H18 replay\r\n" +
			"END:VEVENT\r\nEND:VCALENDAR\r\n";

		try
		{
			await dav.PutAsync(href, ics, "text/calendar", null, ifNoneMatch: true, ct);
			// The replay: an identical create-PUT onto the resource the first one created. Before the
			// fix this threw BackendException ("precondition failed"); now it is treated as success.
			await dav.PutAsync(href, ics, "text/calendar", null, ifNoneMatch: true, ct);

			// The create landed and the replay did not fail it.
			Assert.True(await dav.GetAsync(href, ct) is not null);
		}
		finally
		{
			await dav.DeleteAsync(href, ct);
		}
	}
}

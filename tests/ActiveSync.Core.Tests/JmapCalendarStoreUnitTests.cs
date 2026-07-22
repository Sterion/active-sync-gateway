using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using ActiveSync.Backends.Jmap;
using ActiveSync.Contracts;

namespace ActiveSync.Core.Tests;

/// <summary>
///   H29: <c>GetItemRevisionsAsync</c> ignored the client's calendar FilterType window, so a
///   phone asking for "2 weeks" still listed every event ever. The store enumerates all events, so
///   it must apply the window in memory — while never dropping a recurring event, whose current
///   occurrences may fall inside the window even when its DTSTART does not.
/// </summary>
public sealed class JmapCalendarStoreUnitTests
{
	private static readonly Uri Base = new("http://localhost:5232");

	private const string SessionJson = """
	{
	  "capabilities": { "urn:ietf:params:jmap:core": {}, "urn:ietf:params:jmap:calendars": {} },
	  "primaryAccounts": { "urn:ietf:params:jmap:core": "c", "urn:ietf:params:jmap:calendars": "c" },
	  "apiUrl": "http://localhost:5232/jmap/",
	  "downloadUrl": "http://localhost:5232/jmap/download/{accountId}/{blobId}/{name}?accept={type}",
	  "uploadUrl": "http://localhost:5232/jmap/upload/{accountId}/",
	  "state": "abc"
	}
	""";

	[Fact]
	public async Task GetItemRevisions_AppliesTheCalendarFilterWindow()
	{
		string recent = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
		StubHandler stub = new(request =>
		{
			if (request.RequestUri!.AbsolutePath != "/jmap/")
				return Json(SessionJson);
			return Json($$"""
			{"methodResponses":[["CalendarEvent/get",{"accountId":"c","state":"s","list":[
			  {"id":"OLD","calendarIds":{"C1":true},"start":"2020-01-01T09:00:00"},
			  {"id":"NEW","calendarIds":{"C1":true},"start":"{{recent}}"},
			  {"id":"REC","calendarIds":{"C1":true},"start":"2019-03-04T09:00:00","recurrenceRules":[{"frequency":"weekly"}]}
			]},"0"]],"sessionState":"x"}
			""");
		});
		JmapClient client = new(Base, new HttpClient(stub));
		JmapCalendarStore store = new(client, "u@example.test", pollSeconds: 1);

		IReadOnlyDictionary<string, string> revs = await store.GetItemRevisionsAsync(
			"jmap-cal:C1", new ContentFilter(DateTime.UtcNow.AddDays(-14)), CancellationToken.None);

		Assert.Contains("NEW", revs.Keys);         // inside the window
		Assert.DoesNotContain("OLD", revs.Keys);    // single event before the window — filtered out
		Assert.Contains("REC", revs.Keys);          // recurring — never dropped on a date filter
	}

	// H15: the Ping/Sync wait token re-downloaded the FULL JSCalendar body of every event on every
	// poll tick (CalendarEvent/get ids:null) and SHA-256'd it. The token must instead be the
	// account-level CalendarEvent state (a tiny CalendarEvent/get ids:[] call), so a change is
	// detected without pulling any event body. Proven by advancing ONLY the state between polls
	// (the event list is unchanged) and asserting no ids:null full fetch happened.
	[Fact]
	public async Task WaitForChanges_DetectsViaState_WithoutDownloadingEvents()
	{
		int apiCalls = 0;
		bool sawFullFetch = false;
		StateHandler stub = new(request =>
		{
			if (request.RequestUri!.AbsolutePath != "/jmap/")
				return Json(SessionJson);
			apiCalls++;
			string body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
			using JsonDocument doc = JsonDocument.Parse(body);
			JsonElement call = doc.RootElement.GetProperty("methodCalls")[0];
			bool idsNull = call[1].TryGetProperty("ids", out JsonElement ids) && ids.ValueKind == JsonValueKind.Null;
			if (idsNull)
				sawFullFetch = true;
			string state = apiCalls <= 1 ? "s1" : "s2";
			// The event list is identical across polls; only the state advances.
			string list = idsNull ? "{\"id\":\"E1\",\"calendarIds\":{\"C1\":true},\"start\":\"2024-01-01T09:00:00\"}" : "";
			return Json($"{{\"methodResponses\":[[\"CalendarEvent/get\",{{\"accountId\":\"c\",\"state\":\"{state}\",\"list\":[{list}]}},\"0\"]],\"sessionState\":\"x\"}}");
		});
		JmapClient client = new(Base, new HttpClient(stub));
		JmapCalendarStore store = new(client, "u@example.test", pollSeconds: 1);

		IReadOnlyList<string> changed = await store.WaitForChangesAsync(
			["jmap-cal:C1"], TimeSpan.FromSeconds(4), CancellationToken.None);

		Assert.Contains("jmap-cal:C1", changed);
		Assert.False(sawFullFetch); // the poll must not pull full event bodies
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

	private sealed class StateHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request, CancellationToken cancellationToken)
		{
			return Task.FromResult(responder(request));
		}
	}
}

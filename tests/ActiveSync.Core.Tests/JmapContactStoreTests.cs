using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using ActiveSync.Backends.Jmap;
using ActiveSync.Contracts;

namespace ActiveSync.Core.Tests;

/// <summary>
///   H15: the JMAP contact store's Ping/Sync wait token re-downloaded the full body of every card
///   on each poll tick. The token must be the account-level ContactCard state (a tiny
///   ContactCard/get ids:[] call) so a change is detected without pulling any card body.
/// </summary>
public sealed class JmapContactStoreTests
{
	private static readonly Uri Base = new("http://localhost:5232");

	private const string SessionJson = """
	{
	  "capabilities": { "urn:ietf:params:jmap:core": {}, "urn:ietf:params:jmap:contacts": {} },
	  "primaryAccounts": { "urn:ietf:params:jmap:core": "c", "urn:ietf:params:jmap:contacts": "c" },
	  "apiUrl": "http://localhost:5232/jmap/",
	  "downloadUrl": "http://localhost:5232/jmap/download/{accountId}/{blobId}/{name}?accept={type}",
	  "uploadUrl": "http://localhost:5232/jmap/upload/{accountId}/",
	  "state": "abc"
	}
	""";

	[Fact]
	public async Task WaitForChanges_DetectsViaState_WithoutDownloadingCards()
	{
		int apiCalls = 0;
		bool sawFullFetch = false;
		StubHandler stub = new(request =>
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
			string list = idsNull ? "{\"id\":\"K1\",\"addressBookIds\":{\"B1\":true}}" : "";
			return Json($"{{\"methodResponses\":[[\"ContactCard/get\",{{\"accountId\":\"c\",\"state\":\"{state}\",\"list\":[{list}]}},\"0\"]],\"sessionState\":\"x\"}}");
		});
		JmapClient client = new(Base, new HttpClient(stub));
		JmapContactStore store = new(client, pollSeconds: 1);

		IReadOnlyList<string> changed = await store.WaitForChangesAsync(
			["jmap-contact:B1"], TimeSpan.FromSeconds(4), CancellationToken.None);

		Assert.Contains("jmap-contact:B1", changed);
		Assert.False(sawFullFetch);
	}

	// H5: item revisions used to be a SHA-256 of the raw ContactCard JSON text, which is sensitive to
	// member ORDER and whitespace — both server-defined for a JSON object. A permitted re-serialization
	// flipped every card's revision, so the diff engine (which treats the revision map as the whole
	// truth) re-sent the entire address book. Two logically identical cards whose members differ only
	// in order MUST hash to the same revision. Red-first: over the raw text the two revisions differ.
	[Fact]
	public async Task GetItemRevisions_IsIndependentOfMemberOrder()
	{
		const string ordered =
			"""{"id":"K1","addressBookIds":{"B1":true},"name":{"full":"Jane Doe"},"emails":{"e1":{"address":"jane@x.test"}}}""";
		const string reordered =
			"""{"emails":{"e1":{"address":"jane@x.test"}},"name":{"full":"Jane Doe"},"addressBookIds":{"B1":true},"id":"K1"}""";

		string first = await RevisionOfSingleCard(ordered);
		string second = await RevisionOfSingleCard(reordered);

		Assert.Equal(first, second);
	}

	// H19: SearchGalAsync silently ignored the GalPhotoRequest parameter — a client that asked for
	// photos got neither photo data nor the MS-ASCMD "no photo" (173) status element, unlike every
	// other GAL implementation (which routes through ContactConverter.AppendGalPicture).
	[Fact]
	public async Task SearchGal_WithPhotoRequest_EmitsNoPhotoStatus()
	{
		StubHandler stub = new(request =>
		{
			if (request.RequestUri!.AbsolutePath != "/jmap/")
				return Json(SessionJson);
			return Json("""
			{"methodResponses":[["ContactCard/get",{"accountId":"c","state":"s","list":[
			  {"id":"K1","addressBookIds":{"B1":true},"name":{"full":"Jane Doe"}}
			]},"0"]],"sessionState":"x"}
			""");
		});
		JmapClient client = new(Base, new HttpClient(stub));
		JmapContactStore store = new(client, pollSeconds: 1);

		IReadOnlyList<IReadOnlyList<XElement>> results = await store.SearchGalAsync(
			"Jane", maxResults: 10, new GalPhotoRequest(null, null), CancellationToken.None);

		Assert.Single(results);
		XElement? picture = results[0].FirstOrDefault(e => e.Name.LocalName == "Picture");
		Assert.NotNull(picture);
		Assert.Equal("173", picture!.Elements().FirstOrDefault(e => e.Name.LocalName == "Status")?.Value);
	}

	// H7: GetItemRevisionsAsync is invoked once PER address book within one Sync round; it used to
	// re-download the FULL account's cards every time (ContactCard/get ids:null), so M address
	// books cost M full downloads of the same N cards. The full body download must happen at most
	// once per account-level state, regardless of how many folders are listed.
	[Fact]
	public async Task GetItemRevisions_AcrossMultipleAddressBooks_DownloadsCardBodiesOnce()
	{
		int fullDownloads = 0;
		StubHandler stub = new(request =>
		{
			if (request.RequestUri!.AbsolutePath != "/jmap/")
				return Json(SessionJson);
			string body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
			using JsonDocument doc = JsonDocument.Parse(body);
			JsonElement call = doc.RootElement.GetProperty("methodCalls")[0];
			bool idsNull = call[1].TryGetProperty("ids", out JsonElement ids) && ids.ValueKind == JsonValueKind.Null;
			if (idsNull)
				fullDownloads++;
			string list = idsNull
				? "{\"id\":\"K1\",\"addressBookIds\":{\"B1\":true,\"B2\":true}}"
				: "";
			return Json(
				$"{{\"methodResponses\":[[\"ContactCard/get\",{{\"accountId\":\"c\",\"state\":\"s1\",\"list\":[{list}]}},\"0\"]],\"sessionState\":\"x\"}}");
		});
		JmapClient client = new(Base, new HttpClient(stub));
		JmapContactStore store = new(client, pollSeconds: 1);

		await store.GetItemRevisionsAsync("jmap-contact:B1", ContentFilter.All, CancellationToken.None);
		await store.GetItemRevisionsAsync("jmap-contact:B2", ContentFilter.All, CancellationToken.None);

		Assert.Equal(1, fullDownloads);
	}

	// H7: a server that declares a finite maxObjectsInGet answers requestTooLarge to a blind
	// "ids:null" over a large address book. When one is declared, listing must page the ids
	// through ContactCard/query + ContactCard/get instead of a single unbounded get.
	[Fact]
	public async Task GetItemRevisions_ServerDeclaresFiniteMaxObjectsInGet_PagesTheListing()
	{
		const string sessionWithLimit = """
		{
		  "capabilities": {
		    "urn:ietf:params:jmap:core": { "maxObjectsInGet": 2, "maxObjectsInSet": 2,
		      "maxCallsInRequest": 16, "maxSizeUpload": 1000000, "maxConcurrentRequests": 4 },
		    "urn:ietf:params:jmap:contacts": {}
		  },
		  "primaryAccounts": { "urn:ietf:params:jmap:core": "c", "urn:ietf:params:jmap:contacts": "c" },
		  "apiUrl": "http://localhost:5232/jmap/",
		  "downloadUrl": "http://localhost:5232/jmap/download/{accountId}/{blobId}/{name}?accept={type}",
		  "uploadUrl": "http://localhost:5232/jmap/upload/{accountId}/",
		  "state": "abc"
		}
		""";
		bool sawUnboundedGet = false;
		int queryCalls = 0;
		StubHandler stub = new(request =>
		{
			if (request.RequestUri!.AbsolutePath != "/jmap/")
				return Json(sessionWithLimit);
			string body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
			using JsonDocument doc = JsonDocument.Parse(body);
			JsonElement calls = doc.RootElement.GetProperty("methodCalls");
			JsonElement first = calls[0];
			string firstName = first[0].GetString()!;
			if (firstName == "ContactCard/get")
			{
				bool idsNull = first[1].TryGetProperty("ids", out JsonElement ids) && ids.ValueKind == JsonValueKind.Null;
				if (idsNull)
					sawUnboundedGet = true;
				// The account-level state check (ids:[]).
				string getId = first[2].GetString()!;
				return Json(
					$"{{\"methodResponses\":[[\"ContactCard/get\",{{\"accountId\":\"c\",\"state\":\"s1\",\"list\":[]}},\"{getId}\"]],\"sessionState\":\"x\"}}");
			}

			// A batched [ContactCard/query, ContactCard/get] pair.
			queryCalls++;
			int position = first[1].GetProperty("position").GetInt32();
			string queryId = first[2].GetString()!;
			string getPairId = calls[1][2].GetString()!;
			string[] pageIds = position == 0 ? ["K1", "K2"] : [];
			string idsJson = string.Join(",", pageIds.Select(i => $"\"{i}\""));
			string listJson = string.Join(",", pageIds.Select(i => $"{{\"id\":\"{i}\",\"addressBookIds\":{{\"B1\":true}}}}"));
			string queryResponse =
				$"[\"ContactCard/query\",{{\"accountId\":\"c\",\"queryState\":\"qs1\",\"position\":{position},\"ids\":[{idsJson}]}},\"{queryId}\"]";
			string getResponse = $"[\"ContactCard/get\",{{\"accountId\":\"c\",\"list\":[{listJson}]}},\"{getPairId}\"]";
			return Json($"{{\"methodResponses\":[{queryResponse},{getResponse}],\"sessionState\":\"x\"}}");
		});
		JmapClient client = new(Base, new HttpClient(stub));
		JmapContactStore store = new(client, pollSeconds: 1);

		IReadOnlyDictionary<string, string> revs = await store.GetItemRevisionsAsync(
			"jmap-contact:B1", ContentFilter.All, CancellationToken.None);

		Assert.True(queryCalls > 0, "a server that declares a finite maxObjectsInGet must be paged via ContactCard/query");
		Assert.False(sawUnboundedGet, "must not send a blind ids:null get once a finite maxObjectsInGet is declared");
		Assert.Equal(2, revs.Count);
	}

	private static async Task<string> RevisionOfSingleCard(string cardJson)
	{
		StubHandler stub = new(request =>
		{
			if (request.RequestUri!.AbsolutePath != "/jmap/")
				return Json(SessionJson);
			return Json(
				$"{{\"methodResponses\":[[\"ContactCard/get\",{{\"accountId\":\"c\",\"state\":\"s\",\"list\":[{cardJson}]}},\"0\"]],\"sessionState\":\"x\"}}");
		});
		JmapClient client = new(Base, new HttpClient(stub));
		JmapContactStore store = new(client, pollSeconds: 1);
		IReadOnlyDictionary<string, string> revs = await store.GetItemRevisionsAsync(
			"jmap-contact:B1", ContentFilter.All, CancellationToken.None);
		return revs["K1"];
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
}

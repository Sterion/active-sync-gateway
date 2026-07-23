using System.Net;
using System.Text;
using System.Text.Json;
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

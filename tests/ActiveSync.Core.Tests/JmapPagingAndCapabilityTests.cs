using System.Net;
using System.Text;
using System.Text.Json;
using ActiveSync.Backends.Jmap;
using ActiveSync.Contracts;

namespace ActiveSync.Core.Tests;

/// <summary>
///   H8: <c>JmapMailStore.GetItemRevisionsAsync</c> hardcoded a 500-item page and terminated the
///   loop on <c>returned &lt; page</c>. A server that caps its <c>Email/query</c> response below the
///   requested limit truncates the folder silently (the device sees the tail as deleted), and a
///   server advertising <c>maxObjectsInGet</c> below 500 fails the whole sync with
///   <c>requestTooLarge</c> because up to 500 ids get back-referenced into one <c>Email/get</c>.
///   Paging must size to <c>min(500, maxObjectsInGet)</c> and terminate only on an empty page (or the
///   server's own total).
/// </summary>
public sealed class JmapPagingAndCapabilityTests
{
	private static readonly Uri Base = new("http://localhost:5232");

	// A session whose core capability object advertises the given maxObjectsInGet (0 = omit it).
	private static string SessionJson(int maxObjectsInGet = 0)
	{
		string core = maxObjectsInGet > 0 ? $$"""{ "maxObjectsInGet": {{maxObjectsInGet}} }""" : "{}";
		return $$"""
		{
		  "capabilities": { "urn:ietf:params:jmap:core": {{core}}, "urn:ietf:params:jmap:mail": {} },
		  "primaryAccounts": { "urn:ietf:params:jmap:core": "c", "urn:ietf:params:jmap:mail": "c" },
		  "apiUrl": "http://localhost:5232/jmap/",
		  "downloadUrl": "http://localhost:5232/jmap/download/{accountId}/{blobId}/{name}?accept={type}",
		  "uploadUrl": "http://localhost:5232/jmap/upload/{accountId}/",
		  "state": "abc"
		}
		""";
	}

	// H8 — the server caps every Email/query response at 2 ids even when 500 are requested, and holds
	// 4 messages. The old loop broke after the first (short) page, syncing only 2 of 4; the tail then
	// looks deleted to the device. Paging must keep going until a page returns empty/total is reached.
	[Fact]
	public async Task GetItemRevisions_ServerCapsPageBelowLimit_SyncsEveryItem()
	{
		PagingHandler handler = new(SessionJson(), ["E0", "E1", "E2", "E3"], capPerQuery: 2);
		JmapClient client = new(Base, new HttpClient(handler));
		JmapMailStore store = new(client, "u@example.test", pollSeconds: 1);

		IReadOnlyDictionary<string, string> map = await store.GetItemRevisionsAsync(
			JmapMailStore.ToKey("INBOXID"), ContentFilter.All, CancellationToken.None);

		Assert.Equal(4, map.Count);
		Assert.Equal(new[] { "E0", "E1", "E2", "E3" }, map.Keys.OrderBy(k => k));
	}

	// H8/H9 — the server advertises maxObjectsInGet=2 and answers requestTooLarge to any Email/get
	// carrying more ids. The old code back-referenced up to 500 query ids into one Email/get, so the
	// whole folder sync threw. Paging at min(500, maxObjectsInGet) keeps every Email/get within the
	// advertised ceiling.
	[Fact]
	public async Task GetItemRevisions_HonoursMaxObjectsInGet_DoesNotOverfillEmailGet()
	{
		PagingHandler handler = new(
			SessionJson(maxObjectsInGet: 2), ["E0", "E1", "E2"], maxObjectsInGet: 2);
		JmapClient client = new(Base, new HttpClient(handler));
		JmapMailStore store = new(client, "u@example.test", pollSeconds: 1);

		IReadOnlyDictionary<string, string> map = await store.GetItemRevisionsAsync(
			JmapMailStore.ToKey("INBOXID"), ContentFilter.All, CancellationToken.None);

		Assert.Equal(3, map.Count);
		Assert.True(handler.MaxGetIdsSeen <= 2,
			$"an Email/get carried {handler.MaxGetIdsSeen} ids, over the advertised maxObjectsInGet of 2");
	}

	private static HttpResponseMessage Json(string body)
	{
		return new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent(body, Encoding.UTF8, "application/json")
		};
	}

	/// <summary>
	///   A JMAP handler that pages a fixed corpus of Email ids. <paramref name="capPerQuery" /> caps
	///   how many ids one Email/query returns regardless of the requested limit (to simulate a server
	///   that returns fewer than asked); <paramref name="maxObjectsInGet" /> makes an Email/get carrying
	///   more back-referenced ids than that answer requestTooLarge. Records the largest Email/get id
	///   batch it saw.
	/// </summary>
	private sealed class PagingHandler(
		string sessionJson,
		IReadOnlyList<string> emailIds,
		int capPerQuery = int.MaxValue,
		int maxObjectsInGet = int.MaxValue) : HttpMessageHandler
	{
		public int MaxGetIdsSeen { get; private set; }

		protected override async Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request, CancellationToken cancellationToken)
		{
			if (request.RequestUri!.AbsolutePath != "/jmap/")
				return Json(sessionJson);
			string body = await request.Content!.ReadAsStringAsync(cancellationToken);
			using JsonDocument doc = JsonDocument.Parse(body);
			List<string> responses = new();
			List<string> lastQueryIds = new();
			foreach (JsonElement call in doc.RootElement.GetProperty("methodCalls").EnumerateArray())
			{
				string name = call[0].GetString()!;
				JsonElement args = call[1];
				string id = call[2].GetString()!;
				switch (name)
				{
					case "Email/query":
					{
						int position = args.TryGetProperty("position", out JsonElement p) && p.TryGetInt32(out int pv) ? pv : 0;
						int limit = args.TryGetProperty("limit", out JsonElement l) && l.TryGetInt32(out int lv) ? lv : emailIds.Count;
						int take = Math.Min(Math.Min(limit, capPerQuery), Math.Max(0, emailIds.Count - position));
						lastQueryIds = emailIds.Skip(position).Take(take).ToList();
						string idsJson = string.Join(",", lastQueryIds.Select(x => $"\"{x}\""));
						responses.Add(
							$"[\"Email/query\",{{\"accountId\":\"c\",\"position\":{position},\"total\":{emailIds.Count},\"ids\":[{idsJson}]}},\"{id}\"]");
						break;
					}
					case "Email/get":
					{
						MaxGetIdsSeen = Math.Max(MaxGetIdsSeen, lastQueryIds.Count);
						if (lastQueryIds.Count > maxObjectsInGet)
						{
							responses.Add($"[\"error\",{{\"type\":\"requestTooLarge\"}},\"{id}\"]");
							break;
						}

						string listJson = string.Join(",", lastQueryIds.Select(x => $"{{\"id\":\"{x}\",\"keywords\":{{}}}}"));
						responses.Add($"[\"Email/get\",{{\"accountId\":\"c\",\"state\":\"s\",\"list\":[{listJson}]}},\"{id}\"]");
						break;
					}

					default:
						responses.Add($"[\"{name}\",{{\"accountId\":\"c\",\"list\":[]}},\"{id}\"]");
						break;
				}
			}

			return Json($"{{\"methodResponses\":[{string.Join(",", responses)}],\"sessionState\":\"x\"}}");
		}
	}
}

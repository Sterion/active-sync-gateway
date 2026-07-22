using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using ActiveSync.Backends.Jmap;
using ActiveSync.Contracts;

namespace ActiveSync.Core.Tests;

/// <summary>
///   JMAP mail-store long-poll detection. H19: the per-folder change token was
///   <c>totalEmails:unreadEmails</c>, which is blind to a flag-only change (it moves no counter)
///   and to an equal add+delete (the counts net out). The token must also track the account-level
///   Email state so those changes wake a Ping.
/// </summary>
public sealed class JmapMailStoreTests
{
	private static readonly Uri Base = new("http://localhost:5232");

	private const string SessionJson = """
	{
	  "capabilities": { "urn:ietf:params:jmap:core": {}, "urn:ietf:params:jmap:mail": {} },
	  "primaryAccounts": { "urn:ietf:params:jmap:core": "c", "urn:ietf:params:jmap:mail": "c" },
	  "apiUrl": "http://localhost:5232/jmap/",
	  "downloadUrl": "http://localhost:5232/jmap/download/{accountId}/{blobId}/{name}?accept={type}",
	  "uploadUrl": "http://localhost:5232/jmap/upload/{accountId}/",
	  "state": "abc"
	}
	""";

	// H19: mailbox counts stay identical across the two poll cycles (a flag-only change) but the
	// account Email state advances. WaitForChangesAsync must report the folder as changed.
	[Fact]
	public async Task WaitForChanges_FlagOnlyChange_IsDetectedViaEmailState()
	{
		int apiCalls = 0;
		StubHandler stub = new(request =>
		{
			if (request.RequestUri!.AbsolutePath != "/jmap/")
				return Json(SessionJson);
			apiCalls++;
			// Counts never move; only the Email state does (s1 on the baseline read, s2 afterwards).
			string emailState = apiCalls <= 1 ? "s1" : "s2";
			return Json($$"""
			{"methodResponses":[
			  ["Mailbox/get",{"accountId":"c","state":"m","list":[{"id":"INBOXID","totalEmails":5,"unreadEmails":2}]},"0"],
			  ["Email/get",{"accountId":"c","state":"{{emailState}}","list":[]},"1"]
			],"sessionState":"x"}
			""");
		});

		JmapClient client = new(Base, new HttpClient(stub));
		JmapMailStore store = new(client, "u@example.test", pollSeconds: 1);

		IReadOnlyList<string> changed = await store.WaitForChangesAsync(
			[JmapMailStore.ToKey("INBOXID")], TimeSpan.FromSeconds(4), CancellationToken.None);

		Assert.Contains(JmapMailStore.ToKey("INBOXID"), changed);
	}

	// H10: a permanent delete whose Email/set returns the id in notDestroyed used to be ignored
	// (the response leaked, undisposed) and reported as success. It must surface as a failure.
	[Fact]
	public async Task DeleteItem_ServerReportsNotDestroyed_Throws()
	{
		StubHandler stub = new(request =>
		{
			if (request.RequestUri!.AbsolutePath != "/jmap/")
				return Json(SessionJson);
			return Json("""
			{"methodResponses":[
			  ["Email/set",{"accountId":"c","notDestroyed":{"E1":{"type":"serverFail"}}},"0"]
			],"sessionState":"x"}
			""");
		});
		JmapClient client = new(Base, new HttpClient(stub));
		JmapMailStore store = new(client, "u@example.test", pollSeconds: 1);

		await Assert.ThrowsAsync<BackendException>(() =>
			store.DeleteItemAsync(JmapMailStore.ToKey("INBOXID"), "E1", permanent: true, CancellationToken.None));
	}

	// H20: updating a message the server has since deleted (Email/set returns it in notUpdated with
	// type notFound) must surface as BackendItemNotFoundException so the host reconciles, not as a
	// generic error or a silent success.
	[Fact]
	public async Task UpdateItem_ServerReportsNotFound_ThrowsItemNotFound()
	{
		StubHandler stub = new(request =>
		{
			if (request.RequestUri!.AbsolutePath != "/jmap/")
				return Json(SessionJson);
			return Json("""
			{"methodResponses":[
			  ["Email/set",{"accountId":"c","notUpdated":{"E1":{"type":"notFound"}}},"0"]
			],"sessionState":"x"}
			""");
		});
		JmapClient client = new(Base, new HttpClient(stub));
		JmapMailStore store = new(client, "u@example.test", pollSeconds: 1);
		XElement change = new("ApplicationData",
			new XElement(XName.Get("Read", "Email"), "1"));

		await Assert.ThrowsAsync<BackendItemNotFoundException>(() =>
			store.UpdateItemAsync(JmapMailStore.ToKey("INBOXID"), "E1", change, CancellationToken.None));
	}

	// H25: a flag/read update issued Email/set then a SEPARATE Email/get — two sequential round
	// trips where JMAP's whole point is batching. The set and the trailing get must go in ONE
	// request, so a routine "mark read" costs one API call, not two.
	[Fact]
	public async Task UpdateItem_ReadChange_BatchesSetAndGetInOneRequest()
	{
		MethodStub stub = new();
		JmapClient client = new(Base, new HttpClient(stub));
		JmapMailStore store = new(client, "u@example.test", pollSeconds: 1);
		XElement change = new("ApplicationData", new XElement(XName.Get("Read", "Email"), "1"));

		await store.UpdateItemAsync(JmapMailStore.ToKey("INBOXID"), "E1", change, CancellationToken.None);

		Assert.Equal(1, stub.ApiCalls); // set + get in one request, not two
	}

	// H25: a non-permanent delete calls FindMailboxByRoleAsync, which did Mailbox/get with ids:null
	// (the ENTIRE mailbox list) on every delete, uncached. The role→mailbox map must be cached on
	// the store, so deleting many messages does not re-list the mailboxes each time.
	[Fact]
	public async Task DeleteItem_TrashLookup_IsCachedAcrossCalls()
	{
		MethodStub stub = new();
		JmapClient client = new(Base, new HttpClient(stub));
		JmapMailStore store = new(client, "u@example.test", pollSeconds: 1);

		await store.DeleteItemAsync(JmapMailStore.ToKey("INBOXID"), "E1", permanent: false, CancellationToken.None);
		await store.DeleteItemAsync(JmapMailStore.ToKey("INBOXID"), "E2", permanent: false, CancellationToken.None);

		Assert.Equal(1, stub.FullMailboxListings); // one Mailbox/get ids:null for both deletes
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

	/// <summary>
	///   A JMAP handler that answers any batch of method calls per-method, so it works whether the
	///   store batches or not. Counts total API (<c>/jmap/</c>) requests and full mailbox listings
	///   (<c>Mailbox/get</c> with <c>ids:null</c>).
	/// </summary>
	private sealed class MethodStub : HttpMessageHandler
	{
		public int ApiCalls;
		public int FullMailboxListings;

		protected override async Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request, CancellationToken cancellationToken)
		{
			if (request.RequestUri!.AbsolutePath != "/jmap/")
				return Json(SessionJson);
			ApiCalls++;
			string body = await request.Content!.ReadAsStringAsync(cancellationToken);
			using JsonDocument doc = JsonDocument.Parse(body);
			List<string> responses = new();
			foreach (JsonElement call in doc.RootElement.GetProperty("methodCalls").EnumerateArray())
			{
				string name = call[0].GetString()!;
				JsonElement args = call[1];
				string id = call[2].GetString()!;
				bool idsNull = args.TryGetProperty("ids", out JsonElement ids) && ids.ValueKind == JsonValueKind.Null;
				string argsJson = name switch
				{
					"Mailbox/get" when idsNull => Count(ref FullMailboxListings,
						"\"list\":[{\"id\":\"INBOXID\"},{\"id\":\"TRASHID\",\"role\":\"trash\"}]"),
					"Mailbox/get" => "\"list\":[{\"id\":\"INBOXID\"}]",
					"Email/set" => "\"updated\":{\"E1\":null,\"E2\":null}",
					"Email/get" => "\"state\":\"s\",\"list\":[{\"id\":\"E1\",\"keywords\":{\"$seen\":true}}]",
					_ => "\"list\":[]"
				};
				responses.Add($"[\"{name}\",{{\"accountId\":\"c\",{argsJson}}},\"{id}\"]");
			}

			return Json($"{{\"methodResponses\":[{string.Join(",", responses)}],\"sessionState\":\"x\"}}");
		}

		private static string Count(ref int counter, string value)
		{
			counter++;
			return value;
		}
	}
}

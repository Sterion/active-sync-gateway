using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using ActiveSync.Backends.Jmap;
using ActiveSync.Contracts;

namespace ActiveSync.Core.Tests;

/// <summary>
///   JMAP mail-store long-poll detection. The per-folder change token was
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

	// Mailbox counts stay identical across the two poll cycles (a flag-only change) but the
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

	// A permanent delete whose Email/set returns the id in notDestroyed used to be ignored
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

	// Updating a message the server has since deleted (Email/set returns it in notUpdated with
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

	// A flag/read update issued Email/set then a SEPARATE Email/get — two sequential round
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

	// A non-permanent delete calls FindMailboxByRoleAsync, which did Mailbox/get with ids:null
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

	// Delete-to-trash replaced "mailboxIds" wholesale with just {trash: true}, so a message
	// filed under more than one mailbox (e.g. a label plus Inbox) lost every other membership on
	// a single-folder EAS delete. The update must PATCH only the two affected keys.
	[Fact]
	public async Task DeleteItem_NonPermanent_PatchesMailboxIds_InsteadOfReplacing()
	{
		PatchCapturingStub stub = new();
		JmapClient client = new(Base, new HttpClient(stub));
		JmapMailStore store = new(client, "u@example.test", pollSeconds: 1);

		await store.DeleteItemAsync(JmapMailStore.ToKey("INBOXID"), "E1", permanent: false, CancellationToken.None);

		Assert.NotNull(stub.CapturedUpdate);
		JsonElement patch = stub.CapturedUpdate!.Value.GetProperty("E1");
		Assert.False(patch.TryGetProperty("mailboxIds", out _)); // never a wholesale replace
		Assert.True(patch.GetProperty("mailboxIds/INBOXID").ValueKind == JsonValueKind.Null);
		Assert.True(patch.GetProperty("mailboxIds/TRASHID").GetBoolean());
	}

	// MoveItemAsync has the identical shape — moving a multi-filed message must drop only the
	// source mailbox, not every mailbox the message happened to be in.
	[Fact]
	public async Task MoveItem_PatchesMailboxIds_InsteadOfReplacing()
	{
		PatchCapturingStub stub = new();
		JmapClient client = new(Base, new HttpClient(stub));
		JmapMailStore store = new(client, "u@example.test", pollSeconds: 1);

		await store.MoveItemAsync(
			JmapMailStore.ToKey("INBOXID"), "E1", JmapMailStore.ToKey("ARCHIVEID"), CancellationToken.None);

		Assert.NotNull(stub.CapturedUpdate);
		JsonElement patch = stub.CapturedUpdate!.Value.GetProperty("E1");
		Assert.False(patch.TryGetProperty("mailboxIds", out _));
		Assert.True(patch.GetProperty("mailboxIds/INBOXID").ValueKind == JsonValueKind.Null);
		Assert.True(patch.GetProperty("mailboxIds/ARCHIVEID").GetBoolean());
	}

	// Category keywords were spliced into the JMAP PatchObject path without RFC 6901 escaping,
	// so a category containing '/' (legal EAS free text and a legal JMAP keyword) produced a path
	// the server reads as a NESTED pointer ("keywords/Work/Home") and rejects with invalidPatch,
	// failing the whole Sync Change. '/' must become "~1" per RFC 6901.
	[Fact]
	public async Task UpdateItem_CategoryWithSlash_IsEscapedAsAJsonPointerToken()
	{
		PatchCapturingStub stub = new();
		JmapClient client = new(Base, new HttpClient(stub));
		JmapMailStore store = new(client, "u@example.test", pollSeconds: 1);
		XElement change = new("ApplicationData",
			new XElement(XName.Get("Categories", "Email"),
				new XElement(XName.Get("Category", "Email"), "Work/Home")));

		await store.UpdateItemAsync(JmapMailStore.ToKey("INBOXID"), "E1", change, CancellationToken.None);

		Assert.NotNull(stub.CapturedUpdate);
		JsonElement patch = stub.CapturedUpdate!.Value.GetProperty("E1");
		Assert.True(patch.TryGetProperty("keywords/Work~1Home", out JsonElement v) && v.GetBoolean());
		Assert.False(patch.TryGetProperty("keywords/Work/Home", out _));
	}

	// A category containing a character the JMAP keyword grammar forbids (RFC 8621 §4.1.1 —
	// '(' ')' '{' ']' '%' '*' '"' '\' and non-ASCII) must be dropped, mirroring
	// ImapMailBackend.SanitizeKeyword's drop-don't-mangle rule, rather than sent verbatim.
	[Fact]
	public async Task UpdateItem_CategoryWithForbiddenCharacter_IsDropped()
	{
		PatchCapturingStub stub = new();
		JmapClient client = new(Base, new HttpClient(stub));
		JmapMailStore store = new(client, "u@example.test", pollSeconds: 1);
		XElement change = new("ApplicationData",
			new XElement(XName.Get("Categories", "Email"),
				new XElement(XName.Get("Category", "Email"), "Bad(Cat)")));

		await store.UpdateItemAsync(JmapMailStore.ToKey("INBOXID"), "E1", change, CancellationToken.None);

		// No patch entry at all for the forbidden-character category — no keywords/* key present.
		Assert.True(stub.CapturedUpdate is null ||
		            !stub.CapturedUpdate!.Value.GetProperty("E1").EnumerateObject().Any());
	}

	// Position-based paging over a descending sort is not stable under a concurrent mailbox
	// change. Server-side timeline: [A,B,C,D,E] (positions 0-4). Page 1 (position 0, limit 2)
	// returns [A,B] under queryState "s1". Before page 2 is issued, B is deleted, so the live
	// order becomes [A,C,D,E] under queryState "s2" — C has shifted from position 2 down to
	// position 1. A naive continuation asks for position 2 next and gets [D,E]: C is never
	// returned by any page and silently vanishes from the revision map, which is exactly what
	// makes the diff engine delete a message that still exists on the server. The fix must notice
	// the queryState change and restart the whole enumeration from position 0 so it re-reads the
	// live order and picks C up.
	[Fact]
	public async Task GetItemRevisions_QueryStateChangesMidEnumeration_RecoversTheShiftedItem()
	{
		QueryStateShiftStub stub = new();
		JmapClient client = new(Base, new HttpClient(stub));
		JmapMailStore store = new(client, "u@example.test", pollSeconds: 1);

		IReadOnlyDictionary<string, string> map = await store.GetItemRevisionsAsync(
			JmapMailStore.ToKey("INBOXID"), new ContentFilter(null), CancellationToken.None);

		Assert.True(map.ContainsKey("C"),
			"an item that shifted position under a concurrent delete must not be dropped from the revision map");
	}

	// The query never sets calculateTotal, so RFC 8620 §5.5 says `total` is normally absent
	// and the `position >= total` break is dead — termination rests solely on `returned == 0`. A
	// server that ignores the `position` argument and always reports `position: 0` (while still
	// returning non-empty pages) must not spin this loop forever. Bounded with a cancellation
	// token rather than waited out — an unbounded loop would otherwise hang the test itself.
	[Fact]
	public async Task GetItemRevisions_ServerNeverAdvancesPosition_Terminates()
	{
		StuckPositionStub stub = new();
		JmapClient client = new(Base, new HttpClient(stub));
		JmapMailStore store = new(client, "u@example.test", pollSeconds: 1);
		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));

		IReadOnlyDictionary<string, string> map = await store.GetItemRevisionsAsync(
			JmapMailStore.ToKey("INBOXID"), new ContentFilter(null), cts.Token);

		Assert.True(stub.Calls < 100, $"the loop must terminate on its own; it made {stub.Calls} requests");
		Assert.NotEmpty(map);
	}

	// Email/import is defined under urn:ietf:params:jmap:mail (RFC 8621 §4.8) and needs no
	// blob-management capability. Requiring urn:ietf:params:jmap:blob (RFC 9404) as well means a
	// server that does not implement that separate extension rejects the WHOLE request (RFC 8620
	// §3.6.1's `unknownCapability`) — breaking every Save-to-Sent and draft create/edit, not a
	// blob-specific feature.
	[Fact]
	public async Task SaveToSent_DoesNotRequestTheBlobCapability()
	{
		UsingRejectingStub stub = new();
		JmapClient client = new(Base, new HttpClient(stub));
		JmapMailStore store = new(client, "u@example.test", pollSeconds: 1);
		byte[] mime = Encoding.ASCII.GetBytes("From: a@example.test\r\nTo: b@example.test\r\nSubject: s\r\n\r\nbody\r\n");

		await store.SaveToSentAsync(mime, CancellationToken.None);
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

	/// <summary>
	///   Answers the trash-lookup <c>Mailbox/get</c> and captures the <c>"update"</c> argument of
	///   the next <c>Email/set</c> call (kept alive past the request's own <c>JsonDocument</c> via a
	///   re-parsed copy) so a test can assert on its exact shape.
	/// </summary>
	private sealed class PatchCapturingStub : HttpMessageHandler
	{
		private JsonDocument? _capturedDoc;
		public JsonElement? CapturedUpdate => _capturedDoc?.RootElement;

		protected override async Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request, CancellationToken cancellationToken)
		{
			if (request.RequestUri!.AbsolutePath != "/jmap/")
				return Json(SessionJson);
			string body = await request.Content!.ReadAsStringAsync(cancellationToken);
			using JsonDocument doc = JsonDocument.Parse(body);
			List<string> responses = new();
			foreach (JsonElement call in doc.RootElement.GetProperty("methodCalls").EnumerateArray())
			{
				string name = call[0].GetString()!;
				JsonElement args = call[1];
				string id = call[2].GetString()!;
				bool idsNull = args.TryGetProperty("ids", out JsonElement ids) && ids.ValueKind == JsonValueKind.Null;
				string argsJson;
				if (name == "Mailbox/get" && idsNull)
					argsJson = "\"list\":[{\"id\":\"INBOXID\"},{\"id\":\"ARCHIVEID\"},{\"id\":\"TRASHID\",\"role\":\"trash\"}]";
				else if (name == "Email/set")
				{
					_capturedDoc = JsonDocument.Parse(args.GetProperty("update").GetRawText());
					argsJson = "\"updated\":{\"E1\":null}";
				}
				else
					argsJson = "\"list\":[]";
				responses.Add($"[\"{name}\",{{\"accountId\":\"c\",{argsJson}}},\"{id}\"]");
			}

			return Json($"{{\"methodResponses\":[{string.Join(",", responses)}],\"sessionState\":\"x\"}}");
		}
	}

	/// <summary>
	///   Models a mailbox where a concurrent delete shifts the descending-sort order between
	///   pages: position 0 first returns the PRE-delete page ([A,B], queryState "s1"); every
	///   request thereafter reflects the POST-delete order (queryState "s2") — a position-0 re-read
	///   returns [A,C] and a position-2 read returns [D,E]. B is gone; C shifted from index 2 to
	///   index 1 and must be recovered by restarting from 0 rather than continuing at the old
	///   position.
	/// </summary>
	private sealed class QueryStateShiftStub : HttpMessageHandler
	{
		private bool _changed;

		protected override async Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request, CancellationToken cancellationToken)
		{
			if (request.RequestUri!.AbsolutePath != "/jmap/")
				return Json(SessionJson);
			string body = await request.Content!.ReadAsStringAsync(cancellationToken);
			using JsonDocument doc = JsonDocument.Parse(body);
			JsonElement calls = doc.RootElement.GetProperty("methodCalls");
			JsonElement queryCall = calls[0];
			int position = queryCall[1].GetProperty("position").GetInt32();
			string queryId = queryCall[2].GetString()!;
			string getId = calls[1][2].GetString()!;

			string state;
			string[] ids;
			if (position == 0 && !_changed)
			{
				state = "s1";
				ids = ["A", "B"];
				_changed = true; // the delete happens concurrently right after this page is served
			}
			else if (position == 0)
			{
				state = "s2";
				ids = ["A", "C"];
			}
			else if (position == 2)
			{
				state = "s2";
				ids = ["D", "E"];
			}
			else
			{
				state = "s2";
				ids = [];
			}

			string idsJson = string.Join(",", ids.Select(i => $"\"{i}\""));
			string listJson = string.Join(",", ids.Select(i => $"{{\"id\":\"{i}\",\"keywords\":{{}}}}"));
			string queryResponse =
				$"[\"Email/query\",{{\"accountId\":\"c\",\"queryState\":\"{state}\",\"position\":{position},\"ids\":[{idsJson}]}},\"{queryId}\"]";
			string getResponse = $"[\"Email/get\",{{\"accountId\":\"c\",\"list\":[{listJson}]}},\"{getId}\"]";
			return Json($"{{\"methodResponses\":[{queryResponse},{getResponse}],\"sessionState\":\"x\"}}");
		}
	}

	/// <summary>
	///   Models a JMAP server that does not implement RFC 9404: any request whose top-level
	///   <c>using</c> names <see cref="JmapCapabilities.Blob" /> is rejected wholesale (RFC 8620
	///   §3.6.1's <c>unknownCapability</c>), same as a real such server would.
	/// </summary>
	private sealed class UsingRejectingStub : HttpMessageHandler
	{
		protected override async Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request, CancellationToken cancellationToken)
		{
			string path = request.RequestUri!.AbsolutePath;
			if (path == "/.well-known/jmap")
				return Json(SessionJson);
			if (path.StartsWith("/jmap/upload/", StringComparison.Ordinal))
				return Json("{\"blobId\":\"blob1\"}");

			string body = await request.Content!.ReadAsStringAsync(cancellationToken);
			using JsonDocument doc = JsonDocument.Parse(body);
			bool requestsBlob = doc.RootElement.GetProperty("using").EnumerateArray()
				.Any(u => u.GetString() == JmapCapabilities.Blob);
			if (requestsBlob)
				return new HttpResponseMessage(HttpStatusCode.BadRequest);

			List<string> responses = new();
			foreach (JsonElement call in doc.RootElement.GetProperty("methodCalls").EnumerateArray())
			{
				string name = call[0].GetString()!;
				string id = call[2].GetString()!;
				string argsJson = name switch
				{
					"Mailbox/get" => "\"list\":[{\"id\":\"SENTID\",\"role\":\"sent\"}]",
					"Email/import" => "\"created\":{\"sent\":{\"id\":\"E1\"}}",
					_ => "\"list\":[]"
				};
				responses.Add($"[\"{name}\",{{\"accountId\":\"c\",{argsJson}}},\"{id}\"]");
			}

			return Json($"{{\"methodResponses\":[{string.Join(",", responses)}],\"sessionState\":\"x\"}}");
		}
	}

	/// <summary>
	///   Models a server that ignores the requested <c>position</c> and always reports <c>0</c>
	///   while still returning a non-empty page, which would otherwise hang the pagination loop
	///   forever. Caps itself so a still-broken loop fails the test's iteration-count assertion
	///   instead of only being caught by the cancellation token.
	/// </summary>
	private sealed class StuckPositionStub : HttpMessageHandler
	{
		public int Calls;

		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request, CancellationToken cancellationToken)
		{
			if (request.RequestUri!.AbsolutePath != "/jmap/")
				return Task.FromResult(Json(SessionJson));
			Calls++;
			if (Calls > 200)
				return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
			return Task.FromResult(Json("""
			{"methodResponses":[
			  ["Email/query",{"accountId":"c","queryState":"s1","position":0,"ids":["X1","X2"]},"0"],
			  ["Email/get",{"accountId":"c","list":[{"id":"X1","keywords":{}},{"id":"X2","keywords":{}}]},"1"]
			],"sessionState":"x"}
			"""));
		}
	}
}

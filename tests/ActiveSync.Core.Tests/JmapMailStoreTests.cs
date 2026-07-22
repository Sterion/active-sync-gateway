using System.Net;
using System.Text;
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

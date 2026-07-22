using System.Net;
using System.Text;
using ActiveSync.Backends.Jmap;
using ActiveSync.Contracts;

namespace ActiveSync.Core.Tests;

/// <summary>
///   H9: the session's core capability object was parsed as a bare URN set, so its limits were
///   discarded and <c>HasCapability</c> went unused. A store therefore issued requests with
///   <c>using:[…contacts]</c> the server never advertised, getting an opaque 400. Each store must
///   check the capability up front and fail with a clear, named error before sending anything.
/// </summary>
public sealed class JmapCapabilityTests
{
	private static readonly Uri Base = new("http://localhost:5232");

	// A session that resolves a contacts primaryAccount but does NOT advertise the contacts
	// capability — exactly the shape that made the old code send an AddressBook/get anyway.
	private const string SessionWithoutContactsCapability = """
	{
	  "capabilities": { "urn:ietf:params:jmap:core": {}, "urn:ietf:params:jmap:mail": {} },
	  "primaryAccounts": {
	    "urn:ietf:params:jmap:core": "c",
	    "urn:ietf:params:jmap:mail": "c",
	    "urn:ietf:params:jmap:contacts": "c"
	  },
	  "apiUrl": "http://localhost:5232/jmap/",
	  "downloadUrl": "http://localhost:5232/jmap/download/{accountId}/{blobId}/{name}?accept={type}",
	  "uploadUrl": "http://localhost:5232/jmap/upload/{accountId}/",
	  "state": "abc"
	}
	""";

	[Fact]
	public async Task ContactStore_ServerLacksContactsCapability_ThrowsWithoutIssuingTheRequest()
	{
		CountingHandler handler = new(SessionWithoutContactsCapability);
		JmapClient client = new(Base, new HttpClient(handler));
		JmapContactStore store = new(client, pollSeconds: 1);

		BackendException ex = await Assert.ThrowsAsync<BackendException>(
			() => store.ListFoldersAsync(CancellationToken.None));

		Assert.Contains("contacts", ex.Message, StringComparison.OrdinalIgnoreCase);
		Assert.Equal(0, handler.ApiCalls); // never sent the AddressBook/get it could not honour
	}

	private sealed class CountingHandler(string sessionJson) : HttpMessageHandler
	{
		public int ApiCalls { get; private set; }

		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request, CancellationToken cancellationToken)
		{
			if (request.RequestUri!.AbsolutePath == "/jmap/")
				ApiCalls++;
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(
					request.RequestUri!.AbsolutePath == "/jmap/"
						? """{"methodResponses":[["AddressBook/get",{"accountId":"c","list":[]},"0"]],"sessionState":"x"}"""
						: sessionJson,
					Encoding.UTF8, "application/json")
			});
		}
	}
}

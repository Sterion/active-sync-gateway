using System.Net;
using System.Text;
using ActiveSync.Backends.Jmap;
using ActiveSync.Core.Backend;

namespace ActiveSync.Core.Tests;

/// <summary>
///   The JMAP client's transport logic without a server: session discovery + URL re-anchoring,
///   same-origin redirect following, method invocation and error surfacing, blob transfer, and
///   the authentication-failure signal.
/// </summary>
public class JmapClientTests
{
	// Trimmed real Stalwart session resource: the advertised URLs use the server's own
	// hostname (mail.example.com:8080), which the client must re-anchor onto the base URL.
	private const string SessionJson = """
	{
	  "capabilities": { "urn:ietf:params:jmap:core": {}, "urn:ietf:params:jmap:mail": {} },
	  "primaryAccounts": { "urn:ietf:params:jmap:core": "c", "urn:ietf:params:jmap:mail": "c" },
	  "apiUrl": "http://mail.example.com:8080/jmap/",
	  "downloadUrl": "http://mail.example.com:8080/jmap/download/{accountId}/{blobId}/{name}?accept={type}",
	  "uploadUrl": "http://mail.example.com:8080/jmap/upload/{accountId}/",
	  "eventSourceUrl": "http://mail.example.com:8080/jmap/eventsource",
	  "state": "abc"
	}
	""";

	private static readonly Uri Base = new("http://localhost:5232");

	[Fact]
	public async Task GetSession_ReanchorsAdvertisedUrlsOntoBaseUrl()
	{
		StubHandler stub = new((request, _) => Json(SessionJson));
		using JmapClient client = new(Base, new HttpClient(stub));

		JmapSessionResource session = await client.GetSessionAsync(CancellationToken.None);

		Assert.Equal("http://localhost:5232/jmap/", session.ApiUrl.ToString());
		Assert.StartsWith("http://localhost:5232/jmap/download/", session.DownloadUrlTemplate);
		Assert.Contains("{blobId}", session.DownloadUrlTemplate); // template placeholders survive
		Assert.Equal("http://localhost:5232/jmap/upload/{accountId}/", session.UploadUrlTemplate);
		Assert.Equal("c", session.PrimaryAccount(JmapCapabilities.Mail));
		Assert.True(session.HasCapability(JmapCapabilities.Mail));
		// Discovery hit the well-known resource on the base host.
		Assert.Equal("/.well-known/jmap", stub.Requests[0].RequestUri!.AbsolutePath);
	}

	[Fact]
	public async Task GetSession_FollowsSameOriginRedirect()
	{
		StubHandler stub = new((request, _) =>
		{
			if (request.RequestUri!.AbsolutePath == "/.well-known/jmap")
			{
				HttpResponseMessage redirect = new(HttpStatusCode.Redirect);
				redirect.Headers.Location = new Uri(Base, "/jmap/session");
				return redirect;
			}

			return Json(SessionJson);
		});
		using JmapClient client = new(Base, new HttpClient(stub));

		JmapSessionResource session = await client.GetSessionAsync(CancellationToken.None);

		Assert.Equal("c", session.PrimaryAccount(JmapCapabilities.Mail));
		Assert.Equal("/jmap/session", stub.Requests[^1].RequestUri!.AbsolutePath);
	}

	[Fact]
	public async Task GetSession_DoesNotFollowCrossOriginRedirect()
	{
		StubHandler stub = new((request, _) =>
		{
			if (request.RequestUri!.AbsolutePath == "/.well-known/jmap")
			{
				HttpResponseMessage redirect = new(HttpStatusCode.Redirect);
				redirect.Headers.Location = new Uri("http://evil.example.com/jmap/session");
				return redirect;
			}

			return Json(SessionJson);
		});
		using JmapClient client = new(Base, new HttpClient(stub));

		// A cross-origin redirect is not followed; the 302 surfaces as a failed session fetch.
		await Assert.ThrowsAsync<BackendException>(() => client.GetSessionAsync(CancellationToken.None));
		Assert.Single(stub.Requests); // never chased the evil host
	}

	[Fact]
	public async Task Invoke_ReturnsMethodArguments()
	{
		StubHandler stub = new((request, _) => request.RequestUri!.AbsolutePath == "/jmap/"
			? Json("""{"methodResponses":[["Mailbox/get",{"list":[{"id":"a","role":"inbox"}]},"0"]],"sessionState":"x"}""")
			: Json(SessionJson));
		using JmapClient client = new(Base, new HttpClient(stub));

		using JmapResponse response = await client.CallAsync(
			[JmapCapabilities.Core, JmapCapabilities.Mail], "Mailbox/get",
			new Dictionary<string, object?> { ["accountId"] = "c" }, CancellationToken.None);

		System.Text.Json.JsonElement list = response.Arguments("0").GetProperty("list");
		Assert.Equal("a", list[0].GetProperty("id").GetString());
	}

	[Fact]
	public async Task Invoke_SurfacesMethodError()
	{
		StubHandler stub = new((request, _) => request.RequestUri!.AbsolutePath == "/jmap/"
			? Json("""{"methodResponses":[["error",{"type":"invalidArguments"},"0"]],"sessionState":"x"}""")
			: Json(SessionJson));
		using JmapClient client = new(Base, new HttpClient(stub));

		using JmapResponse response = await client.CallAsync(
			[JmapCapabilities.Core], "Mailbox/get",
			new Dictionary<string, object?> { ["accountId"] = "c" }, CancellationToken.None);

		BackendException ex = Assert.Throws<BackendException>(() => response.Arguments("0"));
		Assert.Contains("invalidArguments", ex.Message);
	}

	[Fact]
	public async Task DownloadBlob_SubstitutesTemplateAndReturnsBytes()
	{
		byte[] payload = Encoding.UTF8.GetBytes("raw-rfc822");
		StubHandler stub = new((request, _) =>
		{
			if (request.RequestUri!.AbsolutePath.StartsWith("/jmap/download/", StringComparison.Ordinal))
				return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
			return Json(SessionJson);
		});
		using JmapClient client = new(Base, new HttpClient(stub));

		byte[] bytes = await client.DownloadBlobAsync("c", "blob123", CancellationToken.None);

		Assert.Equal(payload, bytes);
		string requested = stub.Requests[^1].RequestUri!.ToString();
		Assert.Contains("/jmap/download/c/blob123/", requested); // {accountId}/{blobId} substituted
	}

	[Fact]
	public async Task Session_AuthFailure_RaisesAuthenticationException()
	{
		StubHandler stub = new((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized));
		using JmapClient client = new(Base, new HttpClient(stub));

		await Assert.ThrowsAsync<JmapAuthenticationException>(
			() => client.GetSessionAsync(CancellationToken.None));
	}

	private static HttpResponseMessage Json(string body)
	{
		return new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent(body, Encoding.UTF8, "application/json")
		};
	}

	private sealed class StubHandler(Func<HttpRequestMessage, string, HttpResponseMessage> responder)
		: HttpMessageHandler
	{
		public List<HttpRequestMessage> Requests { get; } = new();

		protected override async Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request, CancellationToken cancellationToken)
		{
			string body = request.Content is null
				? ""
				: await request.Content.ReadAsStringAsync(cancellationToken);
			Requests.Add(request);
			return responder(request, body);
		}
	}
}

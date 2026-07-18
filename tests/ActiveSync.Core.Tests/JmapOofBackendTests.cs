using System.Net;
using System.Text;
using System.Text.Json;
using ActiveSync.Backends.Jmap;
using ActiveSync.Core.Backend;

namespace ActiveSync.Core.Tests;

/// <summary>The JMAP OOF backend maps the semantic reply onto a VacationResponse singleton patch.</summary>
public class JmapOofBackendTests
{
	private const string SessionJson = """
	{
	  "capabilities": { "urn:ietf:params:jmap:core": {}, "urn:ietf:params:jmap:vacationresponse": {} },
	  "primaryAccounts": { "urn:ietf:params:jmap:vacationresponse": "c" },
	  "apiUrl": "http://localhost:5232/jmap/",
	  "downloadUrl": "http://localhost:5232/jmap/download/{accountId}/{blobId}/{name}",
	  "uploadUrl": "http://localhost:5232/jmap/upload/{accountId}/",
	  "state": "x"
	}
	""";

	[Fact]
	public async Task Enable_SetsTheSingletonEnabled_WithTheReplyBody()
	{
		CaptureHandler capture = new();
		JmapOofBackend backend = new(new JmapClient(new Uri("http://localhost:5232"), new HttpClient(capture)));

		string? token = await backend.EnableAsync(
			new OofReply("Away until Monday", false, new DateTime(2026, 7, 18, 0, 0, 0, DateTimeKind.Utc), null),
			CancellationToken.None);

		Assert.Equal("", token);
		JsonElement patch = capture.LastSetPatch();
		Assert.True(patch.GetProperty("isEnabled").GetBoolean());
		Assert.Equal("Away until Monday", patch.GetProperty("textBody").GetString());
		Assert.Equal(JsonValueKind.Null, patch.GetProperty("htmlBody").ValueKind);
		Assert.Equal("2026-07-18T00:00:00Z", patch.GetProperty("fromDate").GetString());
	}

	[Fact]
	public async Task Enable_WithHtmlBody_SetsHtmlBodyAndNullText()
	{
		CaptureHandler capture = new();
		JmapOofBackend backend = new(new JmapClient(new Uri("http://localhost:5232"), new HttpClient(capture)));

		await backend.EnableAsync(new OofReply("<p>Away</p>", true, null, null), CancellationToken.None);

		JsonElement patch = capture.LastSetPatch();
		Assert.Equal("<p>Away</p>", patch.GetProperty("htmlBody").GetString());
		Assert.Equal(JsonValueKind.Null, patch.GetProperty("textBody").ValueKind);
	}

	[Fact]
	public async Task Disable_TurnsTheSingletonOff()
	{
		CaptureHandler capture = new();
		JmapOofBackend backend = new(new JmapClient(new Uri("http://localhost:5232"), new HttpClient(capture)));

		await backend.DisableAsync("", CancellationToken.None);

		Assert.False(capture.LastSetPatch().GetProperty("isEnabled").GetBoolean());
	}

	private sealed class CaptureHandler : HttpMessageHandler
	{
		private readonly List<string> _bodies = new();

		public JsonElement LastSetPatch()
		{
			// The last request is the VacationResponse/set; dig out update.singleton.
			using JsonDocument doc = JsonDocument.Parse(_bodies[^1]);
			return doc.RootElement.GetProperty("methodCalls")[0][1]
				.GetProperty("update").GetProperty("singleton").Clone();
		}

		protected override async Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request, CancellationToken cancellationToken)
		{
			if (request.RequestUri!.AbsolutePath == "/.well-known/jmap")
				return Json(SessionJson);
			_bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
			return Json("""{"methodResponses":[["VacationResponse/set",{"updated":{"singleton":null}},"0"]],"sessionState":"x"}""");
		}

		private static HttpResponseMessage Json(string body)
		{
			return new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(body, Encoding.UTF8, "application/json")
			};
		}
	}
}

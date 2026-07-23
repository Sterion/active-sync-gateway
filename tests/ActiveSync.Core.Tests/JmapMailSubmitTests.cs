using System.Net;
using System.Text;
using System.Text.Json;
using ActiveSync.Backends.Jmap;
using ActiveSync.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Core.Tests;

/// <summary>
///   JMAP outbound submission (RFC 8621 EmailSubmission). H1: the outgoing MIME is Email/imported
///   into Drafts as a staging copy and destroyed on submission success via
///   <c>onSuccessDestroyEmail</c>. When the submission is REJECTED (recipient/quota/greylist →
///   <c>notCreated</c>) that cleanup never fires, so the staged <c>$draft</c> lingers in Drafts and
///   syncs to the device as a phantom copy. The failure path must destroy the staged email before
///   throwing.
/// </summary>
public sealed class JmapMailSubmitTests
{
	private static readonly Uri Base = new("http://localhost:5232");

	private const string SessionJson = """
	{
	  "capabilities": {
	    "urn:ietf:params:jmap:core": {}, "urn:ietf:params:jmap:mail": {},
	    "urn:ietf:params:jmap:submission": {}
	  },
	  "primaryAccounts": {
	    "urn:ietf:params:jmap:core": "c", "urn:ietf:params:jmap:mail": "c",
	    "urn:ietf:params:jmap:submission": "c"
	  },
	  "apiUrl": "http://localhost:5232/jmap/",
	  "downloadUrl": "http://localhost:5232/jmap/download/{accountId}/{blobId}/{name}?accept={type}",
	  "uploadUrl": "http://localhost:5232/jmap/upload/{accountId}/",
	  "state": "abc"
	}
	""";

	private const string Mime =
		"From: sender@example.test\r\nTo: rcpt@example.test\r\nSubject: hi\r\n\r\nbody\r\n";

	// H1: a rejected submission (EmailSubmission/set returns the submission in notCreated) must not
	// leave the staged draft behind — the store must issue an Email/set destroy for the imported
	// email's id before throwing. Red-first: the unmodified store only throws and never destroys.
	[Fact]
	public async Task Send_SubmissionRejected_DestroysStagedDraft_AndThrows()
	{
		bool destroyedStaged = false;
		StubHandler stub = new(request =>
		{
			string path = request.RequestUri!.AbsolutePath;
			if (path == "/.well-known/jmap")
				return Json(SessionJson);
			if (path.StartsWith("/jmap/upload/", StringComparison.Ordinal))
				return Json("{\"blobId\":\"blob1\"}");

			string body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
			using JsonDocument doc = JsonDocument.Parse(body);
			List<string> responses = new();
			foreach (JsonElement call in doc.RootElement.GetProperty("methodCalls").EnumerateArray())
			{
				string name = call[0].GetString()!;
				JsonElement args = call[1];
				string id = call[2].GetString()!;
				if (name == "Email/set" && args.TryGetProperty("destroy", out JsonElement destroy) &&
				    destroy.ValueKind == JsonValueKind.Array)
					foreach (JsonElement d in destroy.EnumerateArray())
						if (d.GetString() == "STAGED1")
							destroyedStaged = true;
				string argsJson = name switch
				{
					"Identity/get" => "\"list\":[{\"id\":\"id1\",\"email\":\"sender@example.test\"}]",
					"Mailbox/get" => "\"list\":[{\"id\":\"mb1\",\"role\":\"drafts\"}]",
					"Email/import" => "\"created\":{\"m\":{\"id\":\"STAGED1\"}}",
					"EmailSubmission/set" => "\"notCreated\":{\"s\":{\"type\":\"forbiddenToSend\"}}",
					"Email/set" => "\"destroyed\":[\"STAGED1\"]",
					_ => "\"list\":[]"
				};
				responses.Add($"[\"{name}\",{{\"accountId\":\"c\",{argsJson}}},\"{id}\"]");
			}

			return Json($"{{\"methodResponses\":[{string.Join(",", responses)}],\"sessionState\":\"x\"}}");
		});
		JmapClient client = new(Base, new HttpClient(stub));
		JmapMailSubmit submit = new(client, "sender@example.test", NullLogger.Instance);

		await Assert.ThrowsAsync<BackendException>(() =>
			submit.SendAsync(Encoding.ASCII.GetBytes(Mime), CancellationToken.None));
		Assert.True(destroyedStaged, "the staged draft STAGED1 must be destroyed after a submission failure");
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

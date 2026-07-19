using System.Net;
using ActiveSync.Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ActiveSync.Integration.Tests.Scenarios;

[Collection("gateway")]
[Trait("Category", "Integration")]
public class SecurityTests(GatewayFixture gateway)
{
	[BackendEnforcesAuthFact]
	public async Task RepeatedAuthFailures_AreThrottledWith429()
	{
		// Private gateway with a tiny limit so the shared factories are unaffected.
		await using WebApplicationFactory<Program> factory = gateway.CreateIsolatedFactory(new Dictionary<string, string?>
		{
			["ActiveSync:Auth:MaxFailures"] = "2",
			["ActiveSync:Auth:FailureWindowSeconds"] = "300"
		});
		using HttpClient http = factory.CreateClient(new WebApplicationFactoryClientOptions
		{
			AllowAutoRedirect = false
		});

		for (int i = 0; i < 2; i++)
		{
			using HttpResponseMessage denied = await SendAsync(http, TestBackend.User1, "definitely-wrong");
			Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
		}

		// Limit reached: refused before authentication, even with valid credentials.
		using HttpResponseMessage blocked = await SendAsync(http, TestBackend.User1, TestBackend.Password);
		Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
		Assert.True(blocked.Headers.RetryAfter is not null, "429 must carry Retry-After");
	}

	[BackendEnforcesAuthFact]
	public async Task ValidLogin_ClearsTheFailureCounter()
	{
		await using WebApplicationFactory<Program> factory = gateway.CreateIsolatedFactory(new Dictionary<string, string?>
		{
			["ActiveSync:Auth:MaxFailures"] = "3"
		});
		using HttpClient http = factory.CreateClient(new WebApplicationFactoryClientOptions
		{
			AllowAutoRedirect = false
		});

		using (await SendAsync(http, TestBackend.User1, "definitely-wrong"))
		{
		}

		using (await SendAsync(http, TestBackend.User1, "definitely-wrong"))
		{
		}

		using HttpResponseMessage ok = await SendAsync(http, TestBackend.User1, TestBackend.Password);
		Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

		// The counter restarted, so two more failures stay under the limit of three.
		using (await SendAsync(http, TestBackend.User1, "definitely-wrong"))
		{
		}

		using HttpResponseMessage stillAllowed = await SendAsync(http, TestBackend.User1, "definitely-wrong");
		Assert.Equal(HttpStatusCode.Unauthorized, stillAllowed.StatusCode);
	}

	[BackendFact]
	public async Task MalformedDeviceId_IsRejectedWith400()
	{
		EasTestClient client = gateway.CreateEasClient(TestBackend.User1, "EVIL!DEVICE");
		using HttpResponseMessage response = await client.OptionsAsync();
		Assert.Equal(HttpStatusCode.OK, response.StatusCode); // OPTIONS has no device id

		using HttpResponseMessage post = await client.PostRawAsync("FolderSync", null);
		Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
	}

	[BackendFact]
	public async Task Responses_CarryNosniffHeader()
	{
		EasTestClient client = gateway.CreateEasClient(TestBackend.User1);
		using HttpResponseMessage response = await client.OptionsAsync();
		Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
	}

	/// <summary>OPTIONS is unauthenticated; a FolderSync POST exercises the full auth path.</summary>
	private static async Task<HttpResponseMessage> SendAsync(HttpClient http, string user, string password)
	{
		EasTestClient client = new(http, user, password, "SECTESTDEVICE1");
		return await client.PostRawAsync("FolderSync", null);
	}
}

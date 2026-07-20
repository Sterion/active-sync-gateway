using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ActiveSync.Core.Security;
using ActiveSync.Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;

namespace ActiveSync.Integration.Tests.Scenarios;

/// <summary>
///   Start-without-config: a gateway with no mail backend boots, stays live AND reports ready —
///   configuring it is what the admin UI is for, and an orchestrator that never routes traffic
///   to it can never get there. EAS and Autodiscover still refuse with 503 until mail is set.
/// </summary>
[Trait("Category", "Integration")]
[Collection("gateway")]
public sealed class UnconfiguredModeTests(GatewayFixture fixture)
{
	private const string AdminUser = "bootstrapadmin";
	private const string Password = "bootstrap-pa55!";

	[Fact]
	public async Task NoMailBackend_StaysHealthyAndReady_ButEasReturns503()
	{
		await using WebApplicationFactory<Program> factory = fixture.CreateUnconfiguredFactory();
		using HttpClient client = factory.CreateClient(
			new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

		// Liveness stays up so the pod is not restarted.
		HttpResponseMessage health = await client.GetAsync("/healthz");
		Assert.Equal(HttpStatusCode.OK, health.StatusCode);

		// Readiness reports the missing configuration but does not fail on it: the verdict is
		// exactly "every probed component passed". (The DAV roles this fixture assigns are
		// probed for real, so on a machine without the backend stack they are what fails —
		// never the unconfigured mail.)
		HttpResponseMessage ready = await client.GetAsync("/readyz");
		JsonElement readyBody = await ready.Content.ReadFromJsonAsync<JsonElement>();
		Dictionary<string, bool> components = readyBody.GetProperty("components")
			.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetBoolean());
		Assert.False(components["configured"]);
		HttpStatusCode expected = components.Where(c => c.Key != "configured").All(c => c.Value)
			? HttpStatusCode.OK
			: HttpStatusCode.ServiceUnavailable;
		Assert.Equal(expected, ready.StatusCode);

		// The EAS endpoint refuses with 503 before it even needs credentials.
		using HttpRequestMessage eas = new(HttpMethod.Post,
			"/Microsoft-Server-ActiveSync?Cmd=Sync&User=u&DeviceId=DEV1&DeviceType=Test");
		HttpResponseMessage easResponse = await client.SendAsync(eas);
		Assert.Equal(HttpStatusCode.ServiceUnavailable, easResponse.StatusCode);
	}

	[Fact]
	public async Task NoMailBackend_AdminCanStillSignIn()
	{
		// The bootstrap guarantee: with no backend to verify against, a locally hashed admin
		// password still logs in — otherwise there is no way to reach the Backends page and
		// configure the gateway in the first place.
		await using WebApplicationFactory<Program> factory = fixture.CreateUnconfiguredFactory(
			new Dictionary<string, string?>
			{
				["ActiveSync:WebUi:Admin:Enabled"] = "true",
				[$"ActiveSync:Users:{AdminUser}:Password"] = GatewayPasswordHasher.Hash(Password),
				[$"ActiveSync:Users:{AdminUser}:Admin"] = "true"
			});
		using HttpClient client = factory.CreateDefaultClient(new CookieContainerHandler());

		using HttpRequestMessage login = new(HttpMethod.Post, "/admin/api/login");
		login.Headers.Add("X-EAS-WebUi", "1");
		login.Content = JsonContent.Create(new { username = AdminUser, password = Password });
		HttpResponseMessage response = await client.SendAsync(login);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);

		// And the session works: the settings API is reachable without any mail backend.
		HttpResponseMessage settings = await client.GetAsync("/admin/api/settings");
		Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
	}
}

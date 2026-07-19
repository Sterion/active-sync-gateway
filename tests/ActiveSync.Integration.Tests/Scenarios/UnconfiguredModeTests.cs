using System.Net;
using ActiveSync.Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ActiveSync.Integration.Tests.Scenarios;

/// <summary>
///   Start-without-config: a gateway with no mail backend boots and stays live (healthz 200) but
///   refuses EAS/Autodiscover with 503 and reports not-ready, so an operator can configure it with
///   `eas config set` rather than the process failing to start.
/// </summary>
[Trait("Category", "Integration")]
[Collection("gateway")]
public sealed class UnconfiguredModeTests(GatewayFixture fixture)
{
	[Fact]
	public async Task NoMailBackend_HealthzOk_ReadyzAndEasReturn503()
	{
		await using WebApplicationFactory<Program> factory = fixture.CreateUnconfiguredFactory();
		using HttpClient client = factory.CreateClient(
			new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

		// Liveness stays up so the pod is not restarted.
		HttpResponseMessage health = await client.GetAsync("/healthz");
		Assert.Equal(HttpStatusCode.OK, health.StatusCode);

		// Readiness reports not-ready with an explicit configured=false component.
		HttpResponseMessage ready = await client.GetAsync("/readyz");
		Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
		string readyBody = await ready.Content.ReadAsStringAsync();
		Assert.True(readyBody.Contains("\"configured\":false", StringComparison.OrdinalIgnoreCase),
			$"readyz body: {readyBody}");

		// The EAS endpoint refuses with 503 before it even needs credentials.
		using HttpRequestMessage eas = new(HttpMethod.Post,
			"/Microsoft-Server-ActiveSync?Cmd=Sync&User=u&DeviceId=DEV1&DeviceType=Test");
		HttpResponseMessage easResponse = await client.SendAsync(eas);
		Assert.Equal(HttpStatusCode.ServiceUnavailable, easResponse.StatusCode);
	}
}

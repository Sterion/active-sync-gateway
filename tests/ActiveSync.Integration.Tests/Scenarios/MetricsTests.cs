using System.Net;
using ActiveSync.Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ActiveSync.Integration.Tests.Scenarios;

/// <summary>
///   Prometheus metrics and readiness: /metrics appears only when enabled and carries the
///   per-user EAS counters after traffic; /readyz reports 200 with per-component detail
///   against the live stack and 503 when the IMAP backend is unreachable (while /healthz
///   stays a liveness 200 throughout).
/// </summary>
[Collection("gateway")]
[Trait("Category", "Integration")]
public sealed class MetricsTests(GatewayFixture gateway)
{
	[BackendFact]
	public async Task Metrics_Enabled_ExposesEasCounters_AndReadyzReportsReady()
	{
		await using WebApplicationFactory<Program> factory = gateway.CreateIsolatedFactory(
			new Dictionary<string, string?> { ["ActiveSync:Metrics:Enabled"] = "true" });
		using HttpClient http = factory.CreateClient(
			new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

		// Some EAS traffic so the counters have something to show.
		EasTestClient client = new(http, TestBackend.User1, TestBackend.Password,
			$"DEV{Guid.NewGuid():N}"[..16].ToUpperInvariant());
		await client.HandshakeAsync();

		using HttpResponseMessage metrics = await http.GetAsync("/metrics");
		Assert.Equal(HttpStatusCode.OK, metrics.StatusCode);
		string body = await metrics.Content.ReadAsStringAsync();
		Assert.Contains("eas_requests", body);
		Assert.Contains(TestBackend.User1, body); // per-user labels are on by default

		using HttpResponseMessage ready = await http.GetAsync("/readyz");
		Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
		string readyBody = await ready.Content.ReadAsStringAsync();
		Assert.Contains("\"database\":true", readyBody);
		Assert.Contains("\"imap\":true", readyBody);
	}

	[BackendFact]
	public async Task Metrics_Disabled_Returns404()
	{
		using HttpClient http = gateway.CreateHttpClient();
		using HttpResponseMessage metrics = await http.GetAsync("/metrics");
		Assert.Equal(HttpStatusCode.NotFound, metrics.StatusCode);
	}

	[BackendFact]
	public async Task Readyz_DeadImap_Reports503_WhileHealthzStays200()
	{
		await using WebApplicationFactory<Program> factory = gateway.CreateIsolatedFactory(
			new Dictionary<string, string?>
			{
				// A port nothing listens on: readiness must flip without restarting the pod.
				["ActiveSync:Imap:Port"] = "59982"
			});
		using HttpClient http = factory.CreateClient(
			new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

		using HttpResponseMessage ready = await http.GetAsync("/readyz");
		Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
		Assert.Contains("\"imap\":false", await ready.Content.ReadAsStringAsync());

		using HttpResponseMessage health = await http.GetAsync("/healthz");
		Assert.Equal(HttpStatusCode.OK, health.StatusCode);
	}
}

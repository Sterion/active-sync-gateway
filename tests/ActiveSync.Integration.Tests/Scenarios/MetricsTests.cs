using System.Net;
using ActiveSync.Core.Options;
using ActiveSync.Core.Settings;
using ActiveSync.Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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
		Assert.Contains("\"mailstore\":true", readyBody);
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
				["ActiveSync:Backends:MailStore:Port"] = "59982"
			});
		using HttpClient http = factory.CreateClient(
			new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

		using HttpResponseMessage ready = await http.GetAsync("/readyz");
		Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
		Assert.Contains("\"mailstore\":false", await ready.Content.ReadAsStringAsync());

		using HttpResponseMessage health = await http.GetAsync("/healthz");
		Assert.Equal(HttpStatusCode.OK, health.StatusCode);
	}

	// ---------- E1: the dedicated metrics listener must serve ONLY /metrics ----------

	[Fact]
	public async Task DedicatedMetricsPort_AnswersOnlyMetrics_EverythingElseIs404()
	{
		// No mail backend needed: the leak is at the pipeline level, not the content.
		const int metricsPort = 19273;
		await using WebApplicationFactory<Program> factory = gateway.CreateUnconfiguredFactory(
			new Dictionary<string, string?>
			{
				["ActiveSync:Metrics:Enabled"] = "true",
				["ActiveSync:Metrics:Port"] = metricsPort.ToString(),
			});

		// TestServer never binds a real socket, so the "dedicated port" is simulated by stamping
		// Connection.LocalPort on every request before it enters the pipeline — exactly the value
		// Kestrel's real listener would report for a connection accepted on that port.
		using HttpMessageHandler handler = factory.Server.CreateHandler(
			context => context.Connection.LocalPort = metricsPort);
		using HttpClient client = new(handler) { BaseAddress = factory.Server.BaseAddress };

		using HttpResponseMessage metrics = await client.GetAsync("/metrics");
		Assert.Equal(HttpStatusCode.OK, metrics.StatusCode);

		// The leak: on unmodified code these answer normally (200/401/400) instead of 404, so the
		// metrics port also serves liveness and the EAS Basic-auth surface in plaintext.
		using HttpResponseMessage healthz = await client.GetAsync("/healthz");
		Assert.Equal(HttpStatusCode.NotFound, healthz.StatusCode);

		using HttpResponseMessage eas = await client.GetAsync("/Microsoft-Server-ActiveSync");
		Assert.Equal(HttpStatusCode.NotFound, eas.StatusCode);
	}

	// ---------- B3/E2: Metrics:PerUser is catalogued live-tier and must actually apply live ----------

	[BackendFact]
	public async Task PerUserLabels_AppliesLive_WithoutRestart()
	{
		await using WebApplicationFactory<Program> factory = gateway.CreateIsolatedFactory(
			new Dictionary<string, string?> { ["ActiveSync:Metrics:Enabled"] = "true" });
		using HttpClient http = factory.CreateClient(
			new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

		// Baseline: PerUser defaults to true, so a handshake's user label carries the real login.
		EasTestClient before = new(http, TestBackend.User1, TestBackend.Password,
			$"DEV{Guid.NewGuid():N}"[..16].ToUpperInvariant());
		await before.HandshakeAsync();
		Assert.Contains(TestBackend.User1,
			await (await http.GetAsync("/metrics")).Content.ReadAsStringAsync());

		// The Prometheus exporter caches a scrape response for 300 ms
		// (ScrapeResponseCacheDurationMilliseconds) — stay clear of it so the next scrape
		// reflects fresh state rather than the response just read above.
		await Task.Delay(TimeSpan.FromMilliseconds(400));

		// Flip PerUser off the live way — the exact write path `eas config set`/the admin Settings
		// page use, both of which report this key as live, no-restart-needed.
		GlobalSettingStore store = factory.Services.GetRequiredService<GlobalSettingStore>();
		await store.UpsertAsync("ActiveSync:Metrics:PerUser", "false", CancellationToken.None);

		// Wait for the live PICKUP itself (SettingsRefresher polls Auth:UsersRefreshSeconds, ~1s)
		// before making the one request that proves the label collapsed — retrying the SAME login
		// while racing the flip would otherwise create one permanent "real login" series on
		// whichever attempt lands first, poisoning every later check of the same cumulative counter.
		IOptionsMonitor<ActiveSyncOptions> optionsMonitor =
			factory.Services.GetRequiredService<IOptionsMonitor<ActiveSyncOptions>>();
		await WaitUntil.TrueAsync(
			() => Task.FromResult(!optionsMonitor.CurrentValue.Metrics.PerUser),
			"Metrics:PerUser=false to reach IOptionsMonitor", TimeSpan.FromSeconds(15));

		// TestBackend.User2 is used for the FIRST time here, strictly after the flip has reached
		// IOptionsMonitor: if PerUser=false actually gates metric emission (not just
		// configuration), its series collapses to "-" and the literal login never appears in
		// /metrics. B3/E2: on unmodified code GatewayMetrics.PerUserLabels was assigned once at
		// startup and never re-read, so it stays true regardless of this live change.
		EasTestClient after = new(http, TestBackend.User2, TestBackend.Password,
			$"DEV{Guid.NewGuid():N}"[..16].ToUpperInvariant());
		await after.HandshakeAsync();
		await Task.Delay(TimeSpan.FromMilliseconds(400)); // clear the 300 ms Prometheus scrape cache
		string afterBody = await (await http.GetAsync("/metrics")).Content.ReadAsStringAsync();
		Assert.DoesNotContain(TestBackend.User2, afterBody);
	}
}

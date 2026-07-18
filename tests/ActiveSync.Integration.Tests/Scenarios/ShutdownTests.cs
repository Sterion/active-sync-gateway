using System.Diagnostics;
using ActiveSync.Integration.Tests.Infrastructure;
using ActiveSync.Protocol;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ActiveSync.Integration.Tests.Scenarios;

/// <summary>
///   A host shutdown must not sit out the 30 s graceful-shutdown window waiting for active
///   long-polls: Ping observes ApplicationStopping and answers status 1 immediately.
/// </summary>
[Collection("gateway")]
[Trait("Category", "Integration")]
public class ShutdownTests(GatewayFixture gateway)
{
	[BackendFact]
	public async Task Shutdown_EndsActivePing_Immediately()
	{
		await using WebApplicationFactory<Program> factory = gateway.CreateIsolatedFactory();
		EasTestClient client = new(
			factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }),
			TestBackend.User1, TestBackend.Password,
			$"DEV{Guid.NewGuid():N}"[..16].ToUpperInvariant());
		await client.HandshakeAsync();
		string inbox = client.FolderOfType(EasFolderType.Inbox).ServerId;
		await client.InitialSyncAsync(inbox);
		await client.PullAllAsync(inbox);

		Task<(string Status, List<string> ChangedFolders)> pingTask = client.PingAsync(60, inbox);
		await Task.Delay(TimeSpan.FromSeconds(2));

		Stopwatch stopwatch = Stopwatch.StartNew();
		factory.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
		(string status, _) = await pingTask;
		stopwatch.Stop();

		Assert.Equal("1", status); // clean heartbeat-expired answer, client just re-pings
		Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
			$"shutdown should end the Ping immediately, took {stopwatch.Elapsed}");
	}
}

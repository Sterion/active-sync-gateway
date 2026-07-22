using ActiveSync.Core.Options;
using ActiveSync.Core.Settings;
using ActiveSync.Core.State;
using ActiveSync.Server.Setup;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ActiveSync.Server.Tests;

/// <summary>
///   E11: the settings refresh poll used to `break` on ANY <see cref="OperationCanceledException" />,
///   so a non-shutdown cancellation (e.g. an EF command timeout surfacing as OCE) killed live
///   settings refresh for the process lifetime with no log. A fault on one tick must not stop the
///   loop while the host is still running.
/// </summary>
public sealed class SettingsRefreshServiceTests
{
	private static ActiveSyncOptions Options(double refreshSeconds) =>
		new() { Auth = new AuthOptions { UsersRefreshSeconds = refreshSeconds } };

	[Fact]
	public async Task NonShutdownCancellation_DoesNotStopThePoll()
	{
		// The factory throws a non-shutdown OCE every time the refresher reads the stamp — exactly
		// what an EF command timeout looks like. The loop must keep ticking, not break after the
		// first one, so the stamp read is attempted more than once.
		ThrowingFactory factory = new();
		IOptionsMonitor<ActiveSyncOptions> monitor = TestOptionsMonitor.Of(Options(0.02));
		SettingsRefresher refresher = new(
			new GlobalSettingStore(factory), new DbSettingsConfigurationProvider(), monitor,
			NullLogger<SettingsRefresher>.Instance);
		SettingsRefreshService service = new(refresher, monitor, NullLogger<SettingsRefreshService>.Instance);

		await service.StartAsync(CancellationToken.None);
		try
		{
			DateTime deadline = DateTime.UtcNow.AddSeconds(5);
			while (Volatile.Read(ref factory.Calls) < 2 && DateTime.UtcNow < deadline)
				await Task.Delay(20);

			Assert.True(Volatile.Read(ref factory.Calls) >= 2,
				$"expected the poll to keep ticking after a non-shutdown fault; saw {factory.Calls} attempt(s)");
		}
		finally
		{
			await service.StopAsync(CancellationToken.None);
		}
	}

	private sealed class ThrowingFactory : ISyncDbContextFactory
	{
		public int Calls;

		public SyncDbContext CreateDbContext()
		{
			Interlocked.Increment(ref Calls);
			throw new OperationCanceledException("simulated EF command timeout");
		}
	}
}

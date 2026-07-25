using ActiveSync.Core.Options;
using ActiveSync.Core.State;
using ActiveSync.Server.Setup;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ActiveSync.Server.Tests;

/// <summary>
///   E2: the retention sweep used to `break` on ANY <see cref="OperationCanceledException" />, so a
///   non-shutdown cancellation (e.g. an EF command timeout surfacing as OCE) permanently stopped
///   retention for the process lifetime — not just the current sweep. A fault on one sweep must not
///   stop the loop while the host is still running; it should fall through to the retry path and
///   remain parked in the inter-sweep delay, not exit.
/// </summary>
public sealed class LogRetentionServiceTests
{
	[Fact]
	public async Task NonShutdownCancellation_DoesNotStopTheSweepLoop()
	{
		ThrowingFactory factory = new();
		IOptionsMonitor<ActiveSyncOptions> monitor = TestOptionsMonitor.Of(
			new ActiveSyncOptions { Log = new LogOptions { RetentionDays = 7 } });
		LogRetentionService service = new(factory, monitor, NullLogger<LogRetentionService>.Instance);

		await service.StartAsync(CancellationToken.None);
		try
		{
			DateTime deadline = DateTime.UtcNow.AddSeconds(5);
			while (Volatile.Read(ref factory.Calls) < 1 && DateTime.UtcNow < deadline)
				await Task.Delay(20);
			Assert.True(Volatile.Read(ref factory.Calls) >= 1, "expected the sweep to have attempted at least once");

			// Give ExecuteAsync a moment to react to the thrown OCE one way or the other.
			await Task.Delay(300);

			Assert.False(service.ExecuteTask!.IsCompleted,
				"a non-shutdown OperationCanceledException from the sweep must not stop the retention loop " +
				"for the process lifetime — it should fall through to the retry path and stay parked in the " +
				"inter-sweep delay");
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

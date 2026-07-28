using ActiveSync.Core.Options;
using ActiveSync.Core.State;
using ActiveSync.Server.Setup;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ActiveSync.Server.Tests;

/// <summary>
///   The send-dedup sweep. `SendDedupStore.PruneAsync` only ever runs from a REAL Sync collection's
///   own commit (`SyncStateService.CommitCollectionStateAsync`), keyed to that collection's new
///   SyncKey — it never touches rows whose CollectionId is one of the fixed namespaces ComposeMail
///   ("compose") or MeetingResponse ("meetingresponse") use, because no Sync round ever commits under
///   those collection ids. Every send-by-reference and every meeting response therefore leaves a
///   permanent completed claim with no other reclaim path. This sweep deletes completed claims past
///   an age-based retention window, independent of any collection commit.
/// </summary>
public sealed class SendDedupRetentionServiceTests : IDisposable
{
	private readonly string _dbPath;

	public SendDedupRetentionServiceTests()
	{
		_dbPath = Path.Combine(Path.GetTempPath(), $"as-senddedupretention-{Guid.NewGuid():N}.db");
		using SyncDbContext db = NewContext();
		db.Database.EnsureCreated();
	}

	public void Dispose()
	{
		SqliteConnection.ClearAllPools();
		try
		{
			File.Delete(_dbPath);
		}
		catch (IOException)
		{
		}
	}

	private SqliteSyncDbContext NewContext()
	{
		DbContextOptions<SqliteSyncDbContext> options = new DbContextOptionsBuilder<SqliteSyncDbContext>()
			.UseSqlite($"Data Source={_dbPath}")
			.Options;
		return new SqliteSyncDbContext(options);
	}

	[Fact]
	public async Task Reclaim_RemovesOnlyStaleCompletedClaims()
	{
		DateTime now = DateTime.UtcNow;
		int deviceKey;

		await using (SyncDbContext seed = NewContext())
		{
			User user = new() { Login = "u@sd1", UpdatedUtc = now };
			await seed.Users.AddAsync(user);
			await seed.SaveChangesAsync();
			Device device = new() { UserId = user.UserId, DeviceId = "DEV1", DeviceType = "Phone" };
			await seed.Devices.AddAsync(device);
			await seed.SaveChangesAsync();
			deviceKey = device.Id;

			// Stale AND completed -- exactly a send-by-reference claim from months ago, never
			// reclaimed by any collection commit because "compose" is not a real collection id.
			await seed.SentCommandTokens.AddAsync(new SentCommandToken
			{
				DeviceKey = deviceKey, CollectionId = "compose", SyncKeyAtClaim = 0, Key = "stale-completed",
				CreatedUtc = now.AddDays(-40), Completed = true
			});
			// Stale but NEVER completed -- still might be in flight (or genuinely failed); an age
			// sweep must not delete an unconfirmed claim, only ones proven done.
			await seed.SentCommandTokens.AddAsync(new SentCommandToken
			{
				DeviceKey = deviceKey, CollectionId = "compose", SyncKeyAtClaim = 0, Key = "stale-incomplete",
				CreatedUtc = now.AddDays(-40), Completed = false
			});
			// Fresh and completed -- inside the retention window, must survive.
			await seed.SentCommandTokens.AddAsync(new SentCommandToken
			{
				DeviceKey = deviceKey, CollectionId = "meetingresponse", SyncKeyAtClaim = 0, Key = "fresh-completed",
				CreatedUtc = now.AddDays(-1), Completed = true
			});
			await seed.SaveChangesAsync();
		}

		await using (SyncDbContext sweep = NewContext())
		{
			int reclaimed = await SendDedupRetentionService.ReclaimAsync(sweep, now.AddDays(-30), CancellationToken.None);
			Assert.Equal(1, reclaimed);
		}

		await using SyncDbContext verify = NewContext();
		List<SentCommandToken> remaining = await verify.SentCommandTokens.ToListAsync();
		Assert.DoesNotContain(remaining, t => t.Key == "stale-completed");
		Assert.Contains(remaining, t => t.Key == "stale-incomplete");
		Assert.Contains(remaining, t => t.Key == "fresh-completed");
	}

	/// <summary>
	///   Mirrors <see cref="FolderRetentionServiceTests.NonShutdownCancellation_DoesNotStopTheSweepLoop" />
	///   — a fault on one sweep (including a non-shutdown <see cref="OperationCanceledException" />, e.g.
	///   an EF command timeout) must not stop the loop for the process lifetime.
	/// </summary>
	[Fact]
	public async Task NonShutdownCancellation_DoesNotStopTheSweepLoop()
	{
		ThrowingFactory factory = new();
		IOptionsMonitor<ActiveSyncOptions> monitor = TestOptionsMonitor.Of(
			new ActiveSyncOptions { Eas = new EasOptions { SendDedupRetentionDays = 30 } });
		SendDedupRetentionService service = new(factory, monitor, NullLogger<SendDedupRetentionService>.Instance);

		await service.StartAsync(CancellationToken.None);
		try
		{
			DateTime deadline = DateTime.UtcNow.AddSeconds(5);
			while (Volatile.Read(ref factory.Calls) < 1 && DateTime.UtcNow < deadline)
				await Task.Delay(20);
			Assert.True(Volatile.Read(ref factory.Calls) >= 1, "expected the sweep to have attempted at least once");

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

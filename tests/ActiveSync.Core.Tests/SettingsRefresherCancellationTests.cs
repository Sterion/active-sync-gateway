using ActiveSync.Core.Options;
using ActiveSync.Core.Settings;
using ActiveSync.Core.State;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ActiveSync.Core.Tests;

/// <summary>
///   E7 — COVERAGE, NOT PROOF. <c>ProgramServer.InitializeAsync</c>'s actual symptom (a container
///   SIGTERMed during a slow first boot hangs to SIGKILL because four post-migration awaits pass
///   <c>CancellationToken.None</c> instead of <c>app.Lifetime.ApplicationStopping</c>) needs I/O slow
///   enough to race a real shutdown signal against one specific await among five that all complete in
///   microseconds against a fresh SQLite temp database — there is no deterministic trigger for that
///   race in a unit test, and <c>ProgramServer.InitializeAsync</c> is a private static method taking a
///   concrete <c>WebApplication</c>, with no seam to substitute a slow/observable fake for any of the
///   four sealed services it resolves. The wiring itself (replacing <c>CancellationToken.None</c> with
///   a single hoisted <c>stopping</c> variable threaded through all five awaits) is a small, visually
///   verifiable diff reviewed directly rather than exercised end-to-end here.
///   <para>
///     What IS deterministic, and what this proves: one of the four rewired calls,
///     <see cref="SettingsRefresher.EnsureFreshAsync" />, genuinely OBSERVES a cancelled token —
///     the premise the fix depends on. Before the fix this call ran with
///     <c>CancellationToken.None</c>, which can never be cancelled and would let it run to completion
///     regardless of shutdown; after the fix it receives the real
///     <c>ApplicationStopping</c> token, and this test shows that token, once cancelled, is honoured
///     rather than silently ignored.
///   </para>
/// </summary>
public sealed class SettingsRefresherCancellationTests
{
	[Fact]
	public async Task EnsureFreshAsync_Force_HonorsAnAlreadyCancelledToken()
	{
		using SqliteConnection connection = new("Data Source=:memory:");
		await connection.OpenAsync();
		TestContextFactory factory = new(connection);
		await using (SyncDbContext db = factory.CreateDbContext())
			await db.Database.EnsureCreatedAsync();

		GlobalSettingStore store = new(factory);
		DbSettingsConfigurationProvider provider = new();
		SettingsRefresher refresher = new(store, provider, ZeroIntervalMonitor());

		using CancellationTokenSource cts = new();
		await cts.CancelAsync();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			() => refresher.EnsureFreshAsync(true, cts.Token));
	}

	private static IOptionsMonitor<ActiveSyncOptions> ZeroIntervalMonitor() =>
		new StubMonitor(new ActiveSyncOptions { Auth = new AuthOptions { UsersRefreshSeconds = 0 } });

	private sealed class StubMonitor(ActiveSyncOptions value) : IOptionsMonitor<ActiveSyncOptions>
	{
		public ActiveSyncOptions CurrentValue => value;
		public ActiveSyncOptions Get(string? name) => value;
		public IDisposable? OnChange(Action<ActiveSyncOptions, string?> listener) => null;
	}

	private sealed class TestContextFactory(SqliteConnection connection) : ISyncDbContextFactory
	{
		public SyncDbContext CreateDbContext()
		{
			DbContextOptions<SqliteSyncDbContext> options = new DbContextOptionsBuilder<SqliteSyncDbContext>()
				.UseSqlite(connection)
				.Options;
			return new SqliteSyncDbContext(options);
		}
	}
}

using ActiveSync.Core.Options;
using ActiveSync.Core.State;
using ActiveSync.Server.Setup;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using Serilog.Context;
using Serilog.Core;

namespace ActiveSync.Server.Tests;

/// <summary>
///   The database log sink (Information+ only, live level/enable, per-row machine name) and the
///   retention sweep, over a temp-file SQLite database. NOT a single shared :memory: connection:
///   every context sharing one physical connection made EF's per-context connection init
///   (CreateFunction for the ef_* SQL functions) race the sink's background drain — SQLite answers
///   "unable to delete/modify user-function due to active statements" when another context is
///   mid-query on the same connection. A file database gives each context its own pooled
///   connection, exactly like production, with the production WAL/busy-timeout pragmas.
/// </summary>
public sealed class DatabaseLogSinkTests : IDisposable
{
	private readonly string _dbPath;
	private readonly TestContextFactory _factory;

	public DatabaseLogSinkTests()
	{
		_dbPath = Path.Combine(Path.GetTempPath(), $"as-dblogsink-{Guid.NewGuid():N}.db");
		_factory = new TestContextFactory($"Data Source={_dbPath}");
		using SyncDbContext db = _factory.CreateDbContext();
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
			// still locked on Windows — temp files get cleaned eventually
		}
	}

	private static ActiveSyncOptions Options(bool database, string level) =>
		new() { Log = new LogOptions { Database = database, DbMinimumLevel = level } };

	private async Task<List<LogEntry>> WaitForRowsAsync(int atLeast, TimeSpan timeout)
	{
		DateTime deadline = DateTime.UtcNow + timeout;
		while (DateTime.UtcNow < deadline)
		{
			await using SyncDbContext db = _factory.CreateDbContext();
			List<LogEntry> rows = await db.LogEntries.AsNoTracking().ToListAsync();
			if (rows.Count >= atLeast)
				return rows;
			await Task.Delay(50);
		}

		await using SyncDbContext final = _factory.CreateDbContext();
		return await final.LogEntries.AsNoTracking().ToListAsync();
	}

	[Fact]
	public async Task PersistsInformationAndAbove_DropsDebug_CapturesUserAndMachine()
	{
		using DatabaseLogSink sink = new();
		sink.Activate(_factory, TestOptionsMonitor.Of(Options(true, "Information")));

		using Logger logger = new LoggerConfiguration()
			.MinimumLevel.Verbose().Enrich.FromLogContext().WriteTo.Sink(sink).CreateLogger();
		logger.Debug("debug-should-be-dropped");
		using (LogContext.PushProperty("User", "alice"))
			logger.Information("hello world");
		logger.Warning("watch out");

		List<LogEntry> rows = await WaitForRowsAsync(2, TimeSpan.FromSeconds(5));
		Assert.DoesNotContain(rows, r => r.Message.Contains("debug-should-be-dropped"));
		Assert.Contains(rows, r => r.Level == "Information" && r.Message == "hello world" && r.User == "alice");
		Assert.Contains(rows, r => r.Level == "Warning");
		Assert.All(rows, r => Assert.False(string.IsNullOrEmpty(r.Machine)));
	}

	[Fact]
	public async Task DbMinimumLevelWarning_DropsInformation()
	{
		using DatabaseLogSink sink = new();
		sink.Activate(_factory, TestOptionsMonitor.Of(Options(true, "Warning")));

		using Logger logger = new LoggerConfiguration()
			.MinimumLevel.Verbose().Enrich.FromLogContext().WriteTo.Sink(sink).CreateLogger();
		logger.Information("info-below-threshold");
		logger.Error("boom");

		List<LogEntry> rows = await WaitForRowsAsync(1, TimeSpan.FromSeconds(5));
		Assert.Contains(rows, r => r.Level == "Error");
		Assert.DoesNotContain(rows, r => r.Message.Contains("info-below-threshold"));
	}

	[Fact]
	public async Task Retention_SweepsRowsOlderThanWindow()
	{
		await using (SyncDbContext db = _factory.CreateDbContext())
		{
#pragma warning disable VSTHRD103
			db.LogEntries.Add(new LogEntry
				{ TimestampUtc = DateTime.UtcNow.AddDays(-10), Level = "Information", Message = "old" });
			db.LogEntries.Add(new LogEntry
				{ TimestampUtc = DateTime.UtcNow, Level = "Information", Message = "fresh" });
#pragma warning restore VSTHRD103
			await db.SaveChangesAsync();
		}

		LogRetentionService service = new(_factory,
			TestOptionsMonitor.Of(new ActiveSyncOptions { Log = new LogOptions { RetentionDays = 7 } }),
			NullLogger<LogRetentionService>.Instance);
		using CancellationTokenSource cts = new();
		await service.StartAsync(cts.Token);

		// The first sweep runs immediately; wait for the old row to disappear.
		DateTime deadline = DateTime.UtcNow.AddSeconds(5);
		List<LogEntry> rows;
		do
		{
			await Task.Delay(50);
			await using SyncDbContext db = _factory.CreateDbContext();
			rows = await db.LogEntries.AsNoTracking().ToListAsync();
		}
		while (rows.Count > 1 && DateTime.UtcNow < deadline);

		await service.StopAsync(CancellationToken.None);
		Assert.Single(rows);
		Assert.Equal("fresh", rows[0].Message);
	}

	private sealed class TestContextFactory(string connectionString) : ISyncDbContextFactory
	{
		public SyncDbContext CreateDbContext()
		{
			DbContextOptions<SqliteSyncDbContext> options = new DbContextOptionsBuilder<SqliteSyncDbContext>()
				.UseSqlite(connectionString)
				.AddInterceptors(new SqlitePragmaInterceptor())
				.Options;
			return new SqliteSyncDbContext(options);
		}
	}
}

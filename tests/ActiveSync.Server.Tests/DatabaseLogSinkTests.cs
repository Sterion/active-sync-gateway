using System.Diagnostics;
using ActiveSync.Core.Options;
using ActiveSync.Core.State;
using ActiveSync.Server.Setup;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using Serilog.Context;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;
using Serilog.Parsing;

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

	[Fact]
	public void Emit_DatabaseDisabledLive_DropsAtEmit_WithoutRenderingTheMessage()
	{
		// The Log:Database switch was only checked in the drain, so every event was fully
		// rendered (and a LogEntry allocated) even when persistence is off. With the option
		// visible at Emit, a disabled sink must not touch the message template at all.
		using DatabaseLogSink sink = new();
		sink.Activate(_factory, TestOptionsMonitor.Of(Options(false, "Information")));

		RenderSpy spy = new();
		MessageTemplate template = new MessageTemplateParser().Parse("value={Spy}");
		LogEvent evt = new(DateTimeOffset.UtcNow, LogEventLevel.Information, null, template,
			[new LogEventProperty("Spy", new ScalarValue(spy))]);

		sink.Emit(evt);

		Assert.Equal(0, spy.Renders);
	}

	[Fact]
	public async Task Drain_PersistFailure_IsReportedToSelfLog_NotSilentlySwallowed()
	{
		// A failing batch write was swallowed with no signal, so a persistent database
		// outage silently disabled DB logging for the process lifetime. The failure must reach
		// SelfLog (the logger itself is suspect, so it cannot be reported through Serilog).
		List<string> selfLog = [];
		SelfLog.Enable(msg =>
		{
			lock (selfLog)
				selfLog.Add(msg);
		});
		try
		{
			using DatabaseLogSink sink = new();
			sink.Activate(new ThrowingContextFactory(), TestOptionsMonitor.Of(Options(true, "Information")));
			using Logger logger = new LoggerConfiguration()
				.MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
			logger.Information("this write will fail");

			DateTime deadline = DateTime.UtcNow.AddSeconds(5);
			while (DateTime.UtcNow < deadline)
			{
				lock (selfLog)
					if (selfLog.Any(m => m.Contains("DatabaseLogSink")))
						break;
				await Task.Delay(50);
			}

			lock (selfLog)
				Assert.Contains(selfLog, m => m.Contains("DatabaseLogSink"));
		}
		finally
		{
			SelfLog.Disable();
		}
	}

	/// <summary>
	///   The shutdown flush bounded only its own <c>Task.Wait(2s)</c>; no token reached
	///   <c>WaitToReadAsync</c>/<c>SaveChangesAsync</c>, so a genuinely hung write was never actually
	///   interrupted — Dispose gave up waiting, but the drain task (and its in-flight save) kept
	///   running, unobserved, against a disposing host. A shutdown-linked token threaded into the
	///   save must be what unblocks the hang; Dispose merely returning after 2s is not proof of that.
	/// </summary>
	[Fact]
	public async Task Dispose_CancelsAnInFlightHungWrite_InsteadOfLeavingItUncancelled()
	{
		HangInterceptor hang = new();
		DatabaseLogSink sink = new();
		sink.Activate(new HangingContextFactory($"Data Source={_dbPath}", hang),
			TestOptionsMonitor.Of(Options(true, "Information")));

		using Logger logger = new LoggerConfiguration()
			.MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
		logger.Information("this write will hang until cancelled");

		// VSTHRD003: awaiting the interceptor's TaskCompletionSources on purpose — they are the
		// synchronization gates this test uses to observe the drain reaching (and unwinding out of)
		// the hang, not tasks representing unrelated background work.
#pragma warning disable VSTHRD003
		// Let the drain reach the interceptor and start hanging before we dispose.
		Assert.True(await WaitWithTimeoutAsync(hang.Started.Task, TimeSpan.FromSeconds(5)),
			"expected the drain to have reached the hanging save");

		sink.Dispose();

		// Give the cancellation a moment beyond Dispose's own bound to actually unwind the hang.
		bool cancelled = await WaitWithTimeoutAsync(hang.Cancelled.Task, TimeSpan.FromSeconds(3));
#pragma warning restore VSTHRD003
		Assert.True(cancelled,
			"expected the shutdown-linked token to cancel the in-flight save instead of abandoning it " +
			"uncancelled once Dispose gave up waiting");
	}

	private static async Task<bool> WaitWithTimeoutAsync(Task task, TimeSpan timeout)
	{
#pragma warning disable VSTHRD003
		Task completed = await Task.WhenAny(task, Task.Delay(timeout));
#pragma warning restore VSTHRD003
		return completed == task;
	}

	private sealed class HangInterceptor : SaveChangesInterceptor
	{
		public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public readonly TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
			DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
		{
			Started.TrySetResult();
			try
			{
				await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
			}
			catch (OperationCanceledException)
			{
				Cancelled.TrySetResult();
				throw;
			}

			return result;
		}
	}

	private sealed class HangingContextFactory(string connectionString, SaveChangesInterceptor interceptor)
		: ISyncDbContextFactory
	{
		public SyncDbContext CreateDbContext()
		{
			DbContextOptions<SqliteSyncDbContext> options = new DbContextOptionsBuilder<SqliteSyncDbContext>()
				.UseSqlite(connectionString)
				.AddInterceptors(new SqlitePragmaInterceptor(), interceptor)
				.Options;
			return new SqliteSyncDbContext(options);
		}
	}

	/// <summary>
	///   Once `Log:Database` flips off live, the drain re-read and discarded every buffered
	///   batch in a tight `continue` loop with no pause at all -- CPU proportional to however fast
	///   the writer had filled the channel. Prove throttling exists (rather than a tight spin) by
	///   timing how long it takes to discard a large pre-buffered backlog once the drain starts
	///   against an already-disabled option: a tight loop drains thousands of buffered entries in a
	///   handful of milliseconds; a throttled drain takes measurably, boundedly longer.
	/// </summary>
	[Fact]
	public async Task DisabledLive_DiscardsBufferedBacklog_WithoutTightSpinning()
	{
		DatabaseLogSink sink = new();
		using Logger logger = new LoggerConfiguration()
			.MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();

		// Buffered while the sink is not yet Activate()d (Emit always buffers pre-Activate), so the
		// whole backlog is sitting in the channel the instant the drain starts against Database=false.
		const int entryCount = 6000;
		for (int i = 0; i < entryCount; i++)
			logger.Information("backlog entry {Index}", i);
		Assert.True(sink.BufferedCount > 0, "expected the backlog to be buffered before Activate");

		Stopwatch sw = Stopwatch.StartNew();
		sink.Activate(_factory, TestOptionsMonitor.Of(Options(false, "Information")));

		DateTime deadline = DateTime.UtcNow.AddSeconds(10);
		while (sink.BufferedCount > 0 && DateTime.UtcNow < deadline)
			await Task.Delay(20);
		sw.Stop();

		Assert.Equal(0, sink.BufferedCount);
		Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(700),
			"expected the disabled-live discard path to be throttled rather than tight-spin drain the " +
			$"whole backlog near-instantly; it took only {sw.Elapsed}");

		sink.Dispose();
	}

	private sealed class RenderSpy
	{
		public int Renders;

		public override string ToString()
		{
			Renders++;
			return "rendered";
		}
	}

	private sealed class ThrowingContextFactory : ISyncDbContextFactory
	{
		public SyncDbContext CreateDbContext() => throw new InvalidOperationException("database unavailable");
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

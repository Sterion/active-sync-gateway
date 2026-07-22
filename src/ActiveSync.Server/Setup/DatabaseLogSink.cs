using System.Globalization;
using System.Threading.Channels;
using ActiveSync.Core.Options;
using ActiveSync.Core.State;
using Microsoft.Extensions.Options;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;

namespace ActiveSync.Server.Setup;

/// <summary>
///   Serilog sink that persists Information+ events to the state database — a rolling buffer for
///   `eas logs` and a future admin UI. Events are buffered in a bounded channel and written in
///   batches by a background drain, so logging never blocks a request thread; under a flood the
///   oldest buffered events are shed rather than growing memory. Trace/Debug are dropped at Emit
///   (the EAS wire dumps never reach the database). The drain starts only after
///   <see cref="Activate" /> (post-Build, post-migrations) so it never hits a missing table, and it
///   honors the live Log:Database / Log:DbMinimumLevel knobs per batch. Multi-pod: rows carry the
///   machine name so a shared database's `eas logs` shows every replica.
/// </summary>
public sealed class DatabaseLogSink : ILogEventSink, IDisposable
{
	private const int Capacity = 10_000;
	private const int BatchSize = 500;

	private readonly Channel<LogEntry> _channel = Channel.CreateBounded<LogEntry>(
		new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

	private readonly string _machine = Environment.MachineName;
	private ISyncDbContextFactory? _contextFactory;
	private IOptionsMonitor<ActiveSyncOptions>? _options;
	private Task? _drain;
	private int _drainErrors;

	public void Emit(LogEvent logEvent)
	{
		// Hard floor: Trace/Debug (and the EAS wire dumps) are never persisted.
		if (logEvent.Level < LogEventLevel.Information)
			return;
		// E10: honor Log:Database at Emit once options are available. The drain re-checks it too
		// (it may flip between enqueue and write), but checking here avoids rendering the message
		// and allocating a LogEntry for an event that will only be discarded. Before Activate the
		// options are unknown, so buffer — the drain applies the live switch when it starts.
		if (_options is { } options && !options.CurrentValue.Log.Database)
			return;
		LogEntry entry = new()
		{
			TimestampUtc = logEvent.Timestamp.UtcDateTime,
			Level = logEvent.Level.ToString(),
			Message = Truncate(logEvent.RenderMessage(CultureInfo.InvariantCulture), 8192),
			Exception = logEvent.Exception is { } ex ? Truncate(ex.ToString(), 16384) : null,
			SourceContext = Scalar(logEvent, "SourceContext"),
			User = Scalar(logEvent, "User"),
			Machine = _machine,
		};
		_channel.Writer.TryWrite(entry); // non-blocking; DropOldest sheds under flood
	}

	/// <summary>Begin persisting — called after the host is built and migrations are applied.</summary>
	public void Activate(ISyncDbContextFactory contextFactory, IOptionsMonitor<ActiveSyncOptions> options)
	{
		_contextFactory = contextFactory;
		_options = options;
		_drain = Task.Run(DrainAsync);
	}

	public void Dispose()
	{
		_channel.Writer.TryComplete();
		try
		{
			_drain?.Wait(TimeSpan.FromSeconds(2)); // best-effort flush of what's buffered
		}
		catch
		{
			// shutdown flush is best-effort — logs are observability, not state
		}
	}

	private async Task DrainAsync()
	{
		try
		{
			List<LogEntry> batch = new(BatchSize);
			while (await _channel.Reader.WaitToReadAsync().ConfigureAwait(false))
			{
				batch.Clear();
				while (batch.Count < BatchSize && _channel.Reader.TryRead(out LogEntry? entry))
					batch.Add(entry);

				ActiveSyncOptions options = _options!.CurrentValue;
				if (!options.Log.Database)
					continue; // persistence disabled live — discard the batch
				int min = Rank(options.Log.DbMinimumLevel);
				List<LogEntry> keep = batch.Where(e => Rank(e.Level) >= min).ToList();
				if (keep.Count == 0)
					continue;
				try
				{
					await using SyncDbContext db = _contextFactory!.CreateDbContext();
					// AddRange is synchronous and local (no I/O); AddRangeAsync exists only for async
					// value generators, which this project doesn't use.
#pragma warning disable VSTHRD103
					db.LogEntries.AddRange(keep);
#pragma warning restore VSTHRD103
					await db.SaveChangesAsync().ConfigureAwait(false);
				}
				catch (Exception ex)
				{
					// E9: a transient database hiccup (or the table not yet present on a very early
					// write) must never crash the drain — drop the batch and keep going. But it must
					// not be swallowed silently either: a persistent outage would otherwise disable DB
					// logging for the process lifetime with no trace. The sink IS the logging pipeline,
					// so route to SelfLog rather than risk recursing back into ourselves. Log the first
					// occurrence and every 100th after, so a sustained failure leaves a trail without
					// flooding.
					int n = Interlocked.Increment(ref _drainErrors);
					if (n == 1 || n % 100 == 0)
						SelfLog.WriteLine(
							"DatabaseLogSink: batch write failed (occurrence {0}); dropping {1} event(s): {2}",
							n, keep.Count, ex);
				}
			}
		}
		catch (Exception ex)
		{
			// E9: WaitToReadAsync (or anything outside the per-batch guard) faulted — the drain is
			// exiting and database logging is now dead for the process lifetime. This is exactly the
			// loss-of-the-diagnostic-channel failure, so it must be announced where the logger itself
			// is suspect.
			SelfLog.WriteLine(
				"DatabaseLogSink: drain terminated unexpectedly; database logging is now disabled: {0}", ex);
		}
	}

	private static int Rank(string level) => level.ToLowerInvariant() switch
	{
		"verbose" or "trace" => 0,
		"debug" => 1,
		"information" or "info" => 2,
		"warning" or "warn" => 3,
		"error" => 4,
		"fatal" or "critical" => 5,
		_ => 2
	};

	private static string? Scalar(LogEvent logEvent, string name) =>
		logEvent.Properties.TryGetValue(name, out LogEventPropertyValue? value)
			? (value as ScalarValue)?.Value?.ToString() ?? value.ToString().Trim('"')
			: null;

	private static string Truncate(string value, int max) =>
		value.Length <= max ? value : value[..max];
}

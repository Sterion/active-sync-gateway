using ActiveSync.Core.State;
using Microsoft.EntityFrameworkCore;

namespace ActiveSync.Core.Administration;

/// <summary>
///   The single read path over the shared <see cref="LogEntry" /> table, consumed by both the web
///   logs view (<c>GET /admin/api/logs</c>) and <c>eas logs</c>. It exists so those two surfaces do
///   not each hand-roll the same EF query with drifting notions of how filters combine (the S3/C18
///   "two paths to one table" split). The C15 substring rule lives here: the free-text filter is a
///   literal <see cref="string.Contains(string)" />, which EF translates to <c>instr</c>/<c>strpos</c>,
///   so a '%' or '_' the operator types matches itself and never steers a LIKE pattern.
/// </summary>
public sealed class LogQueryService(ISyncDbContextFactory contextFactory)
{
	// Ascending severity; a floor keeps this and everything above it.
	private static readonly string[] LevelOrder = ["Information", "Warning", "Error", "Fatal"];

	/// <summary>The filters both surfaces share; <see cref="After" /> selects tail (cursor) vs history mode.</summary>
	public sealed record LogQuery(
		DateTime Since, long? After, IReadOnlyList<string>? AcceptedLevels,
		string? User, string? Machine, string? Source, string? Text, int Take);

	public sealed record LogPage(IReadOnlyList<LogEntry> Entries, long LastId);

	/// <summary>Recent-error/warning tallies for the dashboard summary — one round trip per level bucket.</summary>
	public sealed record LogCounts(int ErrorsAndFatal, int Warnings);

	/// <summary>
	///   The set of level names at or above <paramref name="level" />, or an empty array when the
	///   name is unrecognized. Accepts the CLI's aliases (info/warn/critical) as well as the exact
	///   names the web filter offers, so both callers resolve a level the same way.
	/// </summary>
	public static string[] LevelsAtOrAbove(string? level)
	{
		if (string.IsNullOrWhiteSpace(level))
			return [];
		string normalized = level.Trim().ToLowerInvariant() switch
		{
			"information" or "info" => "Information",
			"warning" or "warn" => "Warning",
			"error" => "Error",
			"fatal" or "critical" => "Fatal",
			_ => ""
		};
		int floor = Array.IndexOf(LevelOrder, normalized);
		return floor < 0 ? [] : LevelOrder[floor..];
	}

	public async Task<LogPage> QueryAsync(LogQuery query, CancellationToken ct)
	{
		await using SyncDbContext db = contextFactory.CreateDbContext();
		IQueryable<LogEntry> rows = db.LogEntries.AsNoTracking();

		if (query.AcceptedLevels is { Count: > 0 } levels)
		{
			// Materialize to an array so EF can translate Contains to a SQL IN.
			string[] accepted = [.. levels];
			rows = rows.Where(e => accepted.Contains(e.Level));
		}

		if (!string.IsNullOrWhiteSpace(query.User))
			rows = rows.Where(e => e.User == query.User);
		if (!string.IsNullOrWhiteSpace(query.Machine))
			rows = rows.Where(e => e.Machine == query.Machine);
		if (!string.IsNullOrWhiteSpace(query.Source))
			rows = rows.Where(e => e.SourceContext != null && e.SourceContext.Contains(query.Source));
		// string.Contains, NOT EF.Functions.Like: EF translates it to a literal substring search
		// (instr on Sqlite, strpos on Npgsql), so a '%' or '_' the operator types is matched as
		// itself and never steers a pattern (C15).
		if (!string.IsNullOrWhiteSpace(query.Text))
			rows = rows.Where(e => e.Message.Contains(query.Text) ||
				(e.Exception != null && e.Exception.Contains(query.Text)));

		// The time window bounds BOTH modes: an unfloored tail walks the table from the row the
		// cursor names, however old, on a poll that repeats every 2 s (C15's tail half).
		rows = rows.Where(e => e.TimestampUtc >= query.Since);

		List<LogEntry> entries;
		if (query.After is { } cursor)
			// Tail mode: a PK range scan, chronological — multi-pod correct (one shared
			// autoincrement) and immune to timestamp skew between replicas.
			entries = await rows.Where(e => e.Id > cursor)
				.OrderBy(e => e.Id).Take(query.Take).ToListAsync(ct).ConfigureAwait(false);
		else
			// History mode: anchored on the indexed id, newest first.
			entries = await rows.OrderByDescending(e => e.Id).Take(query.Take)
				.ToListAsync(ct).ConfigureAwait(false);

		long lastId = entries.Count > 0 ? entries.Max(e => e.Id) : query.After ?? 0;
		return new LogPage(entries, lastId);
	}

	/// <summary>Error/fatal and warning counts since <paramref name="since" /> (the dashboard summary).</summary>
	public async Task<LogCounts> CountsSinceAsync(DateTime since, CancellationToken ct)
	{
		await using SyncDbContext db = contextFactory.CreateDbContext();
		int errors = await db.LogEntries
			.CountAsync(e => e.TimestampUtc >= since && (e.Level == "Error" || e.Level == "Fatal"), ct)
			.ConfigureAwait(false);
		int warnings = await db.LogEntries
			.CountAsync(e => e.TimestampUtc >= since && e.Level == "Warning", ct)
			.ConfigureAwait(false);
		return new LogCounts(errors, warnings);
	}
}

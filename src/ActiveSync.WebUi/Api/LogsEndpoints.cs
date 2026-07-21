using ActiveSync.Core.State;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace ActiveSync.WebUi.Api;

/// <summary>
///   The logs view over the shared LogEntries table — the only cluster-complete source (every
///   replica writes here; Machine tags the pod). Two modes on one endpoint:
///   history (no cursor: newest-first inside a time window) and tail (?after=&lt;id&gt;:
///   strictly-increasing Id cursor, chronological — the client polls every ~2 s). Filters:
///   minimum level, user/machine/source equality, free-text substring over message +
///   exception. The <c>sinceMinutes</c> window applies to both modes.
/// </summary>
internal static class LogsEndpoints
{
	private static readonly string[] LevelOrder = ["Information", "Warning", "Error", "Fatal"];

	internal sealed record LogDto(
		long Id, DateTime TimestampUtc, string Level, string Message,
		string? Exception, string? SourceContext, string? User, string? Machine);

	internal static void Map(RouteGroupBuilder api)
	{
		api.MapGet("logs", async (
			long? after, string? level, string? user, string? text, string? machine, string? source,
			int? sinceMinutes, int? limit, SyncDbContext db, CancellationToken ct) =>
		{
			IQueryable<LogEntry> query = db.LogEntries.AsNoTracking();

			if (!string.IsNullOrWhiteSpace(level))
			{
				int floor = Array.FindIndex(LevelOrder, l => l.Equals(level, StringComparison.OrdinalIgnoreCase));
				if (floor < 0)
					return EndpointHelpers.BadRequest($"level must be one of: {string.Join(", ", LevelOrder)}");
				string[] accepted = LevelOrder[floor..];
				query = query.Where(e => accepted.Contains(e.Level));
			}

			if (!string.IsNullOrWhiteSpace(user))
				query = query.Where(e => e.User == user);
			if (!string.IsNullOrWhiteSpace(machine))
				query = query.Where(e => e.Machine == machine);
			if (!string.IsNullOrWhiteSpace(source))
				query = query.Where(e => e.SourceContext != null && e.SourceContext.Contains(source));
			// string.Contains, NOT EF.Functions.Like: EF translates it to a literal substring
			// search (instr on Sqlite, strpos on Npgsql), so a '%' or '_' the operator types is
			// matched as itself and never steers a pattern. Moving to Like would need explicit
			// escaping to get back to where this already is.
			if (!string.IsNullOrWhiteSpace(text))
				query = query.Where(e => e.Message.Contains(text) ||
					(e.Exception != null && e.Exception.Contains(text)));

			// The time window bounds BOTH branches: an unfloored tail walks the table from the
			// row the cursor names, however old that is, on a poll that repeats every 2 s.
			DateTime since = DateTime.UtcNow.AddMinutes(-Math.Clamp(sinceMinutes ?? 60, 1, 60 * 24 * 30));
			query = query.Where(e => e.TimestampUtc >= since);

			int take = Math.Clamp(limit ?? (after is null ? 100 : 200), 1, 500);
			List<LogEntry> entries;
			if (after is { } cursor)
			{
				// Tail mode: a PK range scan, chronological — multi-pod correct (one shared
				// autoincrement) and immune to timestamp skew between replicas.
				entries = await query.Where(e => e.Id > cursor)
					.OrderBy(e => e.Id).Take(take).ToListAsync(ct);
			}
			else
			{
				// History mode: anchored on the indexed timestamp, newest first.
				entries = await query.OrderByDescending(e => e.Id).Take(take).ToListAsync(ct);
			}

			long lastId = entries.Count > 0 ? entries.Max(e => e.Id) : after ?? 0;
			return Results.Ok(new
			{
				entries = entries.Select(e => new LogDto(
					e.Id, e.TimestampUtc, e.Level, e.Message, e.Exception, e.SourceContext, e.User, e.Machine)),
				lastId
			});
		});
	}
}

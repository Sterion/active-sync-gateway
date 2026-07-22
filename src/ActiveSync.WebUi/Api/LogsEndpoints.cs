using ActiveSync.Core.Administration;
using ActiveSync.Core.State;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ActiveSync.WebUi.Api;

/// <summary>
///   The logs view over the shared LogEntries table — the only cluster-complete source (every
///   replica writes here; Machine tags the pod). Two modes on one endpoint:
///   history (no cursor: newest-first inside a time window) and tail (?after=&lt;id&gt;:
///   strictly-increasing Id cursor, chronological — the client polls every ~2 s). Filters:
///   minimum level, user/machine/source equality, free-text substring over message +
///   exception. The <c>sinceMinutes</c> window applies to both modes. The query itself lives in
///   <see cref="LogQueryService" />, shared with <c>eas logs</c>.
/// </summary>
internal static class LogsEndpoints
{
	internal sealed record LogDto(
		long Id, DateTime TimestampUtc, string Level, string Message,
		string? Exception, string? SourceContext, string? User, string? Machine);

	internal static void Map(RouteGroupBuilder api)
	{
		api.MapGet("logs", async (
			long? after, string? level, string? user, string? text, string? machine, string? source,
			int? sinceMinutes, int? limit, LogQueryService logs, CancellationToken ct) =>
		{
			string[]? accepted = null;
			if (!string.IsNullOrWhiteSpace(level))
			{
				accepted = LogQueryService.LevelsAtOrAbove(level);
				if (accepted.Length == 0)
					return EndpointHelpers.BadRequest(
						"level must be one of: Information, Warning, Error, Fatal");
			}

			DateTime since = DateTime.UtcNow.AddMinutes(-Math.Clamp(sinceMinutes ?? 60, 1, 60 * 24 * 30));
			int take = Math.Clamp(limit ?? (after is null ? 100 : 200), 1, 500);
			LogQueryService.LogPage page = await logs.QueryAsync(
				new LogQueryService.LogQuery(since, after, accepted, user, machine, source, text, take), ct);

			return Results.Ok(new
			{
				entries = page.Entries.Select(e => new LogDto(
					e.Id, e.TimestampUtc, e.Level, e.Message, e.Exception, e.SourceContext, e.User, e.Machine)),
				lastId = page.LastId
			});
		});
	}
}

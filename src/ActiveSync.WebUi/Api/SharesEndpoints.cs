using ActiveSync.Core.State;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace ActiveSync.WebUi.Api;

/// <summary>
///   Shared-calendar grants — the web face of `eas share`. Same semantics: a grant applies
///   when the user's backend session is next built (idle recycle or restart); adding an
///   existing grant just re-modes it.
/// </summary>
internal static class SharesEndpoints
{
	internal sealed record ShareDto(string User, string CollectionHref, bool ReadOnly, DateTime CreatedUtc);

	internal sealed record ShareRequest(string? User, string? CollectionHref, bool ReadOnly);

	internal static void Map(RouteGroupBuilder api)
	{
		api.MapGet("shares", async (string? user, SyncDbContext db, CancellationToken ct) =>
		{
			List<ShareDto> grants = await db.SharedCalendarGrants.AsNoTracking()
				.Where(g => user == null || g.UserName == user)
				.OrderBy(g => g.UserName).ThenBy(g => g.CollectionHref)
				.Select(g => new ShareDto(g.UserName, g.CollectionHref, g.ReadOnly, g.CreatedUtc))
				.ToListAsync(ct);
			return Results.Ok(grants);
		});

		api.MapPost("shares", async (ShareRequest request, SyncDbContext db, CancellationToken ct) =>
		{
			if (string.IsNullOrWhiteSpace(request.User))
				return Results.BadRequest(new { error = "user is required" });
			string href = request.CollectionHref?.Trim() ?? "";
			if (!href.StartsWith('/'))
				return Results.BadRequest(new { error = "the collection must be an absolute path starting with '/'" });

			SharedCalendarGrant? existing = await db.SharedCalendarGrants.FirstOrDefaultAsync(
				g => g.UserName == request.User && g.CollectionHref == href, ct);
			if (existing is not null)
			{
				existing.ReadOnly = request.ReadOnly;
				await db.SaveChangesAsync(ct);
			}
			else
			{
				// DbSet.Add is synchronous and local (no I/O) — AddAsync exists only for async
				// value generators, which this project doesn't use.
#pragma warning disable VSTHRD103
				db.SharedCalendarGrants.Add(new SharedCalendarGrant
				{
					UserName = request.User,
					CollectionHref = href,
					ReadOnly = request.ReadOnly,
					CreatedUtc = DateTime.UtcNow,
				});
#pragma warning restore VSTHRD103
				await db.SaveChangesAsync(ct);
			}

			return Results.Ok(new ShareDto(request.User, href, request.ReadOnly,
				existing?.CreatedUtc ?? DateTime.UtcNow));
		});

		api.MapDelete("shares", async (string user, string collectionHref, SyncDbContext db, CancellationToken ct) =>
		{
			string href = collectionHref.Trim();
			SharedCalendarGrant? existing = await db.SharedCalendarGrants.FirstOrDefaultAsync(
				g => g.UserName == user && g.CollectionHref == href, ct);
			if (existing is null)
				return Results.NotFound();
			db.SharedCalendarGrants.Remove(existing);
			await db.SaveChangesAsync(ct);
			return Results.Ok(new { user, collectionHref = href });
		});
	}
}

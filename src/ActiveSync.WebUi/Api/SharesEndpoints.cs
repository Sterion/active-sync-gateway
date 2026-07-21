using ActiveSync.Core.Accounts;
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
		api.MapGet("shares", async (
			string? user, int? limit, int? offset, SyncDbContext db, CancellationToken ct) =>
		{
			IQueryable<SharedCalendarGrant> query = db.SharedCalendarGrants.AsNoTracking()
				.Where(g => user == null || g.UserName == user);
			// Bounded like /logs and /devices — see C10.
			int total = await query.CountAsync(ct);
			List<ShareDto> grants = await query
				.OrderBy(g => g.UserName).ThenBy(g => g.CollectionHref)
				.Skip(Math.Max(offset ?? 0, 0))
				.Take(Math.Clamp(limit ?? 200, 1, 500))
				.Select(g => new ShareDto(g.UserName, g.CollectionHref, g.ReadOnly, g.CreatedUtc))
				.ToListAsync(ct);
			return Results.Ok(new { total, entries = grants });
		});

		api.MapPost("shares", async (
			ShareRequest request, SyncDbContext db, AccountResolver resolver, CancellationToken ct) =>
		{
			if (AdminIdentifiers.LoginProblem(request.User) is { } loginError)
				return EndpointHelpers.BadRequest(loginError);
			if (AdminIdentifiers.HrefProblem(request.CollectionHref) is { } hrefError)
				return EndpointHelpers.BadRequest(hrefError);
			string user = request.User!.Trim();
			string href = request.CollectionHref!.Trim();
			// Like a device block: an undeclared login is allowed (pass-through accounts have no
			// entry) but reported, so a typo is visible instead of silently never applying.
			await resolver.EnsureFreshAsync(false, ct);
			bool knownUser = resolver.MergedUsers.ContainsKey(user);

			SharedCalendarGrant? existing = await db.SharedCalendarGrants.FirstOrDefaultAsync(
				g => g.UserName == user && g.CollectionHref == href, ct);
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
					UserName = user,
					CollectionHref = href,
					ReadOnly = request.ReadOnly,
					CreatedUtc = DateTime.UtcNow,
				});
#pragma warning restore VSTHRD103
				await db.SaveChangesAsync(ct);
			}

			return Results.Ok(new
			{
				user, collectionHref = href, request.ReadOnly,
				createdUtc = existing?.CreatedUtc ?? DateTime.UtcNow,
				knownUser
			});
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

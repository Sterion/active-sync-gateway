using ActiveSync.Core.Accounts;
using ActiveSync.Core.Administration;
using ActiveSync.Core.State;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ActiveSync.WebUi.Api;

/// <summary>
///   Shared-calendar grants — the web face of `eas share`. Same semantics: a grant applies
///   when the user's backend session is next built (idle recycle or restart); adding an
///   existing grant just re-modes it. The DB work lives in <see cref="ShareAdminService" />,
///   shared with the CLI.
/// </summary>
internal static class SharesEndpoints
{
	internal sealed record ShareDto(string User, string CollectionHref, bool ReadOnly, DateTime CreatedUtc);

	internal sealed record ShareRequest(string? User, string? CollectionHref, bool ReadOnly);

	internal static void Map(RouteGroupBuilder api)
	{
		api.MapGet("shares", async (
			string? user, int? limit, int? offset, ShareAdminService shares, CancellationToken ct) =>
		{
			// Bounded like /logs and /devices — see C10.
			ShareAdminService.SharePage page = await shares.ListAsync(
				user, Math.Max(offset ?? 0, 0), Math.Clamp(limit ?? 200, 1, 500), ct);
			return Results.Ok(new
			{
				total = page.Total,
				entries = page.Grants.Select(g => new ShareDto(g.UserName, g.CollectionHref, g.ReadOnly, g.CreatedUtc))
			});
		});

		api.MapPost("shares", async (
			ShareRequest request, ShareAdminService shares, AccountResolver resolver, CancellationToken ct) =>
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

			ShareAdminService.ShareUpsert result = await shares.AddOrUpdateAsync(user, href, request.ReadOnly, ct);
			return Results.Ok(new
			{
				user, collectionHref = href, request.ReadOnly,
				createdUtc = result.CreatedUtc,
				knownUser
			});
		});

		api.MapDelete("shares", async (
			string user, string collectionHref, ShareAdminService shares, CancellationToken ct) =>
		{
			string href = collectionHref.Trim();
			return await shares.RemoveAsync(user, href, ct)
				? Results.Ok(new { user, collectionHref = href })
				: Results.NotFound();
		});
	}
}

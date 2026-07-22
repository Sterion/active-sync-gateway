using ActiveSync.Core.Accounts;
using ActiveSync.Core.Administration;
using ActiveSync.Core.State;
using ActiveSync.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ActiveSync.WebUi.Api;

/// <summary>
///   Device management — the web face of `eas devices/block/unblock/device wipe/purge`.
///   Destructive actions (wipe, purge) demand a typed confirmation echo instead of the CLI's
///   --yes flag; blocks mirror the CLI exactly (user-level when deviceId is omitted). The DB work
///   lives in <see cref="DeviceAdminService" />, shared with the CLI.
/// </summary>
internal static class DevicesEndpoints
{
	internal sealed record DeviceDto(
		string User, string DeviceId, string DeviceType, DateTime CreatedUtc, DateTime LastSeenUtc,
		string? LastProtocolVersion, bool PendingAccountWipe, bool Blocked, bool UserBlocked, bool UserDisabled);

	internal sealed record BlockRequest(string? User, string? DeviceId);

	internal sealed record WipeRequest(string? User, string? DeviceId, bool Cancel, string? Confirm);

	internal sealed record PurgeRequest(string? User, string? DeviceId, string? Confirm);

	internal static void Map(RouteGroupBuilder api)
	{
		api.MapGet("devices", async (
			string? user, int? limit, int? offset, DeviceAdminService devices, AccountResolver resolver,
			CancellationToken ct) =>
		{
			await resolver.EnsureFreshAsync(false, ct);
			// Bounded like /logs — the table is unbounded and an admin refresh must not
			// materialize all of it (C10).
			DeviceAdminService.DevicePage page = await devices.ListAsync(
				user, Math.Max(offset ?? 0, 0), Math.Clamp(limit ?? 200, 1, 500), ct);

			return Results.Ok(new
			{
				total = page.Total,
				entries = page.Devices.Select(listing => new DeviceDto(
					listing.Device.UserName, listing.Device.DeviceId, listing.Device.DeviceType,
					listing.Device.CreatedUtc, listing.Device.LastSeenUtc,
					listing.Device.LastProtocolVersion, listing.Device.PendingAccountWipe,
					listing.Blocked, listing.UserBlocked,
					resolver.IsLoginDisabled(listing.Device.UserName))).ToList()
			});
		});

		api.MapPost("devices/block", async (
			BlockRequest request, DeviceAdminService devices, AccountResolver resolver, CancellationToken ct) =>
		{
			// A block is stored text matched against the login a device presents, so it has to
			// meet the same rules as a declared login — ':' cannot survive Basic auth and a
			// control character corrupts the session key separator, so either would persist as
			// a row that can never apply.
			if (AdminIdentifiers.LoginProblem(request.User) is { } loginError)
				return EndpointHelpers.BadRequest(loginError);
			string user = request.User!.Trim();
			// Blocking a login that is NOT declared is legitimate and deliberately allowed:
			// pass-through authentication means most users have no entry. The flag lets the UI
			// warn about a typo without the gateway refusing a valid pre-emptive block.
			await resolver.EnsureFreshAsync(false, ct);
			bool knownUser = resolver.MergedUsers.ContainsKey(user);

			await devices.BlockAsync(user, request.DeviceId, ct);
			return Results.Ok(new { user, request.DeviceId, blocked = true, knownUser });
		});

		api.MapPost("devices/unblock", async (BlockRequest request, DeviceAdminService devices, CancellationToken ct) =>
		{
			DeviceAdminService.UnblockResult result = await devices.UnblockAsync(request.User, request.DeviceId, ct);
			return result.Removed
				? Results.Ok(new { request.User, request.DeviceId, blocked = false })
				: Results.NotFound();
		});

		api.MapPost("devices/wipe", async (WipeRequest request, DeviceAdminService devices, CancellationToken ct) =>
		{
			// Arming a wipe demands the device id typed back (a click can't do this by accident).
			if (!request.Cancel && !string.Equals(request.Confirm, request.DeviceId, StringComparison.Ordinal))
				return EndpointHelpers.BadRequest("confirm must echo the exact device id");

			Device? device = await devices.SetPendingWipeAsync(request.User, request.DeviceId, !request.Cancel, ct);
			if (device is null)
				return Results.NotFound();

			// Account-only wipe is a 16.1 directive — a pre-16.1 client would loop on Provision.
			string? warning = !request.Cancel && EasVersion.Parse(device.LastProtocolVersion) < EasVersion.V161
				? $"this device last spoke EAS {device.LastProtocolVersion ?? "(unknown)"} — account-only " +
				  "wipe requires 16.1; a pre-16.1 client loops on Provision, use a block instead"
				: null;
			return Results.Ok(new { request.User, request.DeviceId, pending = device.PendingAccountWipe, warning });
		});

		api.MapPost("devices/purge", async (PurgeRequest request, DeviceAdminService devices, CancellationToken ct) =>
		{
			if (string.IsNullOrWhiteSpace(request.User))
				return EndpointHelpers.BadRequest("user is required");
			// Purge demands the target typed back: the device id, or the user for a full purge.
			string expected = request.DeviceId ?? request.User;
			if (!string.Equals(request.Confirm, expected, StringComparison.Ordinal))
				return EndpointHelpers.BadRequest($"confirm must echo '{expected}'");

			IReadOnlyList<DeviceAdminService.PurgeCount> counts = await devices.PurgeAsync(request.User, request.DeviceId, ct);
			Dictionary<string, int> deleted = counts.ToDictionary(c => c.Table, c => c.Count);
			return Results.Ok(new { request.User, request.DeviceId, deleted });
		});
	}
}

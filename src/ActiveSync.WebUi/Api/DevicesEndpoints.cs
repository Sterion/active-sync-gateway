using ActiveSync.Core.Accounts;
using ActiveSync.Core.State;
using ActiveSync.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace ActiveSync.WebUi.Api;

/// <summary>
///   Device management — the web face of `eas devices/block/unblock/device wipe/purge`.
///   Destructive actions (wipe, purge) demand a typed confirmation echo instead of the CLI's
///   --yes flag; blocks mirror the CLI exactly (user-level when deviceId is omitted).
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
			string? user, int? limit, int? offset, SyncDbContext db, AccountResolver resolver,
			CancellationToken ct) =>
		{
			await resolver.EnsureFreshAsync(false, ct);
			List<LoginBlock> blocks = await db.LoginBlocks.AsNoTracking().ToListAsync(ct);
			// Two sets instead of a linear scan per device: the listing was O(devices×blocks).
			HashSet<string> userBlocks = new(
				blocks.Where(b => b.DeviceId is null).Select(b => b.UserName), StringComparer.Ordinal);
			HashSet<(string User, string Device)> deviceBlocks = new(
				blocks.Where(b => b.DeviceId is not null).Select(b => (b.UserName, b.DeviceId!)));

			IQueryable<Device> query = db.Devices.AsNoTracking().Where(d => user == null || d.UserName == user);
			// Bounded like /logs — the table is unbounded and an admin refresh must not
			// materialize all of it.
			int total = await query.CountAsync(ct);
			List<Device> devices = await query
				.OrderBy(d => d.UserName).ThenBy(d => d.DeviceId)
				.Skip(Math.Max(offset ?? 0, 0))
				.Take(Math.Clamp(limit ?? 200, 1, 500))
				.ToListAsync(ct);

			return Results.Ok(new
			{
				total,
				entries = devices.Select(d => new DeviceDto(
					d.UserName, d.DeviceId, d.DeviceType, d.CreatedUtc, d.LastSeenUtc,
					d.LastProtocolVersion, d.PendingAccountWipe,
					userBlocks.Contains(d.UserName) || deviceBlocks.Contains((d.UserName, d.DeviceId)),
					userBlocks.Contains(d.UserName),
					resolver.IsLoginDisabled(d.UserName))).ToList()
			});
		});

		api.MapPost("devices/block", async (BlockRequest request, SyncDbContext db, CancellationToken ct) =>
		{
			if (string.IsNullOrWhiteSpace(request.User))
				return Results.BadRequest(new { error = "user is required" });
			LoginBlock? existing = await db.LoginBlocks.FirstOrDefaultAsync(
				b => b.UserName == request.User && b.DeviceId == request.DeviceId, ct);
			if (existing is null)
			{
				// DbSet.Add is synchronous and local (no I/O).
#pragma warning disable VSTHRD103
				db.LoginBlocks.Add(new LoginBlock
				{
					UserName = request.User,
					DeviceId = request.DeviceId,
					CreatedUtc = DateTime.UtcNow,
				});
#pragma warning restore VSTHRD103
				await db.SaveChangesAsync(ct);
			}

			return Results.Ok(new { request.User, request.DeviceId, blocked = true });
		});

		api.MapPost("devices/unblock", async (BlockRequest request, SyncDbContext db, CancellationToken ct) =>
		{
			LoginBlock? existing = await db.LoginBlocks.FirstOrDefaultAsync(
				b => b.UserName == request.User && b.DeviceId == request.DeviceId, ct);
			if (existing is null)
				return Results.NotFound();
			db.LoginBlocks.Remove(existing);
			await db.SaveChangesAsync(ct);
			return Results.Ok(new { request.User, request.DeviceId, blocked = false });
		});

		api.MapPost("devices/wipe", async (WipeRequest request, SyncDbContext db, CancellationToken ct) =>
		{
			Device? device = await db.Devices.FirstOrDefaultAsync(
				d => d.UserName == request.User && d.DeviceId == request.DeviceId, ct);
			if (device is null)
				return Results.NotFound();
			// Arming a wipe demands the device id typed back (a click can't do this by accident).
			if (!request.Cancel && !string.Equals(request.Confirm, request.DeviceId, StringComparison.Ordinal))
				return Results.BadRequest(new { error = "confirm must echo the exact device id" });

			device.PendingAccountWipe = !request.Cancel;
			await db.SaveChangesAsync(ct);
			// Account-only wipe is a 16.1 directive — a pre-16.1 client would loop on Provision.
			string? warning = !request.Cancel && EasVersion.Parse(device.LastProtocolVersion) < EasVersion.V161
				? $"this device last spoke EAS {device.LastProtocolVersion ?? "(unknown)"} — account-only " +
				  "wipe requires 16.1; a pre-16.1 client loops on Provision, use a block instead"
				: null;
			return Results.Ok(new { request.User, request.DeviceId, pending = device.PendingAccountWipe, warning });
		});

		api.MapPost("devices/purge", async (PurgeRequest request, SyncDbContext db, CancellationToken ct) =>
		{
			if (string.IsNullOrWhiteSpace(request.User))
				return Results.BadRequest(new { error = "user is required" });
			// Purge demands the target typed back: the device id, or the user for a full purge.
			string expected = request.DeviceId ?? request.User;
			if (!string.Equals(request.Confirm, expected, StringComparison.Ordinal))
				return Results.BadRequest(new { error = $"confirm must echo '{expected}'" });

			Dictionary<string, int> deleted = new();
			if (request.DeviceId is null)
			{
				// Whole user — children counted before the cascades delete them (CLI parity).
				deleted["DeviceFolders"] = await db.DeviceFolders.CountAsync(f => f.Device.UserName == request.User, ct);
				deleted["CollectionStates"] = await db.CollectionStates.CountAsync(c => c.Device.UserName == request.User, ct);
				deleted["DavItems"] = await db.DavItems.CountAsync(i => i.Folder.UserName == request.User, ct);
				deleted["Devices"] = await db.Devices.Where(d => d.UserName == request.User).ExecuteDeleteAsync(ct);
				deleted["UserFolders"] = await db.UserFolders.Where(f => f.UserName == request.User).ExecuteDeleteAsync(ct);
				deleted["LocalItems"] = await db.LocalItems.Where(i => i.UserName == request.User).ExecuteDeleteAsync(ct);
				deleted["LoginBlocks"] = await db.LoginBlocks.Where(b => b.UserName == request.User).ExecuteDeleteAsync(ct);
			}
			else
			{
				deleted["DeviceFolders"] = await db.DeviceFolders.CountAsync(
					f => f.Device.UserName == request.User && f.Device.DeviceId == request.DeviceId, ct);
				deleted["CollectionStates"] = await db.CollectionStates.CountAsync(
					c => c.Device.UserName == request.User && c.Device.DeviceId == request.DeviceId, ct);
				deleted["Devices"] = await db.Devices
					.Where(d => d.UserName == request.User && d.DeviceId == request.DeviceId)
					.ExecuteDeleteAsync(ct);
				deleted["LoginBlocks"] = await db.LoginBlocks
					.Where(b => b.UserName == request.User && b.DeviceId == request.DeviceId)
					.ExecuteDeleteAsync(ct);
			}

			return Results.Ok(new { request.User, request.DeviceId, deleted });
		});
	}
}

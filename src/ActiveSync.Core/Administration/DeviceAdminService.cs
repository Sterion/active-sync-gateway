using ActiveSync.Core.State;
using Microsoft.EntityFrameworkCore;

namespace ActiveSync.Core.Administration;

/// <summary>
///   The single read/write path over the device registry and its login blocks (`eas devices`,
///   `eas block`/`unblock`, `eas device wipe`, `eas purge`, and the web Devices page). Both
///   surfaces used to hand-roll the same EF against <see cref="Device" />, <see cref="LoginBlock" />
///   and the cascade tables — the S3/C18 defect. Presentation (paging clamps, confirmation echoes,
///   the disabled-account flag from <c>AccountResolver</c>) stays with the caller; the DB work and
///   its block-cross-join live here.
/// </summary>
public sealed class DeviceAdminService(ISyncDbContextFactory contextFactory)
{
	/// <summary>A device paired with its block state — user-level and effective (user OR device).</summary>
	public sealed record DeviceListing(Device Device, bool Blocked, bool UserBlocked);

	public sealed record DevicePage(int Total, IReadOnlyList<DeviceListing> Devices);

	public sealed record UnblockResult(bool Removed, int RemainingForUser);

	public sealed record PurgeCount(string Table, int Count);

	public sealed record SummaryCounts(int DeviceUsers, int Devices, int Blocks, int PendingWipes);

	/// <summary>
	///   Devices ordered by (user, id) with block state resolved via two set lookups rather than a
	///   per-device scan (the listing was O(devices×blocks)). <paramref name="take" /> null returns
	///   every match (the CLI, printing to a terminal); the web page passes a clamped page size and
	///   reads <c>Total</c>.
	/// </summary>
	public async Task<DevicePage> ListAsync(string? user, int skip, int? take, CancellationToken ct)
	{
		await using SyncDbContext db = contextFactory.CreateDbContext();
		List<LoginBlock> blocks = await db.LoginBlocks.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
		HashSet<string> userBlocks = new(
			blocks.Where(b => b.DeviceId is null).Select(b => b.UserName), StringComparer.Ordinal);
		HashSet<(string User, string Device)> deviceBlocks = new(
			blocks.Where(b => b.DeviceId is not null).Select(b => (b.UserName, b.DeviceId!)));

		IQueryable<Device> query = db.Devices.AsNoTracking().Where(d => user == null || d.UserName == user);
		int total = await query.CountAsync(ct).ConfigureAwait(false);
		query = query.OrderBy(d => d.UserName).ThenBy(d => d.DeviceId).Skip(Math.Max(skip, 0));
		if (take is { } t)
			query = query.Take(t);
		List<Device> devices = await query.ToListAsync(ct).ConfigureAwait(false);

		List<DeviceListing> listings = devices
			.Select(d => new DeviceListing(
				d,
				userBlocks.Contains(d.UserName) || deviceBlocks.Contains((d.UserName, d.DeviceId)),
				userBlocks.Contains(d.UserName)))
			.ToList();
		return new DevicePage(total, listings);
	}

	/// <summary>Block a login (device-scoped when <paramref name="deviceId" /> is set); returns false when already blocked.</summary>
	public async Task<bool> BlockAsync(string user, string? deviceId, CancellationToken ct)
	{
		await using SyncDbContext db = contextFactory.CreateDbContext();
		LoginBlock? existing = await db.LoginBlocks
			.FirstOrDefaultAsync(b => b.UserName == user && b.DeviceId == deviceId, ct).ConfigureAwait(false);
		if (existing is not null)
			return false;
		// DbSet.Add is synchronous and local (no I/O).
#pragma warning disable VSTHRD103
		db.LoginBlocks.Add(new LoginBlock
		{
			UserName = user,
			DeviceId = deviceId,
			CreatedUtc = DateTime.UtcNow,
		});
#pragma warning restore VSTHRD103
		await db.SaveChangesAsync(ct).ConfigureAwait(false);
		return true;
	}

	/// <summary>The <see cref="LoginBlock" /> for this exact (user, device) scope, or null.</summary>
	public async Task<LoginBlock?> FindBlockAsync(string? user, string? deviceId, CancellationToken ct)
	{
		await using SyncDbContext db = contextFactory.CreateDbContext();
		return await db.LoginBlocks.AsNoTracking()
			.FirstOrDefaultAsync(b => b.UserName == user && b.DeviceId == deviceId, ct).ConfigureAwait(false);
	}

	/// <summary>Remove a block; reports whether one existed and how many blocks remain for the user.</summary>
	public async Task<UnblockResult> UnblockAsync(string? user, string? deviceId, CancellationToken ct)
	{
		await using SyncDbContext db = contextFactory.CreateDbContext();
		LoginBlock? existing = await db.LoginBlocks
			.FirstOrDefaultAsync(b => b.UserName == user && b.DeviceId == deviceId, ct).ConfigureAwait(false);
		if (existing is null)
			return new UnblockResult(false, await db.LoginBlocks.CountAsync(b => b.UserName == user, ct).ConfigureAwait(false));
		db.LoginBlocks.Remove(existing);
		await db.SaveChangesAsync(ct).ConfigureAwait(false);
		int remaining = await db.LoginBlocks.CountAsync(b => b.UserName == user, ct).ConfigureAwait(false);
		return new UnblockResult(true, remaining);
	}

	/// <summary>
	///   Arm or cancel an account-only wipe on a device. Returns the (updated) device so the caller
	///   can warn on a pre-16.1 <see cref="Device.LastProtocolVersion" />, or null when it is unknown.
	/// </summary>
	public async Task<Device?> SetPendingWipeAsync(string? user, string? deviceId, bool pending, CancellationToken ct)
	{
		await using SyncDbContext db = contextFactory.CreateDbContext();
		Device? device = await db.Devices
			.FirstOrDefaultAsync(d => d.UserName == user && d.DeviceId == deviceId, ct).ConfigureAwait(false);
		if (device is null)
			return null;
		device.PendingAccountWipe = pending;
		await db.SaveChangesAsync(ct).ConfigureAwait(false);
		return device;
	}

	/// <summary>
	///   Delete all gateway state for a user, or a single device's state when
	///   <paramref name="deviceId" /> is set. Children are counted before ON DELETE CASCADE removes
	///   them. Returns one row per affected table (Devices first) so the caller can report it.
	/// </summary>
	public async Task<IReadOnlyList<PurgeCount>> PurgeAsync(string user, string? deviceId, CancellationToken ct)
	{
		await using SyncDbContext db = contextFactory.CreateDbContext();
		if (deviceId is null)
		{
			int deviceFolders = await db.DeviceFolders.CountAsync(f => f.Device.UserName == user, ct).ConfigureAwait(false);
			int collections = await db.CollectionStates.CountAsync(c => c.Device.UserName == user, ct).ConfigureAwait(false);
			int davItems = await db.DavItems.CountAsync(i => i.Folder.UserName == user, ct).ConfigureAwait(false);
			int devices = await db.Devices.Where(d => d.UserName == user).ExecuteDeleteAsync(ct).ConfigureAwait(false);
			int folders = await db.UserFolders.Where(f => f.UserName == user).ExecuteDeleteAsync(ct).ConfigureAwait(false);
			int items = await db.LocalItems.Where(i => i.UserName == user).ExecuteDeleteAsync(ct).ConfigureAwait(false);
			int blocks = await db.LoginBlocks.Where(b => b.UserName == user).ExecuteDeleteAsync(ct).ConfigureAwait(false);
			return
			[
				new PurgeCount("Devices", devices), new PurgeCount("DeviceFolders", deviceFolders),
				new PurgeCount("CollectionStates", collections), new PurgeCount("UserFolders", folders),
				new PurgeCount("DavItems", davItems), new PurgeCount("LocalItems", items),
				new PurgeCount("LoginBlocks", blocks),
			];
		}

		int devDeviceFolders = await db.DeviceFolders
			.CountAsync(f => f.Device.UserName == user && f.Device.DeviceId == deviceId, ct).ConfigureAwait(false);
		int devCollections = await db.CollectionStates
			.CountAsync(c => c.Device.UserName == user && c.Device.DeviceId == deviceId, ct).ConfigureAwait(false);
		int devDevices = await db.Devices
			.Where(d => d.UserName == user && d.DeviceId == deviceId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
		int devBlocks = await db.LoginBlocks
			.Where(b => b.UserName == user && b.DeviceId == deviceId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
		return
		[
			new PurgeCount("Devices", devDevices), new PurgeCount("DeviceFolders", devDeviceFolders),
			new PurgeCount("CollectionStates", devCollections), new PurgeCount("LoginBlocks", devBlocks),
		];
	}

	/// <summary>Cheap DB-derived dashboard tallies (distinct device users, devices, blocks, pending wipes).</summary>
	public async Task<SummaryCounts> SummaryAsync(CancellationToken ct)
	{
		await using SyncDbContext db = contextFactory.CreateDbContext();
		return new SummaryCounts(
			await db.Devices.Select(d => d.UserName).Distinct().CountAsync(ct).ConfigureAwait(false),
			await db.Devices.CountAsync(ct).ConfigureAwait(false),
			await db.LoginBlocks.CountAsync(ct).ConfigureAwait(false),
			await db.Devices.CountAsync(d => d.PendingAccountWipe, ct).ConfigureAwait(false));
	}
}

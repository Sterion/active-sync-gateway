using ActiveSync.Core.Accounts;
using ActiveSync.Core.State;
using Microsoft.EntityFrameworkCore;

namespace ActiveSync.Core.Administration;

/// <summary>
///   The single read/write path over the device registry and its login blocks (`eas devices`,
///   `eas block`/`unblock`, `eas device wipe`, `eas purge`, and the web Devices page). Both
///   surfaces used to hand-roll the same EF against <see cref="Device" /> and
///   <see cref="LoginBlock" /> — the S3/C18 defect. The public surface speaks LOGINS (what the
///   operator types); rows are keyed by the immutable <c>UserId</c>, so every method translates
///   through the <see cref="User" /> table (case-folded seek). Presentation (paging clamps,
///   confirmation echoes, the disabled-account flag from <c>UserResolver</c>) stays with the
///   caller; the DB work and its block-cross-join live here.
/// </summary>
public sealed class DeviceAdminService(ISyncDbContextFactory contextFactory, UserStore users)
{
	/// <summary>A device paired with its owner's login and block state — user-level and effective.</summary>
	public sealed record DeviceListing(Device Device, string Login, bool Blocked, bool UserBlocked);

	public sealed record DevicePage(int Total, IReadOnlyList<DeviceListing> Devices);

	public sealed record UnblockResult(bool Removed, int RemainingForUser);

	public sealed record PurgeCount(string Table, int Count);

	public sealed record SummaryCounts(int DeviceUsers, int Devices, int Blocks, int PendingWipes);

	/// <summary>
	///   Devices ordered by (login, id) with block state resolved via two set lookups rather than a
	///   per-device scan (the listing was O(devices×blocks)). <paramref name="take" /> null returns
	///   every match (the CLI, printing to a terminal); the web page passes a clamped page size and
	///   reads <c>Total</c>.
	/// </summary>
	public async Task<DevicePage> ListAsync(string? user, int skip, int? take, CancellationToken ct)
	{
		await using SyncDbContext db = contextFactory.CreateDbContext();
		List<LoginBlock> blocks = await db.LoginBlocks.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
		HashSet<int> userBlocks = new(
			blocks.Where(b => b.DeviceId is null).Select(b => b.UserId));
		HashSet<(int UserId, string Device)> deviceBlocks = new(
			blocks.Where(b => b.DeviceId is not null).Select(b => (b.UserId, b.DeviceId!)));

		string? normalized = user is null ? null : UserStore.NormalizeLogin(user);
		IQueryable<Device> query = db.Devices.AsNoTracking().Include(d => d.User)
			.Where(d => normalized == null || d.User.Login == normalized);
		int total = await query.CountAsync(ct).ConfigureAwait(false);
		query = query.OrderBy(d => d.User.Login).ThenBy(d => d.DeviceId).Skip(Math.Max(skip, 0));
		if (take is { } t)
			query = query.Take(t);
		List<Device> devices = await query.ToListAsync(ct).ConfigureAwait(false);

		List<DeviceListing> listings = devices
			.Select(d => new DeviceListing(
				d,
				d.User.Login,
				userBlocks.Contains(d.UserId) || deviceBlocks.Contains((d.UserId, d.DeviceId)),
				userBlocks.Contains(d.UserId)))
			.ToList();
		return new DevicePage(total, listings);
	}

	/// <summary>Block a login (device-scoped when <paramref name="deviceId" /> is set); returns false when already blocked.</summary>
	public async Task<bool> BlockAsync(string user, string? deviceId, CancellationToken ct)
	{
		// A block may target a user that never synced — mint the identity row so the FK holds
		// (blocking pre-emptively is a supported operator move).
		(int userId, _) = await users.GetOrCreateUserAsync(user, null, ct).ConfigureAwait(false);
		await using SyncDbContext db = contextFactory.CreateDbContext();
		LoginBlock? existing = await db.LoginBlocks
			.FirstOrDefaultAsync(b => b.UserId == userId && b.DeviceId == deviceId, ct).ConfigureAwait(false);
		if (existing is not null)
			return false;
		// DbSet.Add is synchronous and local (no I/O).
#pragma warning disable VSTHRD103
		db.LoginBlocks.Add(new LoginBlock
		{
			UserId = userId,
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
		int? userId = user is null ? null : await users.FindUserIdAsync(user, ct).ConfigureAwait(false);
		if (userId is null)
			return null;
		await using SyncDbContext db = contextFactory.CreateDbContext();
		return await db.LoginBlocks.AsNoTracking()
			.FirstOrDefaultAsync(b => b.UserId == userId && b.DeviceId == deviceId, ct).ConfigureAwait(false);
	}

	/// <summary>Remove a block; reports whether one existed and how many blocks remain for the user.</summary>
	public async Task<UnblockResult> UnblockAsync(string? user, string? deviceId, CancellationToken ct)
	{
		int? userId = user is null ? null : await users.FindUserIdAsync(user, ct).ConfigureAwait(false);
		if (userId is null)
			return new UnblockResult(false, 0);
		await using SyncDbContext db = contextFactory.CreateDbContext();
		LoginBlock? existing = await db.LoginBlocks
			.FirstOrDefaultAsync(b => b.UserId == userId && b.DeviceId == deviceId, ct).ConfigureAwait(false);
		if (existing is null)
			return new UnblockResult(false, await db.LoginBlocks.CountAsync(b => b.UserId == userId, ct).ConfigureAwait(false));
		db.LoginBlocks.Remove(existing);
		await db.SaveChangesAsync(ct).ConfigureAwait(false);
		int remaining = await db.LoginBlocks.CountAsync(b => b.UserId == userId, ct).ConfigureAwait(false);
		return new UnblockResult(true, remaining);
	}

	/// <summary>
	///   Arm or cancel an account-only wipe on a device. Returns the (updated) device so the caller
	///   can warn on a pre-16.1 <see cref="Device.LastProtocolVersion" />, or null when it is unknown.
	/// </summary>
	public async Task<Device?> SetPendingWipeAsync(string? user, string? deviceId, bool pending, CancellationToken ct)
	{
		int? userId = user is null ? null : await users.FindUserIdAsync(user, ct).ConfigureAwait(false);
		if (userId is null)
			return null;
		await using SyncDbContext db = contextFactory.CreateDbContext();
		Device? device = await db.Devices
			.FirstOrDefaultAsync(d => d.UserId == userId && d.DeviceId == deviceId, ct).ConfigureAwait(false);
		if (device is null)
			return null;
		device.PendingAccountWipe = pending;
		await db.SaveChangesAsync(ct).ConfigureAwait(false);
		return device;
	}

	/// <summary>
	///   Delete all gateway SYNC state for a user, or a single device's state when
	///   <paramref name="deviceId" /> is set. Children are counted before ON DELETE CASCADE removes
	///   them. The user's identity row itself survives — purging state is not deleting the user.
	///   Returns one row per affected table (Devices first) so the caller can report it.
	/// </summary>
	public async Task<IReadOnlyList<PurgeCount>> PurgeAsync(string user, string? deviceId, CancellationToken ct)
	{
		int? found = await users.FindUserIdAsync(user, ct).ConfigureAwait(false);
		await using SyncDbContext db = contextFactory.CreateDbContext();
		if (found is not { } userId)
		{
			return deviceId is null
				?
				[
					new PurgeCount("Devices", 0), new PurgeCount("DeviceFolders", 0),
					new PurgeCount("CollectionStates", 0), new PurgeCount("UserFolders", 0),
					new PurgeCount("DavItems", 0), new PurgeCount("LocalItems", 0),
					new PurgeCount("LoginBlocks", 0),
				]
				:
				[
					new PurgeCount("Devices", 0), new PurgeCount("DeviceFolders", 0),
					new PurgeCount("CollectionStates", 0), new PurgeCount("LoginBlocks", 0),
				];
		}

		if (deviceId is null)
		{
			int deviceFolders = await db.DeviceFolders.CountAsync(f => f.Device.UserId == userId, ct).ConfigureAwait(false);
			int collections = await db.CollectionStates.CountAsync(c => c.Device.UserId == userId, ct).ConfigureAwait(false);
			int davItems = await db.DavItems.CountAsync(i => i.Folder.UserId == userId, ct).ConfigureAwait(false);
			int devices = await db.Devices.Where(d => d.UserId == userId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
			int folders = await db.UserFolders.Where(f => f.UserId == userId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
			int items = await db.LocalItems.Where(i => i.UserId == userId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
			int blocks = await db.LoginBlocks.Where(b => b.UserId == userId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
			return
			[
				new PurgeCount("Devices", devices), new PurgeCount("DeviceFolders", deviceFolders),
				new PurgeCount("CollectionStates", collections), new PurgeCount("UserFolders", folders),
				new PurgeCount("DavItems", davItems), new PurgeCount("LocalItems", items),
				new PurgeCount("LoginBlocks", blocks),
			];
		}

		int devDeviceFolders = await db.DeviceFolders
			.CountAsync(f => f.Device.UserId == userId && f.Device.DeviceId == deviceId, ct).ConfigureAwait(false);
		int devCollections = await db.CollectionStates
			.CountAsync(c => c.Device.UserId == userId && c.Device.DeviceId == deviceId, ct).ConfigureAwait(false);
		int devDevices = await db.Devices
			.Where(d => d.UserId == userId && d.DeviceId == deviceId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
		int devBlocks = await db.LoginBlocks
			.Where(b => b.UserId == userId && b.DeviceId == deviceId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
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
			await db.Devices.Select(d => d.UserId).Distinct().CountAsync(ct).ConfigureAwait(false),
			await db.Devices.CountAsync(ct).ConfigureAwait(false),
			await db.LoginBlocks.CountAsync(ct).ConfigureAwait(false),
			await db.Devices.CountAsync(d => d.PendingAccountWipe, ct).ConfigureAwait(false));
	}
}

using ActiveSync.Core.Accounts;
using ActiveSync.Core.State;
using Microsoft.EntityFrameworkCore;

namespace ActiveSync.Core.Administration;

/// <summary>
///   The single read/write path over the device registry and its login blocks (`eas devices`,
///   `eas block`/`unblock`, `eas device wipe`, `eas purge`, and the web Devices page). Both
///   surfaces used to hand-roll the same EF against <see cref="Device" /> and
///   <see cref="LoginBlock" /> separately and drifted. The public surface speaks LOGINS (what the
///   operator types); rows are keyed by the immutable <c>UserId</c>, so every method translates
///   through the <see cref="User" /> table (case-folded seek). Presentation (paging clamps,
///   confirmation echoes, the disabled-account flag from <c>UserResolver</c>) stays with the
///   caller; the DB work and its block-cross-join live here.
/// </summary>
public sealed class DeviceAdminService(ISyncDbContextFactory contextFactory, UserStore users)
{
	/// <summary>
	///   A device paired with its owner's login, whether the DEVICE is blocked, and whether the
	///   USER is disabled. The two are distinct mechanisms, each with exactly one owner: a
	///   block cuts off this device, <c>User.Enabled = false</c> turns the user off everywhere.
	/// </summary>
	public sealed record DeviceListing(Device Device, string Login, bool Blocked, bool UserDisabled);

	public sealed record DevicePage(int Total, IReadOnlyList<DeviceListing> Devices);

	public sealed record UnblockResult(bool Removed, int RemainingForUser);

	/// <summary>Why a block/unblock could not be applied — the caller phrases the message.</summary>
	public enum BlockOutcome
	{
		/// <summary>The block was created (or removed).</summary>
		Applied,

		/// <summary>Nothing to do: already blocked, or no such block to remove.</summary>
		Unchanged,

		/// <summary>
		///   No device id was supplied. Blocks are per-device only — disabling the whole user is
		///   <c>eas user disable</c> / the Users page, a different mechanism on purpose.
		/// </summary>
		DeviceRequired,

		/// <summary>That (user, device) partnership does not exist, so there is nothing to block.</summary>
		UnknownDevice,
	}

	public sealed record PurgeCount(string Table, int Count);

	/// <summary>
	///   What deleting a user (or one of its devices) WOULD destroy, counted before anything is
	///   issued. <see cref="Content" /> is the part that matters: <c>LocalItem</c> rows are real
	///   PIM data — contacts, calendar, tasks and always notes — which in a local-stores
	///   deployment exist NOWHERE ELSE. Sync state rebuilds on the next sync and is not worth
	///   prompting over, so the two are counted apart and the caller graduates the friction.
	/// </summary>
	public sealed record DeletionImpact(
		IReadOnlyList<PurgeCount> Content, IReadOnlyList<PurgeCount> SyncState)
	{
		/// <summary>True when something irreplaceable would go — the trigger for a typed echo.</summary>
		public bool DestroysContent => Content.Any(c => c.Count > 0);

		/// <summary>"342 contacts, 89 events, 12 notes" — the phrase both surfaces put in front of the operator.</summary>
		public string DescribeContent() =>
			string.Join(", ", Content.Where(c => c.Count > 0).Select(c => $"{c.Count} {c.Table}"));
	}

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
		HashSet<int> blockedDevices = (await db.LoginBlocks.AsNoTracking()
			.Select(b => b.DeviceKey).ToListAsync(ct).ConfigureAwait(false)).ToHashSet();

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
				d, d.User.Login, blockedDevices.Contains(d.Id), d.User.Enabled == false))
			.ToList();
		return new DevicePage(total, listings);
	}

	/// <summary>
	///   Blocks ONE DEVICE. Device-scoped is the only shape there is: a bare user
	///   is refused with <see cref="BlockOutcome.DeviceRequired" /> rather than silently doing
	///   something subtly different from <c>eas user disable</c>. Enforced HERE, in the seam both
	///   the CLI and the admin API share, so neither surface can write a shape the schema no
	///   longer has.
	/// </summary>
	public async Task<BlockOutcome> BlockAsync(string user, string? deviceId, CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(deviceId))
			return BlockOutcome.DeviceRequired;
		int? userId = await users.FindUserIdAsync(user, ct).ConfigureAwait(false);
		if (userId is null)
			return BlockOutcome.UnknownDevice;

		await using SyncDbContext db = contextFactory.CreateDbContext();
		Device? device = await db.Devices
			.FirstOrDefaultAsync(d => d.UserId == userId && d.DeviceId == deviceId, ct).ConfigureAwait(false);
		if (device is null)
			return BlockOutcome.UnknownDevice;
		if (await db.LoginBlocks.AnyAsync(b => b.DeviceKey == device.Id, ct).ConfigureAwait(false))
			return BlockOutcome.Unchanged;

		// DbSet.Add is synchronous and local (no I/O).
#pragma warning disable VSTHRD103
		db.LoginBlocks.Add(new LoginBlock { DeviceKey = device.Id, CreatedUtc = DateTime.UtcNow });
#pragma warning restore VSTHRD103
		await db.SaveChangesAsync(ct).ConfigureAwait(false);
		return BlockOutcome.Applied;
	}

	/// <summary>The <see cref="LoginBlock" /> on this (user, device) partnership, or null.</summary>
	public async Task<LoginBlock?> FindBlockAsync(string? user, string? deviceId, CancellationToken ct)
	{
		if (user is null || string.IsNullOrWhiteSpace(deviceId))
			return null;
		int? userId = await users.FindUserIdAsync(user, ct).ConfigureAwait(false);
		if (userId is null)
			return null;
		await using SyncDbContext db = contextFactory.CreateDbContext();
		return await db.LoginBlocks.AsNoTracking()
			.FirstOrDefaultAsync(b => b.Device.UserId == userId && b.Device.DeviceId == deviceId, ct)
			.ConfigureAwait(false);
	}

	/// <summary>Removes a device block; reports whether one existed and how many remain for the user.</summary>
	public async Task<(BlockOutcome Outcome, int RemainingForUser)> UnblockAsync(
		string? user, string? deviceId, CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(deviceId))
			return (BlockOutcome.DeviceRequired, 0);
		int? userId = user is null ? null : await users.FindUserIdAsync(user, ct).ConfigureAwait(false);
		if (userId is null)
			return (BlockOutcome.UnknownDevice, 0);

		await using SyncDbContext db = contextFactory.CreateDbContext();
		LoginBlock? existing = await db.LoginBlocks
			.FirstOrDefaultAsync(b => b.Device.UserId == userId && b.Device.DeviceId == deviceId, ct)
			.ConfigureAwait(false);
		if (existing is not null)
		{
			db.LoginBlocks.Remove(existing);
			await db.SaveChangesAsync(ct).ConfigureAwait(false);
		}

		int remaining = await db.LoginBlocks
			.CountAsync(b => b.Device.UserId == userId, ct).ConfigureAwait(false);
		return (existing is null ? BlockOutcome.Unchanged : BlockOutcome.Applied, remaining);
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
	///   Delete gateway state for a user, or a single device's state when <paramref name="deviceId" />
	///   is set. Children are counted before ON DELETE CASCADE removes them. The user's identity row
	///   itself survives — purging is not deleting the user. Returns one row per affected table
	///   (Devices first) so the caller can report it.
	///   <para>
	///     The DEVICE-scoped form (<paramref name="deviceId" /> set) really is reclaimable sync
	///     state only. The USER-scoped form (<paramref name="deviceId" /> null) is NOT — it also
	///     deletes every <see cref="LocalItem" /> the user owns (contacts/calendar/tasks/notes
	///     content, which in a local-stores deployment exists nowhere else) via
	///     <c>db.LocalItems...ExecuteDeleteAsync</c> below. Both callers already gate the user-scoped
	///     form on <see cref="CountDeletionImpactAsync" /> plus operator confirmation
	///     (<c>PurgeCommands.cs</c>, <c>DevicesEndpoints.cs</c>) — do not add a third caller of the
	///     user-scoped form without the same guard.
	///   </para>
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
			// Blocks hang off the device, so they are counted BEFORE the device delete cascades them.
			int blocks = await db.LoginBlocks.CountAsync(b => b.Device.UserId == userId, ct).ConfigureAwait(false);
			int devices = await db.Devices.Where(d => d.UserId == userId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
			int folders = await db.UserFolders.Where(f => f.UserId == userId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
			int items = await db.LocalItems.Where(i => i.UserId == userId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
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
		int devBlocks = await db.LoginBlocks
			.CountAsync(b => b.Device.UserId == userId && b.Device.DeviceId == deviceId, ct).ConfigureAwait(false);
		int devDevices = await db.Devices
			.Where(d => d.UserId == userId && d.DeviceId == deviceId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
		return
		[
			new PurgeCount("Devices", devDevices), new PurgeCount("DeviceFolders", devDeviceFolders),
			new PurgeCount("CollectionStates", devCollections), new PurgeCount("LoginBlocks", devBlocks),
		];
	}

	/// <summary>
	///   Counts what a delete WOULD remove, changing nothing. <see cref="PurgeAsync" /> reports
	///   the same tables but only AFTER deleting, which is no use to a guard that has to decide
	///   first — so this is the count-only sibling, and the ONE implementation both the CLI's
	///   question text and the web dialog's warning are built from.
	/// </summary>
	public async Task<DeletionImpact> CountDeletionImpactAsync(
		string user, string? deviceId, CancellationToken ct)
	{
		int? found = await users.FindUserIdAsync(user, ct).ConfigureAwait(false);
		if (found is not { } userId)
			return new DeletionImpact([], []);

		await using SyncDbContext db = contextFactory.CreateDbContext();
		if (deviceId is not null)
		{
			// A device delete touches sync state only — local items belong to the USER.
			return new DeletionImpact([],
			[
				new PurgeCount("devices", await db.Devices
					.CountAsync(d => d.UserId == userId && d.DeviceId == deviceId, ct).ConfigureAwait(false)),
				new PurgeCount("acknowledged folders", await db.DeviceFolders
					.CountAsync(f => f.Device.UserId == userId && f.Device.DeviceId == deviceId, ct).ConfigureAwait(false)),
				new PurgeCount("collection states", await db.CollectionStates
					.CountAsync(c => c.Device.UserId == userId && c.Device.DeviceId == deviceId, ct).ConfigureAwait(false)),
			]);
		}

		// Content is counted per collection, because "342 contacts" reads and warns very
		// differently from "443 local items". Projected to an anonymous type in SQL and shaped
		// afterwards — EF cannot translate a constructor call inside a GroupBy projection.
		var perCollection = await db.LocalItems
			.Where(i => i.UserId == userId)
			.GroupBy(i => i.Collection)
			.Select(g => new { Collection = g.Key, Count = g.Count() })
			.ToListAsync(ct).ConfigureAwait(false);
		List<PurgeCount> content = perCollection
			.OrderBy(c => c.Collection, StringComparer.Ordinal)
			.Select(c => new PurgeCount(c.Collection, c.Count))
			.ToList();

		return new DeletionImpact(content,
		[
			new PurgeCount("devices", await db.Devices
				.CountAsync(d => d.UserId == userId, ct).ConfigureAwait(false)),
			new PurgeCount("folders", await db.UserFolders
				.CountAsync(f => f.UserId == userId, ct).ConfigureAwait(false)),
			new PurgeCount("collection states", await db.CollectionStates
				.CountAsync(c => c.Device.UserId == userId, ct).ConfigureAwait(false)),
			new PurgeCount("shared-calendar grants", await db.SharedCalendarGrants
				.CountAsync(g => g.UserId == userId, ct).ConfigureAwait(false)),
			new PurgeCount("device blocks", await db.LoginBlocks
				.CountAsync(b => b.Device.UserId == userId, ct).ConfigureAwait(false)),
			// OofSetting and WebSessionRevocation both carry a UserId FK with cascade delete,
			// the same shape as every other row counted above — they were silently absent, which
			// understated what the operator's confirmation prompt claims to be the full impact.
			new PurgeCount("oof settings", await db.OofSettings
				.CountAsync(o => o.UserId == userId, ct).ConfigureAwait(false)),
			new PurgeCount("web session revocations", await db.WebSessionRevocations
				.CountAsync(w => w.UserId == userId, ct).ConfigureAwait(false)),
		]);
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

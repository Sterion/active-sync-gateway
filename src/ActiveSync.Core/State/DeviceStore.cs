using Microsoft.EntityFrameworkCore;

namespace ActiveSync.Core.State;

/// <summary>
///   Device partnerships and the login/session gates that hang off them: the per-(user, device)
///   <see cref="Device" /> row, operator login blocks, web-session revocation cut-offs, and the
///   MS-ASPROV policy-key / account-wipe / recovery-password fields. One of the collaborators
///   composed by <see cref="SyncStateService" />; it shares that facade's request-scoped
///   <see cref="SyncDbContext" /> so the change tracker stays unified.
/// </summary>
internal sealed class DeviceStore(SyncDbContext db)
{
	/// <summary>
	///   True when an operator block refuses THIS DEVICE. Blocks are per-device only — turning a
	///   whole user off is <c>User.Enabled = false</c>, a distinct mechanism with a distinct
	///   meaning (see <see cref="LoginBlock" />). A null <paramref name="deviceId" /> (Autodiscover
	///   carries none) therefore has no block to match and is never refused here.
	/// </summary>
	public Task<bool> IsDeviceBlockedAsync(int userId, string? deviceId, CancellationToken ct)
		=> deviceId is null
			? Task.FromResult(false)
			: db.LoginBlocks
				.AnyAsync(b => b.Device.UserId == userId && b.Device.DeviceId == deviceId, ct);

	/// <summary>
	///   The cut-off for this login's web sessions: one started before it is no longer valid.
	///   Null = nothing was ever revoked.
	/// </summary>
	public async Task<DateTime?> GetSessionsValidAfterAsync(int userId, CancellationToken ct)
	{
		WebSessionRevocation? row = await db.WebSessionRevocations
			.AsNoTracking()
			.FirstOrDefaultAsync(r => r.UserId == userId, ct)
			.ConfigureAwait(false);
		return row?.ValidAfterUtc;
	}

	/// <summary>
	///   Invalidates every web session of this login that started before <paramref name="whenUtc" />
	///   — the server-side half of signing out, since the cookie is a self-contained ticket that
	///   stays cryptographically valid after the browser drops it. Monotonic: an earlier cut-off
	///   never replaces a later one.
	/// </summary>
	public async Task RevokeSessionsBeforeAsync(int userId, DateTime whenUtc, CancellationToken ct)
	{
		WebSessionRevocation? row = await db.WebSessionRevocations
			.FirstOrDefaultAsync(r => r.UserId == userId, ct)
			.ConfigureAwait(false);
		if (row is null)
		{
			// DbSet.Add is synchronous and local (no I/O) — see GetOrCreateDeviceAsync.
#pragma warning disable VSTHRD103
			db.WebSessionRevocations.Add(
				new WebSessionRevocation { UserId = userId, ValidAfterUtc = whenUtc });
#pragma warning restore VSTHRD103
		}
		else if (row.ValidAfterUtc < whenUtc)
		{
			row.ValidAfterUtc = whenUtc;
		}
		else
		{
			return;
		}

		await db.SaveChangesAsync(ct).ConfigureAwait(false);
	}

	public async Task<Device> GetOrCreateDeviceAsync(
		int userId, string deviceId, string deviceType, CancellationToken ct,
		string? protocolVersion = null)
	{
		Device? device = await db.Devices
			.FirstOrDefaultAsync(d => d.UserId == userId && d.DeviceId == deviceId, ct)
			.ConfigureAwait(false);
		if (device is null)
		{
			device = new Device
			{
				UserId = userId,
				DeviceId = deviceId,
				DeviceType = deviceType,
				CreatedUtc = DateTime.UtcNow
			};
			// DbSet.Add is synchronous and local (no I/O) — AddAsync exists only to support
			// async value generators (e.g. HiLo/Cosmos), which this project doesn't use.
#pragma warning disable VSTHRD103
			db.Devices.Add(device);
#pragma warning restore VSTHRD103
		}

		device.LastSeenUtc = DateTime.UtcNow;
		if (!string.IsNullOrEmpty(deviceType))
			device.DeviceType = deviceType;
		if (!string.IsNullOrEmpty(protocolVersion))
			device.LastProtocolVersion = protocolVersion;
		try
		{
			await db.SaveChangesAsync(ct).ConfigureAwait(false);
		}
		catch (DbUpdateException ex) when (
			db.Entry(device).State == EntityState.Added && DbExceptions.IsUniqueViolation(ex))
		{
			// A concurrent first request for the same (user, device) inserted the row first.
			// Re-read the winner and re-apply the touch as an update. Only a unique violation
			// takes this path — any other failure propagates with its diagnostic intact (A9).
			db.Entry(device).State = EntityState.Detached;
			device = await db.Devices
				.FirstAsync(d => d.UserId == userId && d.DeviceId == deviceId, ct).ConfigureAwait(false);
			device.LastSeenUtc = DateTime.UtcNow;
			if (!string.IsNullOrEmpty(deviceType))
				device.DeviceType = deviceType;
			if (!string.IsNullOrEmpty(protocolVersion))
				device.LastProtocolVersion = protocolVersion;
			await db.SaveChangesAsync(ct).ConfigureAwait(false);
		}

		return device;
	}

	public async Task SaveDeviceInfoAsync(Device device, string deviceInfoJson, CancellationToken ct)
	{
		device.DeviceInfoJson = deviceInfoJson;
		await db.SaveChangesAsync(ct).ConfigureAwait(false);
	}

	/// <summary>
	///   Stores the issued policy key and, on the acknowledging phase of the Provision
	///   handshake, the hash of the policy document the device just accepted. Phase 1
	///   passes null — a device mid-handshake has not acknowledged anything yet.
	/// </summary>
	public async Task SetPolicyKeyAsync(Device device, uint policyKey, string? policyDocHash, CancellationToken ct)
	{
		device.PolicyKey = policyKey;
		device.PolicyDocHash = policyDocHash;
		await db.SaveChangesAsync(ct).ConfigureAwait(false);
	}

	/// <summary>Stores the sealed recovery password escrowed via Settings→DevicePassword.</summary>
	public async Task SetRecoveryPasswordAsync(Device device, string? sealedPassword, CancellationToken ct)
	{
		device.RecoveryPasswordProtected = sealedPassword;
		await db.SaveChangesAsync(ct).ConfigureAwait(false);
	}

	/// <summary>
	///   Marks the account-only wipe acknowledged and blocks the partnership, so stolen
	///   credentials stay dead after the account is removed from the device.
	/// </summary>
	public async Task CompleteAccountWipeAsync(Device device, CancellationToken ct)
	{
		// A6: bounded retry — either failure below means this attempt's save never landed, so
		// each pass re-derives its own state from a fresh read rather than assuming the first
		// attempt's decisions still hold.
		const int maxAttempts = 4;
		for (int attempt = 1; ; attempt++)
		{
			device.PendingAccountWipe = false;
			// The block is per-partnership and keyed by the device row itself, so the wipe path is
			// unaffected by blocks having become device-scoped.
			bool alreadyBlocked = await db.LoginBlocks
				.AnyAsync(b => b.DeviceKey == device.Id, ct)
				.ConfigureAwait(false);
			LoginBlock? added = null;
			if (!alreadyBlocked)
			{
				added = new LoginBlock { DeviceKey = device.Id, CreatedUtc = DateTime.UtcNow };
				await db.LoginBlocks.AddAsync(added, ct).ConfigureAwait(false);
			}

			try
			{
				await db.SaveChangesAsync(ct).ConfigureAwait(false);
				return;
			}
			catch (DbUpdateException ex) when (added is not null && DbExceptions.IsUniqueViolation(ex))
			{
				// A concurrent wipe ack already inserted this device's block between our
				// AnyAsync check and this insert — the block we want exists, so this is success, not
				// a 500. Drop our duplicate insert; the next pass's AnyAsync sees the winner and
				// skips the insert, persisting only the wipe-completion flag (A22).
				db.Entry(added).State = EntityState.Detached;
			}
			catch (DbUpdateConcurrencyException) when (attempt < maxAttempts)
			{
				// A concurrent write to the SAME device row (e.g. a touch from
				// GetOrCreateDeviceAsync, or a pipelined FolderSync bumping FolderSyncKey)
				// advanced its ConcurrencyToken between our read and this save. Reload the row
				// and re-apply the wipe-completion flag on the next pass rather than surfacing a
				// 500 on this security-critical ack (A6).
				if (added is not null)
					db.Entry(added).State = EntityState.Detached;
				await db.Entry(device).ReloadAsync(ct).ConfigureAwait(false);
			}
		}
	}
}

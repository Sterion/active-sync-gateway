using ActiveSync.Core.State;
using Microsoft.EntityFrameworkCore;

namespace ActiveSync.Core.Settings;

/// <summary>
///   CRUD over database-stored global settings (<see cref="GlobalSetting" /> rows: a full
///   configuration path → string value). Every mutation bumps the single
///   <see cref="SettingsStamp" /> row IN THE SAME SaveChanges, so each running gateway notices
///   changes with one primary-key point-read — the same idiom as
///   <see cref="ActiveSync.Core.Accounts.AccountStore" />. Registered as a singleton; used by the
///   CLI (writes) and the server's <see cref="SettingsRefresher" /> (reads).
/// </summary>
public sealed class GlobalSettingStore(ISyncDbContextFactory contextFactory)
{
	/// <summary>Current change stamp, or null when no setting was ever written.</summary>
	public async Task<Guid?> ReadStampAsync(CancellationToken ct)
	{
		await using SyncDbContext db = contextFactory.CreateDbContext();
		SettingsStamp? stamp = await db.SettingsStamps.AsNoTracking()
			.FirstOrDefaultAsync(s => s.Id == 1, ct).ConfigureAwait(false);
		return stamp?.Version;
	}

	/// <summary>All settings as a config-key → value map (case-insensitive keys, like configuration).</summary>
	public async Task<Dictionary<string, string?>> LoadAllAsync(CancellationToken ct)
	{
		await using SyncDbContext db = contextFactory.CreateDbContext();
		List<GlobalSetting> rows = await db.GlobalSettings.AsNoTracking()
			.ToListAsync(ct).ConfigureAwait(false);
		Dictionary<string, string?> result = new(StringComparer.OrdinalIgnoreCase);
		foreach (GlobalSetting row in rows)
			result[row.Key] = row.Value;
		return result;
	}

	public async Task<string?> GetAsync(string key, CancellationToken ct)
	{
		await using SyncDbContext db = contextFactory.CreateDbContext();
		GlobalSetting? row = await db.GlobalSettings.AsNoTracking()
			.FirstOrDefaultAsync(s => s.Key == key, ct).ConfigureAwait(false);
		return row?.Value;
	}

	public async Task<List<(string Key, string Value, DateTime UpdatedUtc)>> ListAsync(CancellationToken ct)
	{
		await using SyncDbContext db = contextFactory.CreateDbContext();
		List<GlobalSetting> rows = await db.GlobalSettings.AsNoTracking()
			.OrderBy(s => s.Key).ToListAsync(ct).ConfigureAwait(false);
		return rows.Select(r => (r.Key, r.Value, r.UpdatedUtc)).ToList();
	}

	public async Task UpsertAsync(string key, string value, CancellationToken ct)
	{
		await using SyncDbContext db = contextFactory.CreateDbContext();
		GlobalSetting? row = await db.GlobalSettings
			.FirstOrDefaultAsync(s => s.Key == key, ct).ConfigureAwait(false);
		if (row is null)
		{
			// DbSet.Add is synchronous and local (no I/O); AddAsync exists only for async value
			// generators (HiLo/Cosmos), which this project doesn't use.
#pragma warning disable VSTHRD103
			db.GlobalSettings.Add(new GlobalSetting { Key = key, Value = value, UpdatedUtc = DateTime.UtcNow });
#pragma warning restore VSTHRD103
		}
		else
		{
			row.Value = value;
			row.UpdatedUtc = DateTime.UtcNow;
		}

		await BumpStampAsync(db, ct).ConfigureAwait(false);
		await db.SaveChangesAsync(ct).ConfigureAwait(false);
	}

	public async Task<bool> DeleteAsync(string key, CancellationToken ct)
	{
		await using SyncDbContext db = contextFactory.CreateDbContext();
		GlobalSetting? row = await db.GlobalSettings
			.FirstOrDefaultAsync(s => s.Key == key, ct).ConfigureAwait(false);
		if (row is null)
			return false;
		db.GlobalSettings.Remove(row);
		await BumpStampAsync(db, ct).ConfigureAwait(false);
		await db.SaveChangesAsync(ct).ConfigureAwait(false);
		return true;
	}

	private static async Task BumpStampAsync(SyncDbContext db, CancellationToken ct)
	{
		SettingsStamp? stamp = await db.SettingsStamps
			.FirstOrDefaultAsync(s => s.Id == 1, ct).ConfigureAwait(false);
		if (stamp is null)
		{
			// DbSet.Add false positive for VSTHRD103 — see UpsertAsync above.
#pragma warning disable VSTHRD103
			db.SettingsStamps.Add(new SettingsStamp { Id = 1, Version = Guid.NewGuid() });
#pragma warning restore VSTHRD103
		}
		else
			stamp.Version = Guid.NewGuid();
	}
}

using ActiveSync.Core.Administration;
using ActiveSync.Core.State;
using Microsoft.EntityFrameworkCore;

namespace ActiveSync.Core.Settings;

/// <summary>
///   CRUD over database-stored global settings (<see cref="GlobalSetting" /> rows: a full
///   configuration path → string value). Every mutation bumps the <c>"settings"</c>
///   <see cref="DataChange" /> row IN THE SAME SaveChanges, so each running gateway notices
///   changes with one primary-key point-read — the same idiom as
///   <see cref="ActiveSync.Core.Accounts.UserStore" />. Registered as a singleton; used by the
///   CLI (writes) and the server's <see cref="SettingsRefresher" /> (reads).
/// </summary>
public sealed class GlobalSettingStore(ISyncDbContextFactory contextFactory)
{
	/// <summary>Current change stamp of the "settings" area, or null when nothing was ever written.</summary>
	public Task<Guid?> ReadStampAsync(CancellationToken ct) =>
		DataChangeStamps.ReadAsync(contextFactory, DataChangeAreas.Settings, ct);

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
		// B7: the row's Key is normalized to lowercase on write (see UpsertAsync), so a plain
		// equality on the normalized value is both case-insensitive AND a sargable seek on the
		// primary-key index — unlike the previous `s.Key.ToLower() == key.ToLower()`, which put a
		// function on the indexed column and forced a full table scan on every lookup.
		string normalized = key.ToLowerInvariant();
		GlobalSetting? row = await db.GlobalSettings.AsNoTracking()
			.FirstOrDefaultAsync(s => s.Key == normalized, ct).ConfigureAwait(false);
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
		// Defence in depth (B12): the write surfaces (`eas config set`, the web settings API) already
		// refuse bootstrap/host-controlled keys, and DbSettingsConfigurationProvider drops any that
		// reach the table by another route — but the store is the last common chokepoint, so refuse
		// here too. A stored Database:ConnectionString / Encryption:Key row would be read on the next
		// start (the DB provider is layered last) and repoint the gateway at a database it then trusts.
		if (SettingKeys.HostControlledReason(key) is { } reason)
			throw new InvalidOperationException($"'{key}' cannot be stored in the database: {reason}.");

		// B7: normalize the STORED key itself (not just the comparison) — every get/upsert/delete
		// then becomes a sargable equality seek on the primary key, and the primary key (case-
		// sensitive on both providers) can no longer hold two rows that differ only in case (a race
		// between two replicas' UpsertAsync calls, a restored dump, or any out-of-band write): both
		// normalize to the identical key, so the second write always lands on the first row rather
		// than beside it. Display casing lives in SettingKeys.Find/definition.Key, not the row.
		string normalized = key.ToLowerInvariant();
		await using SyncDbContext db = contextFactory.CreateDbContext();
		GlobalSetting? row = await db.GlobalSettings
			.FirstOrDefaultAsync(s => s.Key == normalized, ct).ConfigureAwait(false);
		if (row is null)
		{
			// DbSet.Add is synchronous and local (no I/O); AddAsync exists only for async value
			// generators (HiLo/Cosmos), which this project doesn't use.
#pragma warning disable VSTHRD103
			db.GlobalSettings.Add(new GlobalSetting { Key = normalized, Value = value, UpdatedUtc = DateTime.UtcNow });
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
		string normalized = key.ToLowerInvariant();
		await using SyncDbContext db = contextFactory.CreateDbContext();
		GlobalSetting? row = await db.GlobalSettings
			.FirstOrDefaultAsync(s => s.Key == normalized, ct).ConfigureAwait(false);
		if (row is null)
			return false;
		db.GlobalSettings.Remove(row);
		await BumpStampAsync(db, ct).ConfigureAwait(false);
		await db.SaveChangesAsync(ct).ConfigureAwait(false);
		return true;
	}

	private static Task BumpStampAsync(SyncDbContext db, CancellationToken ct) =>
		DataChangeStamps.BumpAsync(db, DataChangeAreas.Settings, ct);
}

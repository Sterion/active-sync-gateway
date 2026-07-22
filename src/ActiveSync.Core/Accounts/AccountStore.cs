using System.Text.Json;
using ActiveSync.Core.Options;
using ActiveSync.Core.State;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ActiveSync.Core.Accounts;

/// <summary>
///   CRUD over database-declared account entries (<see cref="AccountEntry" /> rows holding a
///   serialized <see cref="AccountOptions" />). Every mutation bumps the single
///   <see cref="AccountsStamp" /> row IN THE SAME SaveChanges, so each running gateway
///   notices changes with one primary-key point-read. Registered as a singleton.
/// </summary>
public sealed class AccountStore(ISyncDbContextFactory contextFactory)
{
	/// <summary>Serialization shape for AccountEntry.Json (camelCase, nulls omitted).</summary>
	public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
	{
		DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
	};

	/// <summary>Current change stamp, or null when no account mutation was ever written.</summary>
	public async Task<Guid?> ReadStampAsync(CancellationToken ct)
	{
		await using SyncDbContext db = contextFactory.CreateDbContext();
		AccountsStamp? stamp = await db.AccountsStamps.AsNoTracking()
			.FirstOrDefaultAsync(s => s.Id == 1, ct).ConfigureAwait(false);
		return stamp?.Version;
	}

	/// <summary>
	///   All database entries keyed by login (case-insensitive, like config Users). A row
	///   whose Json no longer deserializes is skipped with a warning — one bad row must
	///   never take authentication down.
	/// </summary>
	public async Task<Dictionary<string, AccountOptions>> LoadAllAsync(ILogger? logger, CancellationToken ct)
	{
		await using SyncDbContext db = contextFactory.CreateDbContext();
		List<AccountEntry> entries = await db.AccountEntries.AsNoTracking()
			.ToListAsync(ct).ConfigureAwait(false);

		Dictionary<string, AccountOptions> result = new(StringComparer.OrdinalIgnoreCase);
		foreach (AccountEntry entry in entries)
		{
			try
			{
				result[entry.UserName] =
					JsonSerializer.Deserialize<AccountOptions>(entry.Json, JsonOptions) ?? new AccountOptions();
			}
			catch (JsonException ex)
			{
				logger?.LogWarning(ex,
					"Skipping database account entry for {User} — stored JSON does not parse", entry.UserName);
			}
		}

		return result;
	}

	public async Task<AccountOptions?> GetAsync(string login, CancellationToken ct)
	{
		await using SyncDbContext db = contextFactory.CreateDbContext();
		// B2: logins are case-insensitive everywhere in memory, so match that way in SQL too —
		// otherwise a differently-cased login misses the existing row (see UpsertAsync).
		AccountEntry? entry = await db.AccountEntries.AsNoTracking()
			.FirstOrDefaultAsync(a => a.UserName.ToLower() == login.ToLower(), ct).ConfigureAwait(false);
		return entry is null
			? null
			: JsonSerializer.Deserialize<AccountOptions>(entry.Json, JsonOptions) ?? new AccountOptions();
	}

	public async Task<List<(string UserName, AccountOptions Options, DateTime UpdatedUtc)>> ListAsync(
		CancellationToken ct)
	{
		await using SyncDbContext db = contextFactory.CreateDbContext();
		List<AccountEntry> entries = await db.AccountEntries.AsNoTracking()
			.OrderBy(a => a.UserName).ToListAsync(ct).ConfigureAwait(false);
		List<(string, AccountOptions, DateTime)> result = [];
		foreach (AccountEntry entry in entries)
			result.Add((entry.UserName,
				JsonSerializer.Deserialize<AccountOptions>(entry.Json, JsonOptions) ?? new AccountOptions(),
				entry.UpdatedUtc));
		return result;
	}

	public async Task UpsertAsync(string login, AccountOptions options, CancellationToken ct)
	{
		await using SyncDbContext db = contextFactory.CreateDbContext();
		// B2: match case-insensitively so `eas user set Phone1` updates the existing `phone1` row
		// instead of inserting a second, colliding one.
		AccountEntry? entry = await db.AccountEntries
			.FirstOrDefaultAsync(a => a.UserName.ToLower() == login.ToLower(), ct).ConfigureAwait(false);
		string json = JsonSerializer.Serialize(options, JsonOptions);
		if (entry is null)
		{
			// DbSet.Add is synchronous and local (no I/O) — AddAsync exists only to support
			// async value generators (e.g. HiLo/Cosmos), which this project doesn't use.
#pragma warning disable VSTHRD103
			db.AccountEntries.Add(new AccountEntry { UserName = login, Json = json, UpdatedUtc = DateTime.UtcNow });
#pragma warning restore VSTHRD103
		}
		else
		{
			entry.Json = json;
			entry.UpdatedUtc = DateTime.UtcNow;
		}

		await BumpStampAsync(db, ct).ConfigureAwait(false);
		await db.SaveChangesAsync(ct).ConfigureAwait(false);
	}

	public async Task<bool> DeleteAsync(string login, CancellationToken ct)
	{
		await using SyncDbContext db = contextFactory.CreateDbContext();
		AccountEntry? entry = await db.AccountEntries
			.FirstOrDefaultAsync(a => a.UserName.ToLower() == login.ToLower(), ct).ConfigureAwait(false);
		if (entry is null)
			return false;
		db.AccountEntries.Remove(entry);
		await BumpStampAsync(db, ct).ConfigureAwait(false);
		await db.SaveChangesAsync(ct).ConfigureAwait(false);
		return true;
	}

	/// <summary>
	///   One-time startup upgrade of pre-role-model rows to the role-keyed shape (see
	///   <see cref="LegacyAccountJson" />). Rows that cannot be converted are left in place
	///   and reported as ERRORS — the resolver will then skip them loudly rather than
	///   silently dropping their overrides.
	/// </summary>
	public async Task UpgradeLegacyRowsAsync(ILogger logger, CancellationToken ct)
	{
		await using SyncDbContext db = contextFactory.CreateDbContext();
		List<AccountEntry> entries = await db.AccountEntries.ToListAsync(ct).ConfigureAwait(false);
		int upgraded = 0;
		foreach (AccountEntry entry in entries)
		{
			string? converted = LegacyAccountJson.TryConvert(entry.Json, out string? error);
			if (error is not null)
			{
				logger.LogError("Account row for {User} could not be upgraded: {Error}", entry.UserName, error);
				continue;
			}

			if (converted is null)
				continue;
			entry.Json = converted;
			entry.UpdatedUtc = DateTime.UtcNow;
			upgraded++;
			logger.LogInformation("Upgraded legacy account row for {User} to the role-keyed shape", entry.UserName);
		}

		if (upgraded > 0)
		{
			await BumpStampAsync(db, ct).ConfigureAwait(false);
			await db.SaveChangesAsync(ct).ConfigureAwait(false);
		}
	}

	private static async Task BumpStampAsync(SyncDbContext db, CancellationToken ct)
	{
		AccountsStamp? stamp = await db.AccountsStamps
			.FirstOrDefaultAsync(s => s.Id == 1, ct).ConfigureAwait(false);
		if (stamp is null)
		{
			// DbSet.Add false positive for VSTHRD103 — see UpsertAsync above.
#pragma warning disable VSTHRD103
			db.AccountsStamps.Add(new AccountsStamp { Id = 1, Version = Guid.NewGuid() });
#pragma warning restore VSTHRD103
		}
		else
			stamp.Version = Guid.NewGuid();
	}
}

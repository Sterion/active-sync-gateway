using System.Text.Json;
using ActiveSync.Core.Options;
using ActiveSync.Core.State;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ActiveSync.Core.Accounts;

/// <summary>
///   CRUD over <see cref="User" /> rows. A row is two things at once: the IDENTITY (the
///   immutable <see cref="User.UserId" /> everything per-user hangs off) and, when
///   <see cref="User.Json" /> is set, a database DECLARATION (a serialized
///   <see cref="UserOptions" />). Identity-only rows (null Json) exist for every login that
///   ever authenticated — including config-declared ones — without shadowing configuration.
///   Every declaration mutation bumps the single <see cref="AccountsStamp" /> row IN THE SAME
///   SaveChanges, so each running gateway notices changes with one primary-key point-read.
///   Registered as a singleton.
/// </summary>
public sealed class UserStore(ISyncDbContextFactory contextFactory)
{
	/// <summary>Serialization shape for User.Json (camelCase, nulls omitted).</summary>
	public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
	{
		DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
	};

	/// <summary>
	///   Canonical (case-folded) form of a login. Logins are case-insensitive everywhere in memory
	///   (the <see cref="LoadAllAsync" /> map uses <see cref="StringComparer.OrdinalIgnoreCase" />),
	///   so <see cref="User.Login" /> is STORED in this form (B1/B8): the raw unique index
	///   then enforces case-folded uniqueness on its own — two BINARY-distinct rows like `Phone1` and
	///   `phone1` can no longer both exist — and every lookup is an exact index seek rather than a
	///   non-sargable `LOWER()` scan.
	/// </summary>
	public static string NormalizeLogin(string login) => login.ToLowerInvariant();

	/// <summary>The user id for a login, or null when no such user exists.</summary>
	public async Task<int?> FindUserIdAsync(string login, CancellationToken ct)
	{
		await using SyncDbContext db = contextFactory.CreateDbContext();
		string normalized = NormalizeLogin(login);
		User? row = await db.Users.AsNoTracking()
			.FirstOrDefaultAsync(u => u.Login == normalized, ct).ConfigureAwait(false);
		return row?.UserId;
	}

	/// <summary>
	///   The identity for a login, creating the row if none exists — the one path that mints a
	///   <c>UserId</c>. <paramref name="declarationIfMissing" /> is written when this call inserts,
	///   and also when the row exists but carries no declaration (an auto-provisioned declaration
	///   removed earlier is re-created on the next sign-in while the flag is on — the documented
	///   semantics); pass null to keep/create an identity-only row (config-declared logins —
	///   configuration keeps supplying the values). First-auth is a race: two devices of a
	///   brand-new login both try to insert, so the loser catches the unique violation on Login
	///   and re-reads the winner (the DeviceStore/DavItemMap idiom). Returns the id plus whether
	///   a declaration was written (callers refresh the resolver then).
	/// </summary>
	public async Task<(int UserId, bool DeclarationWritten)> GetOrCreateUserAsync(
		string login, UserOptions? declarationIfMissing, CancellationToken ct)
	{
		await using SyncDbContext db = contextFactory.CreateDbContext();
		string normalized = NormalizeLogin(login);
		string? json = declarationIfMissing is null
			? null
			: JsonSerializer.Serialize(declarationIfMissing, JsonOptions);
		User? row = await db.Users
			.FirstOrDefaultAsync(u => u.Login == normalized, ct).ConfigureAwait(false);
		if (row is not null)
		{
			if (row.Json is not null || json is null)
				return (row.UserId, false);
			row.Json = json;
			row.UpdatedUtc = DateTime.UtcNow;
			await BumpStampAsync(db, ct).ConfigureAwait(false);
			await db.SaveChangesAsync(ct).ConfigureAwait(false);
			return (row.UserId, true);
		}

		User created = new()
		{
			Login = normalized,
			Json = json,
			UpdatedUtc = DateTime.UtcNow,
		};
		// DbSet.Add is synchronous and local (no I/O) — see UpsertAsync.
#pragma warning disable VSTHRD103
		db.Users.Add(created);
#pragma warning restore VSTHRD103
		if (json is not null)
			await BumpStampAsync(db, ct).ConfigureAwait(false);
		try
		{
			await db.SaveChangesAsync(ct).ConfigureAwait(false);
			return (created.UserId, json is not null);
		}
		catch (DbUpdateException ex) when (DbExceptions.IsUniqueViolation(ex))
		{
			// A concurrent first-auth (another device, another replica) won the insert race.
			db.Entry(created).State = EntityState.Detached;
			User winner = await db.Users.AsNoTracking()
				.FirstAsync(u => u.Login == normalized, ct).ConfigureAwait(false);
			return (winner.UserId, false);
		}
	}

	/// <summary>Current change stamp, or null when no account mutation was ever written.</summary>
	public async Task<Guid?> ReadStampAsync(CancellationToken ct)
	{
		await using SyncDbContext db = contextFactory.CreateDbContext();
		AccountsStamp? stamp = await db.AccountsStamps.AsNoTracking()
			.FirstOrDefaultAsync(s => s.Id == 1, ct).ConfigureAwait(false);
		return stamp?.Version;
	}

	/// <summary>
	///   All database DECLARATIONS keyed by login (case-insensitive, like config Users) —
	///   identity-only rows are not declarations and are skipped. A row whose Json no longer
	///   deserializes is skipped with a warning — one bad row must never take authentication down.
	/// </summary>
	public async Task<Dictionary<string, UserOptions>> LoadAllAsync(ILogger? logger, CancellationToken ct)
	{
		await using SyncDbContext db = contextFactory.CreateDbContext();
		List<User> entries = await db.Users.AsNoTracking()
			.Where(u => u.Json != null)
			.ToListAsync(ct).ConfigureAwait(false);

		Dictionary<string, UserOptions> result = new(StringComparer.OrdinalIgnoreCase);
		foreach (User entry in entries)
			if (TryDeserialize(entry, logger, out UserOptions options))
			{
				// B1: the store writes Login case-folded so this can't happen through the app, but a
				// pre-fix pair, a restored dump or an out-of-band write can still leave two BINARY-distinct
				// rows that collapse here last-write-wins. Surface it — never silently drop a user's overrides.
				if (result.ContainsKey(entry.Login))
					logger?.LogWarning(
						"Multiple database account rows collapse to login {User} (case-insensitive); keeping the " +
						"last and ignoring an earlier duplicate — remove the redundant row (`eas user` / the Users page)",
						entry.Login);
				result[entry.Login] = options;
			}

		return result;
	}

	public async Task<UserOptions?> GetAsync(string login, CancellationToken ct)
	{
		await using SyncDbContext db = contextFactory.CreateDbContext();
		// B8: Login is stored case-folded (see NormalizeLogin), so an exact match on the normalized
		// login is an index seek AND sees every casing.
		string normalized = NormalizeLogin(login);
		User? entry = await db.Users.AsNoTracking()
			.FirstOrDefaultAsync(u => u.Login == normalized, ct).ConfigureAwait(false);
		// B15: tolerate an unparseable row like LoadAllAsync does — `eas user show` must never
		// hard-fail with JsonException (it is one of the tools for finding the bad row).
		return entry is { Json: not null } && TryDeserialize(entry, null, out UserOptions options) ? options : null;
	}

	public async Task<List<(string Login, UserOptions Options, DateTime UpdatedUtc, bool Valid)>> ListAsync(
		CancellationToken ct)
	{
		await using SyncDbContext db = contextFactory.CreateDbContext();
		List<User> entries = await db.Users.AsNoTracking()
			.Where(u => u.Json != null)
			.OrderBy(u => u.Login).ToListAsync(ct).ConfigureAwait(false);
		List<(string, UserOptions, DateTime, bool)> result = [];
		foreach (User entry in entries)
		{
			// B15: surface a bad row FLAGGED rather than omitting it or throwing — `eas users`
			// and the admin list must still render, marking the row the operator has to fix.
			bool valid = TryDeserialize(entry, null, out UserOptions options);
			result.Add((entry.Login, options, entry.UpdatedUtc, valid));
		}

		return result;
	}

	/// <summary>
	///   Deserializes one declaration's JSON, tolerating a malformed value: on failure it logs
	///   (when a logger is supplied), yields an empty <see cref="UserOptions" /> and returns
	///   false. Shared by every read path so one corrupt row can never take a surface down (B15).
	/// </summary>
	private static bool TryDeserialize(User entry, ILogger? logger, out UserOptions options)
	{
		try
		{
			options = JsonSerializer.Deserialize<UserOptions>(entry.Json!, JsonOptions) ?? new UserOptions();
			return true;
		}
		catch (Exception ex) when (ex is JsonException or NotSupportedException)
		{
			logger?.LogWarning(ex,
				"Database account entry for {User} does not parse — treating it as invalid", entry.Login);
			options = new UserOptions();
			return false;
		}
	}

	public async Task UpsertAsync(string login, UserOptions options, CancellationToken ct)
	{
		await using SyncDbContext db = contextFactory.CreateDbContext();
		// B8: match on the case-folded login so `eas user set Phone1` updates the existing `phone1`
		// row instead of inserting a second, colliding one (index seek, sees every casing).
		string normalized = NormalizeLogin(login);
		User? entry = await db.Users
			.FirstOrDefaultAsync(u => u.Login == normalized, ct).ConfigureAwait(false);
		string json = JsonSerializer.Serialize(options, JsonOptions);
		if (entry is null)
		{
			// B1: store the case-folded login so the raw unique index enforces case-folded uniqueness.
			// DbSet.Add is synchronous and local (no I/O) — AddAsync exists only to support
			// async value generators (e.g. HiLo/Cosmos), which this project doesn't use.
#pragma warning disable VSTHRD103
			db.Users.Add(new User { Login = normalized, Json = json, UpdatedUtc = DateTime.UtcNow });
#pragma warning restore VSTHRD103
		}
		else
		{
			entry.Json = json;
			entry.UpdatedUtc = DateTime.UtcNow;
		}

		await BumpStampAsync(db, ct).ConfigureAwait(false);
		try
		{
			await db.SaveChangesAsync(ct).ConfigureAwait(false);
		}
		catch (DbUpdateException ex) when (entry is null && DbExceptions.IsUniqueViolation(ex))
		{
			// A concurrent identity insert (first-auth race) landed between our read and this
			// insert — re-read the winner and apply the declaration as an update.
			foreach (Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry tracked
				in db.ChangeTracker.Entries().ToList())
				tracked.State = EntityState.Detached;
			User winner = await db.Users
				.FirstAsync(u => u.Login == normalized, ct).ConfigureAwait(false);
			winner.Json = json;
			winner.UpdatedUtc = DateTime.UtcNow;
			await BumpStampAsync(db, ct).ConfigureAwait(false);
			await db.SaveChangesAsync(ct).ConfigureAwait(false);
		}
	}

	/// <summary>
	///   Removes the database DECLARATION for a login — the entry falls back to configuration
	///   (or to plain pass-through), exactly as `eas user remove` always meant. The IDENTITY
	///   row and its <c>UserId</c> survive: sync state, encrypted local items and blocks all
	///   hang off the id, and removing a declaration must not destroy data. Deleting the user
	///   outright (cascade) is a separate, guarded operation (db-restructure item 6b).
	/// </summary>
	public async Task<bool> DeleteAsync(string login, CancellationToken ct)
	{
		await using SyncDbContext db = contextFactory.CreateDbContext();
		// B8: exact match on the case-folded login (index seek, sees every casing).
		string normalized = NormalizeLogin(login);
		User? entry = await db.Users
			.FirstOrDefaultAsync(u => u.Login == normalized, ct).ConfigureAwait(false);
		if (entry is not { Json: not null })
			return false;
		entry.Json = null;
		entry.UpdatedUtc = DateTime.UtcNow;
		await BumpStampAsync(db, ct).ConfigureAwait(false);
		await db.SaveChangesAsync(ct).ConfigureAwait(false);
		return true;
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

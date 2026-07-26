using System.Text.Json;
using ActiveSync.Contracts;
using ActiveSync.Core.Options;
using ActiveSync.Core.State;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ActiveSync.Core.Accounts;

/// <summary>
///   CRUD over <see cref="User" /> rows. A row is two things at once: the IDENTITY (the
///   immutable <see cref="User.UserId" /> everything per-user hangs off) and, when
///   <see cref="User.Declared" /> is set, a database DECLARATION. Identity-only rows exist for
///   every login that ever authenticated — including config-declared ones — without shadowing
///   configuration. Every declaration mutation bumps the single <see cref="AccountsStamp" /> row
///   IN THE SAME SaveChanges, so each running gateway notices changes with one primary-key
///   point-read. Registered as a singleton.
///   <para>
///     <see cref="UserOptions" /> remains the in-memory and config-bound shape (it is what
///     <c>ActiveSync:Users</c> binds to), so the resolver, the editing pipeline, the CLI, the
///     admin API and the banner all keep operating on it unchanged. Only PERSISTENCE is
///     normalised: <see cref="ToEntity" /> and <see cref="FromEntity" /> below are the entire
///     seam between the object graph and its columns.
///   </para>
/// </summary>
public sealed class UserStore(ISyncDbContextFactory contextFactory)
{
	/// <summary>Serialization shape for the per-role Settings blob (camelCase, nulls omitted).</summary>
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
		User? row = await db.Users.Include(u => u.BackendRoles)
			.FirstOrDefaultAsync(u => u.Login == normalized, ct).ConfigureAwait(false);
		if (row is not null)
		{
			if (row.Declared || declarationIfMissing is null)
				return (row.UserId, false);
			ToEntity(declarationIfMissing, row);
			await BumpStampAsync(db, ct).ConfigureAwait(false);
			await db.SaveChangesAsync(ct).ConfigureAwait(false);
			return (row.UserId, true);
		}

		User created = new() { Login = normalized, UpdatedUtc = DateTime.UtcNow };
		if (declarationIfMissing is not null)
			ToEntity(declarationIfMissing, created);
		// DbSet.Add is synchronous and local (no I/O) — see UpsertAsync.
#pragma warning disable VSTHRD103
		db.Users.Add(created);
#pragma warning restore VSTHRD103
		if (declarationIfMissing is not null)
			await BumpStampAsync(db, ct).ConfigureAwait(false);
		try
		{
			await db.SaveChangesAsync(ct).ConfigureAwait(false);
			return (created.UserId, declarationIfMissing is not null);
		}
		catch (DbUpdateException ex) when (DbExceptions.IsUniqueViolation(ex))
		{
			// A concurrent first-auth (another device, another replica) won the insert race.
			// A9: only the LOGIN index means that — the row is now there, so re-read the winner.
			// Any OTHER unique violation (OidcSubject) finds nothing and must surface with its
			// own diagnostic rather than as "Sequence contains no elements".
			db.Entry(created).State = EntityState.Detached;
			User? winner = await db.Users.AsNoTracking()
				.FirstOrDefaultAsync(u => u.Login == normalized, ct).ConfigureAwait(false);
			if (winner is null)
				throw;
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
	///   identity-only rows are not declarations and are skipped. A row whose settings blob no
	///   longer parses is surfaced with a warning and its settings dropped; the typed columns
	///   still apply, so one corrupt blob can no longer take a whole user's overrides down.
	/// </summary>
	public async Task<Dictionary<string, UserOptions>> LoadAllAsync(ILogger? logger, CancellationToken ct)
	{
		await using SyncDbContext db = contextFactory.CreateDbContext();
		List<User> entries = await db.Users.AsNoTracking().Include(u => u.BackendRoles)
			.Where(u => u.Declared)
			.ToListAsync(ct).ConfigureAwait(false);

		Dictionary<string, UserOptions> result = new(StringComparer.OrdinalIgnoreCase);
		foreach (User entry in entries)
		{
			// B1: the store writes Login case-folded so this can't happen through the app, but a
			// pre-fix pair, a restored dump or an out-of-band write can still leave two BINARY-distinct
			// rows that collapse here last-write-wins. Surface it — never silently drop a user's overrides.
			if (result.ContainsKey(entry.Login))
				logger?.LogWarning(
					"Multiple database account rows collapse to login {User} (case-insensitive); keeping the " +
					"last and ignoring an earlier duplicate — remove the redundant row (`eas user` / the Users page)",
					entry.Login);
			result[entry.Login] = FromEntity(entry, logger, out _);
		}

		return result;
	}

	public async Task<UserOptions?> GetAsync(string login, CancellationToken ct)
	{
		await using SyncDbContext db = contextFactory.CreateDbContext();
		// B8: Login is stored case-folded (see NormalizeLogin), so an exact match on the normalized
		// login is an index seek AND sees every casing.
		string normalized = NormalizeLogin(login);
		User? entry = await db.Users.AsNoTracking().Include(u => u.BackendRoles)
			.FirstOrDefaultAsync(u => u.Login == normalized, ct).ConfigureAwait(false);
		return entry is { Declared: true } ? FromEntity(entry, null, out _) : null;
	}

	public async Task<List<(string Login, UserOptions Options, DateTime UpdatedUtc, bool Valid)>> ListAsync(
		CancellationToken ct)
	{
		await using SyncDbContext db = contextFactory.CreateDbContext();
		List<User> entries = await db.Users.AsNoTracking().Include(u => u.BackendRoles)
			.Where(u => u.Declared)
			.OrderBy(u => u.Login).ToListAsync(ct).ConfigureAwait(false);
		List<(string, UserOptions, DateTime, bool)> result = [];
		foreach (User entry in entries)
		{
			// B15: surface a row with an unparseable settings blob FLAGGED rather than omitting it
			// or throwing — `eas users` and the admin list must still render, marking the row to fix.
			UserOptions options = FromEntity(entry, null, out bool valid);
			result.Add((entry.Login, options, entry.UpdatedUtc, valid));
		}

		return result;
	}

	public async Task UpsertAsync(string login, UserOptions options, CancellationToken ct)
	{
		await using SyncDbContext db = contextFactory.CreateDbContext();
		// B8: match on the case-folded login so `eas user set Phone1` updates the existing `phone1`
		// row instead of inserting a second, colliding one (index seek, sees every casing).
		string normalized = NormalizeLogin(login);
		User? entry = await db.Users.Include(u => u.BackendRoles)
			.FirstOrDefaultAsync(u => u.Login == normalized, ct).ConfigureAwait(false);
		if (entry is null)
		{
			// B1: store the case-folded login so the raw unique index enforces case-folded uniqueness.
			// DbSet.Add is synchronous and local (no I/O) — AddAsync exists only to support
			// async value generators (e.g. HiLo/Cosmos), which this project doesn't use.
			entry = new User { Login = normalized, UpdatedUtc = DateTime.UtcNow };
			ToEntity(options, entry);
#pragma warning disable VSTHRD103
			db.Users.Add(entry);
#pragma warning restore VSTHRD103
			await BumpStampAsync(db, ct).ConfigureAwait(false);
			try
			{
				await db.SaveChangesAsync(ct).ConfigureAwait(false);
				return;
			}
			catch (DbUpdateException ex) when (DbExceptions.IsUniqueViolation(ex))
			{
				// A concurrent identity insert (first-auth race) landed between our read and this
				// insert — re-read the winner and apply the declaration as an update. A9: that is
				// only what the LOGIN index means; any other unique violation (OidcSubject —
				// two users may not bind to one identity-provider subject) finds no winner and
				// must surface with its own diagnostic intact.
				foreach (Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry tracked
					in db.ChangeTracker.Entries().ToList())
					tracked.State = EntityState.Detached;
				entry = await db.Users.Include(u => u.BackendRoles)
					.FirstOrDefaultAsync(u => u.Login == normalized, ct).ConfigureAwait(false);
				if (entry is null)
					throw;
			}
		}

		ToEntity(options, entry);
		await BumpStampAsync(db, ct).ConfigureAwait(false);
		await db.SaveChangesAsync(ct).ConfigureAwait(false);
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
		User? entry = await db.Users.Include(u => u.BackendRoles)
			.FirstOrDefaultAsync(u => u.Login == normalized, ct).ConfigureAwait(false);
		if (entry is not { Declared: true })
			return false;
		ClearDeclaration(entry);
		db.UserBackendRoles.RemoveRange(entry.BackendRoles);
		entry.BackendRoles.Clear();
		entry.UpdatedUtc = DateTime.UtcNow;
		await BumpStampAsync(db, ct).ConfigureAwait(false);
		await db.SaveChangesAsync(ct).ConfigureAwait(false);
		return true;
	}

	/// <summary>
	///   Applies a <see cref="UserOptions" /> declaration onto its columns. Role rows are DIFFED
	///   by role name rather than deleted-and-reinserted, so an untouched role keeps its row id
	///   (churn-free, and the FK cascade stays meaningful).
	/// </summary>
	private static void ToEntity(UserOptions options, User entity)
	{
		entity.Declared = true;
		entity.Password = options.Password;
		entity.DefaultBackendLogin = options.DefaultBackendLogin;
		entity.DefaultBackendPassword = options.DefaultBackendPassword;
		entity.MailAddress = options.MailAddress;
		entity.Admin = options.Admin;
		entity.Enabled = options.Enabled;
		entity.OidcSubject = options.OidcSubject;
		entity.AutoProvisioned = options.AutoProvisioned;
		entity.UpdatedUtc = DateTime.UtcNow;

		Dictionary<string, BackendRoleOverride> incoming =
			new(options.Backends ?? [], StringComparer.OrdinalIgnoreCase);

		// Roles no longer declared are removed; the rest are updated in place.
		foreach (UserBackendRole existing in entity.BackendRoles.ToList())
			if (!incoming.ContainsKey(existing.Role))
				entity.BackendRoles.Remove(existing);

		foreach ((string role, BackendRoleOverride @override) in incoming)
		{
			UserBackendRole? row = entity.BackendRoles
				.FirstOrDefault(r => r.Role.Equals(role, StringComparison.OrdinalIgnoreCase));
			if (row is null)
			{
				row = new UserBackendRole { Role = role, UserId = entity.UserId };
				entity.BackendRoles.Add(row);
			}

			row.Enabled = @override.Enabled;
			row.Provider = @override.Provider;
			row.UserName = @override.UserName;
			row.Password = @override.Password;
			row.SettingsJson = @override.Settings is { Count: > 0 }
				? JsonSerializer.Serialize(@override.Settings, JsonOptions)
				: null;
		}
	}

	/// <summary>
	///   Rebuilds the in-memory declaration from its columns. <paramref name="valid" /> is false
	///   when a per-role settings blob did not parse — the row is still returned (typed columns
	///   intact, that role's settings dropped) so one corrupt blob cannot take a surface down or
	///   break authentication (B15).
	/// </summary>
	private static UserOptions FromEntity(User entity, ILogger? logger, out bool valid)
	{
		valid = true;
		UserOptions options = new()
		{
			Password = entity.Password,
			DefaultBackendLogin = entity.DefaultBackendLogin,
			DefaultBackendPassword = entity.DefaultBackendPassword,
			MailAddress = entity.MailAddress,
			Admin = entity.Admin,
			Enabled = entity.Enabled,
			OidcSubject = entity.OidcSubject,
			AutoProvisioned = entity.AutoProvisioned,
		};

		if (entity.BackendRoles.Count == 0)
			return options;

		Dictionary<string, BackendRoleOverride> backends = new(StringComparer.OrdinalIgnoreCase);
		foreach (UserBackendRole row in entity.BackendRoles.OrderBy(r => r.Role, StringComparer.Ordinal))
		{
			Dictionary<string, string?>? settings = null;
			if (row.SettingsJson is { Length: > 0 })
			{
				try
				{
					settings = JsonSerializer.Deserialize<Dictionary<string, string?>>(row.SettingsJson, JsonOptions);
				}
				catch (Exception ex) when (ex is JsonException or NotSupportedException)
				{
					valid = false;
					logger?.LogWarning(ex,
						"Settings of role {Role} for {User} do not parse — ignoring them for this role",
						row.Role, entity.Login);
				}
			}

			backends[row.Role] = new BackendRoleOverride
			{
				Enabled = row.Enabled,
				Provider = row.Provider,
				UserName = row.UserName,
				Password = row.Password,
				Settings = settings,
			};
		}

		options.Backends = backends;
		return options;
	}

	private static void ClearDeclaration(User entity)
	{
		entity.Declared = false;
		entity.Password = null;
		entity.DefaultBackendLogin = null;
		entity.DefaultBackendPassword = null;
		entity.MailAddress = null;
		entity.Admin = null;
		entity.Enabled = null;
		entity.OidcSubject = null;
		entity.AutoProvisioned = null;
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

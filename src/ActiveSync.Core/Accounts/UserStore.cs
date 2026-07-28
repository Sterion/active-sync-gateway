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
///   configuration. Every declaration mutation bumps the <c>"users"</c> <see cref="DataChange" />
///   row IN THE SAME SaveChanges, so each running gateway notices changes with one primary-key
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
	///   Canonical (case-folded, trimmed) form of a login. Logins are case-insensitive everywhere in
	///   memory (the <see cref="LoadAllAsync" /> map uses <see cref="StringComparer.OrdinalIgnoreCase" />),
	///   so <see cref="User.Login" /> is STORED in this form: the raw unique index
	///   then enforces case-folded uniqueness on its own — two BINARY-distinct rows like `Phone1` and
	///   `phone1` can no longer both exist — and every lookup is an exact index seek rather than a
	///   non-sargable `LOWER()` scan. Also trimmed — Basic auth delivers leading/trailing
	///   whitespace verbatim, and an untrimmed lookup (`" bob"` vs `"bob"`) would otherwise mint a
	///   second, permanent identity that <see cref="UserResolver.ValidateLogin" /> now refuses at the
	///   config-declared write surface but which pass-through auto-provisioning could still reach.
	/// </summary>
	public static string NormalizeLogin(string login) => login.Trim().ToLowerInvariant();

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
		// No Include here — UserProvisioner.EnsureUserAsync calls this on EVERY authenticated
		// request (the Ping/Sync hot path), but BackendRoles is only needed in the branches below
		// that actually WRITE a declaration (essentially never after first sign-in). Re-load with
		// the Include only when a write is about to happen.
		User? row = await db.Users
			.FirstOrDefaultAsync(u => u.Login == normalized, ct).ConfigureAwait(false);
		if (row is not null)
		{
			if (row.Declared || declarationIfMissing is null)
				return (row.UserId, false);
			row = await db.Users.Include(u => u.BackendRoles)
				.FirstAsync(u => u.UserId == row.UserId, ct).ConfigureAwait(false);
			ToEntity(declarationIfMissing, row);
			await BumpStampAsync(db, ct).ConfigureAwait(false);
			return (row.UserId, true);
		}

		User created = new() { Login = normalized, UpdatedUtc = DateTime.UtcNow };
		if (declarationIfMissing is not null)
			ToEntity(declarationIfMissing, created);
		// DbSet.Add is synchronous and local (no I/O) — see UpsertAsync.
#pragma warning disable VSTHRD103
		db.Users.Add(created);
#pragma warning restore VSTHRD103
		try
		{
			// BumpStampAsync now bumps AND saves (DataChangeStamps.BumpAndSaveAsync), tolerating
			// a concurrent replica's first-ever bump of this area; when there is no declaration to
			// bump, save directly — either way this is the ONE SaveChangesAsync for this branch.
			if (declarationIfMissing is not null)
				await BumpStampAsync(db, ct).ConfigureAwait(false);
			else
				await db.SaveChangesAsync(ct).ConfigureAwait(false);
			return (created.UserId, declarationIfMissing is not null);
		}
		catch (DbUpdateException ex) when (DbExceptions.IsUniqueViolation(ex))
		{
			// A concurrent first-auth (another device, another replica) won the insert race.
			// Only the LOGIN index means that — the row is now there, so re-read the winner.
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

	/// <summary>Why a rename could not be applied — the caller phrases the message.</summary>
	public enum RenameOutcome
	{
		Renamed,

		/// <summary>No user has the old login.</summary>
		UnknownUser,

		/// <summary>The new login is already taken by another user.</summary>
		Collision,
	}

	/// <summary>
	///   Renames a user — a SINGLE-ROW UPDATE, which is the whole point of the surrogate key:
	///   sync state stays attached, encrypted local items stay readable (the AAD binds
	///   <c>UserId</c>), and the device keeps its folder registry and sync keys. The holder just
	///   updates the username on the phone.
	///   <para>
	///     Refuses a collision, case-folded like every other login comparison. The
	///     config-declared immutability guard lives in the CALLER, which is the layer that knows
	///     the configuration — see the CLI/admin surfaces.
	///   </para>
	/// </summary>
	public async Task<RenameOutcome> RenameAsync(string oldLogin, string newLogin, CancellationToken ct)
	{
		string from = NormalizeLogin(oldLogin);
		string to = NormalizeLogin(newLogin);
		await using SyncDbContext db = contextFactory.CreateDbContext();
		User? user = await db.Users.FirstOrDefaultAsync(u => u.Login == from, ct).ConfigureAwait(false);
		if (user is null)
			return RenameOutcome.UnknownUser;
		if (from == to)
			return RenameOutcome.Renamed;   // nothing to do; not an error
		if (await db.Users.AnyAsync(u => u.Login == to, ct).ConfigureAwait(false))
			return RenameOutcome.Collision;

		user.Login = to;
		user.UpdatedUtc = DateTime.UtcNow;
		try
		{
			await BumpStampAsync(db, ct).ConfigureAwait(false);
			return RenameOutcome.Renamed;
		}
		catch (DbUpdateException ex) when (DbExceptions.IsUniqueViolation(ex))
		{
			// A concurrent writer took the new login between our check and this save.
			return RenameOutcome.Collision;
		}
	}

	/// <summary>
	///   Deletes the USER — identity and all — cascading every per-user row the database owns.
	///   Destructive in a way removing a declaration is not: <c>LocalItem</c> content goes with
	///   it, and in a local-stores deployment that data exists nowhere else. Callers MUST count
	///   the impact and obtain confirmation first (<c>DeviceAdminService.CountDeletionImpactAsync</c>);
	///   the database cascades, the application is what refuses to issue it blind.
	/// </summary>
	public async Task<bool> DeleteUserAsync(string login, CancellationToken ct)
	{
		await using SyncDbContext db = contextFactory.CreateDbContext();
		string normalized = NormalizeLogin(login);
		User? user = await db.Users.FirstOrDefaultAsync(u => u.Login == normalized, ct).ConfigureAwait(false);
		if (user is null)
			return false;
		db.Users.Remove(user);
		await BumpStampAsync(db, ct).ConfigureAwait(false);
		return true;
	}

	/// <summary>Current change stamp of the "users" area, or null when nothing was ever written.</summary>
	public Task<Guid?> ReadStampAsync(CancellationToken ct) =>
		DataChangeStamps.ReadAsync(contextFactory, DataChangeAreas.Users, ct);

	/// <summary>
	///   All database DECLARATIONS keyed by login (case-insensitive, like config Users) —
	///   identity-only rows are not declarations and are skipped. A row whose settings blob no
	///   longer parses is surfaced with a warning and its settings dropped; the typed columns
	///   still apply, so one corrupt blob can no longer take a whole user's overrides down.
	/// </summary>
	public async Task<Dictionary<string, UserOptions>> LoadAllAsync(ILogger? logger, CancellationToken ct)
	{
		await using SyncDbContext db = contextFactory.CreateDbContext();
		// Ordered by UserId so "keeping the last" (below) is a DEFINED rule — the highest
		// UserId always wins — rather than depending on storage/enumeration order, which can differ
		// between providers and between replicas of the same database.
		List<User> entries = await db.Users.AsNoTracking().Include(u => u.BackendRoles)
			.Where(u => u.Declared)
			.OrderBy(u => u.UserId)
			.ToListAsync(ct).ConfigureAwait(false);

		Dictionary<string, UserOptions> result = new(StringComparer.OrdinalIgnoreCase);
		foreach (User entry in entries)
		{
			// The store writes Login case-folded so this can't happen through the app, but a
			// pre-fix pair, a restored dump or an out-of-band write can still leave two BINARY-distinct
			// rows that collapse here last-write-wins. Surface it — never silently drop a user's overrides.
			if (result.ContainsKey(entry.Login))
				logger?.LogWarning(
					"Multiple database account rows collapse to login {User} (case-insensitive); keeping the " +
					"one with the highest UserId ({UserId}) and ignoring an earlier duplicate — remove the " +
					"redundant row (`eas user` / the Users page)",
					entry.Login, entry.UserId);
			result[entry.Login] = FromEntity(entry, logger, out _);
		}

		return result;
	}

	public async Task<UserOptions?> GetAsync(string login, CancellationToken ct)
	{
		await using SyncDbContext db = contextFactory.CreateDbContext();
		// Login is stored case-folded (see NormalizeLogin), so an exact match on the normalized
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
			// Surface a row with an unparseable settings blob FLAGGED rather than omitting it
			// or throwing — `eas users` and the admin list must still render, marking the row to fix.
			UserOptions options = FromEntity(entry, null, out bool valid);
			result.Add((entry.Login, options, entry.UpdatedUtc, valid));
		}

		return result;
	}

	public async Task UpsertAsync(string login, UserOptions options, CancellationToken ct)
	{
		await using SyncDbContext db = contextFactory.CreateDbContext();
		// Match on the case-folded login so `eas user set Phone1` updates the existing `phone1`
		// row instead of inserting a second, colliding one (index seek, sees every casing).
		string normalized = NormalizeLogin(login);
		User? entry = await db.Users.Include(u => u.BackendRoles)
			.FirstOrDefaultAsync(u => u.Login == normalized, ct).ConfigureAwait(false);
		if (entry is null)
		{
			// Store the case-folded login so the raw unique index enforces case-folded uniqueness.
			// DbSet.Add is synchronous and local (no I/O) — AddAsync exists only to support
			// async value generators (e.g. HiLo/Cosmos), which this project doesn't use.
			entry = new User { Login = normalized, UpdatedUtc = DateTime.UtcNow };
			ToEntity(options, entry);
#pragma warning disable VSTHRD103
			db.Users.Add(entry);
#pragma warning restore VSTHRD103
			try
			{
				await BumpStampAsync(db, ct).ConfigureAwait(false);
				return;
			}
			catch (DbUpdateException ex) when (DbExceptions.IsUniqueViolation(ex))
			{
				// A concurrent identity insert (first-auth race) landed between our read and this
				// insert — re-read the winner and apply the declaration as an update. That is
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
	}

	/// <summary>
	///   Removes the database DECLARATION for a login — the entry falls back to configuration
	///   (or to plain pass-through), exactly as `eas user remove` always meant. The IDENTITY
	///   row and its <c>UserId</c> survive: sync state, encrypted local items and blocks all
	///   hang off the id, and removing a declaration must not destroy data. Deleting the user
	///   outright (cascade) is a separate, guarded operation requiring a confirmed round trip
	///   before the CLI or admin UI will issue it.
	/// </summary>
	public async Task<bool> DeleteAsync(string login, CancellationToken ct)
	{
		await using SyncDbContext db = contextFactory.CreateDbContext();
		// Exact match on the case-folded login (index seek, sees every casing).
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
	///   break authentication.
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

	/// <summary>
	///   Bumps the "users" area AND saves — the one SaveChangesAsync for whichever mutation the
	///   caller staged. A UserBackendRoles write bumps this SAME area rather than getting its own:
	///   a stamp belongs to a consumer's aggregate, and the resolver rebuilds the whole user
	///   snapshot on any bump. Uses <see cref="DataChangeStamps.BumpAndSaveAsync" /> rather than
	///   the racing <see cref="DataChangeStamps.BumpAsync" /> + a bare save, so two replicas' very
	///   first bump of this area resolves as an update instead of a raw PK violation.
	/// </summary>
	private static Task BumpStampAsync(SyncDbContext db, CancellationToken ct) =>
		DataChangeStamps.BumpAndSaveAsync(db, DataChangeAreas.Users, ct);
}

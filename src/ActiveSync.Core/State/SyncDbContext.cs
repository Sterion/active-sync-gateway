using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace ActiveSync.Core.State;

/// <summary>
///   Provider-neutral state context. Migrations are provider-specific (SQLite and PostgreSQL
///   emit different DDL), so each provider has its own concrete subclass with its own migration
///   set (<see cref="SqliteSyncDbContext" />, <see cref="NpgsqlSyncDbContext" />). The rest of the
///   app depends on this base type; DI supplies the right subclass.
/// </summary>
public abstract class SyncDbContext(DbContextOptions options) : DbContext(options)
{
	public DbSet<Device> Devices => Set<Device>();
	public DbSet<UserFolder> UserFolders => Set<UserFolder>();
	public DbSet<DeviceFolder> DeviceFolders => Set<DeviceFolder>();
	public DbSet<CollectionState> CollectionStates => Set<CollectionState>();
	public DbSet<SentCommandToken> SentCommandTokens => Set<SentCommandToken>();
	public DbSet<DavItem> DavItems => Set<DavItem>();
	public DbSet<LocalItem> LocalItems => Set<LocalItem>();
	public DbSet<LoginBlock> LoginBlocks => Set<LoginBlock>();
	public DbSet<User> Users => Set<User>();
	public DbSet<UserBackendRole> UserBackendRoles => Set<UserBackendRole>();
	public DbSet<GlobalSetting> GlobalSettings => Set<GlobalSetting>();
	public DbSet<DataChange> DataChanges => Set<DataChange>();
	public DbSet<LogEntry> LogEntries => Set<LogEntry>();
	public DbSet<ServerCertificate> ServerCertificates => Set<ServerCertificate>();
	public DbSet<DataProtectionKeyEntry> DataProtectionKeys => Set<DataProtectionKeyEntry>();
	public DbSet<OofSetting> OofSettings => Set<OofSetting>();
	public DbSet<SharedCalendarGrant> SharedCalendarGrants => Set<SharedCalendarGrant>();
	public DbSet<WebSessionRevocation> WebSessionRevocations => Set<WebSessionRevocation>();

	protected override void OnModelCreating(ModelBuilder modelBuilder)
	{
		modelBuilder.Entity<Device>(e =>
		{
			e.HasIndex(d => new { d.UserId, d.DeviceId }).IsUnique();
			e.HasOne(d => d.User).WithMany().HasForeignKey(d => d.UserId)
				.OnDelete(DeleteBehavior.Cascade);
			e.HasMany(d => d.Folders).WithOne(f => f.Device).HasForeignKey(f => f.DeviceKey)
				.OnDelete(DeleteBehavior.Cascade);
			e.HasMany(d => d.Collections).WithOne(c => c.Device).HasForeignKey(c => c.DeviceKey)
				.OnDelete(DeleteBehavior.Cascade);
			// Guards FolderSyncKey against pipelined FolderSyncs losing a bump (A6).
			e.Property(d => d.ConcurrencyToken).IsConcurrencyToken();
		});

		modelBuilder.Entity<UserFolder>(e =>
		{
			e.HasIndex(f => new { f.UserId, f.BackendKey }).IsUnique();
			e.HasOne(f => f.User).WithMany().HasForeignKey(f => f.UserId)
				.OnDelete(DeleteBehavior.Cascade);
			e.HasMany(f => f.DavItems).WithOne(i => i.Folder).HasForeignKey(i => i.UserFolderKey)
				.OnDelete(DeleteBehavior.Cascade);
		});

		// One identity per row: the natural key IS the primary key (the surrogate Id these two
		// carried was never looked up). No index is added — the PK replaces the unique one.
		modelBuilder.Entity<DeviceFolder>(e =>
			e.HasKey(f => new { f.DeviceKey, f.ServerId }));

		modelBuilder.Entity<CollectionState>(e =>
		{
			e.HasKey(c => new { c.DeviceKey, c.CollectionId });
			e.Property(c => c.ConcurrencyToken).IsConcurrencyToken();
		});

		// F2: one claim per (device, collection, attempt, key); the unique index is what makes a
		// concurrent double-claim race safe (the loser's insert fails and re-reads the winner).
		// This table KEEPS its surrogate key (see SentCommandToken) but gains the FK it lacked, so
		// deleting a device no longer orphans its claims.
		modelBuilder.Entity<SentCommandToken>(e =>
		{
			e.HasIndex(t => new { t.DeviceKey, t.CollectionId, t.SyncKeyAtClaim, t.Key }).IsUnique();
			e.HasOne(t => t.Device).WithMany().HasForeignKey(t => t.DeviceKey)
				.OnDelete(DeleteBehavior.Cascade);
		});

		modelBuilder.Entity<DavItem>(e =>
			e.HasIndex(i => new { i.UserFolderKey, i.Href }).IsUnique());

		modelBuilder.Entity<LocalItem>(e =>
		{
			e.HasIndex(i => new { i.UserId, i.Collection });
			e.HasOne(i => i.User).WithMany().HasForeignKey(i => i.UserId)
				.OnDelete(DeleteBehavior.Cascade);
			e.Property(i => i.ConcurrencyToken).IsConcurrencyToken();
		});

		// Per-device only: the device IS the identity, so "one block per device" is a constraint.
		// No UserId — a device already reaches its user through this FK.
		modelBuilder.Entity<LoginBlock>(e =>
		{
			e.HasKey(b => b.DeviceKey);
			e.HasOne(b => b.Device).WithMany().HasForeignKey(b => b.DeviceKey)
				.OnDelete(DeleteBehavior.Cascade);
		});

		modelBuilder.Entity<User>(e =>
		{
			e.HasIndex(u => u.Login).IsUnique();
			// Two users bound to one identity-provider subject is an account-takeover vector, so
			// the binding is enforced by the database rather than by whoever writes it. No filter
			// needed — both providers treat NULLs as distinct in a unique index, so the (many)
			// unbound users don't collide on null; HasFilter(null) only suppresses the SQL Server
			// default (an implicit "WHERE x IS NOT NULL"), which neither provider here applies.
			e.HasIndex(u => u.OidcSubject).IsUnique().HasFilter(null);
			e.HasMany(u => u.BackendRoles).WithOne(r => r.User).HasForeignKey(r => r.UserId)
				.OnDelete(DeleteBehavior.Cascade);
		});

		// One override row per (user, role) — enforced by identity.
		modelBuilder.Entity<UserBackendRole>(e =>
			e.HasKey(r => new { r.UserId, r.Role }));

		// The configuration path IS the identity (one identity per row).
		modelBuilder.Entity<GlobalSetting>(e => e.HasKey(s => s.Key));

		// The area string IS the identity — one row per watched area. No surrogate id, so the
		// "one row per area" rule is a constraint rather than a convention, and the primary-key
		// conflict is what serializes two replicas racing to create the same area's first row.
		modelBuilder.Entity<DataChange>(e => e.HasKey(c => c.Key));

		// Indexed by time for the `eas logs` window queries and the retention sweep.
		modelBuilder.Entity<LogEntry>(e =>
			e.HasIndex(l => l.TimestampUtc));

		// Single well-known row (Id=1) — same explicit-key idiom as AccountsStamp, and the
		// primary-key conflict is what serializes concurrent first-boot generation races.
		// ConcurrencyToken (K6) additionally serializes the *replace an unreadable/expiring row*
		// race, which the primary-key conflict alone doesn't cover (that path is an UPDATE).
		modelBuilder.Entity<ServerCertificate>(e =>
		{
			e.Property(c => c.Id).ValueGeneratedNever();
			e.Property(c => c.ConcurrencyToken).IsConcurrencyToken();
		});

		// One Oof row per user — enforced by identity rather than a unique index beside an
		// unused surrogate.
		modelBuilder.Entity<OofSetting>(e =>
		{
			e.HasKey(o => o.UserId);
			e.Property(o => o.UserId).ValueGeneratedNever();
			e.HasOne<User>().WithMany().HasForeignKey(o => o.UserId)
				.OnDelete(DeleteBehavior.Cascade);
		});

		modelBuilder.Entity<SharedCalendarGrant>(e =>
		{
			e.HasKey(g => new { g.UserId, g.CollectionHref });
			e.HasOne(g => g.User).WithMany().HasForeignKey(g => g.UserId)
				.OnDelete(DeleteBehavior.Cascade);
		});

		// One row per user — enforced by identity, which is what keeps the revocation a REWRITE
		// (never an append; an appended row would leave the older, weaker cut-off in play).
		modelBuilder.Entity<WebSessionRevocation>(e =>
		{
			e.HasKey(r => r.UserId);
			e.Property(r => r.UserId).ValueGeneratedNever();
			e.HasOne<User>().WithMany().HasForeignKey(r => r.UserId)
				.OnDelete(DeleteBehavior.Cascade);
		});
	}

	// Re-stamp the concurrency token on every insert/update so a lost update (two writers
	// off the same snapshot) turns into a DbUpdateConcurrencyException instead of silently
	// overwriting — the token EF compares in the UPDATE's WHERE is the value originally read.
	// Overridden on the two-argument forms: those are EF's real interception point, through
	// which the parameterless overloads and every execution-strategy retry funnel, so stamping
	// here can never be bypassed (A5).
	public override int SaveChanges(bool acceptAllChangesOnSuccess)
	{
		StampConcurrencyTokens();
		return base.SaveChanges(acceptAllChangesOnSuccess);
	}

	public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
	{
		StampConcurrencyTokens();
		return base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);
	}

	private void StampConcurrencyTokens()
	{
		foreach (EntityEntry entry in ChangeTracker.Entries())
			if (entry.State is EntityState.Added or EntityState.Modified &&
			    entry.Entity is CollectionState or LocalItem or Device or ServerCertificate)
				entry.CurrentValues["ConcurrencyToken"] = Guid.NewGuid();
	}
}

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
	public DbSet<DavItem> DavItems => Set<DavItem>();
	public DbSet<LocalItem> LocalItems => Set<LocalItem>();
	public DbSet<LoginBlock> LoginBlocks => Set<LoginBlock>();
	public DbSet<AccountEntry> AccountEntries => Set<AccountEntry>();
	public DbSet<AccountsStamp> AccountsStamps => Set<AccountsStamp>();
	public DbSet<GlobalSetting> GlobalSettings => Set<GlobalSetting>();
	public DbSet<SettingsStamp> SettingsStamps => Set<SettingsStamp>();
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
			e.HasIndex(d => new { d.UserName, d.DeviceId }).IsUnique();
			e.HasMany(d => d.Folders).WithOne(f => f.Device).HasForeignKey(f => f.DeviceKey)
				.OnDelete(DeleteBehavior.Cascade);
			e.HasMany(d => d.Collections).WithOne(c => c.Device).HasForeignKey(c => c.DeviceKey)
				.OnDelete(DeleteBehavior.Cascade);
			// Guards FolderSyncKey against pipelined FolderSyncs losing a bump (A6).
			e.Property(d => d.ConcurrencyToken).IsConcurrencyToken();
		});

		modelBuilder.Entity<UserFolder>(e =>
		{
			e.HasIndex(f => new { f.UserName, f.BackendKey }).IsUnique();
			e.HasMany(f => f.DavItems).WithOne(i => i.Folder).HasForeignKey(i => i.UserFolderKey)
				.OnDelete(DeleteBehavior.Cascade);
		});

		modelBuilder.Entity<DeviceFolder>(e =>
			e.HasIndex(f => new { f.DeviceKey, f.ServerId }).IsUnique());

		modelBuilder.Entity<CollectionState>(e =>
		{
			e.HasIndex(c => new { c.DeviceKey, c.CollectionId }).IsUnique();
			e.Property(c => c.ConcurrencyToken).IsConcurrencyToken();
		});

		modelBuilder.Entity<DavItem>(e =>
			e.HasIndex(i => new { i.UserFolderKey, i.Href }).IsUnique());

		modelBuilder.Entity<LocalItem>(e =>
		{
			e.HasIndex(i => new { i.UserName, i.Collection });
			e.Property(i => i.ConcurrencyToken).IsConcurrencyToken();
		});

		modelBuilder.Entity<LoginBlock>(e =>
			e.HasIndex(b => new { b.UserName, b.DeviceId }).IsUnique());

		modelBuilder.Entity<AccountEntry>(e =>
			e.HasIndex(a => a.UserName).IsUnique());

		// Deliberately no identity column: the CLI writes Id=1 explicitly so the stamp
		// stays a single well-known row on both providers.
		modelBuilder.Entity<AccountsStamp>(e =>
			e.Property(s => s.Id).ValueGeneratedNever());

		modelBuilder.Entity<GlobalSetting>(e =>
			e.HasIndex(s => s.Key).IsUnique());

		// Single well-known row (Id=1), same explicit-key idiom as AccountsStamp.
		modelBuilder.Entity<SettingsStamp>(e =>
			e.Property(s => s.Id).ValueGeneratedNever());

		// Indexed by time for the `eas logs` window queries and the retention sweep.
		modelBuilder.Entity<LogEntry>(e =>
			e.HasIndex(l => l.TimestampUtc));

		// Single well-known row (Id=1) — same explicit-key idiom as AccountsStamp, and the
		// primary-key conflict is what serializes concurrent first-boot generation races.
		modelBuilder.Entity<ServerCertificate>(e =>
			e.Property(c => c.Id).ValueGeneratedNever());

		modelBuilder.Entity<OofSetting>(e =>
			e.HasIndex(o => o.UserName).IsUnique());

		modelBuilder.Entity<SharedCalendarGrant>(e =>
			e.HasIndex(g => new { g.UserName, g.CollectionHref }).IsUnique());

		// One row per login; the unique index is what keeps the revocation a rewrite.
		modelBuilder.Entity<WebSessionRevocation>(e =>
			e.HasIndex(r => r.UserName).IsUnique());
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
			    entry.Entity is CollectionState or LocalItem or Device)
				entry.CurrentValues["ConcurrencyToken"] = Guid.NewGuid();
	}
}

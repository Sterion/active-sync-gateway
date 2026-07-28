using ActiveSync.Core.Accounts;
using ActiveSync.Core.Administration;
using ActiveSync.Core.Options;
using ActiveSync.Core.Security;
using ActiveSync.Core.State;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ActiveSync.Core.Tests;

/// <summary>
///   Item 6b of docs/design/db-restructure.md — the user lifecycle the surrogate key makes
///   possible: renaming a login without losing anything, and deleting a user without losing
///   anything by accident.
/// </summary>
public sealed class UserLifecycleTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly TestContextFactory _factory;
	private readonly UserStore _store;

	public UserLifecycleTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();
		_factory = new TestContextFactory(_connection);
		using SyncDbContext db = _factory.CreateDbContext();
		db.Database.EnsureCreated();
		db.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON;");
		_store = new UserStore(_factory);
	}

	public void Dispose() => _connection.Dispose();

	private static byte[] Key()
	{
		byte[] key = new byte[32];
		Array.Fill(key, (byte)9);
		return key;
	}

	/// <summary>A user with a device, a folder, an encrypted note and a share grant.</summary>
	private async Task<int> SeedAsync(string login, int contacts = 2, int notes = 1)
	{
		(int userId, _) = await _store.GetOrCreateUserAsync(login, new UserOptions(), CancellationToken.None);
		using LocalContentProtector protector = LocalContentProtector.CreateProtected(Key());
		await using SyncDbContext db = _factory.CreateDbContext();
		Device device = new()
		{
			UserId = userId, DeviceId = $"{login}-phone", DeviceType = "T",
			CreatedUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow,
		};
		UserFolder folder = new()
		{
			UserId = userId, BackendKey = "local:notes", DisplayName = "Notes", EasClass = "Notes",
		};
#pragma warning disable VSTHRD103
		db.AddRange(device, folder);
		db.SaveChanges();
		for (int i = 0; i < contacts; i++)
			db.LocalItems.Add(new LocalItem
			{
				UserId = userId, Collection = "contacts", Uid = $"c-{i}",
				Content = protector.Protect($"contact {i}", userId, "contacts"),
				Version = 1, LastModifiedUtc = DateTime.UtcNow,
			});
		for (int i = 0; i < notes; i++)
			db.LocalItems.Add(new LocalItem
			{
				UserId = userId, Collection = "notes", Uid = $"n-{i}",
				Content = protector.Protect($"note {i}", userId, "notes"),
				Version = 1, LastModifiedUtc = DateTime.UtcNow,
			});
		db.SharedCalendarGrants.Add(new SharedCalendarGrant
		{
			UserId = userId, CollectionHref = "/dav/shared/", CreatedUtc = DateTime.UtcNow,
		});
#pragma warning restore VSTHRD103
		await db.SaveChangesAsync();
		return userId;
	}

	// ---- rename ----

	[Fact]
	public async Task Rename_KeepsTheId_TheState_AndTheDecryptableContent()
	{
		int userId = await SeedAsync("anna@old.example");

		Assert.Equal(UserStore.RenameOutcome.Renamed,
			await _store.RenameAsync("anna@old.example", "anna@new.example", CancellationToken.None));

		Assert.Null(await _store.FindUserIdAsync("anna@old.example", CancellationToken.None));
		Assert.Equal(userId, await _store.FindUserIdAsync("anna@new.example", CancellationToken.None));

		using LocalContentProtector protector = LocalContentProtector.CreateProtected(Key());
		await using SyncDbContext db = _factory.CreateDbContext();
		Assert.Single(db.Devices.Where(d => d.UserId == userId));
		Assert.Single(db.UserFolders.Where(f => f.UserId == userId));
		Assert.Single(db.SharedCalendarGrants.Where(g => g.UserId == userId));
		LocalItem note = await db.LocalItems.AsNoTracking().FirstAsync(i => i.Collection == "notes");
		Assert.Equal("note 0", protector.Unprotect(note.Content, userId, "notes"));
	}

	[Fact]
	public async Task Rename_IsCaseFolded_AndRefusesACollision()
	{
		await SeedAsync("anna", contacts: 0, notes: 0);
		await SeedAsync("bob", contacts: 0, notes: 0);

		Assert.Equal(UserStore.RenameOutcome.Collision,
			await _store.RenameAsync("anna", "bob", CancellationToken.None));
		// Case-folded: "BOB" is the same login as "bob".
		Assert.Equal(UserStore.RenameOutcome.Collision,
			await _store.RenameAsync("anna", "BOB", CancellationToken.None));
		// ...and anna is untouched.
		Assert.NotNull(await _store.FindUserIdAsync("anna", CancellationToken.None));
	}

	[Fact]
	public async Task Rename_StoresTheNewLoginCaseFolded()
	{
		await SeedAsync("anna", contacts: 0, notes: 0);

		Assert.Equal(UserStore.RenameOutcome.Renamed,
			await _store.RenameAsync("anna", "Anna@New.Example", CancellationToken.None));

		await using SyncDbContext db = _factory.CreateDbContext();
		Assert.Equal("anna@new.example", await db.Users.Select(u => u.Login).SingleAsync());
		// ...and it is findable under any casing.
		Assert.NotNull(await _store.FindUserIdAsync("ANNA@NEW.EXAMPLE", CancellationToken.None));
	}

	[Fact]
	public async Task Rename_OfAnUnknownUser_SaysSo()
	{
		Assert.Equal(UserStore.RenameOutcome.UnknownUser,
			await _store.RenameAsync("ghost", "somebody", CancellationToken.None));
	}

	[Fact]
	public async Task Rename_BumpsTheUsersStamp_SoReplicasPickItUp()
	{
		await SeedAsync("anna", contacts: 0, notes: 0);
		Guid? before = await _store.ReadStampAsync(CancellationToken.None);

		await _store.RenameAsync("anna", "anna2", CancellationToken.None);

		Assert.NotEqual(before, await _store.ReadStampAsync(CancellationToken.None));
	}

	// ---- delete ----

	[Fact]
	public async Task CountDeletionImpact_SeparatesContentFromSyncState()
	{
		await SeedAsync("anna", contacts: 3, notes: 2);
		DeviceAdminService devices = new(_factory, _store);

		DeviceAdminService.DeletionImpact impact =
			await devices.CountDeletionImpactAsync("anna", null, CancellationToken.None);

		Assert.True(impact.DestroysContent);
		Assert.Equal(3, impact.Content.Single(c => c.Table == "contacts").Count);
		Assert.Equal(2, impact.Content.Single(c => c.Table == "notes").Count);
		Assert.Contains("3 contacts", impact.DescribeContent());
		Assert.Contains("2 notes", impact.DescribeContent());
		// Sync state is counted apart — it rebuilds on the next sync and does not warrant a
		// typed echo on its own.
		Assert.Equal(1, impact.SyncState.Single(c => c.Table == "devices").Count);
		Assert.Equal(1, impact.SyncState.Single(c => c.Table == "shared-calendar grants").Count);
	}

	[Fact]
	public async Task CountDeletionImpact_IncludesOofSettingsAndWebSessionRevocations()
	{
		// B18: OofSetting and WebSessionRevocation both carry a UserId FK with cascade delete —
		// the same shape as the other rows already counted (devices, folders, collection states,
		// shared-calendar grants, device blocks) — but were silently absent from the operator's
		// "what will be lost" summary, understating the impact.
		int userId = await SeedAsync("anna", contacts: 0, notes: 0);
		await using (SyncDbContext db = _factory.CreateDbContext())
		{
#pragma warning disable VSTHRD103
			db.OofSettings.Add(new OofSetting { UserId = userId, State = 1, Message = "away", UpdatedUtc = DateTime.UtcNow });
			db.WebSessionRevocations.Add(new WebSessionRevocation { UserId = userId, ValidAfterUtc = DateTime.UtcNow });
#pragma warning restore VSTHRD103
			await db.SaveChangesAsync();
		}

		DeviceAdminService devices = new(_factory, _store);
		DeviceAdminService.DeletionImpact impact =
			await devices.CountDeletionImpactAsync("anna", null, CancellationToken.None);

		Assert.Equal(1, impact.SyncState.Single(c => c.Table == "oof settings").Count);
		Assert.Equal(1, impact.SyncState.Single(c => c.Table == "web session revocations").Count);
	}

	[Fact]
	public async Task CountDeletionImpact_OfAContentlessUser_SaysNothingIsAtRisk()
	{
		await SeedAsync("bob", contacts: 0, notes: 0);
		DeviceAdminService devices = new(_factory, _store);

		DeviceAdminService.DeletionImpact impact =
			await devices.CountDeletionImpactAsync("bob", null, CancellationToken.None);

		Assert.False(impact.DestroysContent);
		Assert.Equal("", impact.DescribeContent());
		Assert.Equal(1, impact.SyncState.Single(c => c.Table == "devices").Count);
	}

	[Fact]
	public async Task CountDeletionImpact_CountsNothing_AndDoesNotThrow_ForAnUnknownUser()
	{
		DeviceAdminService devices = new(_factory, _store);

		DeviceAdminService.DeletionImpact impact =
			await devices.CountDeletionImpactAsync("ghost", null, CancellationToken.None);

		Assert.False(impact.DestroysContent);
		Assert.Empty(impact.Content);
	}

	[Fact]
	public async Task CountDeletionImpact_ScopedToADevice_ExcludesUserContent()
	{
		await SeedAsync("anna", contacts: 3, notes: 2);
		DeviceAdminService devices = new(_factory, _store);

		DeviceAdminService.DeletionImpact impact =
			await devices.CountDeletionImpactAsync("anna", "anna-phone", CancellationToken.None);

		// Local items belong to the USER, so deleting one device never destroys them.
		Assert.False(impact.DestroysContent);
		Assert.Equal(1, impact.SyncState.Single(c => c.Table == "devices").Count);
	}

	[Fact]
	public async Task DeleteUser_RemovesTheIdentityAndEverythingItOwns()
	{
		await SeedAsync("anna", contacts: 3, notes: 2);
		await SeedAsync("bob", contacts: 1, notes: 1);

		Assert.True(await _store.DeleteUserAsync("anna", CancellationToken.None));

		await using SyncDbContext db = _factory.CreateDbContext();
		Assert.Null(await _store.FindUserIdAsync("anna", CancellationToken.None));
		Assert.Empty(db.Users.Where(u => u.Login == "anna"));
		// ...and bob is untouched.
		int bobId = (await _store.FindUserIdAsync("bob", CancellationToken.None))!.Value;
		Assert.Equal(2, await db.LocalItems.CountAsync(i => i.UserId == bobId));
		Assert.Equal(1, await db.Devices.CountAsync(d => d.UserId == bobId));
	}

	[Fact]
	public async Task DeleteUser_IsDistinctFromRemovingTheDeclaration()
	{
		// `user remove` drops the DECLARATION and keeps the identity (so nothing is destroyed);
		// `user delete` removes the identity and cascades. Two different operations on purpose.
		int userId = await SeedAsync("anna", contacts: 2, notes: 0);

		Assert.True(await _store.DeleteAsync("anna", CancellationToken.None));
		Assert.Equal(userId, await _store.FindUserIdAsync("anna", CancellationToken.None));
		await using (SyncDbContext afterRemove = _factory.CreateDbContext())
			Assert.Equal(2, await afterRemove.LocalItems.CountAsync(i => i.UserId == userId));

		Assert.True(await _store.DeleteUserAsync("anna", CancellationToken.None));
		Assert.Null(await _store.FindUserIdAsync("anna", CancellationToken.None));
		await using SyncDbContext afterDelete = _factory.CreateDbContext();
		Assert.Empty(afterDelete.LocalItems);
	}

	[Fact]
	public async Task DeleteUser_OfAnUnknownUser_ReportsIt()
	{
		Assert.False(await _store.DeleteUserAsync("ghost", CancellationToken.None));
	}

	private sealed class TestContextFactory(SqliteConnection connection) : ISyncDbContextFactory
	{
		public SyncDbContext CreateDbContext()
		{
			DbContextOptions<SqliteSyncDbContext> options = new DbContextOptionsBuilder<SqliteSyncDbContext>()
				.UseSqlite(connection)
				.Options;
			return new SqliteSyncDbContext(options);
		}
	}
}

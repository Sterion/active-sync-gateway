using ActiveSync.Core.Accounts;
using ActiveSync.Core.Options;
using ActiveSync.Core.State;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ActiveSync.Core.Tests;

/// <summary>
///   Item 3b of docs/design/db-restructure.md: the soft links became real foreign keys with
///   cascade delete. Two properties are load-bearing — deleting a user removes EXACTLY its own
///   rows (and nothing of anyone else's), and deleting a device no longer orphans the
///   <see cref="SentCommandToken" /> claims that used to have no FK at all.
/// </summary>
public sealed class CascadeDeleteTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly TestContextFactory _factory;
	private readonly UserStore _users;

	public CascadeDeleteTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();
		_factory = new TestContextFactory(_connection);
		using SyncDbContext db = _factory.CreateDbContext();
		db.Database.EnsureCreated();
		// SQLite enforces foreign keys only when asked; the production connection string does
		// this via the pragma interceptor, so the test must match it to exercise real cascades.
		db.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON;");
		_users = new UserStore(_factory);
	}

	public void Dispose() => _connection.Dispose();

	/// <summary>Seeds one fully-populated user: declaration, role, device + its state, folder, item, grant, oof.</summary>
	private async Task<int> SeedUserAsync(string login)
	{
		await _users.UpsertAsync(login, new UserOptions
		{
			MailAddress = $"{login}@example.com",
			Backends = new Dictionary<string, BackendRoleOverride> { ["MailStore"] = new() { UserName = login } },
		}, CancellationToken.None);
		int userId = (await _users.FindUserIdAsync(login, CancellationToken.None))!.Value;

		await using SyncDbContext db = _factory.CreateDbContext();
		Device device = new()
		{
			UserId = userId, DeviceId = $"{login}-phone", DeviceType = "T",
			CreatedUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow,
		};
		UserFolder folder = new()
		{
			UserId = userId, BackendKey = "imap:INBOX", DisplayName = "Inbox", EasClass = "Email",
		};
#pragma warning disable VSTHRD103
		db.AddRange(device, folder);
		db.SaveChanges();

		db.AddRange(
			new DeviceFolder { DeviceKey = device.Id, ServerId = folder.Id.ToString(), DisplayName = "Inbox", Type = 2 },
			new CollectionState { DeviceKey = device.Id, CollectionId = folder.Id.ToString(), SyncKey = 1 },
			new SentCommandToken
			{
				DeviceKey = device.Id, CollectionId = folder.Id.ToString(), SyncKeyAtClaim = 1,
				Key = "add:c1", CreatedUtc = DateTime.UtcNow, Completed = true,
			},
			new LoginBlock { DeviceKey = device.Id, CreatedUtc = DateTime.UtcNow },
			new DavItem { UserFolderKey = folder.Id, Href = "/dav/item-1.ics" },
			new LocalItem
			{
				UserId = userId, Collection = "notes", Uid = "n-1", Content = "note",
				Version = 1, LastModifiedUtc = DateTime.UtcNow,
			},
			new SharedCalendarGrant
			{
				UserId = userId, CollectionHref = "/dav/shared/", CreatedUtc = DateTime.UtcNow,
			},
			new OofSetting { UserId = userId, State = 1, Message = "away" },
			new WebSessionRevocation { UserId = userId, ValidAfterUtc = DateTime.UtcNow });
#pragma warning restore VSTHRD103
		await db.SaveChangesAsync();
		return userId;
	}

	[Fact]
	public async Task DeletingAUser_RemovesExactlyItsOwnRows_AndNobodyElses()
	{
		int annaId = await SeedUserAsync("anna");
		int bobId = await SeedUserAsync("bob");

		await using (SyncDbContext db = _factory.CreateDbContext())
		{
			db.Users.Remove(await db.Users.SingleAsync(u => u.UserId == annaId));
			await db.SaveChangesAsync();
		}

		await using SyncDbContext verify = _factory.CreateDbContext();
		// Anna is gone, root and branch — including the transitively-scoped rows that reach her
		// only through Device (DeviceFolder/CollectionState/SentCommandToken/LoginBlock) and
		// through UserFolder (DavItem).
		Assert.Empty(verify.Users.Where(u => u.UserId == annaId));
		Assert.Empty(verify.UserBackendRoles.Where(r => r.UserId == annaId));
		Assert.Empty(verify.Devices.Where(d => d.UserId == annaId));
		Assert.Empty(verify.UserFolders.Where(f => f.UserId == annaId));
		Assert.Empty(verify.LocalItems.Where(i => i.UserId == annaId));
		Assert.Empty(verify.SharedCalendarGrants.Where(g => g.UserId == annaId));
		Assert.Empty(verify.OofSettings.Where(o => o.UserId == annaId));
		Assert.Empty(verify.WebSessionRevocations.Where(r => r.UserId == annaId));
		Assert.Empty(verify.DeviceFolders.Where(f => f.Device.UserId == annaId));
		Assert.Empty(verify.CollectionStates.Where(c => c.Device.UserId == annaId));
		Assert.Empty(verify.SentCommandTokens.Where(t => t.Device.UserId == annaId));
		Assert.Empty(verify.LoginBlocks.Where(b => b.Device.UserId == annaId));
		Assert.Empty(verify.DavItems.Where(i => i.Folder.UserId == annaId));

		// Bob is untouched — every one of his rows survives.
		Assert.Single(verify.Users.Where(u => u.UserId == bobId));
		Assert.Single(verify.UserBackendRoles.Where(r => r.UserId == bobId));
		Assert.Single(verify.Devices.Where(d => d.UserId == bobId));
		Assert.Single(verify.UserFolders.Where(f => f.UserId == bobId));
		Assert.Single(verify.LocalItems.Where(i => i.UserId == bobId));
		Assert.Single(verify.SharedCalendarGrants.Where(g => g.UserId == bobId));
		Assert.Single(verify.OofSettings.Where(o => o.UserId == bobId));
		Assert.Single(verify.WebSessionRevocations.Where(r => r.UserId == bobId));
		Assert.Single(verify.DeviceFolders.Where(f => f.Device.UserId == bobId));
		Assert.Single(verify.CollectionStates.Where(c => c.Device.UserId == bobId));
		Assert.Single(verify.SentCommandTokens.Where(t => t.Device.UserId == bobId));
		Assert.Single(verify.LoginBlocks.Where(b => b.Device.UserId == bobId));
		Assert.Single(verify.DavItems.Where(i => i.Folder.UserId == bobId));
	}

	[Fact]
	public async Task DeletingADevice_NoLongerOrphansItsSendClaims()
	{
		// SentCommandToken.DeviceKey held a Device.Id with NO constraint behind it, so deleting a
		// device left its claims behind forever. It is a real FK now.
		int userId = await SeedUserAsync("anna");

		await using (SyncDbContext db = _factory.CreateDbContext())
		{
			Device device = await db.Devices.SingleAsync(d => d.UserId == userId);
			db.Devices.Remove(device);
			await db.SaveChangesAsync();
		}

		await using SyncDbContext verify = _factory.CreateDbContext();
		Assert.Empty(verify.SentCommandTokens);
		Assert.Empty(verify.LoginBlocks);
		Assert.Empty(verify.DeviceFolders);
		Assert.Empty(verify.CollectionStates);
		// The user and their non-device state survive — a device delete is not a user delete.
		Assert.Single(verify.Users.Where(u => u.UserId == userId));
		Assert.Single(verify.UserFolders.Where(f => f.UserId == userId));
		Assert.Single(verify.LocalItems.Where(i => i.UserId == userId));
	}

	[Fact]
	public async Task NaturalKeys_RejectADuplicate_RatherThanAllowingTwoRowsForOneThing()
	{
		// The point of promoting the natural keys: "one Oof per user", "one block per device",
		// "one revocation per user" are CONSTRAINTS now, not conventions riding a unique index
		// beside an unused surrogate.
		int userId = await SeedUserAsync("anna");
		int deviceKey;
		await using (SyncDbContext db = _factory.CreateDbContext())
			deviceKey = await db.Devices.Where(d => d.UserId == userId).Select(d => d.Id).SingleAsync();

		await Assert.ThrowsAnyAsync<DbUpdateException>(async () =>
		{
			await using SyncDbContext db = _factory.CreateDbContext();
#pragma warning disable VSTHRD103
			db.OofSettings.Add(new OofSetting { UserId = userId, State = 0, Message = "second" });
#pragma warning restore VSTHRD103
			await db.SaveChangesAsync();
		});

		await Assert.ThrowsAnyAsync<DbUpdateException>(async () =>
		{
			await using SyncDbContext db = _factory.CreateDbContext();
#pragma warning disable VSTHRD103
			db.LoginBlocks.Add(new LoginBlock { DeviceKey = deviceKey, CreatedUtc = DateTime.UtcNow });
#pragma warning restore VSTHRD103
			await db.SaveChangesAsync();
		});

		await Assert.ThrowsAnyAsync<DbUpdateException>(async () =>
		{
			await using SyncDbContext db = _factory.CreateDbContext();
#pragma warning disable VSTHRD103
			db.WebSessionRevocations.Add(
				new WebSessionRevocation { UserId = userId, ValidAfterUtc = DateTime.UtcNow });
#pragma warning restore VSTHRD103
			await db.SaveChangesAsync();
		});
	}

	[Fact]
	public async Task ABlockCannotOutliveOrPrecedeItsDevice()
	{
		// The FK is what makes "block" mean "this partnership": there is no way to write one for
		// a device that does not exist.
		int userId = await SeedUserAsync("anna");
		await using SyncDbContext db = _factory.CreateDbContext();
		int missingDeviceKey = await db.Devices.MaxAsync(d => d.Id) + 1000;
#pragma warning disable VSTHRD103
		db.LoginBlocks.Add(new LoginBlock { DeviceKey = missingDeviceKey, CreatedUtc = DateTime.UtcNow });
#pragma warning restore VSTHRD103

		await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
		Assert.True(userId > 0);
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

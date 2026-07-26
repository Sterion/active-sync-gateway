using ActiveSync.Core.Accounts;
using ActiveSync.Core.Security;
using ActiveSync.Core.State;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ActiveSync.Core.Tests;

/// <summary>
///   The item-2 acceptance tests of docs/design/db-restructure.md: <c>UserId</c> is THE
///   identity — a login rename is a single-row update that leaves sync state attached and
///   encrypted local content decryptable, and the id column can never recycle a value
///   (security-critical: a reused id would let a new user decrypt a dead user's rows).
/// </summary>
public sealed class UserIdentityTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly TestContextFactory _factory;

	public UserIdentityTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();
		_factory = new TestContextFactory(_connection);
		using SyncDbContext db = _factory.CreateDbContext();
		db.Database.EnsureCreated();
	}

	public void Dispose() => _connection.Dispose();

	private static byte[] Key()
	{
		byte[] key = new byte[32];
		Array.Fill(key, (byte)7);
		return key;
	}

	[Fact]
	public async Task Rename_IsASingleRowUpdate_SyncStateSurvives_AndSealedContentStillDecrypts()
	{
		// Before the rename: a user with a device partnership, a folder, and an encrypted
		// local item — the exact state the old login-keyed model would orphan/brick.
		UserStore store = new(_factory);
		(int userId, _) = await store.GetOrCreateUserAsync("anna@old.example", null, CancellationToken.None);
		using LocalContentProtector protector = LocalContentProtector.CreateProtected(Key());
		const string plaintext = "BEGIN:VCARD\r\nFN:Anna\r\nEND:VCARD\r\n";
		int deviceId, itemId;
		await using (SyncDbContext db = _factory.CreateDbContext())
		{
			Device device = new()
			{
				UserId = userId, DeviceId = "PHONE1", DeviceType = "Test",
				CreatedUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow,
			};
			UserFolder folder = new()
			{
				UserId = userId, BackendKey = "local:contacts", DisplayName = "Contacts", EasClass = "Contacts",
			};
			LocalItem item = new()
			{
				UserId = userId, Collection = "contacts", Uid = "c1", Version = 1,
				Content = protector.Protect(plaintext, userId, "contacts"),
				LastModifiedUtc = DateTime.UtcNow,
			};
			// DbSet.Add/AddRange are synchronous and local (no I/O) — the async variants exist
			// only for async value generators, which this project doesn't use.
#pragma warning disable VSTHRD103
			db.AddRange(device, folder, item);
#pragma warning restore VSTHRD103
			await db.SaveChangesAsync();
			deviceId = device.Id;
			itemId = item.Id;
		}

		// THE RENAME — one row, one column. No data movement anywhere else.
		await using (SyncDbContext db = _factory.CreateDbContext())
		{
			User user = await db.Users.SingleAsync(u => u.UserId == userId);
			user.Login = "anna@new.example";
			await db.SaveChangesAsync();
		}

		// The login moved...
		Assert.Null(await store.FindUserIdAsync("anna@old.example", CancellationToken.None));
		Assert.Equal(userId, await store.FindUserIdAsync("anna@new.example", CancellationToken.None));

		// ...and every id-keyed row survived: the device keeps its partnership and folder
		// registry, and the sealed content still decrypts (the AAD binds UserId, not the login).
		await using (SyncDbContext verify = _factory.CreateDbContext())
		{
			Device device = await verify.Devices.AsNoTracking().SingleAsync(d => d.Id == deviceId);
			Assert.Equal(userId, device.UserId);
			Assert.Single(verify.UserFolders.Where(f => f.UserId == userId));
			LocalItem item = await verify.LocalItems.AsNoTracking().SingleAsync(i => i.Id == itemId);
			Assert.Equal(plaintext, protector.Unprotect(item.Content, userId, "contacts"));
		}
	}

	[Fact]
	public async Task DeletedUserId_IsNeverReused_OnSqlite()
	{
		// Plain INTEGER PRIMARY KEY rowids ARE recycled after deleting the highest row;
		// only AUTOINCREMENT guarantees monotonic non-reuse. This is behavioural proof on a
		// real SQLite database; the DDL assertions below pin the annotation itself.
		await using SyncDbContext db = _factory.CreateDbContext();
		User a = new() { Login = "a", UpdatedUtc = DateTime.UtcNow };
		User b = new() { Login = "b", UpdatedUtc = DateTime.UtcNow };
#pragma warning disable VSTHRD103
		db.Users.AddRange(a, b);
#pragma warning restore VSTHRD103
		await db.SaveChangesAsync();
		int deletedId = b.UserId;
		db.Users.Remove(b);
		await db.SaveChangesAsync();

		User c = new() { Login = "c", UpdatedUtc = DateTime.UtcNow };
#pragma warning disable VSTHRD103
		db.Users.Add(c);
#pragma warning restore VSTHRD103
		await db.SaveChangesAsync();

		Assert.True(c.UserId > deletedId,
			$"UserId {deletedId} was recycled as {c.UserId} — a reused id would let a new user " +
			"decrypt a deleted user's surviving sealed rows.");
	}

	[Fact]
	public void UsersIdColumn_IsNonReusing_InTheGeneratedDdl_OnBothProviders()
	{
		// The design demands this be a TEST, not a comment: one future model tweak dropping the
		// annotation is all it takes to reopen cross-user disclosure through a recycled id.
		using SyncDbContext sqlite = _factory.CreateDbContext();
		string sqliteDdl = sqlite.Database.GenerateCreateScript();
		string usersTableSqlite = ExtractCreateTable(sqliteDdl, "Users");
		Assert.Contains("AUTOINCREMENT", usersTableSqlite);

		DbContextOptions<NpgsqlSyncDbContext> npgsqlOptions =
			new DbContextOptionsBuilder<NpgsqlSyncDbContext>()
				.UseNpgsql("Host=placeholder;Database=placeholder")
				.Options;
		using NpgsqlSyncDbContext npgsql = new(npgsqlOptions);
		string npgsqlDdl = npgsql.Database.GenerateCreateScript();
		string usersTableNpgsql = ExtractCreateTable(npgsqlDdl, "Users");
		Assert.Contains("GENERATED BY DEFAULT AS IDENTITY", usersTableNpgsql);
	}

	private static string ExtractCreateTable(string ddl, string table)
	{
		int start = ddl.IndexOf($"CREATE TABLE \"{table}\" (", StringComparison.Ordinal);
		Assert.True(start >= 0, $"CREATE TABLE \"{table}\" not found in the generated script");
		int end = ddl.IndexOf(");", start, StringComparison.Ordinal);
		return ddl[start..(end + 2)];
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

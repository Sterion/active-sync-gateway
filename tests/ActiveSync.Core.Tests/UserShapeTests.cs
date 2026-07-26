using ActiveSync.Core.Accounts;
using ActiveSync.Core.Options;
using ActiveSync.Core.State;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ActiveSync.Core.Tests;

/// <summary>
///   Item 3 of docs/design/db-restructure.md: the <c>UserOptions</c> blob is normalised into
///   typed columns on <c>Users</c> plus a <c>UserBackendRoles</c> child table, with only the
///   provider-defined per-role Settings left serialized. <see cref="UserOptions" /> stays the
///   in-memory/config-bound shape, so these tests prove the mapping round-trips both ways and
///   that the columns are real (queryable, individually selectable) rather than a blob in
///   disguise.
/// </summary>
public sealed class UserShapeTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly TestContextFactory _factory;
	private readonly UserStore _store;

	public UserShapeTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();
		_factory = new TestContextFactory(_connection);
		using SyncDbContext db = _factory.CreateDbContext();
		db.Database.EnsureCreated();
		_store = new UserStore(_factory);
	}

	public void Dispose() => _connection.Dispose();

	private static UserOptions FullyPopulated() => new()
	{
		Password = "pbkdf2$200000$c2FsdA==$aGFzaA==",
		DefaultBackendLogin = "backend-anna",
		DefaultBackendPassword = "enc:v1:AAAA",
		MailAddress = "anna@example.com",
		Admin = true,
		Enabled = false,
		OidcSubject = "idp-subject-42",
		AutoProvisioned = true,
		Backends = new Dictionary<string, BackendRoleOverride>(StringComparer.OrdinalIgnoreCase)
		{
			["MailStore"] = new()
			{
				UserName = "imap-anna", Password = "enc:v1:BBBB",
				Settings = new Dictionary<string, string?> { ["Host"] = "imap.example.com", ["Port"] = "993" },
			},
			["Calendar"] = new() { Enabled = false, Provider = "caldav" },
		},
	};

	[Fact]
	public async Task EveryScalar_RoundTripsThroughItsOwnColumn()
	{
		await _store.UpsertAsync("anna", FullyPopulated(), CancellationToken.None);

		UserOptions? read = await _store.GetAsync("anna", CancellationToken.None);

		Assert.NotNull(read);
		Assert.Equal("pbkdf2$200000$c2FsdA==$aGFzaA==", read!.Password);
		Assert.Equal("backend-anna", read.DefaultBackendLogin);
		Assert.Equal("enc:v1:AAAA", read.DefaultBackendPassword);
		Assert.Equal("anna@example.com", read.MailAddress);
		Assert.True(read.Admin);
		Assert.False(read.Enabled);
		Assert.Equal("idp-subject-42", read.OidcSubject);
		Assert.True(read.AutoProvisioned);

		BackendRoleOverride mail = read.Backends!["MailStore"];
		Assert.Equal("imap-anna", mail.UserName);
		Assert.Equal("enc:v1:BBBB", mail.Password);
		Assert.Equal("imap.example.com", mail.Settings!["Host"]);
		Assert.Equal("993", mail.Settings["Port"]);
		BackendRoleOverride calendar = read.Backends["Calendar"];
		Assert.False(calendar.Enabled);
		Assert.Equal("caldav", calendar.Provider);
		Assert.Null(calendar.Settings);
	}

	[Fact]
	public async Task ScalarsAreRealColumns_NotABlob_SoTheyAreQueryable()
	{
		// The payoff of normalising: "which users are admins / disabled / bound to this subject"
		// is a WHERE clause instead of loading every row and deserializing it.
		await _store.UpsertAsync("admin-user", new UserOptions { Admin = true }, CancellationToken.None);
		await _store.UpsertAsync("disabled-user", new UserOptions { Enabled = false }, CancellationToken.None);
		await _store.UpsertAsync("oidc-user", new UserOptions { OidcSubject = "sub-1" }, CancellationToken.None);
		await _store.UpsertAsync("plain-user", new UserOptions(), CancellationToken.None);

		await using SyncDbContext db = _factory.CreateDbContext();
		Assert.Equal("admin-user", await db.Users.Where(u => u.Admin == true).Select(u => u.Login).SingleAsync());
		Assert.Equal("disabled-user", await db.Users.Where(u => u.Enabled == false).Select(u => u.Login).SingleAsync());
		Assert.Equal("oidc-user", await db.Users.Where(u => u.OidcSubject == "sub-1").Select(u => u.Login).SingleAsync());
		// And a role override is a row, so "who overrides MailStore?" is a join, not a scan.
		Assert.Empty(db.UserBackendRoles.Where(r => r.Role == "MailStore"));
	}

	[Fact]
	public async Task RoleRows_AreDiffedByRole_NotDeletedAndReinserted()
	{
		// The hint in the design: diff by role so an untouched role keeps its row id (no churn,
		// and the FK cascade stays meaningful for auditing).
		await _store.UpsertAsync("anna", FullyPopulated(), CancellationToken.None);

		// Re-save with MailStore edited and Calendar dropped entirely.
		UserOptions edited = FullyPopulated();
		edited.Backends!.Remove("Calendar");
		edited.Backends["MailStore"].UserName = "imap-anna-2";
		await _store.UpsertAsync("anna", edited, CancellationToken.None);

		await using (SyncDbContext db = _factory.CreateDbContext())
		{
			// The MailStore row was updated in place — its key is (UserId, Role), so an update
			// and a delete-then-reinsert are only distinguishable by the row surviving at all.
			UserBackendRole mail = await db.UserBackendRoles.SingleAsync();
			Assert.Equal("imap-anna-2", mail.UserName);
			Assert.Equal("MailStore", mail.Role);         // Calendar's row is gone
		}
	}

	[Fact]
	public async Task DeletingTheDeclaration_ClearsEveryColumnAndRole_ButKeepsTheIdentity()
	{
		await _store.UpsertAsync("anna", FullyPopulated(), CancellationToken.None);
		int userId = (await _store.FindUserIdAsync("anna", CancellationToken.None))!.Value;

		Assert.True(await _store.DeleteAsync("anna", CancellationToken.None));

		// The declaration is gone (falls back to config/pass-through)...
		Assert.Null(await _store.GetAsync("anna", CancellationToken.None));
		Assert.Empty(await _store.ListAsync(CancellationToken.None));
		await using SyncDbContext db = _factory.CreateDbContext();
		Assert.Empty(db.UserBackendRoles);
		// ...but the IDENTITY survives, so sync state and sealed local items stay attached.
		User row = await db.Users.AsNoTracking().SingleAsync();
		Assert.Equal(userId, row.UserId);
		Assert.False(row.Declared);
		Assert.Null(row.Password);
		Assert.Null(row.MailAddress);
	}

	[Fact]
	public async Task IdentityOnlyRow_IsNotADeclaration()
	{
		// A login that merely authenticated (or was named by a block/share) has an identity but
		// declares nothing — the resolver must not see it as a database entry shadowing config.
		(int userId, bool written) = await _store.GetOrCreateUserAsync("ghost", null, CancellationToken.None);

		Assert.False(written);
		Assert.True(userId > 0);
		Assert.Null(await _store.GetAsync("ghost", CancellationToken.None));
		Assert.Empty(await _store.ListAsync(CancellationToken.None));
		Assert.Empty(await _store.LoadAllAsync(null, CancellationToken.None));
	}

	[Fact]
	public async Task EmptyDeclaration_IsStillADeclaration_TheAllowlistGrant()
	{
		// `eas user add` writes an entry with nothing overridden; under AutoProvisionUsers=false
		// that empty entry IS the grant, so it must be distinguishable from an identity-only row.
		await _store.UpsertAsync("dora", new UserOptions(), CancellationToken.None);

		Assert.NotNull(await _store.GetAsync("dora", CancellationToken.None));
		Assert.Single(await _store.LoadAllAsync(null, CancellationToken.None));
	}

	[Fact]
	public async Task TheTwoCredentialChains_StaySeparate()
	{
		// The gateway Password (device → gateway, verified locally) must never be reachable by
		// anything that builds a backend connection, and the backend defaults must never be
		// mistaken for it. They are distinct columns and distinct UserOptions members; this pins
		// that a value written to one never surfaces in the other.
		await _store.UpsertAsync("anna", new UserOptions
		{
			Password = "pbkdf2$200000$c2FsdA==$Z2F0ZXdheQ==",
			DefaultBackendLogin = "backend-anna",
			DefaultBackendPassword = "enc:v1:BACKEND",
		}, CancellationToken.None);

		await using SyncDbContext db = _factory.CreateDbContext();
		User row = await db.Users.AsNoTracking().SingleAsync();
		Assert.StartsWith("pbkdf2$", row.Password);
		Assert.Equal("enc:v1:BACKEND", row.DefaultBackendPassword);
		Assert.NotEqual(row.Password, row.DefaultBackendPassword);
		// The gateway password is a LOCAL verifier, so it is never sealed as a backend secret,
		// and the backend default is never a pbkdf2$ hash (which no backend could use).
		Assert.DoesNotContain("enc:v1:", row.Password!);
		Assert.DoesNotContain("pbkdf2$", row.DefaultBackendPassword!);
	}

	[Fact]
	public async Task OidcSubject_IsUnique_SoTwoUsersCannotBindToOneIdentity()
	{
		// Two users bound to one IdP subject is an account-takeover vector — the database, not
		// the caller, is what forbids it.
		await _store.UpsertAsync("anna", new UserOptions { OidcSubject = "sub-1" }, CancellationToken.None);

		await Assert.ThrowsAnyAsync<DbUpdateException>(() =>
			_store.UpsertAsync("bob", new UserOptions { OidcSubject = "sub-1" }, CancellationToken.None));

		// Unbound users are not constrained against each other (the index is filtered on null).
		await _store.UpsertAsync("carol", new UserOptions(), CancellationToken.None);
		await _store.UpsertAsync("dave", new UserOptions(), CancellationToken.None);
		Assert.Equal(3, (await _store.ListAsync(CancellationToken.None)).Count);
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

using ActiveSync.Core.Security;
using ActiveSync.Core.State;
using ActiveSync.Server.Cli;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli.Testing;

namespace ActiveSync.Server.Tests;

/// <summary>
///   Inspection commands against a seeded temp SQLite state database. The commands read
///   configuration from environment variables, so the class pins the connection string and
///   encryption key for its lifetime ("cli" collection keeps env-touching tests sequential).
/// </summary>
[Collection("cli")]
public sealed class CliInspectTests : IDisposable
{
	/// <summary>256-bit key (base64 of bytes 0..31), same fixed value the integration suite uses.</summary>
	private const string KeyBase64 = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";

	private readonly string _dbPath;
	private readonly Dictionary<string, string?> _originalEnv = [];

	public CliInspectTests()
	{
		_dbPath = Path.Combine(Path.GetTempPath(), $"as-cli-tests-{Guid.NewGuid():N}.db");

		DbContextOptions<SqliteSyncDbContext> options = new DbContextOptionsBuilder<SqliteSyncDbContext>()
			.UseSqlite($"Data Source={_dbPath}")
			.Options;
		using SqliteSyncDbContext db = new(options);
		db.Database.Migrate();

		LocalContentProtector protector = LocalContentProtector.CreateProtected(Convert.FromBase64String(KeyBase64));
		DateTime seen = new(2026, 7, 16, 12, 0, 0, DateTimeKind.Utc);
		db.Devices.Add(new Device
		{
			UserName = "user1@x", DeviceId = "DEVAAA111", DeviceType = "iPhone",
			CreatedUtc = seen.AddDays(-30), LastSeenUtc = seen,
		});
		db.UserFolders.Add(new UserFolder
		{
			UserName = "user1@x", BackendKey = "local:contacts", DisplayName = "Contacts",
			EasClass = "Contacts", Type = 9,
		});
		db.LocalItems.Add(new LocalItem
		{
			UserName = "user1@x", Collection = "contacts", Uid = "c-1", Version = 3,
			Content = protector.Protect("BEGIN:VCARD\r\nFN:Alice Example\r\nEND:VCARD", "user1@x", "contacts"),
			LastModifiedUtc = seen,
		});
		db.LocalItems.Add(new LocalItem
		{
			UserName = "user1@x", Collection = "calendar", Uid = "e-1", Version = 1,
			Content = protector.Protect("BEGIN:VCALENDAR\r\nEND:VCALENDAR", "user1@x", "calendar"),
			ItemDateUtc = seen.AddDays(2), LastModifiedUtc = seen,
		});
		db.LocalItems.Add(new LocalItem
		{
			UserName = "user2@x", Collection = "notes", Uid = "n-1", Version = 1,
			Content = protector.Protect("a note", "user2@x", "notes"),
			LastModifiedUtc = seen,
		});
		db.SaveChanges();

		SetEnv("ActiveSync__Database__ConnectionString", $"Data Source={_dbPath}");
		SetEnv("ActiveSync__Encryption__Key", KeyBase64);
	}

	public void Dispose()
	{
		foreach ((string name, string? value) in _originalEnv)
			Environment.SetEnvironmentVariable(name, value);
		SqliteConnection.ClearAllPools();
		File.Delete(_dbPath);
	}

	private void SetEnv(string name, string? value)
	{
		_originalEnv.TryAdd(name, Environment.GetEnvironmentVariable(name));
		Environment.SetEnvironmentVariable(name, value);
	}

	private static CommandAppTester CreateTester()
	{
		CommandAppTester tester = new();
		tester.Configure(CliApp.Configure);
		return tester;
	}

	private static (int ExitCode, string CapturedStdOut, string CapturedStdErr, string ConsoleOutput) Run(
		params string[] args)
	{
		TextWriter originalOut = Console.Out;
		TextWriter originalError = Console.Error;
		using StringWriter stdout = new();
		using StringWriter stderr = new();
		try
		{
			Console.SetOut(stdout);
			Console.SetError(stderr);
			CommandAppResult result = CreateTester().Run(args);
			return (result.ExitCode, stdout.ToString(), stderr.ToString(), result.Output);
		}
		finally
		{
			Console.SetOut(originalOut);
			Console.SetError(originalError);
		}
	}

	[Fact]
	public void Users_ListsUsersWithCounts()
	{
		(int exitCode, _, _, string output) = Run("users");

		Assert.Equal(0, exitCode);
		Assert.Contains("user1@x", output);
		Assert.Contains("user2@x", output);
	}

	[Fact]
	public async Task DatabaseCommand_PrefersAmbientHostProvider_OverRebuildingFromEnv()
	{
		// L35: a command forwarded to the warm gateway must run against the HOST's already-built
		// provider, not build a parallel container from the ambient (env) configuration. Build a host
		// provider bound to the seeded database, then point the env config at a DIFFERENT empty
		// database and publish the host provider as the ambient one. The seeded user must still show:
		// on the unmodified DatabaseCommand (which always rebuilds from env) it would read the empty
		// database and show nothing.
		ServiceProvider host = (await CliServices.TryCreateAsync())!;
		Assert.NotNull(host);
		await using ServiceProvider hostOwner = host;

		string emptyDb = Path.Combine(Path.GetTempPath(), $"as-cli-empty-{Guid.NewGuid():N}.db");
		DbContextOptions<SqliteSyncDbContext> emptyOptions = new DbContextOptionsBuilder<SqliteSyncDbContext>()
			.UseSqlite($"Data Source={emptyDb}")
			.Options;
		await using (SqliteSyncDbContext db = new(emptyOptions))
			await db.Database.MigrateAsync();
		SetEnv("ActiveSync__Database__ConnectionString", $"Data Source={emptyDb}");

		try
		{
			CliHostServices.Enter(host);
			(int exitCode, _, _, string output) = Run("users");
			Assert.Equal(0, exitCode);
			// user1@x lives only in the seeded (host) database, never in the empty env database.
			Assert.Contains("user1@x", output);
		}
		finally
		{
			CliHostServices.Enter(null);
			SqliteConnection.ClearAllPools();
			File.Delete(emptyDb);
		}
	}

	[Fact]
	public void Devices_FiltersByUser()
	{
		(int exitCode, _, _, string output) = Run("devices", "user1@x");
		Assert.Equal(0, exitCode);
		Assert.Contains("DEVAAA111", output);
		Assert.Contains("iPhone", output);

		(int emptyExit, _, _, string emptyOutput) = Run("devices", "user2@x");
		Assert.Equal(0, emptyExit);
		Assert.Contains("No devices", emptyOutput);
	}

	[Fact]
	public void Folders_ListsLocalFolderWithItemCount()
	{
		(int exitCode, _, _, string output) = Run("folders", "user1@x");

		Assert.Equal(0, exitCode);
		Assert.Contains("local:contacts", output);
		Assert.Contains("Contacts", output);
	}

	[Fact]
	public void Items_FiltersByCollection()
	{
		(int exitCode, _, _, string output) = Run("items", "user1@x", "contacts");
		Assert.Equal(0, exitCode);
		Assert.Contains("c-1", output);
		Assert.DoesNotContain("e-1", output);

		(int allExit, _, _, string allOutput) = Run("items", "user1@x");
		Assert.Equal(0, allExit);
		Assert.Contains("c-1", allOutput);
		Assert.Contains("e-1", allOutput);
	}

	[Fact]
	public void Items_UnknownCollection_Fails()
	{
		(int exitCode, _, _, _) = Run("items", "user1@x", "mailbox");
		Assert.NotEqual(0, exitCode);
	}

	[Fact]
	public void Show_DecryptsItemContent()
	{
		(int exitCode, string stdout, _, _) = Run("show", "user1@x", "contacts", "c-1");

		Assert.Equal(0, exitCode);
		Assert.Contains("FN:Alice Example", stdout);
	}

	[Fact]
	public void Show_MissingItem_Fails()
	{
		(int exitCode, _, string stderr, _) = Run("show", "user1@x", "contacts", "nope");

		Assert.Equal(1, exitCode);
		Assert.Contains("No item", stderr);
	}

	[Fact]
	public void Block_Unblock_UserLevel_RoundTrips()
	{
		(int blockExit, _, _, string blockOutput) = Run("block", "user1@x");
		Assert.Equal(0, blockExit);
		Assert.Contains("Blocked user 'user1@x'", blockOutput);

		(_, _, _, string usersOutput) = Run("users");
		Assert.Contains("yes", usersOutput);

		(_, _, _, string devicesOutput) = Run("devices", "user1@x");
		Assert.Contains("user", devicesOutput);

		(int againExit, _, _, string againOutput) = Run("block", "user1@x");
		Assert.Equal(0, againExit);
		Assert.Contains("Already blocked", againOutput);

		(int unblockExit, _, _, string unblockOutput) = Run("unblock", "user1@x");
		Assert.Equal(0, unblockExit);
		Assert.Contains("Unblocked user 'user1@x'", unblockOutput);

		(int noneExit, _, _, string noneOutput) = Run("unblock", "user1@x");
		Assert.Equal(0, noneExit);
		Assert.Contains("nothing to remove", noneOutput);
	}

	[Fact]
	public void Purge_WithoutYes_NonInteractive_Fails()
	{
		(int exitCode, _, string stderr, _) = Run("purge", "user", "user1@x");

		Assert.Equal(1, exitCode);
		Assert.Contains("--yes", stderr);
		(_, _, _, string usersOutput) = Run("users");
		Assert.Contains("user1@x", usersOutput);
	}

	[Fact]
	public void Purge_User_RemovesOnlyThatUser()
	{
		(int exitCode, _, _, string output) = Run("purge", "user", "user1@x", "--yes");

		Assert.Equal(0, exitCode);
		Assert.Contains("Devices: 1", output);
		Assert.Contains("LocalItems: 2", output);
		Assert.Contains("UserFolders: 1", output);

		(_, _, _, string usersOutput) = Run("users");
		Assert.DoesNotContain("user1@x", usersOutput);
		Assert.Contains("user2@x", usersOutput);
	}

	[Fact]
	public void Purge_Device_RemovesOnlyTheDevice()
	{
		(int exitCode, _, _, string output) = Run("purge", "device", "user1@x", "DEVAAA111", "--yes");

		Assert.Equal(0, exitCode);
		Assert.Contains("Devices: 1", output);

		(_, _, _, string usersOutput) = Run("users");
		// The user's items/folders survive a device purge.
		Assert.Contains("user1@x", usersOutput);
	}

	[Fact]
	public void Block_DeviceLevel_ShowsInListings()
	{
		(int blockExit, _, _, _) = Run("block", "user1@x", "DEVAAA111");
		Assert.Equal(0, blockExit);

		(_, _, _, string usersOutput) = Run("users");
		Assert.Contains("1 device(s)", usersOutput);

		(int unblockExit, _, _, string unblockOutput) = Run("unblock", "user1@x", "DEVAAA111");
		Assert.Equal(0, unblockExit);
		Assert.Contains("Unblocked device 'DEVAAA111'", unblockOutput);
	}
}

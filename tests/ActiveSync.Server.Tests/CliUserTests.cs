using ActiveSync.Core.State;
using ActiveSync.Server.Cli;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Spectre.Console.Cli.Testing;

namespace ActiveSync.Server.Tests;

/// <summary>
///   `eas user` commands against a migrated temp SQLite state database. A config-declared
///   user is injected via environment variables so shadowing behavior is covered.
/// </summary>
[Collection("cli")]
public sealed class CliUserTests : IDisposable
{
	private const string KeyBase64 = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";

	private readonly string _dbPath;
	private readonly Dictionary<string, string?> _originalEnv = [];

	public CliUserTests()
	{
		_dbPath = Path.Combine(Path.GetTempPath(), $"as-cli-user-{Guid.NewGuid():N}.db");
		DbContextOptions<SqliteSyncDbContext> options = new DbContextOptionsBuilder<SqliteSyncDbContext>()
			.UseSqlite($"Data Source={_dbPath}")
			.Options;
		using SqliteSyncDbContext db = new(options);
		db.Database.Migrate();

		SetEnv("ActiveSync__Database__ConnectionString", $"Data Source={_dbPath}");
		SetEnv("ActiveSync__Encryption__Key", KeyBase64);
		SetEnv("ActiveSync__Users__confuser__MailAddress", "cfg@example.com");
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

	private static (int ExitCode, string StdErr, string ConsoleOutput) Run(string? stdin, params string[] args)
	{
		TextWriter originalOut = Console.Out;
		TextWriter originalError = Console.Error;
		TextReader originalIn = Console.In;
		using StringWriter stdout = new();
		using StringWriter stderr = new();
		try
		{
			Console.SetOut(stdout);
			Console.SetError(stderr);
			if (stdin is not null)
				Console.SetIn(new StringReader(stdin));
			CommandAppTester tester = new();
			tester.Configure(CliApp.Configure);
			CommandAppResult result = tester.Run(args);
			return (result.ExitCode, stderr.ToString(), result.Output);
		}
		finally
		{
			Console.SetOut(originalOut);
			Console.SetError(originalError);
			Console.SetIn(originalIn);
		}
	}

	[Fact]
	public void Add_Show_List_Remove_RoundTrip()
	{
		(int addExit, _, string addOutput) = Run(null, "user", "add", "newuser@x");
		Assert.Equal(0, addExit);
		Assert.Contains("allowlist grant", addOutput);

		(int showExit, _, string showOutput) = Run(null, "user", "show", "newuser@x");
		Assert.Equal(0, showExit);
		Assert.Contains("[db]", showOutput);

		(int listExit, _, string listOutput) = Run(null, "user", "list");
		Assert.Equal(0, listExit);
		Assert.Contains("newuser@x", listOutput);
		Assert.Contains("confuser", listOutput);

		(int removeExit, _, _) = Run(null, "user", "remove", "newuser@x");
		Assert.Equal(0, removeExit);
		(int goneExit, string goneErr, _) = Run(null, "user", "show", "newuser@x");
		Assert.Equal(1, goneExit);
		Assert.Contains("No declared user", goneErr);
	}

	[Fact]
	public void Set_TypedValues_AndUnknownKeyFails()
	{
		(int portExit, _, string portOutput) = Run(null, "user", "set", "u1", "Imap:Port", "993");
		Assert.Equal(0, portExit);
		Assert.Contains("port=993", portOutput);

		(int davExit, _, string davOutput) = Run(null, "user", "set", "u1", "CalDav:Enabled", "false");
		Assert.Equal(0, davExit);
		Assert.Contains("caldav=off", davOutput);

		(int unknownExit, string unknownErr, _) = Run(null, "user", "set", "u1", "Imap:Nope", "x");
		Assert.Equal(1, unknownExit);
		Assert.Contains("Valid fields", unknownErr);

		(int parseExit, string parseErr, _) = Run(null, "user", "set", "u1", "Imap:Port", "abc");
		Assert.Equal(1, parseExit);
		Assert.Contains("not a valid Int32", parseErr);
	}

	[Fact]
	public void Set_InvalidEffectivePort_RefusedByValidation()
	{
		(int exitCode, string stderr, _) = Run(null, "user", "set", "u9", "Imap:Port", "99999");
		Assert.Equal(1, exitCode);
		Assert.Contains("out of range", stderr);

		(int showExit, _, _) = Run(null, "user", "show", "u9");
		Assert.Equal(1, showExit);
	}

	[Fact]
	public void Set_PlaintextGatewayPassword_IsHashed_WithWarning()
	{
		(int exitCode, string stderr, string output) = Run(null, "user", "set", "u2", "Password", "hunter2");
		Assert.Equal(0, exitCode);
		Assert.Contains("shell history", stderr);
		Assert.Contains("password=***(pbkdf2)", output);
		Assert.DoesNotContain("hunter2", output);
	}

	[Fact]
	public void Set_PreHashedAndBadShapes()
	{
		string hashed = ActiveSync.Core.Security.GatewayPasswordHasher.Hash("s3cret");
		(int okExit, string okErr, string okOutput) = Run(null, "user", "set", "u3", "Password", hashed);
		Assert.Equal(0, okExit);
		Assert.DoesNotContain("shell history", okErr);
		Assert.Contains("password=***(pbkdf2)", okOutput);

		(int badExit, string badErr, _) = Run(null, "user", "set", "u3", "Password", "pbkdf2$broken");
		Assert.Equal(1, badExit);
		Assert.Contains("pbkdf2", badErr);

		// A backend password must not be a hash — the backend could never verify it.
		(int hashExit, string hashErr, _) = Run(null, "user", "set", "u3", "Imap:Password", hashed);
		Assert.Equal(1, hashExit);
		Assert.Contains("backend password", hashErr);
	}

	[Fact]
	public void Set_PlaintextBackendPassword_IsSealed_WithWarning()
	{
		(int exitCode, string stderr, string output) = Run(null, "user", "set", "u4", "Imap:Password", "imap-pw");
		Assert.Equal(0, exitCode);
		Assert.Contains("shell history", stderr);
		Assert.Contains("pw=***(sealed)", output);
		Assert.DoesNotContain("imap-pw", output);
	}

	[Fact]
	public void PasswordAndSecret_ViaStdin()
	{
		(int pwExit, _, string pwOutput) = Run("topsecret", "user", "password", "u5");
		Assert.Equal(0, pwExit);
		Assert.Contains("password=***(pbkdf2)", pwOutput);

		(int secretExit, _, string secretOutput) = Run("dav-pw", "user", "secret", "u5", "CardDav:Password");
		Assert.Equal(0, secretExit);
		Assert.Contains("pw=***(sealed)", secretOutput);

		(int badKeyExit, string badKeyErr, _) = Run("x", "user", "secret", "u5", "MailAddress");
		Assert.Equal(1, badKeyExit);
		Assert.Contains("Imap:Password", badKeyErr);
	}

	[Fact]
	public void Set_OnConfigUser_CopiesTheConfigEntry()
	{
		(int exitCode, _, string output) = Run(null, "user", "set", "confuser", "Imap:Host", "imap.override");
		Assert.Equal(0, exitCode);
		// The database entry starts as a copy, so the config MailAddress survives the edit.
		Assert.Contains("mail=cfg@example.com", output);
		Assert.Contains("host=imap.override", output);
		Assert.Contains("[db, shadows config]", output);

		(int removeExit, _, string removeOutput) = Run(null, "user", "remove", "confuser");
		Assert.Equal(0, removeExit);
		Assert.Contains("config entry is active again", removeOutput);
	}

	[Fact]
	public void Unset_ClearsField_AndEmptySectionCollapses()
	{
		(int _, _, _) = Run(null, "user", "set", "u6", "Smtp:Host", "smtp.x");
		(int unsetExit, _, string output) = Run(null, "user", "unset", "u6", "Smtp:Host");
		Assert.Equal(0, unsetExit);
		Assert.DoesNotContain("smtp[", output);
		Assert.Contains("allowlist grant", output);
	}
}

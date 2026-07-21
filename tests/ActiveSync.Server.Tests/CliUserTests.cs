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
		SetEnv("ActiveSync__Backends__MailStore__Provider", "imap");
		SetEnv("ActiveSync__Backends__MailStore__Host", "imap.test");
		SetEnv("ActiveSync__Backends__MailSubmit__Provider", "smtp");
		SetEnv("ActiveSync__Backends__MailSubmit__Host", "smtp.test");
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

		// The unified `eas users` overview lists declared accounts (config + database) too.
		(int listExit, _, string listOutput) = Run(null, "users");
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
		(int portExit, _, string portOutput) = Run(null, "user", "set", "u1", "Backends:MailStore:Settings:Port", "993");
		Assert.Equal(0, portExit);
		Assert.Contains("Port=993", portOutput);

		(int davExit, _, string davOutput) = Run(null, "user", "set", "u1", "Backends:Calendar:Enabled", "false");
		Assert.Equal(0, davExit);
		Assert.Contains("calendar[off]", davOutput);

		(int unknownExit, string unknownErr, _) = Run(null, "user", "set", "u1", "Imap:Nope", "x");
		Assert.Equal(1, unknownExit);
		Assert.Contains("Valid fields", unknownErr);

		// Settings are free-form strings; a non-numeric port is caught by the provider's
		// own validation (the config binder), not by CLI-side typing.
		(int parseExit, string parseErr, _) = Run(null, "user", "set", "u1", "Backends:MailStore:Settings:Port", "abc");
		Assert.Equal(1, parseExit);
		Assert.Contains("invalid", parseErr, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void Set_InvalidEffectivePort_RefusedByValidation()
	{
		(int exitCode, string stderr, _) = Run(null, "user", "set", "u9", "Backends:MailStore:Settings:Port", "99999");
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
		(int hashExit, string hashErr, _) = Run(null, "user", "set", "u3", "Backends:MailStore:Password", hashed);
		Assert.Equal(1, hashExit);
		Assert.Contains("backend password", hashErr);
	}

	[Fact]
	public void Set_PlaintextBackendPassword_IsSealed_WithWarning()
	{
		(int exitCode, string stderr, string output) = Run(null, "user", "set", "u4", "Backends:MailStore:Password", "imap-pw");
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

		(int secretExit, _, string secretOutput) = Run("dav-pw", "user", "secret", "u5", "Backends:Contacts:Password");
		Assert.Equal(0, secretExit);
		Assert.Contains("pw=***(sealed)", secretOutput);

		(int badKeyExit, string badKeyErr, _) = Run("x", "user", "secret", "u5", "MailAddress");
		Assert.Equal(1, badKeyExit);
		Assert.Contains("Backends:MailStore:Password", badKeyErr);
	}

	// L42 — COVERAGE, NOT PROOF. The fix moves the master-key ZeroMemory into a finally so a
	// throwing/cancelled stdin read no longer leaks the key on the heap. The key is a local byte[]
	// with no external handle, so the zeroing itself is not observable from a test; this only
	// exercises the failure path to prove the finally is reached and the command exits cleanly
	// (does not hang or crash the process) when the read throws.
	[Fact]
	public void Secret_StdinReadThrows_ExitsCleanly()
	{
		TextReader originalIn = Console.In;
		TextWriter originalOut = Console.Out;
		TextWriter originalError = Console.Error;
		using StringWriter stdout = new();
		using StringWriter stderr = new();
		try
		{
			Console.SetOut(stdout);
			Console.SetError(stderr);
			Console.SetIn(new ThrowingReader());
			CommandAppTester tester = new();
			tester.Configure(CliApp.Configure);
			CommandAppResult result = tester.Run("user", "secret", "u5", "Backends:Contacts:Password");
			// Spectre turns the propagated exception into a non-zero exit; the point is the finally
			// ran (no leak) and the process is still usable, not the specific code.
			Assert.NotEqual(0, result.ExitCode);
		}
		finally
		{
			Console.SetOut(originalOut);
			Console.SetError(originalError);
			Console.SetIn(originalIn);
		}
	}

	private sealed class ThrowingReader : TextReader
	{
		public override Task<string> ReadToEndAsync() =>
			throw new IOException("simulated stdin failure");

		public override Task<string> ReadToEndAsync(CancellationToken cancellationToken) =>
			throw new IOException("simulated stdin failure");
	}

	[Fact]
	public void Set_OnConfigUser_CopiesTheConfigEntry()
	{
		(int exitCode, _, string output) = Run(null, "user", "set", "confuser", "Backends:MailStore:Settings:Host", "imap.override");
		Assert.Equal(0, exitCode);
		// The database entry starts as a copy, so the config MailAddress survives the edit.
		Assert.Contains("mail=cfg@example.com", output);
		Assert.Contains("Host=imap.override", output);
		Assert.Contains("[db, shadows config]", output);

		(int removeExit, _, string removeOutput) = Run(null, "user", "remove", "confuser");
		Assert.Equal(0, removeExit);
		Assert.Contains("config entry is active again", removeOutput);
	}

	[Fact]
	public void Disable_Enable_RoundTrip()
	{
		// Disabling a not-yet-declared login creates the row with the flag set.
		(int disableExit, _, string disableOut) = Run(null, "user", "disable", "u9");
		Assert.Equal(0, disableExit);
		Assert.Contains("DISABLED", disableOut);

		(int _, _, string showOut) = Run(null, "user", "show", "u9");
		Assert.Contains("DISABLED", showOut);

		// The merged overview flags it in the Blocked column.
		(int usersExit, _, string usersOut) = Run(null, "users");
		Assert.Equal(0, usersExit);
		Assert.Contains("disabled", usersOut);

		(int enableExit, _, string enableOut) = Run(null, "user", "enable", "u9");
		Assert.Equal(0, enableExit);
		Assert.DoesNotContain("DISABLED", enableOut);
	}

	[Fact]
	public void Set_AdminFlag_RoundTrip()
	{
		(int setExit, _, string setOutput) = Run(null, "user", "set", "u7", "Admin", "true");
		Assert.Equal(0, setExit);
		Assert.Contains("admin", setOutput);

		(int listExit, _, string listOutput) = Run(null, "users");
		Assert.Equal(0, listExit);
		Assert.Contains("yes", listOutput);

		(int unsetExit, _, string unsetOutput) = Run(null, "user", "unset", "u7", "Admin");
		Assert.Equal(0, unsetExit);
		Assert.DoesNotContain("admin", unsetOutput);
	}

	[Fact]
	public void Unset_ClearsField_AndEmptySectionCollapses()
	{
		(int _, _, _) = Run(null, "user", "set", "u6", "Backends:MailSubmit:Settings:Host", "smtp.x");
		(int unsetExit, _, string output) = Run(null, "user", "unset", "u6", "Backends:MailSubmit:Settings:Host");
		Assert.Equal(0, unsetExit);
		Assert.DoesNotContain("mailsubmit[", output);
		Assert.Contains("allowlist grant", output);
	}
}

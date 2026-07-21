using ActiveSync.Core.State;
using ActiveSync.Server.Cli;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Spectre.Console.Cli.Testing;

namespace ActiveSync.Server.Tests;

/// <summary>
///   `eas config` commands against a migrated temp SQLite state database. A file/env value is
///   injected so the effective-value source labeling (default/config/db) is covered, and the
///   bootstrap/unknown/type guards are exercised.
/// </summary>
[Collection("cli")]
public sealed class CliConfigTests : IDisposable
{
	private readonly string _dbPath;
	private readonly Dictionary<string, string?> _originalEnv = [];

	public CliConfigTests()
	{
		_dbPath = Path.Combine(Path.GetTempPath(), $"as-cli-config-{Guid.NewGuid():N}.db");
		DbContextOptions<SqliteSyncDbContext> options = new DbContextOptionsBuilder<SqliteSyncDbContext>()
			.UseSqlite($"Data Source={_dbPath}")
			.Options;
		using SqliteSyncDbContext db = new(options);
		db.Database.Migrate();

		SetEnv("ActiveSync__Database__ConnectionString", $"Data Source={_dbPath}");
		// A file/env-sourced value, to prove "config" source and database override precedence.
		SetEnv("ActiveSync__Eas__DefaultWindowSize", "77");
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

	private static (int ExitCode, string StdErr, string ConsoleOutput) Run(params string[] args)
	{
		TextWriter originalOut = Console.Out;
		TextWriter originalError = Console.Error;
		using StringWriter stdout = new();
		using StringWriter stderr = new();
		try
		{
			Console.SetOut(stdout);
			Console.SetError(stderr);
			CommandAppTester tester = new();
			tester.Configure(CliApp.Configure);
			CommandAppResult result = tester.Run(args);
			return (result.ExitCode, stderr.ToString(), result.Output);
		}
		finally
		{
			Console.SetOut(originalOut);
			Console.SetError(originalError);
		}
	}

	[Fact]
	public void Set_Get_List_Unset_RoundTrip()
	{
		// RequireDeclaredUsers is absent from appsettings, so its baseline is the code default.
		(int getExit, _, string getOut) = Run("config", "get", "ActiveSync:RequireDeclaredUsers");
		Assert.Equal(0, getExit);
		Assert.Contains("false", getOut);
		Assert.Contains("source: default", getOut);

		(int setExit, _, string setOut) = Run("config", "set", "ActiveSync:RequireDeclaredUsers", "true");
		Assert.Equal(0, setExit);
		Assert.Contains("within ~1s", setOut);

		(_, _, string get2) = Run("config", "get", "ActiveSync:RequireDeclaredUsers");
		Assert.Contains("true", get2);
		Assert.Contains("source: db", get2);

		(_, _, string listOut) = Run("config", "list");
		Assert.Contains("ActiveSync:RequireDeclaredUsers", listOut);

		(int unsetExit, _, string unsetOut) = Run("config", "unset", "ActiveSync:RequireDeclaredUsers");
		Assert.Equal(0, unsetExit);
		(_, _, string get3) = Run("config", "get", "ActiveSync:RequireDeclaredUsers");
		Assert.Contains("source: default", get3);
	}

	[Fact]
	public void DatabaseOverridesConfig_Source()
	{
		// The file/env value shows as "config"...
		(_, _, string get1) = Run("config", "get", "ActiveSync:Eas:DefaultWindowSize");
		Assert.Contains("77", get1);
		Assert.Contains("source: config", get1);

		// ...and a database value wins over it.
		Assert.Equal(0, Run("config", "set", "ActiveSync:Eas:DefaultWindowSize", "200").ExitCode);
		(_, _, string get2) = Run("config", "get", "ActiveSync:Eas:DefaultWindowSize");
		Assert.Contains("200", get2);
		Assert.Contains("source: db", get2);

		// Unset falls back to the file/env value, not the code default.
		Run("config", "unset", "ActiveSync:Eas:DefaultWindowSize");
		(_, _, string get3) = Run("config", "get", "ActiveSync:Eas:DefaultWindowSize");
		Assert.Contains("77", get3);
		Assert.Contains("source: config", get3);
	}

	[Fact]
	public void Set_ValidatesTypeAndRange()
	{
		(int badType, string badTypeErr, _) = Run("config", "set", "ActiveSync:Eas:MaxHeartbeatSeconds", "abc");
		Assert.Equal(1, badType);
		Assert.Contains("not an integer", badTypeErr);

		(int outOfRange, string rangeErr, _) = Run("config", "set", "ActiveSync:Eas:MaxHeartbeatSeconds", "99999");
		Assert.Equal(1, outOfRange);
		Assert.Contains("above the maximum", rangeErr);

		(int badBool, string boolErr, _) = Run("config", "set", "ActiveSync:ReadOnly", "yes");
		Assert.Equal(1, badBool);
		Assert.Contains("not a boolean", boolErr);

		(int badEnum, string enumErr, _) = Run("config", "set", "ActiveSync:Log:Mode", "Loud");
		Assert.Equal(1, badEnum);
		Assert.Contains("not one of", enumErr);

		Assert.Equal(0, Run("config", "set", "ActiveSync:Eas:MaxHeartbeatSeconds", "1200").ExitCode);
	}

	[Fact]
	public void Set_RejectsBootstrapAndUnknownKeys()
	{
		(int enc, string encErr, _) = Run("config", "set", "ActiveSync:Encryption:Key", "hunter2");
		Assert.Equal(1, enc);
		Assert.Contains("bootstrap", encErr);

		(int db, string dbErr, _) = Run("config", "set", "ActiveSync:Database:Provider", "Sqlite");
		Assert.Equal(1, db);
		Assert.Contains("bootstrap", dbErr);

		(int unknown, string unknownErr, _) = Run("config", "set", "ActiveSync:Bogus:Thing", "x");
		Assert.Equal(1, unknown);
		Assert.Contains("not a recognized setting", unknownErr);
	}

	[Fact]
	public void WebUiKeys_SettableAndSecretMasked()
	{
		// The live enable flag round-trips like any live key.
		(int enableExit, _, string enableOut) = Run("config", "set", "ActiveSync:WebUi:Admin:Enabled", "true");
		Assert.Equal(0, enableExit);
		Assert.Contains("within ~1s", enableOut);

		// A secret-flagged key is stored but never echoed back by get/list.
		Assert.Equal(0, Run("config", "set", "ActiveSync:WebUi:Oidc:ClientSecret", "super-secret").ExitCode);
		(_, _, string getOut) = Run("config", "get", "ActiveSync:WebUi:Oidc:ClientSecret");
		Assert.Contains("***", getOut);
		Assert.DoesNotContain("super-secret", getOut);
		(_, _, string listOut) = Run("config", "list");
		Assert.DoesNotContain("super-secret", listOut);
	}

	[Fact]
	public void Set_BackendKey_IsAcceptedAndRestartTierNoted()
	{
		// Open-ended backend settings are accepted (validated on the server by the provider).
		(int exit, _, string output) = Run("config", "set", "ActiveSync:Backends:MailStore:Host", "imap.new");
		Assert.Equal(0, exit);
		Assert.Contains("within ~1s", output);

		// A restart-tier setting says so.
		(_, _, string tls) = Run("config", "set", "ActiveSync:Tls:Port", "6443");
		Assert.Contains("Restart the gateway", tls);
	}

	[Fact]
	public void Set_BackendKey_IsCheckedAgainstTheProvidersOwnSchema()
	{
		// Nothing serves the role yet, so there is no shape to check against.
		Assert.Equal(0, Run("config", "set", "ActiveSync:Backends:MailStore:Port", "abc").ExitCode);
		Assert.Equal(0, Run("config", "unset", "ActiveSync:Backends:MailStore:Port").ExitCode);

		// The provider must be one that exists AND serves the role.
		(int unknown, string unknownErr, _) = Run("config", "set", "ActiveSync:Backends:MailStore:Provider", "nope");
		Assert.Equal(1, unknown);
		Assert.Contains("No backend provider named", unknownErr);

		(int wrongRole, string wrongRoleErr, _) = Run("config", "set", "ActiveSync:Backends:Contacts:Provider", "imap");
		Assert.Equal(1, wrongRole);
		Assert.Contains("does not support the Contacts role", wrongRoleErr);

		// Once imap serves the role, its own field descriptions apply — including to a value
		// stored in the database, which is where the assignment usually lives.
		Assert.Equal(0, Run("config", "set", "ActiveSync:Backends:MailStore:Provider", "imap").ExitCode);

		(int badPort, string badPortErr, _) = Run("config", "set", "ActiveSync:Backends:MailStore:Port", "abc");
		Assert.Equal(1, badPort);
		Assert.Contains("whole number", badPortErr);

		(int range, string rangeErr, _) = Run("config", "set", "ActiveSync:Backends:MailStore:Port", "99999");
		Assert.Equal(1, range);
		Assert.Contains("at most 65535", rangeErr);

		(int badEnum, string enumErr, _) = Run("config", "set", "ActiveSync:Backends:MailStore:Security", "Quantum");
		Assert.Equal(1, badEnum);
		Assert.Contains("is unknown", enumErr);

		Assert.Equal(0, Run("config", "set", "ActiveSync:Backends:MailStore:Port", "993").ExitCode);

		// A key imap does not describe stays settable: plugin providers may describe nothing.
		Assert.Equal(0, Run("config", "set", "ActiveSync:Backends:MailStore:FutureKnob", "42").ExitCode);
	}
}

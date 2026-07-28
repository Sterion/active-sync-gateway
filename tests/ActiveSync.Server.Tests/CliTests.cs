using ActiveSync.Core.Security;
using ActiveSync.Server.Cli;
using Spectre.Console.Cli.Testing;

namespace ActiveSync.Server.Tests;

/// <summary>
///   CLI surface tests via CommandAppTester, running the exact registrations Program uses.
///   Verb output goes through Console (not IAnsiConsole) so stdout is captured explicitly.
///   The "cli" collection keeps env-var-touching CLI test classes sequential.
/// </summary>
[Collection("cli")]
public class CliTests
{
	private static CommandAppTester CreateTester()
	{
		CommandAppTester tester = new();
		tester.SetDefaultCommand<BannerCommand>();
		tester.Configure(CliApp.Configure);
		return tester;
	}

	private static (int ExitCode, string StdOut, string StdErr) RunCaptured(
		CommandAppTester tester, string[] args, string? stdin = null)
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
			CommandAppResult result = tester.Run(args);
			return (result.ExitCode, stdout.ToString(), stderr.ToString());
		}
		finally
		{
			Console.SetOut(originalOut);
			Console.SetError(originalError);
			Console.SetIn(originalIn);
		}
	}

	[Fact]
	public void Help_ListsAllCommands()
	{
		CommandAppTester tester = CreateTester();
		CommandAppResult result = tester.Run("--help");

		Assert.Equal(0, result.ExitCode);
		Assert.Contains("serve", result.Output);
		Assert.Contains("healthcheck", result.Output);
		Assert.Contains("protect", result.Output);
		Assert.Contains("hash-password", result.Output);
	}

	// E12: the `block` help text used to say "Refuse logins (403) for a user, or for one of their
	// devices" — but BlockCommand refuses a bare user outright (BlockOutcome.DeviceRequired, "A
	// device id is required: blocks are per-device"), matching docs/cli.md ("to refuse a user
	// everywhere, use 'eas user disable'") rather than the help text an operator actually sees.
	[Fact]
	public void Help_Block_DescribesItAsPerDevice_NotPerUser()
	{
		CommandAppTester tester = CreateTester();
		CommandAppResult result = tester.Run("block", "--help");

		Assert.Equal(0, result.ExitCode);
		Assert.Contains("ONE DEVICE", result.Output);
		Assert.Contains("user disable", result.Output);
	}

	[Fact]
	public void HashPassword_HashesStdin()
	{
		(int exitCode, string stdout, _) = RunCaptured(CreateTester(), ["hash-password"], "phone-secret");

		Assert.Equal(0, exitCode);
		string hash = stdout.Trim();
		Assert.StartsWith("pbkdf2$", hash);
		Assert.True(GatewayPasswordHasher.Verify(hash, "phone-secret"));
	}

	[Fact]
	public void HashPassword_EmptyStdin_Fails()
	{
		(int exitCode, _, string stderr) = RunCaptured(CreateTester(), ["hash-password"], "");

		Assert.Equal(1, exitCode);
		Assert.Contains("Usage", stderr);
	}

	// E8 — COVERAGE, NOT PROOF. The fix moves the master-key ZeroMemory into a finally so the
	// empty-secret early return also zeroes it (previously only the successful-seal path did).
	// The key is a local byte[] with no external handle, so the zeroing itself is not observable
	// from a test (mirrors the L42 precedent for UserSecretCommand); this only exercises the
	// early-return path to prove behavior is unchanged (still exits 1 with the usage message)
	// with the finally now wrapping it.
	[Fact]
	public void Protect_EmptySecret_StillFailsCleanly_WithKeyConfigured()
	{
		string? original = Environment.GetEnvironmentVariable("ActiveSync__Encryption__Key");
		try
		{
			Environment.SetEnvironmentVariable("ActiveSync__Encryption__Key", "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=");
			(int exitCode, _, string stderr) = RunCaptured(CreateTester(), ["protect"], "");

			Assert.Equal(1, exitCode);
			Assert.Contains("Usage", stderr);
		}
		finally
		{
			Environment.SetEnvironmentVariable("ActiveSync__Encryption__Key", original);
		}
	}

	[Fact]
	public void Healthcheck_NoServer_ExitsNonZero()
	{
		string? original = Environment.GetEnvironmentVariable("Kestrel__Endpoints__Http__Url");
		try
		{
			// Point at a port nothing listens on so a locally running gateway can't skew the test.
			Environment.SetEnvironmentVariable("Kestrel__Endpoints__Http__Url", "http://localhost:59981");
			(int exitCode, _, _) = RunCaptured(CreateTester(), ["healthcheck"]);
			Assert.Equal(1, exitCode);
		}
		finally
		{
			Environment.SetEnvironmentVariable("Kestrel__Endpoints__Http__Url", original);
		}
	}

	[Fact]
	public void Banner_WithValidConfig_ShowsSummaryWithoutServing()
	{
		// The test output directory carries the server's appsettings.json (example IMAP/SMTP
		// hosts); only the encryption requirement needs satisfying for validation to pass.
		string? original = Environment.GetEnvironmentVariable("ActiveSync__Encryption__AllowPlaintext");
		try
		{
			Environment.SetEnvironmentVariable("ActiveSync__Encryption__AllowPlaintext", "true");
			(int exitCode, string stdout, _) = RunCaptured(CreateTester(), []);

			Assert.Equal(0, exitCode);
			Assert.Contains("ActiveSync gateway", stdout);
			Assert.Contains("NOT running", stdout);
			Assert.Contains("eas serve", stdout);
			// The licence notice is load-bearing, not decoration: a noncommercial licence is
			// far easier to enforce when the terms are put in front of the operator, so a
			// refactor must not quietly drop it. The copyright half comes from the assembly's
			// <Copyright> attribute, so this also catches that going missing.
			Assert.Contains("PolyForm Noncommercial", stdout);
			Assert.Contains("Ruben Andersen", stdout);
		}
		finally
		{
			Environment.SetEnvironmentVariable("ActiveSync__Encryption__AllowPlaintext", original);
		}
	}
}

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
		}
		finally
		{
			Environment.SetEnvironmentVariable("ActiveSync__Encryption__AllowPlaintext", original);
		}
	}
}

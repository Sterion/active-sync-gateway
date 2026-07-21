using System.Net;
using System.Security.Cryptography;
using ActiveSync.Core.State;
using ActiveSync.Crypto;
using ActiveSync.Server.Cli;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace ActiveSync.Server.Tests;

/// <summary>
///   The loopback CLI-forwarding endpoint's in-process execution: <see cref="LocalCliEndpoint" />
///   runs the same Spectre command tree a local <c>eas</c> would, capturing stdout/stderr/exit-code
///   (including from stdin-reading secret verbs), and refuses <c>serve</c>. The HTTP gate itself is
///   covered by <see cref="IsLoopback_OnlyLoopbackPeersPass" /> plus an integration 404 assertion.
/// </summary>
[Collection("cli")]
public sealed class CliLocalEndpointTests : IDisposable
{
	private readonly string _dbPath;
	private readonly Dictionary<string, string?> _originalEnv = [];

	public CliLocalEndpointTests()
	{
		_dbPath = Path.Combine(Path.GetTempPath(), $"as-cli-endpoint-{Guid.NewGuid():N}.db");
		DbContextOptions<SqliteSyncDbContext> options = new DbContextOptionsBuilder<SqliteSyncDbContext>()
			.UseSqlite($"Data Source={_dbPath}")
			.Options;
		using SqliteSyncDbContext db = new(options);
		db.Database.Migrate();
		SetEnv("ActiveSync__Database__ConnectionString", $"Data Source={_dbPath}");
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

	[Fact]
	public async Task ForwardsCommand_CapturesOutputAndExitCode()
	{
		LocalCliEndpoint.CliResponse get = await LocalCliEndpoint.ExecuteAsync(
			["config", "get", "ActiveSync:RequireDeclaredUsers"], "", CancellationToken.None);
		Assert.Equal(0, get.ExitCode);
		Assert.Contains("false", get.Stdout);
		Assert.Contains("source: default", get.Stdout);
		Assert.Equal("", get.Stderr);
	}

	[Fact]
	public async Task ForwardsCommand_AppliesWrites()
	{
		LocalCliEndpoint.CliResponse set = await LocalCliEndpoint.ExecuteAsync(
			["config", "set", "ActiveSync:RequireDeclaredUsers", "true"], "", CancellationToken.None);
		Assert.Equal(0, set.ExitCode);
		Assert.Contains("within ~1s", set.Stdout);

		LocalCliEndpoint.CliResponse get = await LocalCliEndpoint.ExecuteAsync(
			["config", "get", "ActiveSync:RequireDeclaredUsers"], "", CancellationToken.None);
		Assert.Contains("true", get.Stdout);
		Assert.Contains("source: db", get.Stdout);
	}

	[Fact]
	public async Task ForwardsStdin_ForSecretVerbs()
	{
		// hash-password reads the secret from stdin and writes the pbkdf2$ hash to stdout — proves
		// the forwarded stdin is delivered and raw Console.Out is captured.
		LocalCliEndpoint.CliResponse response = await LocalCliEndpoint.ExecuteAsync(
			["hash-password"], "s3cr3t-passphrase", CancellationToken.None);
		Assert.Equal(0, response.ExitCode);
		Assert.StartsWith("pbkdf2$", response.Stdout.Trim());
		Assert.DoesNotContain("s3cr3t-passphrase", response.Stdout);
	}

	[Fact]
	public async Task RefusesServe_WithoutStartingIt()
	{
		LocalCliEndpoint.CliResponse response = await LocalCliEndpoint.ExecuteAsync(
			["serve"], "", CancellationToken.None);
		Assert.NotEqual(0, response.ExitCode);
		Assert.Contains("not available over /cli", response.Stderr);
		Assert.Equal("", response.Stdout);
	}

	[Fact]
	public async Task RendersHelpAndErrors_OnEveryInvocation_NotJustTheFirst()
	{
		// Spectre renders --help, a bare branch's USAGE and unknown-command errors through
		// Settings.Console, which otherwise falls back to a process-static it caches on FIRST use —
		// so in the long-lived gateway every /cli help/error after the first came back empty. Run a
		// normal command first to prime that cache, THEN assert the help/error paths still produce
		// output (the endpoint pins Settings.Console per request to defeat the cache).
		await LocalCliEndpoint.ExecuteAsync(["config", "list"], "", CancellationToken.None);

		LocalCliEndpoint.CliResponse help = await LocalCliEndpoint.ExecuteAsync(
			["--help"], "", CancellationToken.None);
		Assert.Equal(0, help.ExitCode);
		Assert.Contains("USAGE", help.Stdout);

		LocalCliEndpoint.CliResponse branchHelp = await LocalCliEndpoint.ExecuteAsync(
			["config", "--help"], "", CancellationToken.None);
		Assert.Contains("config", branchHelp.Stdout);

		LocalCliEndpoint.CliResponse unknown = await LocalCliEndpoint.ExecuteAsync(
			["cli"], "", CancellationToken.None);
		Assert.NotEqual(0, unknown.ExitCode);
		Assert.NotEqual("", unknown.Stdout + unknown.Stderr);
	}

	[Fact]
	public async Task ForwardedCommand_DoesNotSwallowConcurrentGatewayLogOutput()
	{
		// L25: capturing output swapped the PROCESS-GLOBAL Console.Out/Error, so for the duration of
		// every forwarded command all gateway log output — from every concurrent request — was
		// captured into that command's stdout and vanished from the container log. The console
		// logger writes from a thread that exists before the command starts; simulate exactly that
		// and prove its output still reaches the real console and never enters the response.
		TextWriter originalOut = Console.Out;
		StringWriter containerLog = new();
		using ManualResetEventSlim stop = new();
		Thread gatewayLogger = new(() =>
		{
			while (!stop.IsSet)
			{
				Console.Out.Write("[gateway-log]");
				Thread.Sleep(0);
			}
		})
		{ IsBackground = true };

		LocalCliEndpoint.CliResponse response;
		try
		{
			Console.SetOut(containerLog);
			gatewayLogger.Start();
			response = await LocalCliEndpoint.ExecuteAsync(["config", "list"], "", CancellationToken.None);
		}
		finally
		{
			stop.Set();
			gatewayLogger.Join(TimeSpan.FromSeconds(5));
			Console.SetOut(originalOut);
		}

		Assert.DoesNotContain("[gateway-log]", response.Stdout);
		Assert.Contains("[gateway-log]", containerLog.ToString());
		// The command's own output is still captured — the routing is per async flow, not a no-op.
		Assert.Contains("ActiveSync:", response.Stdout);
	}

	[Fact]
	public void ColorRendering_ForcesAnsiEscapes_ToTheCapturedBuffer()
	{
		// The /cli buffer is a StringWriter, not a terminal. Prove that forcing Ansi+colour makes
		// Spectre emit escapes anyway, so any markup a command DOES colour survives the wire. (Most
		// eas output is plain tables, so the common commands look the same coloured or not.)
		StringWriter sw = new();
		IAnsiConsole console = AnsiConsole.Create(new AnsiConsoleSettings
		{
			Ansi = AnsiSupport.Yes,
			ColorSystem = ColorSystemSupport.Standard,
			Interactive = InteractionSupport.No,
			Out = new AnsiConsoleOutput(sw),
		});
		console.Markup("[red]x[/]");
		Assert.Contains(((char)27) + "[", sw.ToString());
	}

	[Fact]
	public void IsLoopback_OnlyLoopbackPeersPass()
	{
		Assert.False(LocalCliEndpoint.IsLoopback(null));
		Assert.True(LocalCliEndpoint.IsLoopback(IPAddress.Loopback));
		Assert.True(LocalCliEndpoint.IsLoopback(IPAddress.IPv6Loopback));
		Assert.True(LocalCliEndpoint.IsLoopback(IPAddress.Parse("127.0.0.5")));
		Assert.False(LocalCliEndpoint.IsLoopback(IPAddress.Parse("8.8.8.8")));
		Assert.False(LocalCliEndpoint.IsLoopback(IPAddress.Parse("10.0.0.1")));
	}

	/* ---- Envelope auth: proof of the master key, so a keyless co-located caller is refused ------ */

	private static byte[] NewKey() => RandomNumberGenerator.GetBytes(32);

	[Fact]
	public void Authorize_AcceptsAFreshEnvelopeSealedWithTheKey()
	{
		byte[] key = NewKey();
		long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		string sealed_ = new LocalCliEnvelope(["users", "--all"], "pw", now).Seal(key);

		Assert.True(LocalCliEndpoint.TryAuthorize(
			new LocalCliEndpoint.CliRequest(null, null, sealed_), key, allowPlaintext: false, now, out string[] args, out string stdin));
		Assert.Equal(["users", "--all"], args);
		Assert.Equal("pw", stdin);
	}

	[Fact]
	public void Authorize_RejectsWrongKey_MissingSeal_AndPlaintextWhenKeyed()
	{
		byte[] key = NewKey();
		long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		string sealedWithOther = new LocalCliEnvelope(["users"], null, now).Seal(NewKey());

		// Sealed by a DIFFERENT key (a sidecar guessing) — rejected.
		Assert.False(LocalCliEndpoint.TryAuthorize(
			new LocalCliEndpoint.CliRequest(null, null, sealedWithOther), key, allowPlaintext: false, now, out _, out _));
		// No envelope at all — rejected.
		Assert.False(LocalCliEndpoint.TryAuthorize(
			new LocalCliEndpoint.CliRequest(null, null, null), key, allowPlaintext: false, now, out _, out _));
		// A plaintext body is ignored when a key is configured — rejected.
		Assert.False(LocalCliEndpoint.TryAuthorize(
			new LocalCliEndpoint.CliRequest(["users"], null, null), key, allowPlaintext: false, now, out _, out _));
	}

	[Fact]
	public void Authorize_RejectsAReplayedEnvelopeOutsideTheWindow()
	{
		byte[] key = NewKey();
		long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		string stale = new LocalCliEnvelope(["users"], null, now - LocalCliEndpoint.AuthWindowMs - 5_000).Seal(key);

		Assert.False(LocalCliEndpoint.TryAuthorize(
			new LocalCliEndpoint.CliRequest(null, null, stale), key, allowPlaintext: false, now, out _, out _));
	}

	[Fact]
	public void Authorize_NoKey_WithoutAllowPlaintext_Refuses()
	{
		// L22: key absence must NOT be what selects plaintext mode. A key that fails to load (an
		// unreadable KeyFile, a mount that came up late) is indistinguishable from "no key
		// configured", and silently degrading to loopback-only is the model the design rejects.
		// Only an explicit ActiveSync:Encryption:AllowPlaintext may open that door.
		long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		Assert.False(LocalCliEndpoint.TryAuthorize(
			new LocalCliEndpoint.CliRequest(["users"], "in", null), key: null, allowPlaintext: false, now,
			out _, out _));
	}

	[Fact]
	public void Authorize_NoKey_WithAllowPlaintext_AcceptsPlaintext()
	{
		// AllowPlaintext dev/test: nothing to prove, so the plain body passes (loopback still gates).
		long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		Assert.True(LocalCliEndpoint.TryAuthorize(
			new LocalCliEndpoint.CliRequest(["users"], "in", null), key: null, allowPlaintext: true, now,
			out string[] args, out string stdin));
		Assert.Equal(["users"], args);
		Assert.Equal("in", stdin);
	}

	[Fact]
	public void ProtectResponse_SealsOutputWhenKeyed_AndLeavesItPlainOtherwise()
	{
		// L23: `eas device password` prints a live credential. Requests are sealed but responses
		// were not, so the secret travelled loopback in the clear. Seal the response with the same
		// key whenever one is configured; the plaintext fields must be empty on the wire.
		byte[] key = NewKey();
		LocalCliEndpoint.CliResponse plain = new(3, "device-password: hunter2", "a warning");

		LocalCliEndpoint.CliResponse sealedResponse = LocalCliEndpoint.ProtectResponse(plain, key);
		Assert.Equal("", sealedResponse.Stdout);
		Assert.Equal("", sealedResponse.Stderr);
		Assert.Equal(0, sealedResponse.ExitCode);
		Assert.NotNull(sealedResponse.Sealed);
		Assert.DoesNotContain("hunter2", sealedResponse.Sealed);

		Assert.True(LocalCliResult.TryOpen(sealedResponse.Sealed, key, out LocalCliResult? opened));
		Assert.Equal(3, opened!.ExitCode);
		Assert.Equal("device-password: hunter2", opened.Stdout);
		Assert.Equal("a warning", opened.Stderr);
		// A different key opens nothing.
		Assert.False(LocalCliResult.TryOpen(sealedResponse.Sealed, NewKey(), out _));

		// AllowPlaintext dev/test: no key to seal with, so the response stays plain.
		LocalCliEndpoint.CliResponse unkeyed = LocalCliEndpoint.ProtectResponse(plain, key: null);
		Assert.Equal("device-password: hunter2", unkeyed.Stdout);
		Assert.Null(unkeyed.Sealed);
	}

	[Fact]
	public void Audit_RecordsEveryForwardedCommand_WithSecretArgumentsRedacted()
	{
		// L24: account deletion, device-password disclosure and password changes left no record at
		// all. Every forwarded command must produce one log line — and it must be safe to keep, so
		// the value following a secret-named option or field is redacted (stdin is never logged).
		RecordingLogger logger = new();

		LocalCliEndpoint.AuditCommand(logger, ["purge", "user", "alice@example.com", "--yes"], 0, 12, true);
		LocalCliEndpoint.AuditCommand(logger, ["config", "set", "ActiveSync:Encryption:Key", "s3cr3t"], 0, 3, true);
		LocalCliEndpoint.AuditCommand(logger, ["user", "set", "bob", "Backends:MailStore:Settings:Password", "hunter2"], 1, 4, false);
		LocalCliEndpoint.AuditCommand(logger, [], 0, 1, true);

		Assert.Equal(4, logger.Messages.Count);
		Assert.Contains("purge user alice@example.com --yes", logger.Messages[0]);
		Assert.Contains("exit 0", logger.Messages[0]);
		Assert.Contains("sealed", logger.Messages[0]);

		Assert.Contains("ActiveSync:Encryption:Key ***", logger.Messages[1]);
		Assert.DoesNotContain("s3cr3t", logger.Messages[1]);

		Assert.Contains("Backends:MailStore:Settings:Password ***", logger.Messages[2]);
		Assert.DoesNotContain("hunter2", logger.Messages[2]);
		Assert.Contains("exit 1", logger.Messages[2]);
		Assert.Contains("plaintext", logger.Messages[2]);

		Assert.Contains("(no arguments)", logger.Messages[3]);
	}

	[Fact]
	public void Audit_RedactsInlineAndOptionSecrets_ButKeepsTheVerbLegible()
	{
		Assert.Equal("device password alice DEV123", LocalCliEndpoint.DescribeCommand(
			["device", "password", "alice", "DEV123"]));
		Assert.Equal("user password bob --password ***", LocalCliEndpoint.DescribeCommand(
			["user", "password", "bob", "--password", "hunter2"]));
		Assert.Equal("user password bob --password=***", LocalCliEndpoint.DescribeCommand(
			["user", "password", "bob", "--password=hunter2"]));
		// A secret-named token with nothing after it must not swallow a following option.
		Assert.Equal("config set ActiveSync:Encryption:Key --force", LocalCliEndpoint.DescribeCommand(
			["config", "set", "ActiveSync:Encryption:Key", "--force"]));
	}

	private sealed class RecordingLogger : ILogger
	{
		public List<string> Messages { get; } = [];

		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

		public bool IsEnabled(LogLevel logLevel) => true;

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
			Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
	}

	[Fact]
	public void Authorize_KeyConfigured_IgnoresAllowPlaintext()
	{
		// A key wins over the flag: a plaintext body is still refused, so a stray AllowPlaintext in
		// a production config can't be used to bypass the envelope.
		byte[] key = NewKey();
		long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		Assert.False(LocalCliEndpoint.TryAuthorize(
			new LocalCliEndpoint.CliRequest(["users"], "in", null), key, allowPlaintext: true, now, out _, out _));
	}
}

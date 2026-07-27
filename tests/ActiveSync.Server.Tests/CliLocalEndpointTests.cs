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
			["config", "get", "ActiveSync:ReadOnly"], "", CancellationToken.None);
		Assert.Equal(0, get.ExitCode);
		Assert.Contains("false", get.Stdout);
		Assert.Contains("source: default", get.Stdout);
		Assert.Equal("", get.Stderr);
	}

	[Fact]
	public async Task ForwardsCommand_AppliesWrites()
	{
		LocalCliEndpoint.CliResponse set = await LocalCliEndpoint.ExecuteAsync(
			["config", "set", "ActiveSync:ReadOnly", "true"], "", CancellationToken.None);
		Assert.Equal(0, set.ExitCode);
		Assert.Contains("within ~1s", set.Stdout);

		LocalCliEndpoint.CliResponse get = await LocalCliEndpoint.ExecuteAsync(
			["config", "get", "ActiveSync:ReadOnly"], "", CancellationToken.None);
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
	public async Task ForwardedHelp_UsesTheSamePreCliAlias_AsTheLocalDispatcher()
	{
		// E10: `eas help` is documented (docs/cli.md) and works locally because Program.cs's
		// pre-parse dispatch translates ["help"] to ["--help"] before Spectre ever sees it — but
		// LocalCliEndpoint.RunCapturedAsync applies CliApp.Configure directly to the raw forwarded
		// args, which has no "help" command registered, so a FORWARDED `eas help` hit Spectre's
		// unknown-command path and exited non-zero instead of listing every command.
		LocalCliEndpoint.CliResponse help = await LocalCliEndpoint.ExecuteAsync(
			["help"], "", CancellationToken.None);
		Assert.Equal(0, help.ExitCode);
		Assert.Contains("USAGE", help.Stdout);

		// The pre-CLI healthcheck spelling gets the same treatment.
		LocalCliEndpoint.CliResponse healthcheck = await LocalCliEndpoint.ExecuteAsync(
			["--healthcheck"], "", CancellationToken.None);
		Assert.Equal(1, healthcheck.ExitCode); // no gateway listening on the probed URL in-process
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

	// E3 — COVERAGE, NOT PROOF. RunCapturedAsync's writer wiring is a private local, and no command
	// in the current tree fans out concurrent writes through both the Console-routing path and
	// AnsiConsole, so the actual race can't be triggered end-to-end through ExecuteAsync. These two
	// tests reproduce the IDENTICAL wiring pattern standalone: one proves the "before" shape (a
	// TextWriter.Synchronized wrapper on one path, the raw StringWriter directly on the other —
	// Synchronized locks on ITSELF, not on the StringWriter, so the two paths still race on the
	// shared buffer underneath) reliably corrupts output under concurrent load; the other proves the
	// fix (both paths sharing ONE synchronized instance, exactly what RunCapturedAsync now does)
	// does not.
	[Fact]
	public void UnfixedPattern_TwoIndependentWrappersOverOneStringWriter_CorruptsUnderConcurrentWrites()
	{
		const int threads = 8;
		const int itersPerThread = 4000;
		const string chunk = "0123456789";
		long expectedLength = (long)threads * itersPerThread * chunk.Length;

		bool corrupted = false;
		for (int attempt = 0; attempt < 10 && !corrupted; attempt++)
		{
			StringWriter outWriter = new();
			TextWriter synced = TextWriter.Synchronized(outWriter); // mirrors the router path
			TextWriter raw = outWriter;                              // mirrors AnsiConsoleOutput(outWriter), unfixed

			Exception? caught = null;
			Thread[] pool = new Thread[threads];
			for (int t = 0; t < threads; t++)
			{
				TextWriter w = t % 2 == 0 ? synced : raw;
				pool[t] = new Thread(() =>
				{
					try
					{
						for (int i = 0; i < itersPerThread; i++)
							w.Write(chunk);
					}
					catch (Exception ex)
					{
						caught = ex;
					}
				});
			}

			foreach (Thread th in pool) th.Start();
			foreach (Thread th in pool) th.Join();

			corrupted = caught is not null || outWriter.ToString().Length != expectedLength;
		}

		Assert.True(corrupted,
			"expected the unfixed dual-writer pattern (independent Synchronized wrapper + raw " +
			"StringWriter) to corrupt the shared buffer at least once in 10 concurrent attempts");
	}

	[Fact]
	public void FixedPattern_OneSharedSynchronizedWriter_NeverCorrupts_UnderTheSameConcurrentLoad()
	{
		const int threads = 8;
		const int itersPerThread = 4000;
		const string chunk = "0123456789";
		long expectedLength = (long)threads * itersPerThread * chunk.Length;

		for (int attempt = 0; attempt < 10; attempt++)
		{
			StringWriter outWriter = new();
			TextWriter sharedSynced = TextWriter.Synchronized(outWriter); // ONE instance, both paths

			Exception? caught = null;
			Thread[] pool = new Thread[threads];
			for (int t = 0; t < threads; t++)
			{
				pool[t] = new Thread(() =>
				{
					try
					{
						for (int i = 0; i < itersPerThread; i++)
							sharedSynced.Write(chunk);
					}
					catch (Exception ex)
					{
						caught = ex;
					}
				});
			}

			foreach (Thread th in pool) th.Start();
			foreach (Thread th in pool) th.Join();

			Assert.Null(caught);
			Assert.Equal(expectedLength, outWriter.ToString().Length);
		}
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

	// E18 — COVERAGE, NOT PROOF. The finding's concern is that an unclamped, caller-supplied `width`
	// reaches Spectre's layout engine unbounded, and "at worst" could drive an expensive allocation
	// inside the long-lived gateway. Tried end-to-end first: driving every actual /cli command this
	// tree exposes (via ExecuteAsync) with width values from 1 up to int.MaxValue produced no
	// measurable time or memory difference and no exception — none of today's commands render a
	// construct (Table.Expand(), Grid, Rule, Canvas) whose COST is driven by Profile.Width, only ones
	// sized to their own content. So the described symptom cannot be exhibited through this app's
	// current command surface, and there is nothing to watch fail red on unmodified code. What CAN be
	// shown is the underlying mechanism: a Spectre construct that genuinely sizes itself to the full
	// profile width (a horizontal Rule is the simplest — it draws exactly Profile.Width characters)
	// turns an unclamped caller-supplied width directly into proportional output. This reproduces that
	// mechanism standalone, under the exact assignment RunCapturedAsync used before the fix
	// (`width > 0 ? width : 200`, with no upper bound) versus the fixed one
	// (`width is > 0 and <= 1000 ? width : 200`), so the clamp is proven correct even though no
	// current in-repo command can be driven to reproduce the allocation itself.
	[Fact]
	public void UnfixedWidthExpression_LetsAnUnboundedCallerValue_DriveRuleOutputProportionally()
	{
		int attackerWidth = 200_000;
		StringWriter sw = new();
		IAnsiConsole console = AnsiConsole.Create(new AnsiConsoleSettings
		{
			Ansi = AnsiSupport.No, ColorSystem = ColorSystemSupport.NoColors, Interactive = InteractionSupport.No,
			Out = new AnsiConsoleOutput(sw),
		});
		// The UNFIXED expression from LocalCliEndpoint.RunCapturedAsync before E18.
		console.Profile.Width = attackerWidth > 0 ? attackerWidth : 200;
		console.Write(new Rule());

		Assert.True(sw.ToString().Length > 100_000,
			$"expected the unclamped width ({attackerWidth}) to drive Rule's output size " +
			$"proportionally; got {sw.ToString().Length} chars");
	}

	[Fact]
	public void FixedWidthExpression_BoundsRuleOutput_ForAnyCallerSuppliedValue()
	{
		// The FIXED expression (E18): whatever the caller sends, Profile.Width never exceeds 1000.
		foreach (int attackerWidth in new[] { 200_000, int.MaxValue - 1, -7, 0, 1 })
		{
			StringWriter sw = new();
			IAnsiConsole console = AnsiConsole.Create(new AnsiConsoleSettings
			{
				Ansi = AnsiSupport.No, ColorSystem = ColorSystemSupport.NoColors, Interactive = InteractionSupport.No,
				Out = new AnsiConsoleOutput(sw),
			});
			console.Profile.Width = attackerWidth is > 0 and <= 1000 ? attackerWidth : 200;
			console.Write(new Rule());

			Assert.True(sw.ToString().Length <= 1100,
				$"width={attackerWidth} produced {sw.ToString().Length} chars — the clamp must bound it");
		}
	}

	/* ---- Envelope auth: proof of the master key, so a keyless co-located caller is refused ------ */

	private static byte[] NewKey() => RandomNumberGenerator.GetBytes(32);

	[Fact]
	public void Authorize_AcceptsAFreshEnvelopeSealedWithTheKey()
	{
		byte[] key = NewKey();
		long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		string sealed_ = LocalCliEnvelope.Create(["users", "--all"], "pw", now).Seal(key);

		Assert.True(LocalCliEndpoint.TryAuthorize(
			new LocalCliEndpoint.CliRequest(null, null, sealed_), key, allowPlaintext: false, now, new LocalCliEndpoint.ReplayCache(LocalCliEndpoint.AuthWindowMs), out string[] args, out string stdin));
		Assert.Equal(["users", "--all"], args);
		Assert.Equal("pw", stdin);
	}

	[Fact]
	public void Authorize_RejectsWrongKey_MissingSeal_AndPlaintextWhenKeyed()
	{
		byte[] key = NewKey();
		long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		string sealedWithOther = LocalCliEnvelope.Create(["users"], null, now).Seal(NewKey());

		// Sealed by a DIFFERENT key (a sidecar guessing) — rejected.
		Assert.False(LocalCliEndpoint.TryAuthorize(
			new LocalCliEndpoint.CliRequest(null, null, sealedWithOther), key, allowPlaintext: false, now, new LocalCliEndpoint.ReplayCache(LocalCliEndpoint.AuthWindowMs), out _, out _));
		// No envelope at all — rejected.
		Assert.False(LocalCliEndpoint.TryAuthorize(
			new LocalCliEndpoint.CliRequest(null, null, null), key, allowPlaintext: false, now, new LocalCliEndpoint.ReplayCache(LocalCliEndpoint.AuthWindowMs), out _, out _));
		// A plaintext body is ignored when a key is configured — rejected.
		Assert.False(LocalCliEndpoint.TryAuthorize(
			new LocalCliEndpoint.CliRequest(["users"], null, null), key, allowPlaintext: false, now, new LocalCliEndpoint.ReplayCache(LocalCliEndpoint.AuthWindowMs), out _, out _));
	}

	[Fact]
	public void Authorize_RejectsAReplayedEnvelopeOutsideTheWindow()
	{
		byte[] key = NewKey();
		long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		string stale = LocalCliEnvelope.Create(["users"], null, now - LocalCliEndpoint.AuthWindowMs - 5_000).Seal(key);

		Assert.False(LocalCliEndpoint.TryAuthorize(
			new LocalCliEndpoint.CliRequest(null, null, stale), key, allowPlaintext: false, now, new LocalCliEndpoint.ReplayCache(LocalCliEndpoint.AuthWindowMs), out _, out _));
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
			new LocalCliEndpoint.CliRequest(["users"], "in", null), key: null, allowPlaintext: false, now, new LocalCliEndpoint.ReplayCache(LocalCliEndpoint.AuthWindowMs),
			out _, out _));
	}

	[Fact]
	public void Authorize_NoKey_WithAllowPlaintext_AcceptsPlaintext()
	{
		// AllowPlaintext dev/test: nothing to prove, so the plain body passes (loopback still gates).
		long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		Assert.True(LocalCliEndpoint.TryAuthorize(
			new LocalCliEndpoint.CliRequest(["users"], "in", null), key: null, allowPlaintext: true, now, new LocalCliEndpoint.ReplayCache(LocalCliEndpoint.AuthWindowMs),
			out string[] args, out string stdin));
		Assert.Equal(["users"], args);
		Assert.Equal("in", stdin);
	}

	[Fact]
	public void Authorize_RefusesTheSecondUseOfAnEnvelope_WithinTheWindow()
	{
		// L27: replay was bounded by time but not identity, so a captured envelope re-executed a
		// destructive verb for as long as the window lasted. Each envelope carries a nonce and is
		// good for exactly one execution.
		byte[] key = NewKey();
		long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		string sealed_ = LocalCliEnvelope.Create(["purge", "user", "alice@example.com", "--yes"], null, now).Seal(key);
		LocalCliEndpoint.CliRequest request = new(null, null, sealed_);
		LocalCliEndpoint.ReplayCache replay = new(LocalCliEndpoint.AuthWindowMs);

		Assert.True(LocalCliEndpoint.TryAuthorize(request, key, allowPlaintext: false, now, replay, out _, out _));
		// Same bytes, same window, still inside the timestamp bound — refused on the nonce.
		Assert.False(LocalCliEndpoint.TryAuthorize(request, key, allowPlaintext: false, now, replay, out _, out _));
		Assert.False(LocalCliEndpoint.TryAuthorize(request, key, allowPlaintext: false, now + 30_000, replay, out _, out _));

		// A fresh envelope for the same command is fine — it is a new nonce.
		LocalCliEndpoint.CliRequest again = new(null, null,
			LocalCliEnvelope.Create(["purge", "user", "alice@example.com", "--yes"], null, now).Seal(key));
		Assert.True(LocalCliEndpoint.TryAuthorize(again, key, allowPlaintext: false, now, replay, out _, out _));
	}

	[Fact]
	public void ReplayCache_CannotBeReopened_WhenClaimedEarlyByAnEnvelopeReceivedUnderClockSkew()
	{
		// E11: TryClaim recorded the CLAIM time (the receipt clock), not the envelope's own
		// TimestampUnixMs, so an envelope received right at the earliest allowed instant (T minus
		// the forward clock-skew allowance) had its replay-cache entry pruned up to FutureSkewMs
		// BEFORE the envelope itself stopped being acceptable under TryOpen's own window check —
		// reopening single-use for that gap. Claim the envelope at its earliest accepted receipt
		// time, then replay the identical sealed request still inside the envelope's own 60s
		// window but past the receipt-clock-keyed retention: it must stay refused.
		byte[] key = NewKey();
		long t = 1_000_000_000L;
		string sealed_ =
			LocalCliEnvelope.Create(["purge", "user", "alice@example.com", "--yes"], null, t).Seal(key);
		LocalCliEndpoint.CliRequest request = new(null, null, sealed_);
		LocalCliEndpoint.ReplayCache replay = new(LocalCliEndpoint.AuthWindowMs);

		long earliestReceipt = t - LocalCliEnvelope.FutureSkewMs;
		Assert.True(LocalCliEndpoint.TryAuthorize(
			request, key, allowPlaintext: false, earliestReceipt, replay, out _, out _));

		// Still within the envelope's own acceptance window (t + 58s <= t + AuthWindowMs), but more
		// than AuthWindowMs after the receipt-clock timestamp the buggy cache keyed its entry on.
		long replayedAt = t + LocalCliEndpoint.AuthWindowMs - 2_000;
		Assert.False(LocalCliEndpoint.TryAuthorize(
			request, key, allowPlaintext: false, replayedAt, replay, out _, out _));
	}

	[Fact]
	public void Envelope_RejectsAFutureTimestamp_SoTheWindowIsNotDoubled()
	{
		// K54: Math.Abs treated a timestamp 60s in the FUTURE as acceptable as one 60s in the past,
		// so a captured envelope stayed replayable for 120s. Only a small clock skew is allowed
		// forward.
		byte[] key = NewKey();
		long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

		string future = LocalCliEnvelope.Create(["users"], null, now + LocalCliEndpoint.AuthWindowMs - 1_000).Seal(key);
		Assert.False(LocalCliEnvelope.TryOpen(future, key, now, LocalCliEndpoint.AuthWindowMs, out _));

		// A clock a couple of seconds ahead still works, and the backward bound is unchanged.
		string skewed = LocalCliEnvelope.Create(["users"], null, now + 2_000).Seal(key);
		Assert.True(LocalCliEnvelope.TryOpen(skewed, key, now, LocalCliEndpoint.AuthWindowMs, out _));
		string recent = LocalCliEnvelope.Create(["users"], null, now - 30_000).Seal(key);
		Assert.True(LocalCliEnvelope.TryOpen(recent, key, now, LocalCliEndpoint.AuthWindowMs, out _));
	}

	// K16 — N/A for red-first: this is a new opt-in capability, not a fix for defective behavior
	// (LocalCliEndpoint's own ReplayCache already enforced single-use correctly; there was nothing
	// wrong to reproduce). TryOpen previously enforced only the timestamp window and left single-use
	// entirely to the caller's own bookkeeping — undocumented beyond the type's summary. This proves
	// the new `seenNonces` parameter makes ONE call to TryOpen self-enforcing: a second open of the
	// same nonce is rejected even with no external replay cache involved.
	[Fact]
	public void TryOpen_WithSeenNonces_RejectsASecondOpen_OfTheSameEnvelope_WithNoExternalReplayCache()
	{
		byte[] key = NewKey();
		long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		string sealed_ = LocalCliEnvelope.Create(["users"], null, now).Seal(key);
		HashSet<string> seenNonces = new(StringComparer.Ordinal);

		Assert.True(LocalCliEnvelope.TryOpen(sealed_, key, now, LocalCliEndpoint.AuthWindowMs, out LocalCliEnvelope? first, seenNonces));
		Assert.NotNull(first);
		Assert.False(LocalCliEnvelope.TryOpen(sealed_, key, now, LocalCliEndpoint.AuthWindowMs, out LocalCliEnvelope? second, seenNonces));
		Assert.Null(second);

		// Omitting seenNonces (the default) keeps the historical window-only behavior — the same
		// envelope opens repeatedly with no tracking at all.
		Assert.True(LocalCliEnvelope.TryOpen(sealed_, key, now, LocalCliEndpoint.AuthWindowMs, out _));
		Assert.True(LocalCliEnvelope.TryOpen(sealed_, key, now, LocalCliEndpoint.AuthWindowMs, out _));
	}

	[Fact]
	public void TryOpen_RejectsASealedEnvelope_WhoseArgsArrayContainsANullElement()
	{
		// E19: LocalCliEnvelope.Args is declared `string[]` (non-nullable elements), but that is only
		// a compile-time promise — JSON happily deserializes `{"args":["users",null]}` into a
		// string?[] masquerading as string[], and TryOpen previously checked only
		// `decoded.Args is null` (the ARRAY reference), never its elements. A downstream consumer
		// (DescribeCommand's `argument.StartsWith('-')`, Spectre's own parser) then dereferences a
		// null it was promised could never appear.
		byte[] key = NewKey();
		long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		string json = System.Text.Json.JsonSerializer.Serialize(new
		{
			args = new string?[] { "users", null }, stdin = (string?)null, timestampUnixMs = now, nonce = "n1",
		}, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
		string sealedValue = SecretValue.Seal(json, key);

		Assert.False(LocalCliEnvelope.TryOpen(sealedValue, key, now, LocalCliEndpoint.AuthWindowMs, out LocalCliEnvelope? envelope));
		Assert.Null(envelope);

		// A well-formed envelope with no null element still opens normally.
		string wellFormed = LocalCliEnvelope.Create(["users"], null, now).Seal(key);
		Assert.True(LocalCliEnvelope.TryOpen(wellFormed, key, now, LocalCliEndpoint.AuthWindowMs, out _));
	}

	[Fact]
	public void TryAuthorize_PlaintextMode_RejectsArgsContainingANullElement()
	{
		// E19, same hole in the plaintext (AllowPlaintext) branch: a CliRequest's Args come straight
		// from an unattributed JSON body with no envelope at all, so the same `[null]` shape reaches
		// TryAuthorize's plaintext path unfiltered.
		long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		string[] argsWithNull = ["users", null!];

		Assert.False(LocalCliEndpoint.TryAuthorize(
			new LocalCliEndpoint.CliRequest(argsWithNull, null, null), key: null, allowPlaintext: true, now,
			new LocalCliEndpoint.ReplayCache(LocalCliEndpoint.AuthWindowMs), out _, out _));

		// A well-formed plaintext body still authorizes normally.
		Assert.True(LocalCliEndpoint.TryAuthorize(
			new LocalCliEndpoint.CliRequest(["users"], null, null), key: null, allowPlaintext: true, now,
			new LocalCliEndpoint.ReplayCache(LocalCliEndpoint.AuthWindowMs), out string[] args, out _));
		Assert.Equal(["users"], args);
	}

	[Fact]
	public void Envelope_MintsAFreshNonceEachTime_AndRejectsOneWithout()
	{
		byte[] key = NewKey();
		long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		Assert.NotEqual(
			LocalCliEnvelope.Create(["users"], null, now).Nonce,
			LocalCliEnvelope.Create(["users"], null, now).Nonce);

		// An envelope minted without a nonce (an older client) can't be distinguished from a replay,
		// so it is refused outright rather than trusted.
		string nonceless = new LocalCliEnvelope(["users"], null, now, "").Seal(key);
		Assert.False(LocalCliEnvelope.TryOpen(nonceless, key, now, LocalCliEndpoint.AuthWindowMs, out _));
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

	[Fact]
	public void Registrar_BreaksDependencyCycles_InsteadOfKillingTheGateway()
	{
		// L26: the Spectre DI bridge resolved constructor parameters recursively with no cycle
		// detection, so one command type whose graph loops back on itself takes the whole gateway
		// process down with an uncatchable StackOverflowException. A cycle must resolve to null.
		StringWriter sw = new();
		IAnsiConsole console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(sw) });
		LocalCliEndpoint.CapturingRegistrar registrar = new(console);

		SelfReferencing? direct = (SelfReferencing?)registrar.Resolve(typeof(SelfReferencing));
		Assert.NotNull(direct);
		Assert.Null(direct.Inner);

		MutualA? mutual = (MutualA?)registrar.Resolve(typeof(MutualA));
		Assert.NotNull(mutual);
		Assert.Null(mutual.B!.A);

		// Non-cyclic graphs still build, and the console is still injected.
		Assert.Same(console, registrar.Resolve(typeof(IAnsiConsole)));
		Assert.Same(console, ((NeedsConsole?)registrar.Resolve(typeof(NeedsConsole)))!.Console);
	}

	private sealed class SelfReferencing(SelfReferencing? inner)
	{
		public SelfReferencing? Inner { get; } = inner;
	}

	private sealed class MutualA(MutualB? b)
	{
		public MutualB? B { get; } = b;
	}

	private sealed class MutualB(MutualA? a)
	{
		public MutualA? A { get; } = a;
	}

	private sealed class NeedsConsole(IAnsiConsole console)
	{
		public IAnsiConsole Console { get; } = console;
	}

	private sealed class RecordingLogger : ILogger
	{
		public List<string> Messages { get; } = [];

		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

		public bool IsEnabled(LogLevel logLevel) => true;

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
			Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
	}

	/* ---- K7: credential-bearing verbs must not run keyless (their response can't be sealed) --- */

	[Fact]
	public void Authorize_RefusesCredentialBearingVerbs_InAllowPlaintextMode()
	{
		// K7: with no master key configured, ProtectResponse has nothing to seal a response with —
		// so `device password` (prints an escrowed recovery password) and `user secret` (confirms a
		// stored backend password) must not be allowed to run at all in AllowPlaintext mode, or the
		// credential travels the /cli wire in the clear to any loopback peer, including a co-located
		// sidecar that doesn't hold the key.
		long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		LocalCliEndpoint.ReplayCache Fresh() => new(LocalCliEndpoint.AuthWindowMs);

		Assert.False(LocalCliEndpoint.TryAuthorize(
			new LocalCliEndpoint.CliRequest(["device", "password", "alice", "DEV1"], null, null),
			key: null, allowPlaintext: true, now, Fresh(), out _, out _));
		Assert.False(LocalCliEndpoint.TryAuthorize(
			new LocalCliEndpoint.CliRequest(["user", "secret", "alice", "Backends:MailStore:Password"], "pw", null),
			key: null, allowPlaintext: true, now, Fresh(), out _, out _));

		// Case-insensitive, and matched only as the LEADING verb path — not a coincidental later arg.
		Assert.False(LocalCliEndpoint.TryAuthorize(
			new LocalCliEndpoint.CliRequest(["DEVICE", "PASSWORD", "alice", "DEV1"], null, null),
			key: null, allowPlaintext: true, now, Fresh(), out _, out _));
		Assert.True(LocalCliEndpoint.TryAuthorize(
			new LocalCliEndpoint.CliRequest(["user", "show", "device", "password"], null, null),
			key: null, allowPlaintext: true, now, Fresh(), out string[] args, out _));
		Assert.Equal(["user", "show", "device", "password"], args);

		// Every other verb is unaffected.
		Assert.True(LocalCliEndpoint.TryAuthorize(
			new LocalCliEndpoint.CliRequest(["users"], null, null),
			key: null, allowPlaintext: true, now, Fresh(), out _, out _));

		// With a master key configured, the response IS sealed (see ProtectResponse), so these verbs
		// run normally — only the keyless path is refused.
		byte[] key = NewKey();
		string sealedDevicePassword =
			LocalCliEnvelope.Create(["device", "password", "alice", "DEV1"], null, now).Seal(key);
		Assert.True(LocalCliEndpoint.TryAuthorize(
			new LocalCliEndpoint.CliRequest(null, null, sealedDevicePassword),
			key, allowPlaintext: false, now, Fresh(), out string[] sealedArgs, out _));
		Assert.Equal(["device", "password", "alice", "DEV1"], sealedArgs);
	}

	[Fact]
	public void Authorize_KeyConfigured_IgnoresAllowPlaintext()
	{
		// A key wins over the flag: a plaintext body is still refused, so a stray AllowPlaintext in
		// a production config can't be used to bypass the envelope.
		byte[] key = NewKey();
		long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		Assert.False(LocalCliEndpoint.TryAuthorize(
			new LocalCliEndpoint.CliRequest(["users"], "in", null), key, allowPlaintext: true, now, new LocalCliEndpoint.ReplayCache(LocalCliEndpoint.AuthWindowMs), out _, out _));
	}
}

using System.Net;
using System.Security.Cryptography;
using ActiveSync.Core.State;
using ActiveSync.Crypto;
using ActiveSync.Server.Cli;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

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
			new LocalCliEndpoint.CliRequest(null, null, sealed_), key, now, out string[] args, out string stdin));
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
			new LocalCliEndpoint.CliRequest(null, null, sealedWithOther), key, now, out _, out _));
		// No envelope at all — rejected.
		Assert.False(LocalCliEndpoint.TryAuthorize(
			new LocalCliEndpoint.CliRequest(null, null, null), key, now, out _, out _));
		// A plaintext body is ignored when a key is configured — rejected.
		Assert.False(LocalCliEndpoint.TryAuthorize(
			new LocalCliEndpoint.CliRequest(["users"], null, null), key, now, out _, out _));
	}

	[Fact]
	public void Authorize_RejectsAReplayedEnvelopeOutsideTheWindow()
	{
		byte[] key = NewKey();
		long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		string stale = new LocalCliEnvelope(["users"], null, now - LocalCliEndpoint.AuthWindowMs - 5_000).Seal(key);

		Assert.False(LocalCliEndpoint.TryAuthorize(
			new LocalCliEndpoint.CliRequest(null, null, stale), key, now, out _, out _));
	}

	[Fact]
	public void Authorize_NoKeyConfigured_AcceptsPlaintext()
	{
		// AllowPlaintext dev/test: nothing to prove, so the plain body passes (loopback still gates).
		long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		Assert.True(LocalCliEndpoint.TryAuthorize(
			new LocalCliEndpoint.CliRequest(["users"], "in", null), key: null, now, out string[] args, out string stdin));
		Assert.Equal(["users"], args);
		Assert.Equal("in", stdin);
	}
}

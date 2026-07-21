using System.Security.Cryptography;
using System.Text.Json;
using ActiveSync.Core.Security;

namespace ActiveSync.Crypto;

/// <summary>
///   The shared sealed-request format for the gateway's loopback <c>/cli</c> endpoint. The slim
///   <c>eas</c> client seals this with the <c>ActiveSync:Encryption</c> master key; the gateway
///   opens it with the same key. Because the key is injected only into the gateway container (not a
///   co-located Kubernetes sidecar or a host-network peer), a valid envelope PROVES the caller is a
///   real key holder — the same trust set that can already decrypt everything at rest — closing the
///   gap that loopback alone leaves in a shared network namespace. The timestamp bounds replay of a
///   sniffed ciphertext in time, and the nonce bounds it in count: the gateway remembers the nonces
///   it has already executed for the length of the window, so an envelope runs exactly once.
/// </summary>
public sealed record LocalCliEnvelope(string[] Args, string? Stdin, long TimestampUnixMs, string Nonce)
{
	private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

	/// <summary>
	///   How far a timestamp may run AHEAD of the gateway's clock. Deliberately far smaller than the
	///   backwards window: a future timestamp buys an attacker replay time, and the two clocks are
	///   the same machine's in the deployment this endpoint exists for.
	/// </summary>
	public const long FutureSkewMs = 5_000;

	/// <summary>Mints an envelope with a fresh 128-bit nonce — the normal way for a client to build one.</summary>
	public static LocalCliEnvelope Create(string[] args, string? stdin, long nowUnixMs) =>
		new(args, stdin, nowUnixMs, Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)));

	/// <summary>AES-256-GCM seals the envelope with the master key (reuses the <c>enc:v1:</c> format).</summary>
	public string Seal(byte[] key) => SecretValue.Seal(JsonSerializer.Serialize(this, Json), key);

	/// <summary>
	///   Opens a sealed envelope: unseals with the key (wrong/absent key ⇒ false), then rejects it
	///   when it carries no nonce, when it is older than <paramref name="windowMs" />, or when it is
	///   dated more than <see cref="FutureSkewMs" /> ahead of <paramref name="nowUnixMs" />. The
	///   bound is deliberately asymmetric: an absolute-value window accepts a future timestamp just
	///   as readily as a past one, which doubles the time a captured envelope stays live. Returns
	///   false — never throws — on any malformed or unauthenticated input.
	/// </summary>
	public static bool TryOpen(string? sealedValue, byte[] key, long nowUnixMs, long windowMs, out LocalCliEnvelope? envelope)
	{
		envelope = null;
		if (string.IsNullOrEmpty(sealedValue))
			return false;
		if (!SecretValue.TryUnseal(sealedValue, key, out string? json, out _) || json is null)
			return false;

		LocalCliEnvelope? decoded;
		try
		{
			decoded = JsonSerializer.Deserialize<LocalCliEnvelope>(json, Json);
		}
		catch (JsonException)
		{
			return false;
		}

		if (decoded is null || decoded.Args is null || string.IsNullOrEmpty(decoded.Nonce))
			return false;
		if (decoded.TimestampUnixMs - nowUnixMs > FutureSkewMs)
			return false;
		if (nowUnixMs - decoded.TimestampUnixMs > windowMs)
			return false;

		envelope = decoded;
		return true;
	}
}

/// <summary>
///   The response half of the <c>/cli</c> exchange. Requests are sealed because they carry secrets;
///   so do plenty of responses (<c>eas device password</c> prints a live credential, <c>eas user
///   secret</c> echoes what it stored), so whenever a master key is configured the gateway seals the
///   captured stdout/stderr/exit-code with it and the client opens it. No timestamp: a response is
///   only ever produced for a caller that already proved key possession, so there is nothing a
///   replay of it can reach.
/// </summary>
public sealed record LocalCliResult(int ExitCode, string Stdout, string Stderr)
{
	private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

	/// <summary>AES-256-GCM seals the result with the master key (reuses the <c>enc:v1:</c> format).</summary>
	public string Seal(byte[] key) => SecretValue.Seal(JsonSerializer.Serialize(this, Json), key);

	/// <summary>
	///   Opens a sealed result. Returns false — never throws — on any malformed, absent or
	///   unauthenticated input (a wrong key included).
	/// </summary>
	public static bool TryOpen(string? sealedValue, byte[] key, out LocalCliResult? result)
	{
		result = null;
		if (string.IsNullOrEmpty(sealedValue))
			return false;
		if (!SecretValue.TryUnseal(sealedValue, key, out string? json, out _) || json is null)
			return false;

		try
		{
			result = JsonSerializer.Deserialize<LocalCliResult>(json, Json);
		}
		catch (JsonException)
		{
			return false;
		}

		return result is not null;
	}
}

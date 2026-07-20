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
///   sniffed ciphertext.
/// </summary>
public sealed record LocalCliEnvelope(string[] Args, string? Stdin, long TimestampUnixMs)
{
	private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

	/// <summary>AES-256-GCM seals the envelope with the master key (reuses the <c>enc:v1:</c> format).</summary>
	public string Seal(byte[] key) => SecretValue.Seal(JsonSerializer.Serialize(this, Json), key);

	/// <summary>
	///   Opens a sealed envelope: unseals with the key (wrong/absent key ⇒ false), then rejects it
	///   when the timestamp is outside <paramref name="windowMs" /> of <paramref name="nowUnixMs" />
	///   (a replayed capture). Returns false — never throws — on any malformed or unauthenticated input.
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

		if (decoded is null || decoded.Args is null)
			return false;
		if (Math.Abs(nowUnixMs - decoded.TimestampUnixMs) > windowMs)
			return false;

		envelope = decoded;
		return true;
	}
}

using System.Text;

namespace ActiveSync.Crypto;

/// <summary>
///   Seals configuration secrets (backend passwords) with the ActiveSync:Encryption master
///   key so the users file can live in a ConfigMap instead of a Secret. Format:
///   "enc:v1:" + base64(12-byte nonce ‖ ciphertext ‖ 16-byte tag), AES-256-GCM.
///   The "enc:v1:" prefix is shared by THREE distinct message types — a config secret
///   (this type's default AAD), the <c>/cli</c> REQUEST envelope and the <c>/cli</c> RESPONSE
///   (both in <see cref="LocalCliEnvelope" />) — so each caller now supplies its OWN AAD via
///   the <see cref="Seal(string, byte[], byte[])" />/<see cref="TryUnseal(string, byte[], byte[], out string?, out string?)" />
///   overloads rather than sharing one process-wide constant. Domain separation lives in the
///   AAD, not in the incidental difference between the sealed JSON shapes: before this, a
///   ciphertext sealed for one type authenticated just as well through another type's unseal
///   path.
///   These ciphertexts are never interchangeable with <see cref="LocalContentProtector" />
///   rows regardless of which AAD a caller here uses — the prefix alone already differs
///   ("enc:v1:" vs "v1:"), and the protector's own AAD is the versioned length-prefixed
///   "v2" ‖ LE64(userId) ‖ LE32(len) ‖ collection framing (not a delimited "user\ncollection"
///   string), keyed off the immutable UserId rather than a login.
/// </summary>
public static class SecretValue
{
	public const string Prefix = "enc:v1:";

	private static readonly byte[] ConfigAad = Encoding.UTF8.GetBytes("activesync:config:v1");

	public static bool IsSealed(string value)
	{
		return SealedBlob.IsSealed(value, Prefix);
	}

	/// <summary>Seals a configuration secret under the default config AAD.</summary>
	public static string Seal(string plaintext, byte[] key)
	{
		return Seal(plaintext, key, ConfigAad);
	}

	/// <summary>Seals under a caller-supplied AAD so distinct message types never share one.</summary>
	public static string Seal(string plaintext, byte[] key, byte[] aad)
	{
		return SealedBlob.Seal(Prefix, aad, key, plaintext);
	}

	/// <summary>Unseals a configuration secret sealed under the default config AAD.</summary>
	public static bool TryUnseal(string sealedValue, byte[] key, out string? plaintext, out string? error)
	{
		return TryUnseal(sealedValue, key, ConfigAad, out plaintext, out error);
	}

	/// <summary>Unseals with a caller-supplied AAD — the counterpart to <see cref="Seal(string, byte[], byte[])" />.</summary>
	public static bool TryUnseal(
		string sealedValue, byte[] key, byte[] aad, out string? plaintext, out string? error)
	{
		if (SealedBlob.TryUnseal(Prefix, aad, key, sealedValue, out plaintext, out SealedBlobError blobError, out _))
		{
			error = null;
			return true;
		}

		error = blobError switch
		{
			SealedBlobError.MissingPrefix => $"value does not start with '{Prefix}'",
			SealedBlobError.InvalidBase64 => $"value is not valid base64 after the '{Prefix}' prefix",
			SealedBlobError.TooShort => "sealed value is too short to contain nonce and tag",
			SealedBlobError.AuthenticationFailed => "wrong ActiveSync:Encryption key or a tampered value",
			_ => "value could not be unsealed",
		};
		return false;
	}
}

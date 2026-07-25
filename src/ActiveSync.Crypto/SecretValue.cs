using System.Text;

namespace ActiveSync.Crypto;

/// <summary>
///   Seals configuration secrets (backend passwords) with the ActiveSync:Encryption master
///   key so the users file can live in a ConfigMap instead of a Secret. Format:
///   "enc:v1:" + base64(12-byte nonce ‖ ciphertext ‖ 16-byte tag), AES-256-GCM.
///   The AAD is a fixed constant: config values are not bound to a row identity at seal
///   time, and the constant (no '\n') plus the distinct prefix guarantee these ciphertexts
///   are never interchangeable with <see cref="LocalContentProtector" /> rows (whose AAD is
///   always "user\ncollection" and whose prefix is "v1:").
/// </summary>
public static class SecretValue
{
	public const string Prefix = "enc:v1:";

	private static readonly byte[] Aad = Encoding.UTF8.GetBytes("activesync:config:v1");

	public static bool IsSealed(string value)
	{
		return SealedBlob.IsSealed(value, Prefix);
	}

	public static string Seal(string plaintext, byte[] key)
	{
		return SealedBlob.Seal(Prefix, Aad, key, plaintext);
	}

	public static bool TryUnseal(string sealedValue, byte[] key, out string? plaintext, out string? error)
	{
		if (SealedBlob.TryUnseal(Prefix, Aad, key, sealedValue, out plaintext, out SealedBlobError blobError, out _))
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

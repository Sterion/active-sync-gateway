using System.Security.Cryptography;
using System.Text;

namespace ActiveSync.Core.Security;

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

	private const int NonceSize = 12;
	private const int TagSize = 16;
	private static readonly byte[] Aad = Encoding.UTF8.GetBytes("activesync:config:v1");

	public static bool IsSealed(string value)
	{
		return value.StartsWith(Prefix, StringComparison.Ordinal);
	}

	public static string Seal(string plaintext, byte[] key)
	{
		byte[] plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
		byte[] payload = new byte[NonceSize + plaintextBytes.Length + TagSize];
		Span<byte> nonce = payload.AsSpan(0, NonceSize);
		Span<byte> ciphertext = payload.AsSpan(NonceSize, plaintextBytes.Length);
		Span<byte> tag = payload.AsSpan(NonceSize + plaintextBytes.Length, TagSize);

		RandomNumberGenerator.Fill(nonce);
		using AesGcm aes = new(key, TagSize);
		aes.Encrypt(nonce, plaintextBytes, ciphertext, tag, Aad);
		return Prefix + Convert.ToBase64String(payload);
	}

	public static bool TryUnseal(string sealedValue, byte[] key, out string? plaintext, out string? error)
	{
		plaintext = null;
		if (!IsSealed(sealedValue))
		{
			error = "value does not start with 'enc:v1:'";
			return false;
		}

		byte[] payload;
		try
		{
			payload = Convert.FromBase64String(sealedValue[Prefix.Length..]);
		}
		catch (FormatException)
		{
			error = "value is not valid base64 after the 'enc:v1:' prefix";
			return false;
		}

		if (payload.Length < NonceSize + TagSize)
		{
			error = "sealed value is too short to contain nonce and tag";
			return false;
		}

		ReadOnlySpan<byte> nonce = payload.AsSpan(0, NonceSize);
		ReadOnlySpan<byte> ciphertext = payload.AsSpan(NonceSize, payload.Length - NonceSize - TagSize);
		ReadOnlySpan<byte> tag = payload.AsSpan(payload.Length - TagSize, TagSize);
		byte[] plaintextBytes = new byte[ciphertext.Length];

		try
		{
			using AesGcm aes = new(key, TagSize);
			aes.Decrypt(nonce, ciphertext, tag, plaintextBytes, Aad);
		}
		catch (CryptographicException)
		{
			error = "wrong ActiveSync:Encryption key or a tampered value";
			return false;
		}

		plaintext = Encoding.UTF8.GetString(plaintextBytes);
		error = null;
		return true;
	}
}

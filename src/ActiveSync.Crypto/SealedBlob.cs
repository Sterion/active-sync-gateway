using System.Security.Cryptography;
using System.Text;

namespace ActiveSync.Crypto;

/// <summary>
///   Reasons <see cref="SealedBlob.TryUnseal" /> can fail, so each caller can format its own
///   error text / exception (they differ: <c>SecretValue</c> surfaces a per-cause string,
///   <c>LocalContentProtector</c> collapses everything into one "undecryptable row" message).
/// </summary>
public enum SealedBlobError
{
	None,
	MissingPrefix,
	InvalidBase64,
	TooShort,
	AuthenticationFailed,
}

/// <summary>
///   The AES-256-GCM "&lt;prefix&gt;" + base64(12-byte nonce ‖ ciphertext ‖ 16-byte tag) framing
///   shared by every sealed-value format in this codebase (S2 / K11). Extracted so a framing fix
///   — constant-time handling, a v2 layout, a nonce-reuse audit — lands once instead of drifting
///   between independent copies. Callers own their own prefix and AAD, so ciphertexts sealed by
///   different callers still can't be interchanged — only the byte layout and the AES-GCM calls
///   are shared.
/// </summary>
public static class SealedBlob
{
	public const int NonceSize = 12;
	public const int TagSize = 16;

	public static bool IsSealed(string value, string prefix)
	{
		return value.StartsWith(prefix, StringComparison.Ordinal);
	}

	public static string Seal(string prefix, ReadOnlySpan<byte> aad, byte[] key, string plaintext)
	{
		byte[] plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
		byte[] payload = new byte[NonceSize + plaintextBytes.Length + TagSize];
		Span<byte> nonce = payload.AsSpan(0, NonceSize);
		Span<byte> ciphertext = payload.AsSpan(NonceSize, plaintextBytes.Length);
		Span<byte> tag = payload.AsSpan(NonceSize + plaintextBytes.Length, TagSize);

		RandomNumberGenerator.Fill(nonce);
		using AesGcm aes = new(key, TagSize);
		aes.Encrypt(nonce, plaintextBytes, ciphertext, tag, aad);
		return prefix + Convert.ToBase64String(payload);
	}

	public static bool TryUnseal(
		string prefix,
		ReadOnlySpan<byte> aad,
		byte[] key,
		string sealedValue,
		out string? plaintext,
		out SealedBlobError error,
		out Exception? innerException)
	{
		plaintext = null;
		innerException = null;

		if (!IsSealed(sealedValue, prefix))
		{
			error = SealedBlobError.MissingPrefix;
			return false;
		}

		byte[] payload;
		try
		{
			payload = Convert.FromBase64String(sealedValue[prefix.Length..]);
		}
		catch (FormatException ex)
		{
			error = SealedBlobError.InvalidBase64;
			innerException = ex;
			return false;
		}

		if (payload.Length < NonceSize + TagSize)
		{
			error = SealedBlobError.TooShort;
			return false;
		}

		ReadOnlySpan<byte> nonce = payload.AsSpan(0, NonceSize);
		ReadOnlySpan<byte> ciphertext = payload.AsSpan(NonceSize, payload.Length - NonceSize - TagSize);
		ReadOnlySpan<byte> tag = payload.AsSpan(payload.Length - TagSize, TagSize);
		byte[] plaintextBytes = new byte[ciphertext.Length];

		try
		{
			using AesGcm aes = new(key, TagSize);
			aes.Decrypt(nonce, ciphertext, tag, plaintextBytes, aad);
		}
		catch (CryptographicException ex)
		{
			error = SealedBlobError.AuthenticationFailed;
			innerException = ex;
			return false;
		}

		plaintext = Encoding.UTF8.GetString(plaintextBytes);
		error = SealedBlobError.None;
		return true;
	}
}

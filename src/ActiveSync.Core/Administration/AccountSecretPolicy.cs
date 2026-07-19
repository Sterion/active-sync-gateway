using System.Security.Cryptography;
using ActiveSync.Core.Options;
using ActiveSync.Core.Security;

namespace ActiveSync.Core.Administration;

/// <summary>
///   The one place that turns a raw secret into its stored shape, shared by the CLI and the
///   web admin/user APIs so both enforce identical rules: the gateway Password is stored as a
///   pbkdf2$ hash (already-hashed values pass through, sealed values are rejected); backend
///   role passwords are stored sealed (enc:v1:) when the encryption master key exists, sealed
///   values pass through, and pbkdf2$ hashes are rejected (a backend cannot verify a hash).
/// </summary>
internal static class AccountSecretPolicy
{
	/// <summary>What happened to a plaintext input — callers phrase their own warnings.</summary>
	internal enum PlaintextDisposition
	{
		/// <summary>The input was not plaintext (already pbkdf2$/enc:v1:), or was rejected.</summary>
		None,
		Hashed,
		Sealed,

		/// <summary>Stored verbatim — no encryption master key is configured.</summary>
		StoredPlaintext
	}

	/// <summary>Prepared value, or null with <paramref name="Error" /> set.</summary>
	internal sealed record SecretResult(
		string? Value, string? Error, PlaintextDisposition Plaintext = PlaintextDisposition.None);

	internal static SecretResult PrepareGatewayPassword(string raw)
	{
		if (GatewayPasswordHasher.IsHashed(raw))
			return GatewayPasswordHasher.TryParse(raw, out string? error)
				? new SecretResult(raw, null)
				: new SecretResult(null, $"Not a valid pbkdf2$ value: {error}");
		if (SecretValue.IsSealed(raw))
			return new SecretResult(null,
				"The gateway Password takes a pbkdf2$ hash (or plaintext), not an enc:v1: sealed value.");
		return new SecretResult(GatewayPasswordHasher.Hash(raw), null, PlaintextDisposition.Hashed);
	}

	internal static SecretResult PrepareBackendPassword(string raw, EncryptionOptions encryption, string fieldKey)
	{
		if (SecretValue.IsSealed(raw))
			return new SecretResult(raw, null);
		if (GatewayPasswordHasher.IsHashed(raw))
			return new SecretResult(null,
				$"{fieldKey} is a backend password — it must be the real password (sealed enc:v1: or plaintext), " +
				"not a pbkdf2$ hash the backend cannot verify against.");

		byte[]? key = EncryptionKeyLoader.TryLoadKey(encryption, out _);
		if (key is null)
			return new SecretResult(raw, null, PlaintextDisposition.StoredPlaintext);
		string sealedValue = SecretValue.Seal(raw, key);
		CryptographicOperations.ZeroMemory(key);
		return new SecretResult(sealedValue, null, PlaintextDisposition.Sealed);
	}
}

using System.Security.Cryptography;
using ActiveSync.Core.Security;
using ActiveSync.Crypto;

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
	/// <summary>
	///   Minimum length for a plaintext gateway password, enforced by every write surface (CLI,
	///   admin API, self-service portal) through <see cref="PrepareGatewayPassword" /> so none can
	///   set a trivially weak value (C6). Already-hashed/sealed values are validated by shape.
	/// </summary>
	internal const int MinGatewayPasswordLength = 8;

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
		// B19: an empty/whitespace plaintext must never be hashed. Hash("") is a valid pbkdf2$
		// credential and Verify(Hash(""), "") is true, so an empty gateway Password would let an
		// empty Basic-auth password authenticate locally and bypass the backend entirely. An
		// already-hashed/sealed value is handled below (its shape is validated, not its length).
		if (!GatewayPasswordHasher.IsHashed(raw) && !SecretValue.IsSealed(raw) &&
		    string.IsNullOrWhiteSpace(raw))
			return new SecretResult(null,
				"The gateway Password cannot be empty (clear it instead to fall through to the backend).");
		// C6: a strength floor on the one shared policy, so the portal (which used to hash
		// directly), the admin API and the CLI all reject a weak plaintext identically.
		if (!GatewayPasswordHasher.IsHashed(raw) && !SecretValue.IsSealed(raw) &&
		    raw.Length < MinGatewayPasswordLength)
			return new SecretResult(null,
				$"The gateway Password must be at least {MinGatewayPasswordLength} characters.");
		if (GatewayPasswordHasher.IsHashed(raw))
			return GatewayPasswordHasher.TryParse(raw, out string? error)
				? new SecretResult(raw, null)
				: new SecretResult(null, $"Not a valid pbkdf2$ value: {error}");
		if (SecretValue.IsSealed(raw))
			return new SecretResult(null,
				"The gateway Password takes a pbkdf2$ hash (or plaintext), not an enc:v1: sealed value.");
		return new SecretResult(GatewayPasswordHasher.Hash(raw), null, PlaintextDisposition.Hashed);
	}

	/// <summary>
	///   Prepares a catalogue-level secret SETTING (the OIDC client secret, the TLS certificate
	///   password) for storage: an already-sealed value passes through, a plaintext is sealed
	///   (enc:v1:) when the master key exists, refused when a key is configured but cannot be
	///   loaded, and stored verbatim only when no key is configured at all. Shared by
	///   <c>eas config set</c> and the web settings editor so both seal identically (B5).
	/// </summary>
	internal static SecretResult PrepareCatalogueSecret(string raw, EncryptionOptions encryption, string key)
	{
		if (SecretValue.IsSealed(raw))
			return new SecretResult(raw, null);

		byte[]? master = EncryptionKeyLoader.TryLoadKey(encryption, out string? keyError);
		if (master is null)
		{
			if (keyError is not null)
				return new SecretResult(null,
					$"{key}: the ActiveSync:Encryption key is configured but could not be loaded " +
					$"({keyError}); refusing to store the secret in plaintext.");
			return new SecretResult(raw, null, PlaintextDisposition.StoredPlaintext);
		}

		string sealedValue = SecretValue.Seal(raw, master);
		CryptographicOperations.ZeroMemory(master);
		return new SecretResult(sealedValue, null, PlaintextDisposition.Sealed);
	}

	internal static SecretResult PrepareBackendPassword(string raw, EncryptionOptions encryption, string fieldKey)
	{
		if (SecretValue.IsSealed(raw))
			return new SecretResult(raw, null);
		if (GatewayPasswordHasher.IsHashed(raw))
			return new SecretResult(null,
				$"{fieldKey} is a backend password — it must be the real password (sealed enc:v1: or plaintext), " +
				"not a pbkdf2$ hash the backend cannot verify against.");

		byte[]? key = EncryptionKeyLoader.TryLoadKey(encryption, out string? keyError);
		if (key is null)
		{
			// B4: null-with-error means a key IS configured but could not be loaded (e.g. Key and
			// KeyFile both set, or an unreadable KeyFile). Discarding that error and storing the
			// backend password in plaintext hides a misconfiguration behind a silent downgrade —
			// refuse. Null-with-no-error is the legitimate "no key configured" case.
			if (keyError is not null)
				return new SecretResult(null,
					$"{fieldKey}: the ActiveSync:Encryption key is configured but could not be loaded " +
					$"({keyError}); refusing to store the backend password in plaintext.");
			return new SecretResult(raw, null, PlaintextDisposition.StoredPlaintext);
		}

		string sealedValue = SecretValue.Seal(raw, key);
		CryptographicOperations.ZeroMemory(key);
		return new SecretResult(sealedValue, null, PlaintextDisposition.Sealed);
	}
}

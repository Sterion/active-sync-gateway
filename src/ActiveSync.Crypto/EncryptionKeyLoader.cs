using System.Security.Cryptography;
using System.Text;

namespace ActiveSync.Crypto;

/// <summary>
///   Loads the local-content encryption key from configuration. The value may be ANY
///   string: canonical base64 decoding to exactly 32 bytes is used as the raw key (the
///   high-entropy path: 'openssl rand -base64 32'); anything else is treated as a passphrase
///   and stretched to 32 bytes with PBKDF2-SHA256 over the PER-DEPLOYMENT salt from
///   <see cref="EncryptionOptions.KeyDerivationSalt" />. The passphrase path is REFUSED when
///   that salt is unset (K1): a single global application salt would let one precomputed
///   rainbow table recover the master key of every default deployment, so the default is
///   fail-closed rather than silently sharing a salt. Derivation stays deterministic from
///   configuration alone (nothing is stored), so the gateway and the slim CLI derive the same
///   key. Shared by startup validation, DI and the protect verb so the rules exist exactly
///   once. Whitespace-only values are treated as absent (empty placeholders in appsettings.json).
/// </summary>
public static class EncryptionKeyLoader
{
	/// <summary>Passphrases shorter than this trigger a startup-banner warning (but still load).</summary>
	public const int ShortPassphraseLength = 12;

	/// <summary>
	///   Hard minimum passphrase length (K46): a shorter passphrase is refused, not merely warned
	///   about. The raw base64-32-byte key path is exempt (it is not a passphrase).
	/// </summary>
	public const int MinPassphraseLength = 8;

	private const int KeySize = 32;
	private const int DerivationIterations = 200_000;

	/// <summary>
	///   Returns the 32-byte key, or null with <paramref name="error" /> set when the
	///   configuration is invalid (Key+KeyFile both set, or KeyFile missing/unreadable), or
	///   null with a null error when no key source is configured (the caller decides whether
	///   AllowPlaintext makes that acceptable). Callers should zero the returned buffer once
	///   it has been handed off.
	/// </summary>
	public static byte[]? TryLoadKey(EncryptionOptions options, out string? error)
	{
		string? material = LoadKeyMaterial(options, out error);
		if (material is null)
			return null;
		bool isPassphrase = TryDecodeRawKey(material) is null;

		// K46: a passphrase (anything that is not a raw base64 32-byte key) must clear a hard length
		// floor, not merely earn a warning. A too-short passphrase is a refusal.
		if (isPassphrase && material.Length < MinPassphraseLength)
		{
			error = $"ActiveSync:Encryption: the key passphrase must be at least {MinPassphraseLength} " +
			        "characters, or supply a base64 32-byte key ('openssl rand -base64 32').";
			return null;
		}

		// K1: a passphrase must be stretched against a PER-DEPLOYMENT salt. Without one, PBKDF2 would
		// fall back to a single global salt shared by every deployment, so one precomputed rainbow
		// table recovers every default gateway's master key. Refuse rather than silently derive
		// against a fixed salt — the default is fail-closed. A raw base64 32-byte key skips PBKDF2 and
		// needs no salt.
		if (isPassphrase && string.IsNullOrWhiteSpace(options.KeyDerivationSalt))
		{
			error = "ActiveSync:Encryption: a passphrase key requires ActiveSync:Encryption:KeyDerivationSalt " +
			        "(a per-deployment value; supply it identically wherever the key is derived), or supply a " +
			        "base64 32-byte key ('openssl rand -base64 32') which needs no salt.";
			return null;
		}

		return DeriveKey(material, options);
	}

	/// <summary>
	///   True when the configured key is a passphrase (not a raw base64 32-byte key) shorter
	///   than <see cref="ShortPassphraseLength" /> characters — worth a startup warning.
	/// </summary>
	public static bool IsShortPassphrase(EncryptionOptions options)
	{
		string? material = LoadKeyMaterial(options, out string? _);
		return material is not null &&
		       material.Length < ShortPassphraseLength &&
		       TryDecodeRawKey(material) is null;
	}

	private static string? LoadKeyMaterial(EncryptionOptions options, out string? error)
	{
		bool hasKey = !string.IsNullOrWhiteSpace(options.Key);
		bool hasKeyFile = !string.IsNullOrWhiteSpace(options.KeyFile);
		if (hasKey && hasKeyFile)
		{
			error = "ActiveSync:Encryption: set either Key or KeyFile, not both.";
			return null;
		}

		if (hasKey)
		{
			error = null;
			return options.Key!.Trim();
		}

		if (hasKeyFile)
		{
			string path = options.KeyFile!;
			if (!File.Exists(path))
			{
				error = $"ActiveSync:Encryption:KeyFile '{path}' does not exist.";
				return null;
			}

			try
			{
				error = null;
				return File.ReadAllText(path).Trim();
			}
			catch (Exception ex)
			{
				error = $"ActiveSync:Encryption:KeyFile '{path}' could not be read: {ex.Message}";
				return null;
			}
		}

		error = null;
		return null;
	}

	private static byte[] DeriveKey(string material, EncryptionOptions options)
	{
		// A CANONICAL base64 value decoding to exactly 32 bytes is the raw key (the documented
		// high-entropy path); everything else — including a non-canonical base64-shaped passphrase
		// (K14) — is stretched with PBKDF2.
		byte[]? raw = TryDecodeRawKey(material);
		if (raw is not null)
			return raw;

		// K47: feed the passphrase to PBKDF2 as a byte buffer we can zero afterwards, rather than
		// the string overload — the derived key is already carefully wiped by callers, so the
		// passphrase copy should be too. UTF-8 bytes match the string overload's own encoding, so
		// the derived key is unchanged. (The origin string — config-bound Key or key-file text —
		// stays unzeroable; that residual is inherent to configuration binding.)
		byte[] passwordBytes = Encoding.UTF8.GetBytes(material);
		try
		{
			return Rfc2898DeriveBytes.Pbkdf2(
				passwordBytes, ResolveSalt(options), DerivationIterations, HashAlgorithmName.SHA256, KeySize);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(passwordBytes);
		}
	}

	/// <summary>
	///   The PBKDF2 salt for passphrase stretching, derived from the operator's per-deployment
	///   <see cref="EncryptionOptions.KeyDerivationSalt" /> (bound under a fixed context so even a
	///   short operator value yields a full-width salt), so one precomputed table does not cover
	///   every deployment. K1: there is deliberately NO fixed application-salt fallback — a passphrase
	///   without a salt is refused upstream in <see cref="TryLoadKey" />, so this is only reached with
	///   a non-empty salt. Deterministic — nothing is stored.
	/// </summary>
	private static byte[] ResolveSalt(EncryptionOptions options)
	{
		string salt = options.KeyDerivationSalt!.Trim();
		return SHA256.HashData(
			Encoding.UTF8.GetBytes("ActiveSync.Encryption.KeyDerivation.v1:" + salt));
	}

	private static byte[]? TryDecodeRawKey(string material)
	{
		try
		{
			byte[] decoded = Convert.FromBase64String(material);
			if (decoded.Length != KeySize)
				return null;
			// K14: take the raw-key interpretation only for CANONICAL base64 — a value that re-encodes
			// to exactly the same string. Convert.FromBase64String is lenient (it ignores embedded
			// whitespace and accepts non-zero unused padding bits), so a human passphrase that merely
			// happens to be base64-decodable to 32 bytes would otherwise be used verbatim as the AES
			// key with NO PBKDF2 stretching and NO length floor. Requiring canonical base64 routes such
			// input to the passphrase path instead. 'openssl rand -base64 32' emits canonical base64, so
			// the documented high-entropy path is unaffected. (A canonical low-entropy base64-32 value
			// IS still taken verbatim — the raw-key path is by definition unstretched; the length floor
			// only protects the passphrase path.)
			return Convert.ToBase64String(decoded) == material ? decoded : null;
		}
		catch (FormatException)
		{
			return null;
		}
	}
}

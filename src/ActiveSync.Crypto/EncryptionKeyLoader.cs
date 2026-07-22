using System.Security.Cryptography;
using System.Text;
using ActiveSync.Core.Options;

namespace ActiveSync.Core.Security;

/// <summary>
///   Loads the local-content encryption key from configuration. The value may be ANY
///   string: base64 decoding to exactly 32 bytes is used as the raw key (the high-entropy
///   path: 'openssl rand -base64 32'); anything else is treated as a passphrase and
///   stretched to 32 bytes with PBKDF2-SHA256 over a fixed application salt (deterministic,
///   so no salt needs storing). Shared by startup validation, DI and the protect verb so
///   the rules exist exactly once. Whitespace-only values are treated as absent (empty
///   placeholders in appsettings.json).
/// </summary>
public static class EncryptionKeyLoader
{
	/// <summary>Passphrases shorter than this trigger a startup-banner warning (never an error).</summary>
	public const int ShortPassphraseLength = 12;

	private const int KeySize = 32;
	private const int DerivationIterations = 200_000;
	private static readonly byte[] DerivationSalt = Encoding.UTF8.GetBytes("ActiveSync.Encryption.KeyDerivation.v1");

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
		// A value that decodes as base64 to exactly 32 bytes is the raw key (back-compat and
		// the documented high-entropy path); everything else is a passphrase. A passphrase
		// that happens to BE valid 32-byte base64 lands on the raw path — deterministic
		// either way, since both interpretations yield a fixed key.
		return TryDecodeRawKey(material)
		       ?? Rfc2898DeriveBytes.Pbkdf2(material, ResolveSalt(options), DerivationIterations,
			       HashAlgorithmName.SHA256, KeySize);
	}

	/// <summary>
	///   The PBKDF2 salt for passphrase stretching. K45: when an operator supplies
	///   <see cref="EncryptionOptions.KeyDerivationSalt" />, the salt is deployment-specific
	///   (bound under a fixed context so even a short operator value yields a full-width salt), so
	///   one precomputed table does not cover every deployment. Unset keeps the historical fixed
	///   application salt for back-compat. Deterministic either way — nothing is stored.
	/// </summary>
	private static byte[] ResolveSalt(EncryptionOptions options)
	{
		if (string.IsNullOrWhiteSpace(options.KeyDerivationSalt))
			return DerivationSalt;
		return SHA256.HashData(
			Encoding.UTF8.GetBytes("ActiveSync.Encryption.KeyDerivation.v1:" + options.KeyDerivationSalt.Trim()));
	}

	private static byte[]? TryDecodeRawKey(string material)
	{
		try
		{
			byte[] decoded = Convert.FromBase64String(material);
			return decoded.Length == KeySize ? decoded : null;
		}
		catch (FormatException)
		{
			return null;
		}
	}
}

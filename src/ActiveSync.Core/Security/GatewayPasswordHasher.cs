using System.Security.Cryptography;
using System.Text;

namespace ActiveSync.Core.Security;

/// <summary>
///   Hashing and verification for the gateway-facing password in Accounts mode (the value
///   the phone sends as Basic auth). Stored format:
///   "pbkdf2$&lt;iterations&gt;$&lt;saltBase64&gt;$&lt;hashBase64&gt;" (SHA-256, 16-byte
///   salt, 32-byte hash). Plaintext stored values are also accepted (compared timing-safe);
///   the startup summary warns about them once.
/// </summary>
public static class GatewayPasswordHasher
{
	public const string Prefix = "pbkdf2$";
	public const int DefaultIterations = 200_000;

	private const int SaltSize = 16;
	private const int HashSize = 32;
	private const int MinIterations = 100_000;

	public static string Hash(string password, int iterations = DefaultIterations)
	{
		byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
		byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashSize);
		return $"{Prefix}{iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
	}

	public static bool IsHashed(string stored)
	{
		return stored.StartsWith(Prefix, StringComparison.Ordinal);
	}

	/// <summary>Validates a "pbkdf2$..." value's shape without verifying any password.</summary>
	public static bool TryParse(string stored, out string? error)
	{
		return TryParse(stored, out _, out _, out _, out error);
	}

	public static bool Verify(string stored, string presented)
	{
		if (IsHashed(stored))
		{
			if (!TryParse(stored, out int iterations, out byte[]? salt, out byte[]? expected, out _))
				return false;
			byte[] actual = Rfc2898DeriveBytes.Pbkdf2(
				presented, salt!, iterations, HashAlgorithmName.SHA256, expected!.Length);
			return CryptographicOperations.FixedTimeEquals(actual, expected);
		}

		// Plaintext stored value: still compare timing-safe over fixed-size digests so the
		// comparison cost does not depend on where the strings diverge or their lengths.
		byte[] storedDigest = SHA256.HashData(Encoding.UTF8.GetBytes(stored));
		byte[] presentedDigest = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
		return CryptographicOperations.FixedTimeEquals(storedDigest, presentedDigest);
	}

	private static bool TryParse(
		string stored, out int iterations, out byte[]? salt, out byte[]? hash, out string? error)
	{
		iterations = 0;
		salt = null;
		hash = null;
		if (!IsHashed(stored))
		{
			error = "value does not start with 'pbkdf2$'";
			return false;
		}

		string[] parts = stored[Prefix.Length..].Split('$');
		if (parts.Length != 3)
		{
			error = "expected pbkdf2$<iterations>$<saltBase64>$<hashBase64>";
			return false;
		}

		if (!int.TryParse(parts[0], out iterations) || iterations < MinIterations)
		{
			error = $"iteration count must be a number >= {MinIterations}";
			return false;
		}

		try
		{
			salt = Convert.FromBase64String(parts[1]);
			hash = Convert.FromBase64String(parts[2]);
		}
		catch (FormatException)
		{
			error = "salt or hash is not valid base64";
			return false;
		}

		if (salt.Length < 8 || hash.Length < 16)
		{
			error = "salt or hash is too short";
			return false;
		}

		error = null;
		return true;
	}
}

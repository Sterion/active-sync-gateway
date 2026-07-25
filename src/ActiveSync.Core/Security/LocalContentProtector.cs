using System.Security.Cryptography;
using System.Text;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Crypto;

namespace ActiveSync.Core.Security;

/// <summary>
///   Encrypts locally-stored item content (the LocalItems Content column) at rest with
///   AES-256-GCM. The owning user name and collection are bound as additional authenticated
///   data, so a ciphertext row cannot be replayed under another user or collection. Stored
///   format: "v1:" + base64(12-byte nonce ‖ ciphertext ‖ 16-byte tag) — the prefix versions
///   the format so a future key-rotation scheme can introduce "v2:" without ambiguity.
///   Random 96-bit nonces are safe far beyond this gateway's write volume (birthday bound
///   ~2^32 encryptions per key).
/// </summary>
public sealed class LocalContentProtector : IDisposable
{
	public const string FormatPrefix = "v1:";

	private const int KeySize = 32;

	private readonly byte[]? _key;

	private LocalContentProtector(byte[]? key)
	{
		_key = key;
	}

	/// <summary>True when a key is loaded; false in the AllowPlaintext passthrough mode.</summary>
	public bool IsEncrypting => _key is not null;

	/// <summary>Creates a protector that encrypts with the given 256-bit key (copied defensively).</summary>
	public static LocalContentProtector CreateProtected(byte[] key)
	{
		ArgumentNullException.ThrowIfNull(key);
		if (key.Length != KeySize)
			throw new ArgumentException($"Encryption key must be exactly {KeySize} bytes (got {key.Length}).", nameof(key));
		return new LocalContentProtector((byte[])key.Clone());
	}

	/// <summary>Creates a passthrough protector for the explicit AllowPlaintext mode.</summary>
	public static LocalContentProtector CreatePlaintext()
	{
		return new LocalContentProtector(null);
	}

	public string Protect(string plaintext, string userName, string collection)
	{
		if (_key is null)
			return plaintext;

		return SealedBlob.Seal(FormatPrefix, Aad(userName, collection), _key, plaintext);
	}

	/// <summary>
	///   Decrypts a stored value. In passthrough mode the value is returned unchanged — even
	///   when it carries the "v1:" prefix (an operator running AllowPlaintext against an
	///   encrypted database made a config error; throwing here would brick the escape hatch).
	///   With a key loaded, anything that is not a well-formed "v1:" payload authenticated by
	///   the current key throws <see cref="BackendException" /> — never item-not-found, which
	///   would make the sync engine delete the item from devices.
	/// </summary>
	public string Unprotect(string stored, string userName, string collection)
	{
		if (_key is null)
			return stored;

		if (SealedBlob.TryUnseal(FormatPrefix, Aad(userName, collection), _key, stored,
			    out string? plaintext, out SealedBlobError error, out Exception? inner))
			return plaintext!;

		throw error switch
		{
			SealedBlobError.InvalidBase64 => UndecryptableRow(inner),
			SealedBlobError.AuthenticationFailed => UndecryptableRow(inner),
			_ => UndecryptableRow(null),
		};
	}

	public void Dispose()
	{
		if (_key is not null)
			CryptographicOperations.ZeroMemory(_key);
	}

	// K2: the AAD binds a ciphertext row to its (user, collection) so it cannot be replayed under
	// another identity. "\n" is the field delimiter, so a control character (the "\n" itself in
	// particular) inside either part would make the encoding non-injective — ("a\nb","c") and
	// ("a","b\nc") would both encode to "a\nb\nc" and cross-decrypt. The user name arrives as an
	// attacker-influenced HTTP Basic login, so reject any C0 control character in either part; the
	// "\n"-join is then unambiguous. Legitimate logins and the fixed internal collection names never
	// contain control characters.
	private static byte[] Aad(string userName, string collection)
	{
		RejectControlChars(userName, nameof(userName));
		RejectControlChars(collection, nameof(collection));
		return Encoding.UTF8.GetBytes(userName + "\n" + collection);
	}

	private static void RejectControlChars(string value, string part)
	{
		foreach (char c in value)
		{
			if (c < ' ')
				throw new ArgumentException(
					$"LocalContentProtector {part} must not contain control characters.", part);
		}
	}

	private static BackendException UndecryptableRow(Exception? inner)
	{
		const string message =
			"Stored local item cannot be decrypted — wrong ActiveSync:Encryption key, a tampered row, " +
			"or a row written before encryption was enabled. Restore the original key, or drop the " +
			"gateway database to start clean.";
		return inner is null ? new BackendException(message) : new BackendException(message, inner);
	}
}

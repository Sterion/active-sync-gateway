using System.Security.Cryptography;
using System.Text;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Crypto;

namespace ActiveSync.Core.Security;

/// <summary>
///   Encrypts locally-stored item content (the LocalItems Content column) at rest with
///   AES-256-GCM. The owning user's immutable <c>UserId</c> and the collection are bound as
///   additional authenticated data, so a ciphertext row cannot be replayed under another user
///   or collection — and a login rename leaves every row decryptable. Stored
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

	/// <summary>
	///   The sentinel owner id for gateway-global (non-user) sealed rows, e.g. the TLS
	///   certificate. Real users never get this id — identity columns start at 1.
	/// </summary>
	public const int GatewayUserId = 0;

	public string Protect(string plaintext, int userId, string collection)
	{
		if (_key is null)
			return plaintext;

		return SealedBlob.Seal(FormatPrefix, Aad(userId, collection), _key, plaintext);
	}

	/// <summary>
	///   Decrypts a stored value. In passthrough mode the value is returned unchanged — even
	///   when it carries the "v1:" prefix (an operator running AllowPlaintext against an
	///   encrypted database made a config error; throwing here would brick the escape hatch).
	///   With a key loaded, anything that is not a well-formed "v1:" payload authenticated by
	///   the current key throws <see cref="BackendException" /> — never item-not-found, which
	///   would make the sync engine delete the item from devices.
	/// </summary>
	public string Unprotect(string stored, int userId, string collection)
	{
		if (_key is null)
			return stored;

		if (SealedBlob.TryUnseal(FormatPrefix, Aad(userId, collection), _key, stored,
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
	// another identity. The owner is the immutable UserId — never the login, so a rename leaves
	// every sealed row decryptable (db-restructure item 2). Framing is versioned and
	// self-delimiting: "v2" ‖ LE64(userId) ‖ LE32(byteLen(collection)) ‖ UTF8(collection). The
	// fixed-width id and the length prefix make the encoding injective by construction — no
	// delimiter, so no delimiter-injection ambiguity, and no control-character rules to depend on.
	// The version tag costs two bytes and buys an unambiguous future re-key path ("v3").
	private static byte[] Aad(long userId, string collection)
	{
		int collectionBytes = Encoding.UTF8.GetByteCount(collection);
		byte[] aad = new byte[2 + 8 + 4 + collectionBytes];
		aad[0] = (byte)'v';
		aad[1] = (byte)'2';
		System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(aad.AsSpan(2, 8), userId);
		System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(aad.AsSpan(10, 4), collectionBytes);
		Encoding.UTF8.GetBytes(collection, aad.AsSpan(14));
		return aad;
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

using System.Security.Cryptography;
using System.Text;
using ActiveSync.Core.Backend;

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

	private const int NonceSize = 12;
	private const int TagSize = 16;
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

		byte[] plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
		byte[] payload = new byte[NonceSize + plaintextBytes.Length + TagSize];
		Span<byte> nonce = payload.AsSpan(0, NonceSize);
		Span<byte> ciphertext = payload.AsSpan(NonceSize, plaintextBytes.Length);
		Span<byte> tag = payload.AsSpan(NonceSize + plaintextBytes.Length, TagSize);

		RandomNumberGenerator.Fill(nonce);
		using AesGcm aes = new(_key, TagSize);
		aes.Encrypt(nonce, plaintextBytes, ciphertext, tag, Aad(userName, collection));
		return FormatPrefix + Convert.ToBase64String(payload);
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
		if (!stored.StartsWith(FormatPrefix, StringComparison.Ordinal))
			throw UndecryptableRow(null);

		byte[] payload;
		try
		{
			payload = Convert.FromBase64String(stored[FormatPrefix.Length..]);
		}
		catch (FormatException ex)
		{
			throw UndecryptableRow(ex);
		}

		if (payload.Length < NonceSize + TagSize)
			throw UndecryptableRow(null);

		ReadOnlySpan<byte> nonce = payload.AsSpan(0, NonceSize);
		ReadOnlySpan<byte> ciphertext = payload.AsSpan(NonceSize, payload.Length - NonceSize - TagSize);
		ReadOnlySpan<byte> tag = payload.AsSpan(payload.Length - TagSize, TagSize);
		byte[] plaintextBytes = new byte[ciphertext.Length];

		try
		{
			using AesGcm aes = new(_key, TagSize);
			aes.Decrypt(nonce, ciphertext, tag, plaintextBytes, Aad(userName, collection));
		}
		catch (CryptographicException ex)
		{
			throw UndecryptableRow(ex);
		}

		return Encoding.UTF8.GetString(plaintextBytes);
	}

	public void Dispose()
	{
		if (_key is not null)
			CryptographicOperations.ZeroMemory(_key);
	}

	// Same "\n"-joined idiom as the BackendSessionFactory session keys.
	private static byte[] Aad(string userName, string collection)
	{
		return Encoding.UTF8.GetBytes(userName + "\n" + collection);
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

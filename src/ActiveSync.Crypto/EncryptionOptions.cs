namespace ActiveSync.Core.Options;

/// <summary>
///   The <c>ActiveSync:Encryption</c> master-key configuration. Lives in the BCL-only crypto
///   assembly (with <see cref="ActiveSync.Core.Security.EncryptionKeyLoader" />) so both the
///   gateway and the slim <c>eas</c> client can bind and derive the key from the same settings.
/// </summary>
public sealed class EncryptionOptions
{
	/// <summary>
	///   Master key for local content encryption at rest — ANY string works. A base64 value
	///   decoding to exactly 32 bytes is used as the raw 256-bit key ('openssl rand -base64
	///   32'); anything else is a passphrase, stretched to 256 bits with PBKDF2-SHA256.
	/// </summary>
	public string? Key { get; set; }

	/// <summary>
	///   Path to a file containing the key (docker-secret friendly; same raw-or-passphrase
	///   interpretation as <see cref="Key" />). Mutually exclusive with <see cref="Key" />.
	/// </summary>
	public string? KeyFile { get; set; }

	/// <summary>
	///   Explicitly store local content unencrypted (dev/test only). Without a key, startup
	///   fails unless this is set. Ignored when a key is configured.
	/// </summary>
	public bool AllowPlaintext { get; set; }
}

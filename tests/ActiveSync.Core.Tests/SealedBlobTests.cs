using ActiveSync.Crypto;

namespace ActiveSync.Core.Tests;

/// <summary>
///   The shared AES-256-GCM "&lt;prefix&gt;" + base64(nonce ‖ ciphertext ‖ tag) framing behind
///   every sealed-value format in the codebase (<see cref="SecretValue" />, <see cref="LocalContentProtector" />).
/// </summary>
public class SealedBlobTests
{
	private static byte[] Key(byte fill = 9)
	{
		byte[] key = new byte[32];
		Array.Fill(key, fill);
		return key;
	}

	[Fact]
	public void SealUnseal_RoundTrips()
	{
		string sealedValue = SealedBlob.Seal("x:", "aad"u8, Key(), "hello world");
		Assert.True(SealedBlob.TryUnseal(
			"x:", "aad"u8, Key(), sealedValue, out string? plaintext, out SealedBlobError error, out _));
		Assert.Equal("hello world", plaintext);
		Assert.Equal(SealedBlobError.None, error);
	}

	[Fact]
	public void TryUnseal_WrongAad_FailsAuthentication()
	{
		string sealedValue = SealedBlob.Seal("x:", "aad-a"u8, Key(), "hello world");
		Assert.False(SealedBlob.TryUnseal(
			"x:", "aad-b"u8, Key(), sealedValue, out string? plaintext, out SealedBlobError error, out _));
		Assert.Null(plaintext);
		Assert.Equal(SealedBlobError.AuthenticationFailed, error);
	}

	// Coverage (not proof): Seal/TryUnseal now wipe the plaintext buffer they allocate
	// internally (CryptographicOperations.ZeroMemory in a finally) instead of leaving it for the
	// GC to collect whenever it gets around to it. The wipe has no external handle to observe —
	// the buffer is a local the caller never sees — so this only guards that the change is
	// behaviour-preserving (same round trip, same failure classification, empty plaintext still
	// works) rather than proving the memory is actually cleared.
	[Fact]
	public void SealUnseal_WithEmptyPlaintext_StillRoundTrips_AfterZeroingIsAdded_Coverage()
	{
		string sealedValue = SealedBlob.Seal("x:", "aad"u8, Key(), "");
		Assert.True(SealedBlob.TryUnseal(
			"x:", "aad"u8, Key(), sealedValue, out string? plaintext, out SealedBlobError error, out _));
		Assert.Equal("", plaintext);
		Assert.Equal(SealedBlobError.None, error);
	}
}

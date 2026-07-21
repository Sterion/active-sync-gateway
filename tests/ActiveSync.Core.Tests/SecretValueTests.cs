using ActiveSync.Core.Security;

namespace ActiveSync.Core.Tests;

public class SecretValueTests
{
	private static byte[] Key(byte fill = 7)
	{
		byte[] key = new byte[32];
		Array.Fill(key, fill);
		return key;
	}

	[Fact]
	public void SealUnseal_RoundTrips()
	{
		string sealedValue = SecretValue.Seal("imap-password!", Key());
		Assert.StartsWith("enc:v1:", sealedValue);
		Assert.True(SecretValue.IsSealed(sealedValue));
		Assert.True(SecretValue.TryUnseal(sealedValue, Key(), out string? plaintext, out string? error));
		Assert.Equal("imap-password!", plaintext);
		Assert.Null(error);
	}

	[Fact]
	public void Seal_SameValueTwice_DiffersByNonce()
	{
		Assert.NotEqual(SecretValue.Seal("pw", Key()), SecretValue.Seal("pw", Key()));
	}

	[Fact]
	public void Unseal_WrongKey_Fails()
	{
		string sealedValue = SecretValue.Seal("pw", Key(1));
		Assert.False(SecretValue.TryUnseal(sealedValue, Key(2), out string? plaintext, out string? error));
		Assert.Null(plaintext);
		Assert.Contains("wrong", error);
	}

	[Fact]
	public void Unseal_TamperedByte_Fails()
	{
		string sealedValue = SecretValue.Seal("pw", Key());
		byte[] payload = Convert.FromBase64String(sealedValue["enc:v1:".Length..]);
		payload[payload.Length / 2] ^= 0xFF;
		string tampered = "enc:v1:" + Convert.ToBase64String(payload);
		Assert.False(SecretValue.TryUnseal(tampered, Key(), out _, out string? error));
		Assert.NotNull(error);
	}

	[Theory]
	[InlineData("plain-password")]
	[InlineData("enc:v1:!!!not-base64")]
	[InlineData("enc:v1:AAAA")]
	public void Unseal_Malformed_FailsWithError(string value)
	{
		Assert.False(SecretValue.TryUnseal(value, Key(), out _, out string? error));
		Assert.NotNull(error);
	}

	[Fact]
	public void LocalContentProtectorOutput_IsNotUnsealable()
	{
		// The two sealed formats must never be interchangeable: different prefix, and
		// SecretValue's constant AAD can never equal the protector's "user\ncollection".
		using LocalContentProtector protector = LocalContentProtector.CreateProtected(Key());
		string row = protector.Protect("BEGIN:VCARD", "alice", "contacts");
		Assert.False(SecretValue.IsSealed(row));
		Assert.False(SecretValue.TryUnseal(row, Key(), out _, out _));
		// And vice versa: a config secret is not a decryptable content row.
		string sealedValue = SecretValue.Seal("pw", Key());
		Assert.Throws<ActiveSync.Contracts.BackendException>(
			() => protector.Unprotect(sealedValue, "alice", "contacts"));
	}
}

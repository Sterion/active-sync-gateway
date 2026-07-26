using System.Security.Cryptography;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Security;

namespace ActiveSync.Core.Tests;

public class LocalContentProtectorTests
{
	private const string Vcard = "BEGIN:VCARD\r\nVERSION:3.0\r\nFN:Test Person\r\nEND:VCARD\r\n";

	private static byte[] Key(byte fill = 1)
	{
		byte[] key = new byte[32];
		Array.Fill(key, fill);
		return key;
	}

	[Fact]
	public void RoundTrip_RestoresPlaintext_AndStoresCiphertext()
	{
		using LocalContentProtector protector = LocalContentProtector.CreateProtected(Key());
		string stored = protector.Protect(Vcard, 1, "contacts");
		Assert.StartsWith(LocalContentProtector.FormatPrefix, stored);
		Assert.DoesNotContain("VCARD", stored);
		Assert.Equal(Vcard, protector.Unprotect(stored, 1, "contacts"));
	}

	[Fact]
	public void SamePlaintextTwice_ProducesDifferentCiphertexts()
	{
		using LocalContentProtector protector = LocalContentProtector.CreateProtected(Key());
		string first = protector.Protect(Vcard, 1, "contacts");
		string second = protector.Protect(Vcard, 1, "contacts");
		Assert.NotEqual(first, second); // fresh random nonce per write
	}

	[Fact]
	public void WrongKey_Throws()
	{
		using LocalContentProtector writer = LocalContentProtector.CreateProtected(Key(1));
		using LocalContentProtector reader = LocalContentProtector.CreateProtected(Key(2));
		string stored = writer.Protect(Vcard, 1, "contacts");
		Assert.Throws<BackendException>(() => reader.Unprotect(stored, 1, "contacts"));
	}

	[Fact]
	public void DifferentUser_Throws()
	{
		using LocalContentProtector protector = LocalContentProtector.CreateProtected(Key());
		string stored = protector.Protect(Vcard, 1, "contacts");
		Assert.Throws<BackendException>(() => protector.Unprotect(stored, 2, "contacts"));
	}

	[Fact]
	public void DifferentCollection_Throws()
	{
		using LocalContentProtector protector = LocalContentProtector.CreateProtected(Key());
		string stored = protector.Protect(Vcard, 1, "contacts");
		Assert.Throws<BackendException>(() => protector.Unprotect(stored, 1, "calendar"));
	}

	[Fact]
	public void TamperedPayload_Throws()
	{
		using LocalContentProtector protector = LocalContentProtector.CreateProtected(Key());
		string stored = protector.Protect(Vcard, 1, "contacts");
		byte[] payload = Convert.FromBase64String(stored[LocalContentProtector.FormatPrefix.Length..]);
		payload[payload.Length / 2] ^= 0xFF;
		string tampered = LocalContentProtector.FormatPrefix + Convert.ToBase64String(payload);
		Assert.Throws<BackendException>(() => protector.Unprotect(tampered, 1, "contacts"));
	}

	[Theory]
	[InlineData(Vcard)] // plaintext row under strict mode (no prefix)
	[InlineData("v1:!!!not-base64!!!")]
	[InlineData("v1:AAAA")] // shorter than nonce + tag
	public void MalformedStoredValue_ThrowsBackendException(string stored)
	{
		using LocalContentProtector protector = LocalContentProtector.CreateProtected(Key());
		Assert.Throws<BackendException>(() => protector.Unprotect(stored, 1, "contacts"));
	}

	[Fact]
	public void AadFraming_IsInjective_WithoutCharacterRules()
	{
		// K2, v2 framing: "v2" ‖ LE64(userId) ‖ LE32(len) ‖ collection. The fixed-width id and
		// the length prefix make the encoding injective BY CONSTRUCTION — no delimiter exists to
		// inject, so even a collection containing "\n" binds unambiguously (the old string-user
		// framing had to reject control characters to stay collision-free).
		using LocalContentProtector protector = LocalContentProtector.CreateProtected(Key());
		string stored = protector.Protect(Vcard, 1, "a\nb");
		Assert.Equal(Vcard, protector.Unprotect(stored, 1, "a\nb"));
		Assert.Throws<BackendException>(() => protector.Unprotect(stored, 1, "a"));
		Assert.Throws<BackendException>(() => protector.Unprotect(stored, 1, "b"));
	}

	[Fact]
	public void GatewaySentinelId_IsItsOwnIdentity()
	{
		// Gateway-global rows (TLS certificate) seal under the reserved id 0 — never a real
		// user's identity (identity columns start at 1).
		using LocalContentProtector protector = LocalContentProtector.CreateProtected(Key());
		string stored = protector.Protect("pfx", LocalContentProtector.GatewayUserId, "tls");
		Assert.Equal("pfx", protector.Unprotect(stored, LocalContentProtector.GatewayUserId, "tls"));
		Assert.Throws<BackendException>(() => protector.Unprotect(stored, 1, "tls"));
	}

	[Fact]
	public void EmptyString_RoundTrips()
	{
		using LocalContentProtector protector = LocalContentProtector.CreateProtected(Key());
		string stored = protector.Protect("", 1, "notes");
		Assert.StartsWith(LocalContentProtector.FormatPrefix, stored);
		Assert.Equal("", protector.Unprotect(stored, 1, "notes"));
	}

	[Theory]
	[InlineData(16)]
	[InlineData(31)]
	[InlineData(64)]
	public void WrongKeyLength_IsRejected(int length)
	{
		Assert.Throws<ArgumentException>(() => LocalContentProtector.CreateProtected(new byte[length]));
	}

	[Fact]
	public void PlaintextMode_PassesThroughBothDirections()
	{
		using LocalContentProtector protector = LocalContentProtector.CreatePlaintext();
		Assert.False(protector.IsEncrypting);
		Assert.Equal(Vcard, protector.Protect(Vcard, 1, "contacts"));
		Assert.Equal(Vcard, protector.Unprotect(Vcard, 1, "contacts"));
		// Even a "v1:" row passes through — operator error, but the escape hatch must not throw.
		Assert.Equal("v1:abc", protector.Unprotect("v1:abc", 1, "contacts"));
	}

	[Fact]
	public void ProtectedMode_ReportsEncrypting_AndCopiesKey()
	{
		byte[] key = Key();
		using LocalContentProtector protector = LocalContentProtector.CreateProtected(key);
		Assert.True(protector.IsEncrypting);
		CryptographicOperations.ZeroMemory(key); // caller zeroes its buffer; protector must hold a copy
		string stored = protector.Protect(Vcard, 1, "contacts");
		Assert.Equal(Vcard, protector.Unprotect(stored, 1, "contacts"));
	}
}

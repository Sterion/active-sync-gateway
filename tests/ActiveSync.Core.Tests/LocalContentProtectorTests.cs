using System.Security.Cryptography;
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
		string stored = protector.Protect(Vcard, "alice", "contacts");
		Assert.StartsWith(LocalContentProtector.FormatPrefix, stored);
		Assert.DoesNotContain("VCARD", stored);
		Assert.Equal(Vcard, protector.Unprotect(stored, "alice", "contacts"));
	}

	[Fact]
	public void SamePlaintextTwice_ProducesDifferentCiphertexts()
	{
		using LocalContentProtector protector = LocalContentProtector.CreateProtected(Key());
		string first = protector.Protect(Vcard, "alice", "contacts");
		string second = protector.Protect(Vcard, "alice", "contacts");
		Assert.NotEqual(first, second); // fresh random nonce per write
	}

	[Fact]
	public void WrongKey_Throws()
	{
		using LocalContentProtector writer = LocalContentProtector.CreateProtected(Key(1));
		using LocalContentProtector reader = LocalContentProtector.CreateProtected(Key(2));
		string stored = writer.Protect(Vcard, "alice", "contacts");
		Assert.Throws<BackendException>(() => reader.Unprotect(stored, "alice", "contacts"));
	}

	[Fact]
	public void DifferentUser_Throws()
	{
		using LocalContentProtector protector = LocalContentProtector.CreateProtected(Key());
		string stored = protector.Protect(Vcard, "alice", "contacts");
		Assert.Throws<BackendException>(() => protector.Unprotect(stored, "bob", "contacts"));
	}

	[Fact]
	public void DifferentCollection_Throws()
	{
		using LocalContentProtector protector = LocalContentProtector.CreateProtected(Key());
		string stored = protector.Protect(Vcard, "alice", "contacts");
		Assert.Throws<BackendException>(() => protector.Unprotect(stored, "alice", "calendar"));
	}

	[Fact]
	public void TamperedPayload_Throws()
	{
		using LocalContentProtector protector = LocalContentProtector.CreateProtected(Key());
		string stored = protector.Protect(Vcard, "alice", "contacts");
		byte[] payload = Convert.FromBase64String(stored[LocalContentProtector.FormatPrefix.Length..]);
		payload[payload.Length / 2] ^= 0xFF;
		string tampered = LocalContentProtector.FormatPrefix + Convert.ToBase64String(payload);
		Assert.Throws<BackendException>(() => protector.Unprotect(tampered, "alice", "contacts"));
	}

	[Theory]
	[InlineData(Vcard)] // plaintext row under strict mode (no prefix)
	[InlineData("v1:!!!not-base64!!!")]
	[InlineData("v1:AAAA")] // shorter than nonce + tag
	public void MalformedStoredValue_ThrowsBackendException(string stored)
	{
		using LocalContentProtector protector = LocalContentProtector.CreateProtected(Key());
		Assert.Throws<BackendException>(() => protector.Unprotect(stored, "alice", "contacts"));
	}

	[Fact]
	public void EmptyString_RoundTrips()
	{
		using LocalContentProtector protector = LocalContentProtector.CreateProtected(Key());
		string stored = protector.Protect("", "alice", "notes");
		Assert.StartsWith(LocalContentProtector.FormatPrefix, stored);
		Assert.Equal("", protector.Unprotect(stored, "alice", "notes"));
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
		Assert.Equal(Vcard, protector.Protect(Vcard, "alice", "contacts"));
		Assert.Equal(Vcard, protector.Unprotect(Vcard, "alice", "contacts"));
		// Even a "v1:" row passes through — operator error, but the escape hatch must not throw.
		Assert.Equal("v1:abc", protector.Unprotect("v1:abc", "alice", "contacts"));
	}

	[Fact]
	public void ProtectedMode_ReportsEncrypting_AndCopiesKey()
	{
		byte[] key = Key();
		using LocalContentProtector protector = LocalContentProtector.CreateProtected(key);
		Assert.True(protector.IsEncrypting);
		CryptographicOperations.ZeroMemory(key); // caller zeroes its buffer; protector must hold a copy
		string stored = protector.Protect(Vcard, "alice", "contacts");
		Assert.Equal(Vcard, protector.Unprotect(stored, "alice", "contacts"));
	}
}

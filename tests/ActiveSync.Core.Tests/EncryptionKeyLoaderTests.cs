using ActiveSync.Core.Options;
using ActiveSync.Core.Security;

namespace ActiveSync.Core.Tests;

public class EncryptionKeyLoaderTests
{
	private static readonly string RawKeyBase64 = Convert.ToBase64String(
		Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());

	private static byte[]? Load(string? key, out string? error, string? keyFile = null)
	{
		return EncryptionKeyLoader.TryLoadKey(
			new EncryptionOptions { Key = key, KeyFile = keyFile }, out error);
	}

	[Fact]
	public void RawBase64Key_IsUsedVerbatim()
	{
		byte[]? key = Load(RawKeyBase64, out string? error);
		Assert.Null(error);
		Assert.Equal(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray(), key);
	}

	[Fact]
	public void Passphrase_DerivesDeterministic32Bytes()
	{
		byte[]? first = Load("correct horse battery staple", out string? error);
		byte[]? second = Load("correct horse battery staple", out _);
		Assert.Null(error);
		Assert.NotNull(first);
		Assert.Equal(32, first.Length);
		Assert.Equal(first, second); // deterministic — the same config always yields the same key
		Assert.NotEqual(first, Load("correct horse battery stapler", out _)); // input-sensitive
	}

	[Fact]
	public void ShortPassphrase_LoadsSuccessfully()
	{
		// No minimum: even "pass" is accepted (a startup warning is the only pushback).
		byte[]? key = Load("pass", out string? error);
		Assert.Null(error);
		Assert.NotNull(key);
		Assert.Equal(32, key.Length);
		using LocalContentProtector protector = LocalContentProtector.CreateProtected(key);
		Assert.Equal("data", protector.Unprotect(protector.Protect("data", "u", "notes"), "u", "notes"));
	}

	[Theory]
	[InlineData("pass", true)]
	[InlineData("elevenchars", true)] // 11 chars — just under the boundary
	[InlineData("twelve chars", false)] // 12 chars — at the boundary, no warning
	[InlineData("a much longer passphrase", false)]
	[InlineData(null, false)] // absent → nothing to warn about
	public void IsShortPassphrase_ClassifiesPassphrases(string? key, bool expected)
	{
		Assert.Equal(expected, EncryptionKeyLoader.IsShortPassphrase(new EncryptionOptions { Key = key }));
	}

	[Fact]
	public void IsShortPassphrase_FalseForRawBase64Key()
	{
		Assert.False(EncryptionKeyLoader.IsShortPassphrase(new EncryptionOptions { Key = RawKeyBase64 }));
	}

	[Fact]
	public void KeyFile_WithPassphraseContent_Works()
	{
		string path = Path.Combine(Path.GetTempPath(), $"activesync-test-{Guid.NewGuid():N}.key");
		File.WriteAllText(path, "my file-mounted passphrase\n");
		try
		{
			byte[]? fromFile = Load(null, out string? error, path);
			Assert.Null(error);
			// Identical derivation whether the passphrase arrives inline or via file.
			Assert.Equal(Load("my file-mounted passphrase", out _), fromFile);
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void PassphraseAndRawKey_YieldDifferentKeys()
	{
		// The derived key never accidentally equals raw-key material.
		byte[]? derived = Load("some passphrase that is long", out _);
		Assert.NotEqual(Convert.ToBase64String(derived!), RawKeyBase64);
	}
}

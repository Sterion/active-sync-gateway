using ActiveSync.Core.Security;
using ActiveSync.Crypto;

namespace ActiveSync.Core.Tests;

public class EncryptionKeyLoaderTests
{
	private static readonly string RawKeyBase64 = Convert.ToBase64String(
		Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());

	// K1: a passphrase now requires a per-deployment salt. Tests that exercise the passphrase path
	// supply this so they derive a key instead of being fail-closed refused.
	private const string TestSalt = "unit-test-deployment-salt";

	private static byte[]? Load(string? key, out string? error, string? keyFile = null, string? salt = null)
	{
		return EncryptionKeyLoader.TryLoadKey(
			new EncryptionOptions { Key = key, KeyFile = keyFile, KeyDerivationSalt = salt }, out error);
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
		byte[]? first = Load("correct horse battery staple", out string? error, salt: TestSalt);
		byte[]? second = Load("correct horse battery staple", out _, salt: TestSalt);
		Assert.Null(error);
		Assert.NotNull(first);
		Assert.Equal(32, first.Length);
		Assert.Equal(first, second); // deterministic — the same config always yields the same key
		Assert.NotEqual(first, Load("correct horse battery stapler", out _, salt: TestSalt)); // input-sensitive
	}

	[Fact]
	public void Passphrase_AtFloor_LoadsSuccessfully()
	{
		// Behaviour change (K46): there is now a hard minimum passphrase length. A passphrase at or
		// above the floor (but still under the 12-char warn threshold) loads and is usable — the
		// warning is the only pushback in that band (was "even 'pass' is accepted"). K1: it also needs
		// a per-deployment salt to be accepted.
		byte[]? key = Load("passpass", out string? error, salt: TestSalt); // 8 chars
		Assert.Null(error);
		Assert.NotNull(key);
		Assert.Equal(32, key.Length);
		using LocalContentProtector protector = LocalContentProtector.CreateProtected(key);
		Assert.Equal("data", protector.Unprotect(protector.Protect("data", "u", "notes"), "u", "notes"));
	}

	[Fact]
	public void ShortPassphrase_BelowFloor_IsRejected()
	{
		// K46: a passphrase below the hard minimum is refused (previously it only produced a
		// startup warning and loaded anyway).
		byte[]? key = Load("pass", out string? error); // 4 chars
		Assert.Null(key);
		Assert.NotNull(error);
		Assert.Contains("at least", error);
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
			byte[]? fromFile = Load(null, out string? error, keyFile: path, salt: TestSalt);
			Assert.Null(error);
			// Identical derivation whether the passphrase arrives inline or via file.
			Assert.Equal(Load("my file-mounted passphrase", out _, salt: TestSalt), fromFile);
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void Passphrase_WithoutKeyDerivationSalt_IsRejected()
	{
		// K1 (red-first): a passphrase (anything but a raw base64 32-byte key) must be stretched
		// against a PER-DEPLOYMENT salt. The historical code fell back to a single global fixed salt
		// whenever KeyDerivationSalt was unset, so one precomputed rainbow table recovered the master
		// key of every default deployment. The passphrase path is now REFUSED with an actionable error
		// unless the operator supplies KeyDerivationSalt (or switches to a raw base64 key). The default
		// is fail-closed — it can never silently use the global salt.
		byte[]? key = EncryptionKeyLoader.TryLoadKey(
			new EncryptionOptions { Key = "correct horse battery staple" }, out string? error);
		Assert.Null(key);
		Assert.NotNull(error);
		Assert.Contains("KeyDerivationSalt", error);
	}

	[Fact]
	public void KeyDerivationSalt_ProducesDeploymentSpecificKeys()
	{
		// K45: a per-deployment PBKDF2 salt, so one precomputed rainbow table cannot cover every
		// deployment. The same passphrase under two different salts must yield different keys, while
		// the unset case stays back-compatible and deterministic.
		byte[]? a = EncryptionKeyLoader.TryLoadKey(
			new EncryptionOptions { Key = "shared passphrase", KeyDerivationSalt = "deployment-a" }, out _);
		byte[]? b = EncryptionKeyLoader.TryLoadKey(
			new EncryptionOptions { Key = "shared passphrase", KeyDerivationSalt = "deployment-b" }, out _);

		Assert.NotEqual(a, b);
		// Deterministic within a deployment: same passphrase + same salt = same key.
		Assert.Equal(a, EncryptionKeyLoader.TryLoadKey(
			new EncryptionOptions { Key = "shared passphrase", KeyDerivationSalt = "deployment-a" }, out _));
	}

	[Fact]
	public void NonCanonicalBase64_IsNotUsedAsRawKey()
	{
		// K14 (red-first): a value that decodes to 32 bytes but is NOT canonical base64 (here an
		// embedded space — Convert.FromBase64String ignores whitespace) is a passphrase, not a raw
		// key. The historical code took the raw path for anything that merely decoded to 32 bytes,
		// using the low-entropy input verbatim as the AES key with NO PBKDF2 stretching. It must
		// instead fall through to the passphrase path and be stretched. (A salt is supplied so the
		// passphrase path derives rather than being K1-refused.)
		string nonCanonical = RawKeyBase64.Insert(4, " ");
		byte[] decoded = Convert.FromBase64String(nonCanonical);
		Assert.Equal(32, decoded.Length);

		byte[]? loaded = Load(nonCanonical, out string? error, salt: TestSalt);
		Assert.Null(error);
		Assert.NotNull(loaded);
		Assert.NotEqual(decoded, loaded); // stretched via PBKDF2, not used verbatim
	}

	[Fact]
	public void CanonicalBase64_32Bytes_IsStillUsedVerbatim()
	{
		// Regression guard: the documented high-entropy path (openssl rand -base64 32 emits canonical
		// base64) is unchanged — a canonical 32-byte base64 value is the raw key verbatim, salt-free.
		byte[]? loaded = Load(RawKeyBase64, out string? error);
		Assert.Null(error);
		Assert.Equal(Convert.FromBase64String(RawKeyBase64), loaded);
	}

	[Fact]
	public void KeyDerivationSalt_IgnoredForRawBase64Key()
	{
		// The raw 32-byte key path skips PBKDF2 entirely, so the salt cannot change it.
		Assert.Equal(
			Load(RawKeyBase64, out _),
			EncryptionKeyLoader.TryLoadKey(
				new EncryptionOptions { Key = RawKeyBase64, KeyDerivationSalt = "whatever" }, out _));
	}

	[Fact]
	public void Passphrase_ByteBasedDerivation_IsBehaviourPreserving_Coverage()
	{
		// K47 COVERAGE (not proof): the passphrase is now fed to PBKDF2 as a byte buffer that is
		// zeroed after derivation, rather than via the string overload, so the highest-value copy
		// we control is wiped. The wipe has no external handle to observe; this guards that the
		// byte-based path yields the SAME key (UTF-8 bytes == the string overload's own encoding),
		// i.e. the change is behaviour-preserving. The origin string (config-bound Key, or the key
		// file text) stays unzeroable and is the documented residual.
		byte[]? a = Load("a stable passphrase value", out string? error, salt: TestSalt);
		Assert.Null(error);
		Assert.NotNull(a);
		Assert.Equal(32, a.Length);
		Assert.Equal(a, Load("a stable passphrase value", out _, salt: TestSalt));
	}

	[Fact]
	public void PassphraseAndRawKey_YieldDifferentKeys()
	{
		// The derived key never accidentally equals raw-key material.
		byte[]? derived = Load("some passphrase that is long", out _, salt: TestSalt);
		Assert.NotEqual(Convert.ToBase64String(derived!), RawKeyBase64);
	}
}

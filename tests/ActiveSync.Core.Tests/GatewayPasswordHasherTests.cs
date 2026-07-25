using ActiveSync.Core.Security;

namespace ActiveSync.Core.Tests;

public class GatewayPasswordHasherTests
{
	[Fact]
	public void Hash_RoundTrips_AndParses()
	{
		string stored = GatewayPasswordHasher.Hash("correct horse battery staple");
		Assert.StartsWith("pbkdf2$", stored);
		Assert.True(GatewayPasswordHasher.IsHashed(stored));
		Assert.True(GatewayPasswordHasher.TryParse(stored, out string? error));
		Assert.Null(error);
		Assert.True(GatewayPasswordHasher.Verify(stored, "correct horse battery staple"));
	}

	[Fact]
	public void Verify_WrongPassword_False()
	{
		string stored = GatewayPasswordHasher.Hash("right");
		Assert.False(GatewayPasswordHasher.Verify(stored, "wrong"));
		Assert.False(GatewayPasswordHasher.Verify(stored, ""));
	}

	[Fact]
	public void Hash_SamePasswordTwice_DiffersBySalt()
	{
		Assert.NotEqual(GatewayPasswordHasher.Hash("pw"), GatewayPasswordHasher.Hash("pw"));
	}

	[Theory]
	[InlineData("pbkdf2$")]
	[InlineData("pbkdf2$100000$onlytwo")]
	[InlineData("pbkdf2$100000$!!!$AAAA")]
	[InlineData("pbkdf2$999$c2FsdHNhbHRzYWx0c2FsdA==$aGFzaGhhc2hoYXNoaGFzaGhhc2hoYXNoaGFzaGhhc2g=")] // < 100k iterations
	[InlineData("pbkdf2$100000$c2FsdA==$aGFzaA==")] // salt/hash too short
	public void TryParse_Malformed_ReportsError(string stored)
	{
		Assert.False(GatewayPasswordHasher.TryParse(stored, out string? error));
		Assert.NotNull(error);
		Assert.False(GatewayPasswordHasher.Verify(stored, "anything"));
	}

	[Fact]
	public void TryParse_WeakerThanGeneratedSaltAndHash_IsRejected()
	{
		// K15: Hash() always emits a 16-byte salt / 32-byte hash, but TryParse's floor is only
		// 8/16 bytes — so an externally-supplied (or lower-privilege-written, cf. K3) value with
		// an 8-byte salt / 16-byte hash is accepted even though it is weaker than anything the
		// generator itself would ever produce.
		const string stored = "pbkdf2$100000$MTIzNDU2Nzg=$QUJDREVGR0hJSktMTU5PUA=="; // salt=8B, hash=16B
		Assert.False(GatewayPasswordHasher.TryParse(stored, out string? error));
		Assert.NotNull(error);
	}

	[Fact]
	public void TryParse_ExcessiveIterationCount_IsRejected()
	{
		// K3: no upper bound on the stored iteration count means an attacker able to write an
		// account row (e.g. `eas user set ... Password pbkdf2$2000000000$...`) can force every
		// login verify against that account to run ~2 billion PBKDF2 rounds, tying up the
		// request thread for seconds — a password-verify denial-of-service. The stored value
		// below is hand-assembled (a real 2-billion-iteration salt/hash, correctly sized, from a
		// cheap default-iteration Hash()) rather than produced via Hash(iterations: 2_000_000_000)
		// — actually running PBKDF2 2 billion times would make this test itself the DoS, on both
		// the unmodified and the fixed code.
		string[] parts = GatewayPasswordHasher.Hash("pw").Split('$');
		string stored = $"pbkdf2$2000000000${parts[2]}${parts[3]}";
		Assert.False(GatewayPasswordHasher.TryParse(stored, out string? error));
		Assert.NotNull(error);
	}

	[Fact]
	public void Verify_PlaintextStoredValue_ComparesExactly()
	{
		Assert.True(GatewayPasswordHasher.Verify("plain-secret", "plain-secret"));
		Assert.False(GatewayPasswordHasher.Verify("plain-secret", "plain-secreT"));
		Assert.False(GatewayPasswordHasher.Verify("plain-secret", "plain-secret2"));
		Assert.False(GatewayPasswordHasher.IsHashed("plain-secret"));
	}
}

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
	public void Verify_PlaintextStoredValue_ComparesExactly()
	{
		Assert.True(GatewayPasswordHasher.Verify("plain-secret", "plain-secret"));
		Assert.False(GatewayPasswordHasher.Verify("plain-secret", "plain-secreT"));
		Assert.False(GatewayPasswordHasher.Verify("plain-secret", "plain-secret2"));
		Assert.False(GatewayPasswordHasher.IsHashed("plain-secret"));
	}
}

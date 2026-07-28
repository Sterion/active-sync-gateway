using System.Security.Cryptography;

using ActiveSync.Cli;
using ActiveSync.Crypto;

namespace ActiveSync.Cli.Tests;

// The slim `eas` client's forwarding logic lived entirely in top-level statements in Program.cs —
// no namespace, no seam to construct it against a fake clock/environment, so none of it was covered by
// any test project. These exercise the pieces that decide what a real `eas` invocation actually does:
// the local-only verbs, EAS_NO_FORWARD, the sealed-envelope construction (round-tripped against the real
// LocalCliEnvelope/SecretValue types Program.cs seals with), the plaintext fallback, and the
// always-127.0.0.1 base URL derivation.
public sealed class EasForwardingClientTests
{
	[Theory]
	[InlineData("serve")]
	[InlineData("protect")]
	[InlineData("SERVE")]
	[InlineData("Protect")]
	public void IsLocalOnlyVerb_ServeOrProtect_ReturnsTrue(string verb) =>
		Assert.True(EasForwardingClient.IsLocalOnlyVerb([verb, "--extra"]));

	[Fact]
	public void IsLocalOnlyVerb_OtherVerb_ReturnsFalse() =>
		Assert.False(EasForwardingClient.IsLocalOnlyVerb(["users", "list"]));

	[Fact]
	public void IsLocalOnlyVerb_NoArgs_ReturnsTrue()
	{
		// A bare `eas` invocation forwarded to a running gateway ran BannerCommand inside the
		// live process, which then told the operator "The gateway is NOT running" — true only when
		// nothing answered /cli. Bare eas must always run locally, matching docs/cli.md's "the
		// config that WOULD run" framing, which is only meaningful against a process that is not
		// already serving.
		Assert.True(EasForwardingClient.IsLocalOnlyVerb([]));
	}

	[Fact]
	public void ShouldForceLocal_EasNoForwardIsOne_ReturnsTrue() =>
		Assert.True(EasForwardingClient.ShouldForceLocal(name => name == "EAS_NO_FORWARD" ? "1" : null));

	[Theory]
	[InlineData(null)]
	[InlineData("0")]
	[InlineData("true")]
	public void ShouldForceLocal_AnythingElse_ReturnsFalse(string? value) =>
		Assert.False(EasForwardingClient.ShouldForceLocal(name => name == "EAS_NO_FORWARD" ? value : null));

	[Fact]
	public void ResolveBaseUrl_NothingConfigured_DefaultsToLoopback5080() =>
		Assert.Equal("http://127.0.0.1:5080",
			EasForwardingClient.ResolveBaseUrl(_ => null, Path.GetTempPath()));

	[Fact]
	public void ResolveBaseUrl_LocalhostFromAspnetcoreUrls_BecomesLoopback() =>
		Assert.Equal("http://127.0.0.1:5080", EasForwardingClient.ResolveBaseUrl(
			name => name == "ASPNETCORE_URLS" ? "http://localhost:5080;http://localhost:5081" : null,
			Path.GetTempPath()));

	[Fact]
	public void ResolveBaseUrl_WildcardHostFromKestrelEnv_BecomesLoopback() =>
		Assert.Equal("http://127.0.0.1:5080", EasForwardingClient.ResolveBaseUrl(
			name => name == "Kestrel__Endpoints__Http__Url" ? "http://0.0.0.0:5080/" : null,
			Path.GetTempPath()));

	// ASPNETCORE_URLS=http://+:5080 and http://*:5080 are idiomatic Kestrel wildcard hosts (the
	// shipped image instead sets Kestrel__Endpoints__Http__Url, so it never hits this), but
	// ResolveBaseUrl only normalized 0.0.0.0/[::]/localhost — a hand-rolled deployment using either
	// wildcard form got a base URL naming a host ("+" or "*") that fails DNS, so every command
	// silently falls back to a full cold start of ActiveSync.Server.dll with no diagnostic.
	[Fact]
	public void ResolveBaseUrl_PlusWildcardHost_BecomesLoopback() =>
		Assert.Equal("http://127.0.0.1:5080", EasForwardingClient.ResolveBaseUrl(
			name => name == "ASPNETCORE_URLS" ? "http://+:5080" : null,
			Path.GetTempPath()));

	[Fact]
	public void ResolveBaseUrl_StarWildcardHost_BecomesLoopback() =>
		Assert.Equal("http://127.0.0.1:5080", EasForwardingClient.ResolveBaseUrl(
			name => name == "Kestrel__Endpoints__Http__Url" ? "http://*:5080" : null,
			Path.GetTempPath()));

	[Fact]
	public void BuildRequest_NoKey_IsThePlaintextFallback()
	{
		CliRequest request = EasForwardingClient.BuildRequest(
			["users", "list"], stdin: null, key: null, color: false, width: 80, now: DateTimeOffset.UtcNow);

		Assert.Equal(["users", "list"], request.Args!);
		Assert.Null(request.Sealed);
	}

	[Fact]
	public void BuildRequest_WithKey_SealsAnEnvelopeThatRoundTripsThroughLocalCliEnvelope()
	{
		byte[] key = RandomNumberGenerator.GetBytes(32);
		DateTimeOffset now = DateTimeOffset.UtcNow;

		CliRequest request = EasForwardingClient.BuildRequest(
			["device", "password"], stdin: "piped stdin", key: key, color: true, width: 120, now: now);

		// The command line and stdin never travel in the clear once a key is configured.
		Assert.Null(request.Args);
		Assert.Null(request.Stdin);
		Assert.NotNull(request.Sealed);

		bool opened = LocalCliEnvelope.TryOpen(
			request.Sealed, key, now.ToUnixTimeMilliseconds(), windowMs: 60_000, out LocalCliEnvelope? envelope);

		Assert.True(opened);
		Assert.Equal(["device", "password"], envelope!.Args);
		Assert.Equal("piped stdin", envelope.Stdin);
		Assert.Equal(now.ToUnixTimeMilliseconds(), envelope.TimestampUnixMs);
	}

	[Fact]
	public void BuildRequest_WithKey_WrongKeyCannotOpenTheEnvelope()
	{
		byte[] key = RandomNumberGenerator.GetBytes(32);
		byte[] wrongKey = RandomNumberGenerator.GetBytes(32);
		DateTimeOffset now = DateTimeOffset.UtcNow;

		CliRequest request = EasForwardingClient.BuildRequest(["ping"], null, key, false, 0, now);

		Assert.False(LocalCliEnvelope.TryOpen(
			request.Sealed, wrongKey, now.ToUnixTimeMilliseconds(), 60_000, out _));
	}
}

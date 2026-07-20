using ActiveSync.Server.Setup;

namespace ActiveSync.Server.Tests;

/// <summary>
///   <see cref="WebApplicationExtensions.ResolvePublicScheme" /> decides the request scheme behind a
///   TLS-terminating proxy: a configured PublicUrl wins over the header, so the OIDC redirect_uri is
///   built as https at both the authorize step and the token exchange.
/// </summary>
public sealed class PublicSchemeTests
{
	[Fact]
	public void PublicUrl_Wins_OverForwardedProto()
	{
		Assert.Equal("https", WebApplicationExtensions.ResolvePublicScheme("https://eas.example.com", "http"));
	}

	[Fact]
	public void ForwardedProto_UsedWhenNoPublicUrl()
	{
		Assert.Equal("https", WebApplicationExtensions.ResolvePublicScheme(null, "https"));
	}

	[Fact]
	public void ForwardedProto_ChainTakesTheFirst()
	{
		Assert.Equal("https", WebApplicationExtensions.ResolvePublicScheme(null, "https, http"));
	}

	[Fact]
	public void NothingToForce_ReturnsNull()
	{
		Assert.Null(WebApplicationExtensions.ResolvePublicScheme(null, null));
		Assert.Null(WebApplicationExtensions.ResolvePublicScheme("   ", "  "));
	}

	[Fact]
	public void MalformedPublicUrl_FallsBackToForwardedProto()
	{
		Assert.Equal("https", WebApplicationExtensions.ResolvePublicScheme("not a url", "https"));
	}
}

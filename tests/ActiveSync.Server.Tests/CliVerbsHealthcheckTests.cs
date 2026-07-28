using ActiveSync.Server.Cli;

namespace ActiveSync.Server.Tests;

/// <summary>
///   E15: `eas healthcheck` resolves the gateway base URL, but used to normalize only
///   <c>0.0.0.0</c>/<c>[::]</c> to <c>localhost</c> rather than <c>127.0.0.1</c> — the repo's own
///   IPv4-only rule (see <c>EasForwardingClient.ResolveBaseUrl</c>, which the slim client's every
///   OTHER verb uses) says "never 'localhost' — the gateway is IPv4-only, and a `::1`-first resolve
///   costs a ~2 s failed connect". The container HEALTHCHECK runs this verb with a 4 s HttpClient
///   timeout inside a 5 s Docker timeout, so a slow `::1` attempt can time out and restart a
///   healthy container.
/// </summary>
public sealed class CliVerbsHealthcheckTests
{
	[Fact]
	public void ResolveHealthcheckBaseUrl_NothingConfigured_DefaultsToLoopback()
	{
		Assert.Equal("http://127.0.0.1:5080", CliVerbs.ResolveHealthcheckBaseUrl(_ => null));
	}

	[Fact]
	public void ResolveHealthcheckBaseUrl_WildcardHostFromKestrelEnv_BecomesLoopback_NotLocalhost()
	{
		Assert.Equal("http://127.0.0.1:5080", CliVerbs.ResolveHealthcheckBaseUrl(
			name => name == "Kestrel__Endpoints__Http__Url" ? "http://0.0.0.0:5080/" : null));
	}

	[Fact]
	public void ResolveHealthcheckBaseUrl_IPv6WildcardFromAspnetcoreUrls_BecomesLoopback_NotLocalhost()
	{
		Assert.Equal("http://127.0.0.1:5080", CliVerbs.ResolveHealthcheckBaseUrl(
			name => name == "ASPNETCORE_URLS" ? "http://[::]:5080;http://[::]:5081" : null));
	}
}

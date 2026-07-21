using ActiveSync.Core.Options;

namespace ActiveSync.WebUi.Tests;

/// <summary>
///   C20 — the Content-Security-Policy of the SPA responses. <c>base-uri</c> is the real gap:
///   it does NOT fall back to <c>default-src</c>, so an injected <c>&lt;base href&gt;</c> would
///   redirect the SPA's dynamic module imports to another origin even with
///   <c>default-src 'self'</c> in place. <c>form-action</c> and <c>object-src</c> do not fall
///   back either. There is no HTML injection sink in the SPA today — this closes the directive
///   set so that stops being the only thing holding it up.
/// </summary>
public sealed class SecurityHeaderTests
{
	[Theory]
	[InlineData("/admin")]
	[InlineData("/user")]
	[InlineData("/shared/app.css")]
	public async Task UiResponses_CarryTheFullDirectiveSet(string path)
	{
		await using WebUiHost host = await WebUiHost.StartAsync(
			WebUiHost.Users(("alice", new AccountOptions { Admin = true })));
		using HttpClient client = host.Anonymous();

		HttpResponseMessage response = await client.GetAsync(path);
		string csp = response.Headers.GetValues("Content-Security-Policy").Single();

		Assert.Contains("default-src 'self'", csp, StringComparison.Ordinal);
		Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
		Assert.Contains("base-uri 'none'", csp, StringComparison.Ordinal);
		Assert.Contains("form-action 'self'", csp, StringComparison.Ordinal);
		Assert.Contains("object-src 'none'", csp, StringComparison.Ordinal);
	}
}

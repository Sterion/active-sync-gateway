using ActiveSync.Backends.Dav;

namespace ActiveSync.Core.Tests;

public class WebDavRedirectTests
{
	[Theory]
	// Same origin (scheme + host + port) → follow with the Authorization header.
	[InlineData("https://dav.example.com/", "https://dav.example.com/cal/", true)]
	[InlineData("https://dav.example.com/", "https://DAV.EXAMPLE.COM/other", true)]
	[InlineData("http://dav.example.com/", "http://dav.example.com/cal/", true)]
	[InlineData("https://dav.example.com:8443/", "https://dav.example.com:8443/cal/", true)]
	// Never hand the Authorization header to another host…
	[InlineData("https://dav.example.com/", "https://evil.example.net/cal/", false)]
	[InlineData("https://dav.example.com/", "https://sub.dav.example.com/cal/", false)]
	// …never downgrade it onto a cleartext connection…
	[InlineData("https://dav.example.com/", "http://dav.example.com/cal/", false)]
	// …never change scheme (even an "upgrade")…
	[InlineData("http://dav.example.com/", "https://dav.example.com/cal/", false)]
	// …and never move to a different port (a co-tenant service could be listening there).
	[InlineData("https://dav.example.com/", "https://dav.example.com:8443/cal/", false)]
	public void IsSafeRedirect_ProtectsCredentials(string baseUri, string target, bool expected)
	{
		Assert.Equal(expected, WebDavClient.IsSafeRedirect(new Uri(baseUri), new Uri(target)));
	}
}

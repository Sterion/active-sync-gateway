namespace ActiveSync.Backends.Dav;

/// <summary>Shared /readyz probe for the DAV providers: any HTTP answer = reachable.</summary>
internal static class DavReadiness
{
	public static async Task<bool> ProbeAsync(string baseUrl, CancellationToken ct)
	{
		// A section without its own BaseUrl (the Tasks overlay on the Calendar section)
		// has no endpoint of its own to probe — the paired role covers it.
		if (string.IsNullOrWhiteSpace(baseUrl))
			return true;
		using HttpClient http = new(new SocketsHttpHandler
		{
			// Reachability only — DAV endpoints legitimately answer 401 without creds,
			// and lab deployments use self-signed certificates the gateway is
			// separately configured to trust.
			SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true }
		});
		using HttpRequestMessage request = new(HttpMethod.Options, baseUrl);
		using HttpResponseMessage response = await http.SendAsync(request, ct).ConfigureAwait(false);
		return true; // any HTTP status = the server answered
	}
}

using ActiveSync.Backends.Common;

namespace ActiveSync.Backends.Dav;

/// <summary>Shared /readyz probe for the DAV providers: any HTTP answer = reachable.</summary>
internal static class DavReadiness
{
	public static async Task<bool> ProbeAsync(
		string baseUrl, bool allowInvalidCertificates, string? caCertificatePath, CancellationToken ct)
	{
		// A section without its own BaseUrl (the Tasks overlay on the Calendar section)
		// has no endpoint of its own to probe — the paired role covers it.
		if (string.IsNullOrWhiteSpace(baseUrl))
			return true;
		// H1: honor the operator's TLS trust settings instead of blanket-accepting every
		// certificate. Reachability only — DAV endpoints legitimately answer 401 without creds —
		// but a self-signed certificate the operator did NOT opt into (AllowInvalidCertificates /
		// CaCertificatePath) must still fail, exactly as a real request would. The pooled probe
		// handler (H26) is reused per TLS shape rather than built and discarded each call.
		using HttpClient http = BackendHttpClientFactory.CreateProbeClient(
			allowInvalidCertificates, caCertificatePath, TimeSpan.FromSeconds(5));
		using HttpRequestMessage request = new(HttpMethod.Options, baseUrl);
		using HttpResponseMessage response = await http.SendAsync(request, ct).ConfigureAwait(false);
		return true; // any HTTP status = the server answered
	}
}

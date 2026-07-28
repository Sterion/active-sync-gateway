using System.Net;
using ActiveSync.Core.Options;
using ActiveSync.Server.Eas;

namespace ActiveSync.Server.Setup;

/// <summary>
///   Shapes the /readyz payload. E16: the component map names every configured backend role — a
///   topology map an anonymous caller on the phone-facing listener has no business enumerating. A
///   readiness probe only needs the verdict (and the HTTP status carries that), so the detail is
///   exposed to local callers (k8s node probes, an operator on the box) and to callers arriving from
///   a configured <see cref="AuthOptions.TrustedProxies" /> hop (E9 — a kubelet's httpGet probe dials
///   the pod from the node/CNI address, never 127.0.0.1, so loopback-only left the documented k8s
///   deployment never seeing any detail at all), and withheld from everyone else.
/// </summary>
internal static class ReadinessResponse
{
	public static object Body(bool ready, IReadOnlyDictionary<string, bool> components, bool includeDetail)
	{
		string status = ready ? "ready" : "not ready";
		return includeDetail
			? new { status, components }
			: new { status };
	}

	/// <summary>
	///   A loopback connection is treated as local and trusted, and so is a connection arriving from a
	///   configured <see cref="AuthOptions.TrustedProxies" /> hop (E9) — the same peer-trust gate
	///   <see cref="EndpointAuth.IsFromTrustedProxy" /> applies to <c>X-Forwarded-Proto</c>, so listing
	///   the k8s node/CIDR there restores the detail for the node probe without opening it to anyone
	///   else. E6: a NULL peer is NOT local in production — some transports can legitimately deliver a
	///   null <c>RemoteIpAddress</c> for a genuinely remote caller too, so treating every null as local
	///   disclosed the same backend topology the check exists to withhold. The one exception is
	///   Microsoft.AspNetCore.TestHost's in-memory transport, which never sets a peer for ANY
	///   caller — the integration suite already marks that situation via
	///   <c>AS_TEST_FORCE_SERVE</c> (the module initializer in the test assembly's
	///   <c>TestBootstrap.cs</c>), so a null peer stays local only there.
	/// </summary>
	public static bool IsLocal(HttpContext http, AuthOptions auth)
	{
		IPAddress? remote = http.Connection.RemoteIpAddress;
		if (remote is not null)
			return IPAddress.IsLoopback(remote) || EndpointAuth.IsFromTrustedProxy(http, auth);
		return Environment.GetEnvironmentVariable("AS_TEST_FORCE_SERVE") == "1";
	}
}

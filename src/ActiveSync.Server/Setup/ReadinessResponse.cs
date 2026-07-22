using System.Net;

namespace ActiveSync.Server.Setup;

/// <summary>
///   Shapes the /readyz payload. E16: the component map names every configured backend role — a
///   topology map an anonymous caller on the phone-facing listener has no business enumerating. A
///   readiness probe only needs the verdict (and the HTTP status carries that), so the detail is
///   exposed to local callers (k8s node probes, an operator on the box) and withheld from everyone
///   else.
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

	/// <summary>A loopback (or feature-less test) connection is treated as local and trusted.</summary>
	public static bool IsLocal(HttpContext http)
	{
		IPAddress? remote = http.Connection.RemoteIpAddress;
		return remote is null || IPAddress.IsLoopback(remote);
	}
}

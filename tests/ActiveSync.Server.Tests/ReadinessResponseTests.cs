using System.Net;
using System.Text.Json;
using ActiveSync.Core.Options;
using ActiveSync.Server.Setup;
using Microsoft.AspNetCore.Http;

namespace ActiveSync.Server.Tests;

/// <summary>
///   E16: /readyz must not disclose the configured backend topology (the component role names) to
///   anonymous, non-local callers on the phone-facing listener. The verdict travels in the HTTP
///   status; only a local caller gets the per-component detail.
///
///   E6: <see cref="ReadinessResponse.IsLocal" /> used to treat a NULL <c>RemoteIpAddress</c> as
///   local unconditionally — round-1's rationale was TestServer's in-memory transport (which never
///   sets a peer for anyone), but some real transports can also legitimately deliver a null peer for
///   a genuinely REMOTE caller, so the shortcut leaked the same topology it was meant to withhold. A
///   null peer is local only under the test-host seam the integration suite already sets
///   (<c>AS_TEST_FORCE_SERVE</c>, from <c>TestBootstrap.cs</c>'s module initializer) — never in a
///   real deployment.
/// </summary>
public sealed class ReadinessResponseTests
{
	// Guards the process-wide env var so this test can't race a parallel test in the same run.
	private static readonly object EnvLock = new();

	/// <summary>No trusted proxies configured — the default, and irrelevant to the tests below it.</summary>
	private static readonly AuthOptions NoTrustedProxies = new();

	[Fact]
	public void IsLocal_NullPeer_IsNotLocal_OutsideTheTestHostSeam()
	{
		lock (EnvLock)
		{
			string? original = Environment.GetEnvironmentVariable("AS_TEST_FORCE_SERVE");
			try
			{
				Environment.SetEnvironmentVariable("AS_TEST_FORCE_SERVE", null);
				DefaultHttpContext http = new();
				Assert.Null(http.Connection.RemoteIpAddress);

				Assert.False(ReadinessResponse.IsLocal(http, NoTrustedProxies));
			}
			finally
			{
				Environment.SetEnvironmentVariable("AS_TEST_FORCE_SERVE", original);
			}
		}
	}

	[Fact]
	public void IsLocal_NullPeer_UnderTheTestHostSeam_StaysLocal()
	{
		// Coverage, not a symptom reproduction: this seam is new behaviour the fix introduces (it
		// keeps the integration suite's TestServer-hosted /readyz assertions on the component map
		// working), so there is no pre-fix "wrong" outcome to reproduce here — the null-peer
		// disclosure above is what E6 is actually about.
		lock (EnvLock)
		{
			string? original = Environment.GetEnvironmentVariable("AS_TEST_FORCE_SERVE");
			try
			{
				Environment.SetEnvironmentVariable("AS_TEST_FORCE_SERVE", "1");
				DefaultHttpContext http = new();

				Assert.True(ReadinessResponse.IsLocal(http, NoTrustedProxies));
			}
			finally
			{
				Environment.SetEnvironmentVariable("AS_TEST_FORCE_SERVE", original);
			}
		}
	}

	[Fact]
	public void IsLocal_LoopbackPeer_IsLocal()
	{
		DefaultHttpContext http = new();
		http.Connection.RemoteIpAddress = IPAddress.Loopback;

		Assert.True(ReadinessResponse.IsLocal(http, NoTrustedProxies));
	}

	[Fact]
	public void IsLocal_RemotePeer_IsNotLocal()
	{
		DefaultHttpContext http = new();
		http.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");

		Assert.False(ReadinessResponse.IsLocal(http, NoTrustedProxies));
	}

	/// <summary>
	///   E9: a kubelet's httpGet probe dials the pod from the node/CNI address, never 127.0.0.1, so
	///   the loopback-only rule left the documented k8s deployment never seeing the component detail
	///   at all. Listing the node/CIDR in <see cref="AuthOptions.TrustedProxies" /> must restore it —
	///   the same peer-trust primitive <c>EndpointAuth.IsFromTrustedProxy</c> already applies to
	///   <c>X-Forwarded-Proto</c> (E1/E16).
	/// </summary>
	[Fact]
	public void IsLocal_NonLoopbackPeer_FromATrustedProxy_IsLocal()
	{
		DefaultHttpContext http = new();
		http.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");
		AuthOptions auth = new() { TrustedProxies = ["203.0.113.9"] };

		Assert.True(ReadinessResponse.IsLocal(http, auth));
	}

	[Fact]
	public void IsLocal_NonLoopbackPeer_NotAConfiguredTrustedProxy_IsNotLocal()
	{
		DefaultHttpContext http = new();
		http.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");
		AuthOptions auth = new() { TrustedProxies = ["10.0.0.1"] };

		Assert.False(ReadinessResponse.IsLocal(http, auth));
	}

	private static readonly Dictionary<string, bool> Components = new()
	{
		["database"] = true,
		["mailstore"] = true,
		["calendar"] = false
	};

	[Fact]
	public void Body_WithoutDetail_OmitsTheComponentTopology()
	{
		string json = JsonSerializer.Serialize(ReadinessResponse.Body(true, Components, includeDetail: false));

		Assert.Contains("\"status\":\"ready\"", json);
		Assert.DoesNotContain("components", json);
		Assert.DoesNotContain("mailstore", json);
		Assert.DoesNotContain("calendar", json);
	}

	[Fact]
	public void Body_WithDetail_KeepsTheComponentMap()
	{
		string json = JsonSerializer.Serialize(ReadinessResponse.Body(false, Components, includeDetail: true));

		Assert.Contains("\"status\":\"not ready\"", json);
		Assert.Contains("mailstore", json);
		Assert.Contains("calendar", json);
	}
}

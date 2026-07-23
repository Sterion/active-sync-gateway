using System.Net;
using System.Net.Http.Json;
using ActiveSync.Core.Options;
using ActiveSync.WebUi.Auth;

namespace ActiveSync.WebUi.Tests;

/// <summary>
///   C1: a successful web login must clear BOTH throttle keys — the per-(address, user) key AND
///   the IP-wide key. When only the per-user key is cleared, the IP-wide counter accrues
///   monotonically across the mixed failed/successful logins of a shared egress IP (a NAT/proxy)
///   until it 429s every user behind that address, including the ones logging in correctly.
/// </summary>
public sealed class WebLoginThrottleTests
{
	[Fact]
	public async Task SuccessfulLogin_ClearsTheIpWideThrottle_SoTyposDoNotLockOutTheWholeNat()
	{
		// MaxFailures = 2 ⇒ the IP-wide ceiling is 2 × 5 = 10. A single legitimate user (alice)
		// shares the egress IP with a stream of failed attempts (unknown users — the shared-NAT
		// "occasional typo"). Every failure bumps the IP-wide counter; only a success that ALSO
		// clears it keeps alice reachable.
		await using WebUiHost host = await WebUiHost.StartAsync(
			WebUiHost.Users(("alice", new AccountOptions())),
			new Dictionary<string, string?>
			{
				["ActiveSync:Auth:MaxFailures"] = "2",
				["ActiveSync:Auth:FailureWindowSeconds"] = "3600"
			});

		HttpClient client = host.Anonymous();

		// Enough iterations that, WITHOUT clearing the IP-wide key, the 10 failed attempts alone
		// drive the counter to its ceiling and every subsequent login — alice's included — 429s.
		for (int i = 0; i < 12; i++)
		{
			HttpResponseMessage failed = await client.PostAsJsonAsync(
				"/user/api/login", new { username = $"ghost{i}", password = "wrong" });
			Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);

			HttpResponseMessage ok = await client.PostAsJsonAsync(
				"/user/api/login", new { username = "alice", password = "irrelevant" });
			Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
		}
	}
}

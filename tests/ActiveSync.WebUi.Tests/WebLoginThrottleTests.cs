using System.Net;
using System.Net.Http.Json;
using ActiveSync.Core.Options;
using ActiveSync.WebUi.Auth;

namespace ActiveSync.WebUi.Tests;

/// <summary>
///   A successful web login must clear ONLY its own per-(address, user) key — never the
///   shared per-address ceiling. This replaces a test that asserted the opposite (a leftover from
///   an earlier review round): clearing the ceiling on any one account's success let an attacker
///   holding any single valid credential reset the address-wide counter after every batch of
///   guesses and rotate usernames indefinitely from that address, voiding
///   <see cref="ActiveSync.Core.Security.AuthThrottle" />'s own documented guarantee. The
///   accepted tradeoff: a shared NAT/proxy egress address that racks up failures from OTHER users
///   behind it is no longer forgiven by one user's successful logins on that address — it can
///   still 429 that address until the failure window drains.
/// </summary>
public sealed class WebLoginThrottleTests
{
	[Fact]
	public async Task SuccessfulLogin_DoesNotClearTheAddressWideCeiling()
	{
		// MaxFailures = 2 ⇒ the address-wide ceiling is 2 × 5 = 10. A legitimate user (alice)
		// shares the egress IP with a stream of failed attempts from other, unknown users
		// (ghosts). Interleave alice's successes with the ghost failures: each success must clear
		// only alice's own key, never the shared ceiling, so the ghost failures still accumulate
		// toward it undisturbed.
		await using WebUiHost host = await WebUiHost.StartAsync(
			WebUiHost.Users(("alice", new UserOptions())),
			new Dictionary<string, string?>
			{
				["ActiveSync:Auth:MaxFailures"] = "2",
				["ActiveSync:Auth:FailureWindowSeconds"] = "3600"
			});

		HttpClient client = host.Anonymous();

		for (int i = 0; i < 9; i++)
		{
			HttpResponseMessage ok = await client.PostAsJsonAsync(
				"/user/api/login", new { username = "alice", password = "irrelevant" });
			Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

			HttpResponseMessage failed = await client.PostAsJsonAsync(
				"/user/api/login", new { username = $"ghost{i}", password = "wrong" });
			Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
		}

		// One more ghost failure crosses the ceiling (10 total) — proving alice's nine intervening
		// successes never reset it.
		HttpResponseMessage tenthFailure = await client.PostAsJsonAsync(
			"/user/api/login", new { username = "ghost9", password = "wrong" });
		Assert.Equal(HttpStatusCode.Unauthorized, tenthFailure.StatusCode);

		// Now even alice — who has been logging in successfully the whole time — is blocked: the
		// address-wide ceiling is not forgiven by anyone's success.
		HttpResponseMessage blocked = await client.PostAsJsonAsync(
			"/user/api/login", new { username = "alice", password = "irrelevant" });
		Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
	}
}

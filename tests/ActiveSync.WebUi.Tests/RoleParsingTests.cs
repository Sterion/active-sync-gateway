using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ActiveSync.Core.Options;

namespace ActiveSync.WebUi.Tests;

/// <summary>
///   C21 — the same four-line "parse the role or 400" block appeared five times across two
///   files, with a different message in one of them, and every handler minted its own
///   <c>{ error = … }</c> object so the SPA's <c>e.body?.error</c> convention was load-bearing
///   but written down nowhere. One parse, one message, one error shape.
/// </summary>
public sealed class RoleParsingTests
{
	private static async Task<WebUiHost> HostAsync()
	{
		return await WebUiHost.StartAsync(WebUiHost.Users(
			("alice", new AccountOptions { Admin = true }),
			("bob", new AccountOptions())));
	}

	[Fact]
	public async Task EveryRoleRoute_RejectsAnUnknownRoleTheSameWay()
	{
		await using WebUiHost host = await HostAsync();
		using HttpClient admin = await host.SignInAsync("alice", admin: true);
		using HttpClient portal = await host.SignInAsync("bob", admin: false);

		List<HttpResponseMessage> responses =
		[
			await admin.PutAsJsonAsync("/admin/api/backends/Nonsense", new { }),
			await admin.DeleteAsync("/admin/api/backends/Nonsense"),
			await admin.PostAsJsonAsync("/admin/api/backends/Nonsense/validate", new { }),
			await admin.PostAsJsonAsync("/admin/api/backends/Nonsense/test", new { }),
			await portal.PutAsJsonAsync("/user/api/backends/Nonsense", new { })
		];

		List<string> messages = [];
		foreach (HttpResponseMessage response in responses)
		{
			Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
			JsonElement body = await host.ReadJsonAsync(response);
			messages.Add(body.GetProperty("error").GetString()!);
			response.Dispose();
		}

		// One message, and it names the roles so the caller can correct it.
		Assert.Single(messages.Distinct(StringComparer.Ordinal));
		Assert.Contains("Calendar", messages[0], StringComparison.Ordinal);
	}
}

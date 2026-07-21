using System.Net.Http.Json;
using System.Text.Json;
using ActiveSync.Core.Accounts;
using ActiveSync.Core.Options;

namespace ActiveSync.WebUi.Tests;

/// <summary>
///   C9 — the admin backend probe's failure detail. The SSRF is acceptable (an admin who sets
///   the backend URL permanently can already make the gateway connect anywhere, and the probe
///   is capped at 5 s); returning <c>GetBaseException().Message</c> is not. It turns the probe
///   into a precise internal-network scanner — refused, timed out, DNS failure and TLS mismatch
///   are four distinguishable answers — and can surface file paths out of the exception text.
/// </summary>
public sealed class BackendProbeTests
{
	private static readonly Dictionary<string, string?> CalDavRole = new()
	{
		["ActiveSync:Backends:Calendar:Provider"] = "caldav",
		["ActiveSync:Backends:Calendar:BaseUrl"] = "https://dav.example.com"
	};

	private static async Task<WebUiHost> AdminHostAsync()
	{
		return await WebUiHost.StartAsync(
			WebUiHost.Users(("alice", new AccountOptions { Admin = true })), CalDavRole);
	}

	[Fact]
	public async Task RefusedConnection_ReportsAClosedOutcome_NotTheRawExceptionText()
	{
		await using WebUiHost host = await AdminHostAsync();
		using HttpClient client = await host.SignInAsync("alice", admin: true);

		// Nothing listens on port 1 of the loopback interface: an immediate, deterministic
		// connection failure, which is exactly the signal a network scan reads.
		HttpResponseMessage response = await client.PostAsJsonAsync("/admin/api/backends/Calendar/test", new
		{
			settings = new Dictionary<string, string?> { ["BaseUrl"] = "http://127.0.0.1:1/" }
		});

		JsonElement body = await host.ReadJsonAsync(response);
		Assert.False(body.GetProperty("reachable").GetBoolean());
		string detail = body.GetProperty("detail").GetString()!;
		Assert.Equal("The server could not be reached.", detail);
	}

	[Fact]
	public async Task UnknownProvider_ReportsAFixedMessage()
	{
		await using WebUiHost host = await AdminHostAsync();
		using HttpClient client = await host.SignInAsync("alice", admin: true);

		HttpResponseMessage response = await client.PostAsJsonAsync("/admin/api/backends/Calendar/test", new
		{
			provider = "nosuchprovider"
		});

		JsonElement body = await host.ReadJsonAsync(response);
		Assert.False(body.GetProperty("supported").GetBoolean());
		Assert.False(body.GetProperty("reachable").GetBoolean());
	}
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ActiveSync.Core.Accounts;
using ActiveSync.Core.Options;
using ActiveSync.Core.Settings;

namespace ActiveSync.WebUi.Tests;

/// <summary>
///   K38 / B22 — the admin settings editor must not be able to write the keys that decide what
///   the host loads from disk. <c>ActiveSync:Plugins:Directory</c> names the directory scanned
///   for plugin ASSEMBLIES, which are loaded into the gateway process with the master key in
///   memory; <c>ActiveSync:UsersFile</c> is read with <c>optional:false</c> at startup. Both were
///   ordinary catalogue entries, so an admin session (or anything that could reach the settings
///   table) escalated to in-process code execution on the next restart.
/// </summary>
public sealed class HostControlledSettingsTests
{
	private static Dictionary<string, AccountOptions> OneAdmin()
	{
		return WebUiHost.Users(("root", new AccountOptions { MailAddress = "root@example.com", Admin = true }));
	}

	[Theory]
	[InlineData("ActiveSync:Plugins:Directory", "/tmp/attacker-plugins")]
	[InlineData("ActiveSync:UsersFile", "/etc/shadow")]
	public async Task Admin_CannotWriteAHostControlledSetting(string key, string value)
	{
		await using WebUiHost host = await WebUiHost.StartAsync(OneAdmin());
		using HttpClient client = await host.SignInAsync("root", admin: true);

		HttpResponseMessage response = await client.PutAsJsonAsync(
			$"/admin/api/settings/{key}", new { value });

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		JsonElement body = await host.ReadJsonAsync(response);
		Assert.Contains("config file", body.GetProperty("error").GetString(), StringComparison.Ordinal);

		// And nothing was stored.
		GlobalSettingStore store = new(host.Factory);
		Assert.Null(await store.GetAsync(key, CancellationToken.None));
	}

	[Fact]
	public async Task Admin_DoesNotSeeHostControlledSettingsInTheEditor()
	{
		await using WebUiHost host = await WebUiHost.StartAsync(OneAdmin());
		using HttpClient client = await host.SignInAsync("root", admin: true);

		JsonElement listing = await host.ReadJsonAsync(await client.GetAsync("/admin/api/settings"));
		string[] keys = listing.EnumerateArray().Select(e => e.GetProperty("key").GetString()!).ToArray();

		Assert.DoesNotContain("ActiveSync:Plugins:Directory", keys);
		Assert.DoesNotContain("ActiveSync:UsersFile", keys);
		Assert.Contains("ActiveSync:ReadOnly", keys);
	}

	[Fact]
	public async Task Admin_CanStillWriteAnOrdinarySetting()
	{
		await using WebUiHost host = await WebUiHost.StartAsync(OneAdmin());
		using HttpClient client = await host.SignInAsync("root", admin: true);

		HttpResponseMessage response = await client.PutAsJsonAsync(
			"/admin/api/settings/ActiveSync:ReadOnly", new { value = "true" });

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		GlobalSettingStore store = new(host.Factory);
		Assert.Equal("true", await store.GetAsync("ActiveSync:ReadOnly", CancellationToken.None));
	}
}

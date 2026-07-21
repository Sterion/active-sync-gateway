using System.Net.Http.Json;
using System.Text.Json;
using ActiveSync.Core.Accounts;
using ActiveSync.Core.Options;

namespace ActiveSync.WebUi.Tests;

/// <summary>
///   C5 — a per-account backend role's free-form <c>Settings</c> were returned verbatim by the
///   admin and portal account APIs, unlike the global backends editor which masks secret fields.
///   A secret-named setting (ApiKey/Token/ClientSecret) on a role override therefore left the
///   server in the clear.
/// </summary>
public sealed class AccountSettingsRedactionTests
{
	private static Dictionary<string, AccountOptions> AliceWithSecretSetting()
	{
		return WebUiHost.Users(("alice", new AccountOptions
		{
			Admin = true,
			Backends = new Dictionary<string, BackendRoleOverride>
			{
				["Calendar"] = new()
				{
					Provider = "caldav",
					Settings = new Dictionary<string, string?>
					{
						["ApiKey"] = "account-api-secret",
						["BaseUrl"] = "https://dav.example.com",
					},
				},
			},
		}));
	}

	[Fact]
	public async Task AdminUserApi_MasksSecretRoleSettings()
	{
		await using WebUiHost host = await WebUiHost.StartAsync(AliceWithSecretSetting());
		using HttpClient client = await host.SignInAsync("alice", admin: true);

		HttpResponseMessage response = await client.GetAsync("/admin/api/users/alice");
		string raw = await response.Content.ReadAsStringAsync();
		Assert.DoesNotContain("account-api-secret", raw);

		JsonElement settings = (await host.ReadJsonAsync(response))
			.GetProperty("backends").GetProperty("Calendar").GetProperty("settings");
		Assert.Equal("***", settings.GetProperty("ApiKey").GetString());
		Assert.Equal("https://dav.example.com", settings.GetProperty("BaseUrl").GetString());
	}

	[Fact]
	public async Task AdminUserApi_RePostedMask_KeepsTheStoredSecret()
	{
		// Masking on read must not clobber on write: an unchanged (re-posted "***") secret setting
		// keeps its stored value, so an admin editing an unrelated field doesn't wipe the ApiKey.
		await using WebUiHost host = await WebUiHost.StartAsync(AliceWithSecretSetting());
		using HttpClient client = await host.SignInAsync("alice", admin: true);

		HttpResponseMessage put = await client.PutAsJsonAsync("/admin/api/users/alice", new
		{
			admin = true,
			enabled = true,
			backends = new Dictionary<string, object>
			{
				["Calendar"] = new
				{
					provider = "caldav",
					settings = new Dictionary<string, string?>
					{
						["ApiKey"] = "***",                       // unchanged — the mask the GET returned
						["BaseUrl"] = "https://dav.example.com",
					},
				},
			},
		});
		Assert.True(put.IsSuccessStatusCode, $"PUT failed: {put.StatusCode}");

		AccountOptions? stored = await new AccountStore(host.Factory).GetAsync("alice", CancellationToken.None);
		Assert.Equal("account-api-secret", stored!.Backends!["Calendar"].Settings!["ApiKey"]);
	}

	[Fact]
	public async Task PortalUserApi_MasksSecretRoleSettings()
	{
		await using WebUiHost host = await WebUiHost.StartAsync(AliceWithSecretSetting());
		using HttpClient client = await host.SignInAsync("alice", admin: false);

		HttpResponseMessage response = await client.GetAsync("/user/api/me");
		string raw = await response.Content.ReadAsStringAsync();
		Assert.DoesNotContain("account-api-secret", raw);

		JsonElement settings = (await host.ReadJsonAsync(response))
			.GetProperty("backends").GetProperty("Calendar").GetProperty("settings");
		Assert.Equal("***", settings.GetProperty("ApiKey").GetString());
		Assert.Equal("https://dav.example.com", settings.GetProperty("BaseUrl").GetString());
	}
}

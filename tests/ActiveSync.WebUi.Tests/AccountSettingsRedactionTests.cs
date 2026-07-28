using System.Net.Http.Json;
using System.Text.Json;
using ActiveSync.Core.Accounts;
using ActiveSync.Core.Options;

namespace ActiveSync.WebUi.Tests;

/// <summary>
///   A per-account backend role's free-form <c>Settings</c> were returned verbatim by the
///   admin and portal account APIs, unlike the global backends editor which masks secret fields.
///   A secret-named setting (ApiKey/Token/ClientSecret) on a role override therefore left the
///   server in the clear.
/// </summary>
public sealed class AccountSettingsRedactionTests
{
	private static Dictionary<string, UserOptions> AliceWithSecretSetting()
	{
		return WebUiHost.Users(("alice", new UserOptions
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
		// keeps its effective value, so an admin editing an unrelated field doesn't wipe the ApiKey.
		//
		// Behaviour change: alice's Calendar role here is entirely CONFIG-declared, and every
		// resubmitted field (provider, ApiKey behind the mask, BaseUrl) is now unmasked against the
		// merged view and then compared against configuration — since all three match config exactly,
		// nothing is a real deviation, so no database override is written at all (previously this
		// wrote the unmasked secret straight into the row, freezing it there — precisely the config-value-freeze
		// trap this item closes). The secret is unaffected either way: it keeps resolving through
		// configuration, and a later GET still reports it set and masked, never as the literal "***".
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

		UserOptions? stored = await new UserStore(host.Factory).GetAsync("alice", CancellationToken.None);
		Assert.Null(stored?.Backends?.GetValueOrDefault("Calendar")?.Settings);

		JsonElement afterGet = await host.ReadJsonAsync(await client.GetAsync("/admin/api/users/alice"));
		JsonElement settingsAfter = afterGet.GetProperty("backends").GetProperty("Calendar").GetProperty("settings");
		Assert.Equal("***", settingsAfter.GetProperty("ApiKey").GetString());
	}

	[Fact]
	public async Task AdminUserApi_RePostedMask_OnADatabaseOverride_KeepsTheStoredSecretInTheRow()
	{
		// The database-override counterpart of the test above: when the secret is a REAL database
		// deviation (no config counterpart), resubmitting the mask must still keep it stored — the
		// elision only drops a value that matches configuration, never one with nothing to match.
		await using WebUiHost host = await WebUiHost.StartAsync(WebUiHost.Users(("alice", new UserOptions { Admin = true })));
		UserStore store = new(host.Factory);
		await store.UpsertAsync("alice", new UserOptions
		{
			Admin = true,
			Backends = new Dictionary<string, BackendRoleOverride>(StringComparer.OrdinalIgnoreCase)
			{
				["Calendar"] = new()
				{
					Provider = "caldav",
					Settings = new Dictionary<string, string?>
					{
						["ApiKey"] = "db-only-api-secret",
						["BaseUrl"] = "https://dav.example.com",
					},
				},
			},
		}, CancellationToken.None);
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
						["ApiKey"] = "***",
						["BaseUrl"] = "https://dav.example.com",
					},
				},
			},
		});
		Assert.True(put.IsSuccessStatusCode, $"PUT failed: {put.StatusCode}");

		UserOptions? stored = await store.GetAsync("alice", CancellationToken.None);
		Assert.Equal("db-only-api-secret", stored!.Backends!["Calendar"].Settings!["ApiKey"]);
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

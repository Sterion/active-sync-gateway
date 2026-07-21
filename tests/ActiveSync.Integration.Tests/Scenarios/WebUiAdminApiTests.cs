using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ActiveSync.Core.Security;
using ActiveSync.Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;

namespace ActiveSync.Integration.Tests.Scenarios;

/// <summary>
///   The admin JSON API (settings / users / shares): same validation as the CLI, provenance
///   and source labeling for the default-as-placeholder UX, password sentinels, and the
///   no-secret-ever-leaves-the-server guarantee.
/// </summary>
[Collection("gateway")]
[Trait("Category", "Integration")]
public sealed class WebUiAdminApiTests(GatewayFixture gateway) : IAsyncLifetime
{
	private const string AdminUser = "apiadmin";
	private const string Password = "api-pa55!";

	private WebApplicationFactory<Program> _factory = null!;
	private HttpClient _client = null!;

	public async Task InitializeAsync()
	{
		_factory = gateway.CreateIsolatedFactory(new Dictionary<string, string?>
		{
			["ActiveSync:WebUi:Admin:Enabled"] = "true",
			[$"ActiveSync:Users:{AdminUser}:Password"] = GatewayPasswordHasher.Hash(Password),
			[$"ActiveSync:Users:{AdminUser}:Admin"] = "true",
			["ActiveSync:Users:cfguser:MailAddress"] = "cfg@example.com"
		});
		_client = _factory.CreateDefaultClient(new CookieContainerHandler());
		HttpResponseMessage login = await PostAsync("/admin/api/login", new { username = AdminUser, password = Password });
		Assert.Equal(HttpStatusCode.OK, login.StatusCode);
	}

	public async Task DisposeAsync()
	{
		_client.Dispose();
		await _factory.DisposeAsync();
	}

	private async Task<HttpResponseMessage> PostAsync(string path, object body, string method = "POST")
	{
		using HttpRequestMessage request = new(HttpMethod.Parse(method), path);
		request.Headers.Add("X-EAS-WebUi", "1");
		request.Content = JsonContent.Create(body);
		return await _client.SendAsync(request);
	}

	private async Task<HttpResponseMessage> DeleteAsync(string path)
	{
		using HttpRequestMessage request = new(HttpMethod.Delete, path);
		request.Headers.Add("X-EAS-WebUi", "1");
		return await _client.SendAsync(request);
	}

	[Fact]
	public async Task Settings_ListEditReset_WithSourceAndTier()
	{
		// A key the fixture never sets reports the code default with a null value (the UI
		// renders the default as a placeholder); a fixture-set key reports source=config.
		JsonElement list = await _client.GetFromJsonAsync<JsonElement>("/admin/api/settings");
		JsonElement declared = list.EnumerateArray()
			.Single(e => e.GetProperty("key").GetString() == "ActiveSync:RequireDeclaredUsers");
		Assert.Equal("default", declared.GetProperty("source").GetString());
		Assert.Equal("false", declared.GetProperty("default").GetString());
		Assert.Null(declared.GetProperty("value").GetString());
		Assert.Equal("config", list.EnumerateArray()
			.Single(e => e.GetProperty("key").GetString() == "ActiveSync:ReadOnly")
			.GetProperty("source").GetString());

		// Validation mirrors the CLI: bad values and bootstrap keys are rejected.
		Assert.Equal(HttpStatusCode.BadRequest,
			(await PostAsync("/admin/api/settings/ActiveSync:ReadOnly", new { value = "maybe" }, "PUT")).StatusCode);
		Assert.Equal(HttpStatusCode.BadRequest,
			(await PostAsync("/admin/api/settings/ActiveSync:Encryption:Key", new { value = "x" }, "PUT")).StatusCode);
		Assert.Equal(HttpStatusCode.BadRequest,
			(await PostAsync("/admin/api/settings/ActiveSync:Bogus", new { value = "x" }, "PUT")).StatusCode);

		// A valid write persists and reports its tier; the list then shows source=db.
		HttpResponseMessage put = await PostAsync(
			"/admin/api/settings/ActiveSync:Eas:DefaultWindowSize", new { value = "150" }, "PUT");
		Assert.Equal(HttpStatusCode.OK, put.StatusCode);
		Assert.Contains("live", await put.Content.ReadAsStringAsync());
		list = await _client.GetFromJsonAsync<JsonElement>("/admin/api/settings");
		JsonElement window = list.EnumerateArray()
			.Single(e => e.GetProperty("key").GetString() == "ActiveSync:Eas:DefaultWindowSize");
		Assert.Equal("db", window.GetProperty("source").GetString());
		Assert.Equal("150", window.GetProperty("value").GetString());

		// DELETE reverts to the default.
		Assert.Equal(HttpStatusCode.OK,
			(await DeleteAsync("/admin/api/settings/ActiveSync:Eas:DefaultWindowSize")).StatusCode);
		list = await _client.GetFromJsonAsync<JsonElement>("/admin/api/settings");
		window = list.EnumerateArray()
			.Single(e => e.GetProperty("key").GetString() == "ActiveSync:Eas:DefaultWindowSize");
		Assert.Equal("default", window.GetProperty("source").GetString());
	}

	[Fact]
	public async Task Settings_SecretKey_SealedAtRest_MaskedOnRead()
	{
		Assert.Equal(HttpStatusCode.OK, (await PostAsync(
			"/admin/api/settings/ActiveSync:WebUi:Oidc:ClientSecret", new { value = "super-secret" }, "PUT")).StatusCode);
		string list = await _client.GetStringAsync("/admin/api/settings");
		Assert.DoesNotContain("super-secret", list);
		Assert.Contains("***", list);
		await DeleteAsync("/admin/api/settings/ActiveSync:WebUi:Oidc:ClientSecret");
	}

	[Fact]
	public async Task Users_CrudWithProvenance_SentinelsAndLeakGuard()
	{
		// The merged list carries provenance: the config user and the admin itself.
		JsonElement users = await _client.GetFromJsonAsync<JsonElement>("/admin/api/users");
		Assert.Contains(users.EnumerateArray(), u =>
			u.GetProperty("login").GetString() == "cfguser" &&
			u.GetProperty("origin").GetString() == "config");

		// Create a DB user with a gateway password + a role override.
		HttpResponseMessage create = await PostAsync("/admin/api/users/apiuser", new
		{
			mailAddress = "api@example.com",
			admin = false,
			password = "user-pw-1",
			backends = new Dictionary<string, object>
			{
				["Contacts"] = new { enabled = false },
				["MailStore"] = new { userName = "real-imap-user", password = "imap-pw", settings = new Dictionary<string, string> { ["Port"] = "993" } }
			}
		}, "PUT");
		Assert.Equal(HttpStatusCode.OK, create.StatusCode);

		// Validation failures are config-grade and nothing is saved.
		HttpResponseMessage invalid = await PostAsync("/admin/api/users/apiuser2", new
		{
			backends = new Dictionary<string, object>
			{
				["MailStore"] = new { settings = new Dictionary<string, string> { ["Port"] = "99999" } }
			}
		}, "PUT");
		Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
		Assert.Contains("out of range", await invalid.Content.ReadAsStringAsync());

		// Leak guard: no stored secret shape ever appears in any read.
		string everything = await _client.GetStringAsync("/admin/api/users") +
		                    await _client.GetStringAsync("/admin/api/users/apiuser");
		Assert.DoesNotContain("pbkdf2$", everything);
		Assert.DoesNotContain("enc:v1:", everything);
		Assert.DoesNotContain("user-pw-1", everything);
		Assert.DoesNotContain("imap-pw", everything);
		Assert.Contains("passwordSet", everything);

		// Sentinel: an update with password=null KEEPS the stored password (the user can
		// still log into the portal with it afterwards).
		Assert.Equal(HttpStatusCode.OK, (await PostAsync("/admin/api/users/apiuser", new
		{
			mailAddress = "api2@example.com",
			password = (string?)null,
			backends = new Dictionary<string, object> { ["MailStore"] = new { userName = "real-imap-user", password = (string?)null } }
		}, "PUT")).StatusCode);
		JsonElement updated = await _client.GetFromJsonAsync<JsonElement>("/admin/api/users/apiuser");
		Assert.Equal("api2@example.com", updated.GetProperty("mailAddress").GetString());
		Assert.True(updated.GetProperty("passwordSet").GetBoolean());
		Assert.True(updated.GetProperty("backends").GetProperty("MailStore").GetProperty("passwordSet").GetBoolean());

		// Sentinel: "" clears the stored password.
		Assert.Equal(HttpStatusCode.OK, (await PostAsync("/admin/api/users/apiuser", new
		{
			mailAddress = "api2@example.com",
			password = ""
		}, "PUT")).StatusCode);
		updated = await _client.GetFromJsonAsync<JsonElement>("/admin/api/users/apiuser");
		Assert.False(updated.GetProperty("passwordSet").GetBoolean());

		// Delete removes the DB row; a config-only login reports the fallback.
		Assert.Equal(HttpStatusCode.OK, (await DeleteAsync("/admin/api/users/apiuser")).StatusCode);
		Assert.Equal(HttpStatusCode.NotFound, (await DeleteAsync("/admin/api/users/apiuser")).StatusCode);
	}

	[Fact]
	public async Task Shares_GrantRemodeRemove()
	{
		Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync("/admin/api/shares",
			new { user = "u", collectionHref = "not-absolute" })).StatusCode);

		Assert.Equal(HttpStatusCode.OK, (await PostAsync("/admin/api/shares",
			new { user = "shareuser", collectionHref = "/dav/cal/family/", readOnly = false })).StatusCode);
		// Re-granting re-modes.
		Assert.Equal(HttpStatusCode.OK, (await PostAsync("/admin/api/shares",
			new { user = "shareuser", collectionHref = "/dav/cal/family/", readOnly = true })).StatusCode);

		// Paged like /devices: { total, entries }.
		JsonElement shares = await _client.GetFromJsonAsync<JsonElement>("/admin/api/shares?user=shareuser");
		JsonElement grant = Assert.Single(shares.GetProperty("entries").EnumerateArray());
		Assert.True(grant.GetProperty("readOnly").GetBoolean());

		Assert.Equal(HttpStatusCode.OK, (await DeleteAsync(
			"/admin/api/shares?user=shareuser&collectionHref=%2Fdav%2Fcal%2Ffamily%2F")).StatusCode);
		Assert.Equal(HttpStatusCode.NotFound, (await DeleteAsync(
			"/admin/api/shares?user=shareuser&collectionHref=%2Fdav%2Fcal%2Ffamily%2F")).StatusCode);
	}
}

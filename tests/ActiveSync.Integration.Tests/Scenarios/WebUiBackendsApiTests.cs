using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using ActiveSync.Core.Security;
using ActiveSync.Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;

namespace ActiveSync.Integration.Tests.Scenarios;

/// <summary>
///   The Backends editor API: provider schemas the UI renders forms from, role config as
///   database overrides over the config file (with source attribution and reset), validation
///   that is the providers' own, and the reachability probe.
/// </summary>
[Collection("gateway")]
[Trait("Category", "Integration")]
public sealed class WebUiBackendsApiTests(GatewayFixture gateway) : IAsyncLifetime
{
	private const string AdminUser = "backendsadmin";
	private const string Password = "backends-pa55!";

	private WebApplicationFactory<Program> _factory = null!;
	private HttpClient _client = null!;

	public async Task InitializeAsync()
	{
		_factory = gateway.CreateIsolatedFactory(new Dictionary<string, string?>
		{
			["ActiveSync:WebUi:Admin:Enabled"] = "true",
			[$"ActiveSync:Users:{AdminUser}:Password"] = GatewayPasswordHasher.Hash(Password),
			[$"ActiveSync:Users:{AdminUser}:Admin"] = "true"
		});
		_client = _factory.CreateDefaultClient(new CookieContainerHandler());
		HttpResponseMessage login = await SendAsync("POST", "/admin/api/login",
			new { username = AdminUser, password = Password });
		Assert.Equal(HttpStatusCode.OK, login.StatusCode);
	}

	public async Task DisposeAsync()
	{
		_client.Dispose();
		await _factory.DisposeAsync();
	}

	private async Task<HttpResponseMessage> SendAsync(string method, string path, object? body = null)
	{
		using HttpRequestMessage request = new(HttpMethod.Parse(method), path);
		request.Headers.Add("X-EAS-WebUi", "1");
		if (body is not null)
			request.Content = JsonContent.Create(body);
		return await _client.SendAsync(request);
	}

	private async Task<JsonElement> RoleAsync(string role)
	{
		JsonElement roles = await _client.GetFromJsonAsync<JsonElement>("/admin/api/backends");
		return roles.EnumerateArray().Single(r => r.GetProperty("role").GetString() == role);
	}

	private static string? Setting(JsonElement role, string key)
	{
		foreach (JsonElement setting in role.GetProperty("settings").EnumerateArray())
			if (setting.GetProperty("key").GetString() == key)
				return setting.GetProperty("value").GetString();
		return null;
	}

	private static string? Source(JsonElement role, string key)
	{
		foreach (JsonElement setting in role.GetProperty("settings").EnumerateArray())
			if (setting.GetProperty("key").GetString() == key)
				return setting.GetProperty("source").GetString();
		return null;
	}

	[Fact]
	public async Task Providers_ExposeRolesAndSchemas()
	{
		JsonElement providers = await _client.GetFromJsonAsync<JsonElement>("/admin/api/backends/providers");
		JsonElement imap = providers.EnumerateArray().Single(p => p.GetProperty("name").GetString() == "imap");

		Assert.Contains("MailStore", imap.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
		Assert.True(imap.GetProperty("probe").GetBoolean()); // imap implements IReadinessSource

		JsonElement fields = imap.GetProperty("schemas").GetProperty("MailStore");
		JsonElement host = fields.EnumerateArray().Single(f => f.GetProperty("name").GetString() == "Host");
		Assert.True(host.GetProperty("required").GetBoolean());
		JsonElement port = fields.EnumerateArray().Single(f => f.GetProperty("name").GetString() == "Port");
		Assert.Equal("Int", port.GetProperty("type").GetString());
		Assert.Equal("993", port.GetProperty("default").GetString());
		JsonElement security = fields.EnumerateArray().Single(f => f.GetProperty("name").GetString() == "Security");
		Assert.Contains("StartTls", security.GetProperty("enumValues").EnumerateArray().Select(v => v.GetString()));

		// The local provider fills content roles but describes no settings — the UI falls back
		// to its raw editor rather than showing an empty form.
		JsonElement local = providers.EnumerateArray().Single(p => p.GetProperty("name").GetString() == "local");
		Assert.False(local.GetProperty("probe").GetBoolean());
		Assert.Empty(local.GetProperty("schemas").GetProperty("Contacts").EnumerateArray());
	}

	[Fact]
	public async Task Roles_ReportConfigSource_AndEveryRoleIsListed()
	{
		JsonElement roles = await _client.GetFromJsonAsync<JsonElement>("/admin/api/backends");
		Assert.Equal(7, roles.GetArrayLength()); // every role, assigned or not

		// The fixture configures MailStore in the "config file" layer.
		JsonElement mailStore = await RoleAsync("MailStore");
		Assert.True(mailStore.GetProperty("assigned").GetBoolean());
		Assert.Equal("imap", mailStore.GetProperty("provider").GetString());
		Assert.Equal("config", mailStore.GetProperty("providerSource").GetString());
		Assert.Equal("config", Source(mailStore, "Host"));

		// Notes is served by the local store; nothing is assigned to it.
		JsonElement notes = await RoleAsync("Notes");
		Assert.False(notes.GetProperty("assigned").GetBoolean());
		Assert.Null(notes.GetProperty("provider").GetString());
	}

	[Fact]
	public async Task Save_StoresOverride_AndResetBringsTheConfigValueBack()
	{
		// The config file assigns carddav here; moving the role to another provider is a real
		// deviation and is stored as one.
		HttpResponseMessage saved = await SendAsync("PUT", "/admin/api/backends/Contacts", new
		{
			provider = "jmap",
			settings = new Dictionary<string, string?> { ["BaseUrl"] = "https://dav.example.com" }
		});
		Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

		JsonElement contacts = await RoleAsync("Contacts");
		Assert.Equal("jmap", contacts.GetProperty("provider").GetString());
		Assert.Equal("db", contacts.GetProperty("providerSource").GetString());
		Assert.Equal("https://dav.example.com", Setting(contacts, "BaseUrl"));
		Assert.Equal("db", Source(contacts, "BaseUrl"));

		// A null value drops the override; with nothing in the config file the leaf disappears.
		Assert.Equal(HttpStatusCode.OK, (await SendAsync("PUT", "/admin/api/backends/Contacts", new
		{
			settings = new Dictionary<string, string?> { ["HomeSetPath"] = null }
		})).StatusCode);

		// DELETE clears the whole role: the config file (which assigns carddav here) takes over.
		HttpResponseMessage cleared = await SendAsync("DELETE", "/admin/api/backends/Contacts");
		Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
		contacts = await RoleAsync("Contacts");
		Assert.Equal("carddav", contacts.GetProperty("provider").GetString());
		Assert.Equal("config", contacts.GetProperty("providerSource").GetString());
		Assert.Equal("config", Source(contacts, "BaseUrl"));
	}

	[Fact]
	public async Task Save_ElidesAValueThatMatchesTheLayerBelow()
	{
		// Storing what the config file already says would be an override that overrides nothing;
		// re-saving the configured Host must leave no database row behind.
		JsonElement before = await RoleAsync("MailStore");
		string host = Setting(before, "Host")!;
		Assert.Equal(HttpStatusCode.OK, (await SendAsync("PUT", "/admin/api/backends/MailStore", new
		{
			settings = new Dictionary<string, string?> { ["Host"] = host }
		})).StatusCode);

		JsonElement after = await RoleAsync("MailStore");
		Assert.Equal(host, Setting(after, "Host"));
		Assert.Equal("config", Source(after, "Host")); // still the config file, no db row

		// A real deviation IS stored, and setting it back to the configured value removes it again.
		Assert.Equal(HttpStatusCode.OK, (await SendAsync("PUT", "/admin/api/backends/MailStore", new
		{
			settings = new Dictionary<string, string?> { ["Host"] = "elsewhere.example.com" }
		})).StatusCode);
		Assert.Equal("db", Source(await RoleAsync("MailStore"), "Host"));

		Assert.Equal(HttpStatusCode.OK, (await SendAsync("PUT", "/admin/api/backends/MailStore", new
		{
			settings = new Dictionary<string, string?> { ["Host"] = host }
		})).StatusCode);
		Assert.Equal("config", Source(await RoleAsync("MailStore"), "Host"));
	}

	[Fact]
	public async Task Save_RejectsWhatTheProviderWouldReject()
	{
		HttpResponseMessage badPort = await SendAsync("PUT", "/admin/api/backends/MailStore", new
		{
			settings = new Dictionary<string, string?> { ["Port"] = "99999" }
		});
		Assert.Equal(HttpStatusCode.BadRequest, badPort.StatusCode);

		HttpResponseMessage badEnum = await SendAsync("PUT", "/admin/api/backends/MailStore", new
		{
			settings = new Dictionary<string, string?> { ["Security"] = "Quantum" }
		});
		Assert.Equal(HttpStatusCode.BadRequest, badEnum.StatusCode);

		HttpResponseMessage unknownProvider = await SendAsync("PUT", "/admin/api/backends/Calendar", new
		{
			provider = "nope"
		});
		Assert.Equal(HttpStatusCode.BadRequest, unknownProvider.StatusCode);

		HttpResponseMessage wrongRole = await SendAsync("PUT", "/admin/api/backends/Contacts", new
		{
			provider = "imap" // imap serves MailStore only
		});
		Assert.Equal(HttpStatusCode.BadRequest, wrongRole.StatusCode);

		// The rejected values were not stored.
		JsonElement mailStore = await RoleAsync("MailStore");
		Assert.NotEqual("99999", Setting(mailStore, "Port"));
	}

	[Fact]
	public async Task Validate_ReportsFailuresPerField_WithoutStoringAnything()
	{
		HttpResponseMessage response = await SendAsync("POST", "/admin/api/backends/Calendar/validate", new
		{
			provider = "caldav",
			settings = new Dictionary<string, string?> { ["BaseUrl"] = "not-a-url" }
		});
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);

		JsonElement failures = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("failures");
		Assert.Contains(failures.EnumerateArray(), f => f.GetProperty("field").GetString() == "BaseUrl");

		// A dry run stores nothing.
		Assert.NotEqual("not-a-url", Setting(await RoleAsync("Calendar"), "BaseUrl"));

		// Valid input comes back clean.
		HttpResponseMessage ok = await SendAsync("POST", "/admin/api/backends/Calendar/validate", new
		{
			provider = "caldav",
			settings = new Dictionary<string, string?> { ["BaseUrl"] = "https://dav.example.com" }
		});
		Assert.Empty((await ok.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("failures").EnumerateArray());
	}

	[Fact]
	public async Task Test_ProbesReachability_AndSaysWhenItCannot()
	{
		// A provider without IReadinessSource cannot be probed at all.
		JsonElement unsupported = await (await SendAsync("POST", "/admin/api/backends/Notes/test", new
		{
			provider = "local"
		})).Content.ReadFromJsonAsync<JsonElement>();
		Assert.False(unsupported.GetProperty("supported").GetBoolean());

		// A listening socket answers; the port nobody is on does not.
		using TcpListener listener = new(System.Net.IPAddress.Loopback, 0);
		listener.Start();
		int openPort = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

		JsonElement reachable = await (await SendAsync("POST", "/admin/api/backends/MailStore/test", new
		{
			provider = "imap",
			settings = new Dictionary<string, string?>
			{
				["Host"] = "127.0.0.1", ["Port"] = openPort.ToString(), ["Security"] = "None", ["UseSsl"] = "false"
			}
		})).Content.ReadFromJsonAsync<JsonElement>();
		Assert.True(reachable.GetProperty("supported").GetBoolean());
		Assert.True(reachable.GetProperty("reachable").GetBoolean());
		listener.Stop();

		JsonElement dead = await (await SendAsync("POST", "/admin/api/backends/MailStore/test", new
		{
			provider = "imap",
			settings = new Dictionary<string, string?>
			{
				["Host"] = "127.0.0.1", ["Port"] = openPort.ToString(), ["Security"] = "None", ["UseSsl"] = "false"
			}
		})).Content.ReadFromJsonAsync<JsonElement>();
		Assert.False(dead.GetProperty("reachable").GetBoolean());
	}

	[Fact]
	public async Task StoredSecretsAreNeverEchoed()
	{
		// A Password leaf under a role is masked wherever it surfaces, and sending the mask
		// back is "unchanged" — it must never be stored as the literal value.
		Assert.Equal(HttpStatusCode.OK, (await SendAsync("PUT", "/admin/api/backends/Oof", new
		{
			settings = new Dictionary<string, string?> { ["Password"] = "sieve-secret-pw" }
		})).StatusCode);

		string everything = await _client.GetStringAsync("/admin/api/backends") +
		                    await _client.GetStringAsync("/admin/api/backends/providers");
		Assert.DoesNotContain("sieve-secret-pw", everything);
		Assert.DoesNotContain("pbkdf2$", everything);
		Assert.DoesNotContain("enc:v1:", everything);
		Assert.Equal("***", Setting(await RoleAsync("Oof"), "Password"));

		Assert.Equal(HttpStatusCode.OK, (await SendAsync("PUT", "/admin/api/backends/Oof", new
		{
			settings = new Dictionary<string, string?> { ["Password"] = "***" }
		})).StatusCode);
		Assert.Equal("***", Setting(await RoleAsync("Oof"), "Password"));
	}

	[Fact]
	public async Task EndpointsRequireAnAdminSessionAndTheCsrfHeader()
	{
		using HttpClient anonymous = _factory.CreateDefaultClient(new CookieContainerHandler());
		Assert.Equal(HttpStatusCode.Unauthorized,
			(await anonymous.GetAsync("/admin/api/backends")).StatusCode);

		// Signed in, but without the CSRF header a write is refused.
		using HttpRequestMessage noHeader = new(HttpMethod.Put, "/admin/api/backends/Calendar")
		{
			Content = JsonContent.Create(new { provider = "caldav" })
		};
		HttpResponseMessage refused = await _client.SendAsync(noHeader);
		Assert.NotEqual(HttpStatusCode.OK, refused.StatusCode);
	}
}

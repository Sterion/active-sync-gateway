using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ActiveSync.Core.Security;
using ActiveSync.Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;

namespace ActiveSync.Integration.Tests.Scenarios;

/// <summary>
///   The user portal's self-service API: identity comes from the session only (user
///   isolation), the edit surface is limited to the own password + role credentials/settings
///   (Provider/Enabled are admin-only and survive edits untouched), and a password change
///   requires re-verifying the current one.
/// </summary>
[Collection("gateway")]
[Trait("Category", "Integration")]
public sealed class WebUiPortalTests(GatewayFixture gateway) : IAsyncLifetime
{
	private const string UserA = "portala";
	private const string UserB = "portalb";
	private const string Password = "portal-pa55!";

	private WebApplicationFactory<Program> _factory = null!;

	public Task InitializeAsync()
	{
		_factory = gateway.CreateIsolatedFactory(new Dictionary<string, string?>
		{
			["ActiveSync:WebUi:UserPortal:Enabled"] = "true",
			[$"ActiveSync:Users:{UserA}:Password"] = GatewayPasswordHasher.Hash(Password),
			// A pre-existing admin-shaped override: the portal must never be able to change
			// Enabled/Provider, only credentials/settings.
			[$"ActiveSync:Users:{UserA}:Backends:Contacts:Enabled"] = "false",
			[$"ActiveSync:Users:{UserB}:Password"] = GatewayPasswordHasher.Hash(Password)
		});
		return Task.CompletedTask;
	}

	public async Task DisposeAsync()
	{
		await _factory.DisposeAsync();
	}

	private async Task<HttpClient> LoginAsync(string user, string password = Password)
	{
		HttpClient client = _factory.CreateDefaultClient(new CookieContainerHandler());
		HttpResponseMessage response = await SendAsync(client, "POST", "/user/api/login",
			new { username = user, password });
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		return client;
	}

	private static async Task<HttpResponseMessage> SendAsync(
		HttpClient client, string method, string path, object? body = null)
	{
		using HttpRequestMessage request = new(HttpMethod.Parse(method), path);
		request.Headers.Add("X-EAS-WebUi", "1");
		if (body is not null)
			request.Content = JsonContent.Create(body);
		return await client.SendAsync(request);
	}

	[Fact]
	public async Task SelfService_IsIsolated_AndPreservesAdminOnlyFields()
	{
		HttpClient clientA = await LoginAsync(UserA);
		HttpClient clientB = await LoginAsync(UserB);

		// Identity comes from the session: A sees A, B sees B — there is no user parameter.
		JsonElement meA = await clientA.GetFromJsonAsync<JsonElement>("/user/api/me");
		Assert.Equal(UserA, meA.GetProperty("login").GetString());

		// The admin API does not exist for a portal session.
		Assert.Equal(HttpStatusCode.Forbidden, (await clientA.GetAsync("/admin/api/users")).StatusCode);

		// A customizes MailStore (a "provider" field in the body is simply not part of the
		// contract and must be ignored).
		HttpResponseMessage update = await SendAsync(clientA, "PUT", "/user/api/backends/MailStore", new
		{
			userName = "real-imap-a",
			password = "imap-secret-a",
			provider = "jmap",
			enabled = false
		});
		Assert.Equal(HttpStatusCode.OK, update.StatusCode);

		// A connection setting is NOT part of the self-service surface (C1): the imap
		// provider marks no field SelfServiceEditable, so every settings key is refused.
		HttpResponseMessage repoint = await SendAsync(clientA, "PUT", "/user/api/backends/MailStore", new
		{
			settings = new Dictionary<string, string> { ["Host"] = "attacker.example.net" }
		});
		Assert.Equal(HttpStatusCode.BadRequest, repoint.StatusCode);

		meA = await clientA.GetFromJsonAsync<JsonElement>("/user/api/me");
		JsonElement mailStore = meA.GetProperty("backends").GetProperty("MailStore");
		Assert.Equal("real-imap-a", mailStore.GetProperty("userName").GetString());
		Assert.True(mailStore.GetProperty("passwordSet").GetBoolean());
		Assert.Equal(JsonValueKind.Null, mailStore.GetProperty("provider").ValueKind);

		// The pre-existing admin-set Enabled=false on Contacts survives a portal edit of it.
		Assert.Equal(HttpStatusCode.OK, (await SendAsync(clientA, "PUT", "/user/api/backends/Contacts",
			new { userName = "contacts-user" })).StatusCode);
		meA = await clientA.GetFromJsonAsync<JsonElement>("/user/api/me");
		JsonElement contacts = meA.GetProperty("backends").GetProperty("Contacts");
		Assert.False(contacts.GetProperty("enabled").GetBoolean());
		Assert.Equal("contacts-user", contacts.GetProperty("userName").GetString());

		// No secret ever leaves the server.
		string raw = await clientA.GetStringAsync("/user/api/me");
		Assert.DoesNotContain("imap-secret-a", raw);
		Assert.DoesNotContain("enc:v1:", raw);
		Assert.DoesNotContain("pbkdf2$", raw);

		// B is untouched by all of it.
		JsonElement meB = await clientB.GetFromJsonAsync<JsonElement>("/user/api/me");
		Assert.Equal(UserB, meB.GetProperty("login").GetString());
		Assert.Equal(JsonValueKind.Null, meB.GetProperty("backends").ValueKind);

		// Nonsense role names are rejected.
		Assert.Equal(HttpStatusCode.BadRequest,
			(await SendAsync(clientA, "PUT", "/user/api/backends/Nope", new { userName = "x" })).StatusCode);
	}

	[Fact]
	public async Task PasswordChange_RequiresCurrent_AndApplies()
	{
		HttpClient client = await LoginAsync(UserB);

		// The wrong current password is refused.
		HttpResponseMessage wrong = await SendAsync(client, "PUT", "/user/api/password",
			new { current = "not-it", @new = "new-pa55!" });
		Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);

		// The right one changes it...
		Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, "PUT", "/user/api/password",
			new { current = Password, @new = "new-pa55!" })).StatusCode);

		// ...the old password stops working and the new one signs in.
		HttpClient fresh = _factory.CreateDefaultClient(new CookieContainerHandler());
		Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(fresh, "POST", "/user/api/login",
			new { username = UserB, password = Password })).StatusCode);
		Assert.Equal(HttpStatusCode.OK, (await SendAsync(fresh, "POST", "/user/api/login",
			new { username = UserB, password = "new-pa55!" })).StatusCode);
	}

	[Fact]
	public async Task BackendsMeta_NamesTheServingProviderAndItsFields()
	{
		using HttpClient client = await LoginAsync(UserA);
		JsonElement meta = await client.GetFromJsonAsync<JsonElement>("/user/api/backends/meta");

		// The fixture serves mail over imap. The provider is named so the portal can label the
		// credential fields, but the FIELD list is only what this caller may write for
		// themselves (C1) — imap opts nothing in, so it is empty. Host/Port are connection
		// settings and live in the admin editor.
		JsonElement mailStore = meta.GetProperty("MailStore");
		Assert.Equal("imap", mailStore.GetProperty("provider").GetString());
		string[] names = [.. mailStore.GetProperty("fields").EnumerateArray()
			.Select(f => f.GetProperty("name").GetString()!)];
		Assert.DoesNotContain("Host", names);
		Assert.DoesNotContain("Port", names);

		// Descriptions only — no configured value, and nothing secret-shaped, is in there.
		string body = await client.GetStringAsync("/user/api/backends/meta");
		Assert.DoesNotContain("pbkdf2$", body);
		Assert.DoesNotContain("enc:v1:", body);
	}

	[Fact]
	public async Task Saving_RefusesAdministeredSettings_AndLeavesThemAlone()
	{
		// INVERTED BY C1. This used to assert that the portal carried undescribed keys through
		// its own save. It no longer accepts them at all: a connection key is refused with 400,
		// and keys an administrator set are preserved by the server instead of being echoed
		// back by the client. Same guarantee ("a portal save does not delete them"), moved to
		// the side of the wire that can be trusted with it.
		using HttpClient client = await LoginAsync(UserA);
		HttpResponseMessage refused = await SendAsync(client, "PUT", "/user/api/backends/Contacts", new
		{
			settings = new Dictionary<string, string?>
			{
				["BaseUrl"] = "https://dav.example.com", ["PluginOnlyKey"] = "keep-me"
			}
		});
		Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
		JsonElement error = await refused.Content.ReadFromJsonAsync<JsonElement>();
		string[] fields = [.. error.GetProperty("failures").EnumerateArray()
			.Select(f => f.GetProperty("field").GetString()!)];
		Assert.Equal(["BaseUrl", "PluginOnlyKey"], fields);

		// A credential-only save still works and does not disturb the role.
		Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, "PUT", "/user/api/backends/Contacts",
			new { userName = "contacts-a" })).StatusCode);
		JsonElement me = await client.GetFromJsonAsync<JsonElement>("/user/api/me");
		JsonElement contacts = me.GetProperty("backends").GetProperty("Contacts");
		Assert.Equal("contacts-a", contacts.GetProperty("userName").GetString());
		Assert.False(contacts.GetProperty("enabled").GetBoolean());
	}
}

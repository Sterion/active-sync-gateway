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
			settings = new Dictionary<string, string> { ["Port"] = "993" },
			provider = "jmap",
			enabled = false
		});
		Assert.Equal(HttpStatusCode.OK, update.StatusCode);

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
}

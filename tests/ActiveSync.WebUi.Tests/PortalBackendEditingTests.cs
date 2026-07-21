using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ActiveSync.Core.Accounts;
using ActiveSync.Core.Options;

namespace ActiveSync.WebUi.Tests;

/// <summary>
///   C1 — what a NON-ADMIN portal user may change about their own backend connection.
///   <c>PUT /user/api/backends/{role}</c> withholds Enabled and Provider as admin surface but
///   used to take the settings dictionary wholesale, and a user key replaces the whole global
///   subtree it addresses. Setting BaseUrl therefore repointed the caller's own CalDAV role at
///   any host they liked; the gateway then connects there and presents the role's stored
///   credential, unsealed for exactly that purpose. Credential exfiltration plus an
///   authenticated SSRF pivot, from the lowest privilege level in the system.
/// </summary>
public sealed class PortalBackendEditingTests
{
	private static readonly Dictionary<string, string?> CalDavRole = new()
	{
		["ActiveSync:Backends:Calendar:Provider"] = "caldav",
		["ActiveSync:Backends:Calendar:BaseUrl"] = "https://dav.example.com",
		["ActiveSync:Backends:Tasks:Provider"] = "caldav",
		["ActiveSync:Backends:Tasks:BaseUrl"] = "https://dav.example.com"
	};

	private static Dictionary<string, AccountOptions> OneUser()
	{
		return WebUiHost.Users(("bob", new AccountOptions { MailAddress = "bob@example.com" }));
	}

	[Fact]
	public async Task PortalUser_CannotRepointTheBackendAtAnotherHost()
	{
		await using WebUiHost host = await WebUiHost.StartAsync(OneUser(), CalDavRole);
		using HttpClient client = await host.SignInAsync("bob", admin: false);

		HttpResponseMessage response = await client.PutAsJsonAsync("/user/api/backends/Calendar", new
		{
			settings = new Dictionary<string, string?> { ["BaseUrl"] = "https://attacker.example.net" }
		});

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		JsonElement body = await host.ReadJsonAsync(response);
		Assert.Contains("BaseUrl", body.GetProperty("error").GetString(), StringComparison.Ordinal);

		// And nothing was stored: the next read still shows no override for the role.
		AccountStore store = new(host.Factory);
		AccountOptions? stored = await store.GetAsync("bob", CancellationToken.None);
		Assert.Null(stored?.Backends?.GetValueOrDefault("Calendar")?.Settings);
	}

	[Fact]
	public async Task PortalUser_CannotDowngradeCertificateValidation()
	{
		// The same class of key one field over: AllowInvalidCertificates would strip TLS
		// verification from the connection that carries the stored credential.
		await using WebUiHost host = await WebUiHost.StartAsync(OneUser(), CalDavRole);
		using HttpClient client = await host.SignInAsync("bob", admin: false);

		HttpResponseMessage response = await client.PutAsJsonAsync("/user/api/backends/Calendar", new
		{
			settings = new Dictionary<string, string?> { ["AllowInvalidCertificates"] = "true" }
		});

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task PortalUser_CannotSetAnUndescribedKey()
	{
		// A key no field claims is not "harmless" — the provider may still read it, and
		// BackendConfigValidation deliberately passes unclaimed values through untouched.
		await using WebUiHost host = await WebUiHost.StartAsync(OneUser(), CalDavRole);
		using HttpClient client = await host.SignInAsync("bob", admin: false);

		HttpResponseMessage response = await client.PutAsJsonAsync("/user/api/backends/Calendar", new
		{
			settings = new Dictionary<string, string?> { ["Whatever"] = "x" }
		});

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task PortalUser_CanStillEditTheirOwnPreferences()
	{
		// The portal is not read-only: fields a provider marks self-service are still writable,
		// and the credential fields the portal exists for are untouched by this.
		await using WebUiHost host = await WebUiHost.StartAsync(OneUser(), CalDavRole);
		using HttpClient client = await host.SignInAsync("bob", admin: false);

		HttpResponseMessage response = await client.PutAsJsonAsync("/user/api/backends/Calendar", new
		{
			userName = "bob.dav",
			settings = new Dictionary<string, string?> { ["CalendarAttachments"] = "Off" }
		});

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		AccountStore store = new(host.Factory);
		AccountOptions? stored = await store.GetAsync("bob", CancellationToken.None);
		BackendRoleOverride role = stored!.Backends!["Calendar"];
		Assert.Equal("bob.dav", role.UserName);
		Assert.Equal("Off", role.Settings!["CalendarAttachments"]);
	}

	[Fact]
	public async Task PortalUser_DoesNotWipeAdminSetSettingsTheyCannotEdit()
	{
		// The endpoint replaces the settings dictionary wholesale. Once the connection keys are
		// no longer accepted from the portal, a portal save must PRESERVE the ones an admin put
		// on the account rather than dropping them.
		await using WebUiHost host = await WebUiHost.StartAsync(OneUser(), CalDavRole);
		AccountStore store = new(host.Factory);
		await store.UpsertAsync("bob", new AccountOptions
		{
			MailAddress = "bob@example.com",
			Backends = new Dictionary<string, BackendRoleOverride>(StringComparer.OrdinalIgnoreCase)
			{
				["Calendar"] = new BackendRoleOverride
				{
					Settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
					{
						["HomeSetPath"] = "/dav/bob/"
					}
				}
			}
		}, CancellationToken.None);

		using HttpClient client = await host.SignInAsync("bob", admin: false);
		HttpResponseMessage response = await client.PutAsJsonAsync("/user/api/backends/Calendar", new
		{
			settings = new Dictionary<string, string?> { ["CalendarAttachments"] = "Off" }
		});
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);

		AccountOptions? stored = await store.GetAsync("bob", CancellationToken.None);
		Dictionary<string, string?> settings = stored!.Backends!["Calendar"].Settings!;
		Assert.Equal("/dav/bob/", settings["HomeSetPath"]);
		Assert.Equal("Off", settings["CalendarAttachments"]);
	}

	[Fact]
	public async Task BackendsMeta_OnlyDescribesFieldsThePortalMayWrite()
	{
		// The portal renders its form from this, so an unwritable field showing up here is a
		// form that fails on save.
		await using WebUiHost host = await WebUiHost.StartAsync(OneUser(), CalDavRole);
		using HttpClient client = await host.SignInAsync("bob", admin: false);

		JsonElement meta = await host.ReadJsonAsync(await client.GetAsync("/user/api/backends/meta"));
		string[] names = [.. meta.GetProperty("Calendar").GetProperty("fields").EnumerateArray()
			.Select(field => field.GetProperty("name").GetString()!)];

		Assert.DoesNotContain("BaseUrl", names);
		Assert.DoesNotContain("AllowInvalidCertificates", names);
		Assert.Contains("CalendarAttachments", names);
	}

	[Fact]
	public async Task AdminEditor_StillSeesEveryField()
	{
		// The restriction is on the PORTAL, not on the schema: the admin backends editor must
		// keep rendering the connection fields.
		await using WebUiHost host = await WebUiHost.StartAsync(
			WebUiHost.Users(("alice", new AccountOptions { Admin = true })), CalDavRole);
		using HttpClient client = await host.SignInAsync("alice", admin: true);

		JsonElement providers = await host.ReadJsonAsync(await client.GetAsync("/admin/api/backends/providers"));
		JsonElement caldav = providers.EnumerateArray().First(p => p.GetProperty("name").GetString() == "caldav");
		string[] names = [.. caldav.GetProperty("schemas").GetProperty("Calendar").EnumerateArray()
			.Select(field => field.GetProperty("name").GetString()!)];

		Assert.Contains("BaseUrl", names);
		Assert.Contains("AllowInvalidCertificates", names);
	}
}

using System.Net.Http.Json;
using System.Text.Json;
using ActiveSync.Core.Accounts;
using ActiveSync.Core.Options;

namespace ActiveSync.WebUi.Tests;

/// <summary>
///   C5 (round 2) — <c>GET /user/api/me</c> echoed the admin-set backend <c>userName</c> verbatim
///   for EVERY role, including one the provider keeps fully administered: carddav's Contacts role
///   describes only BaseUrl/HomeSetPath plus the shared network knobs, none of them
///   <c>SelfServiceEditable</c> (unlike caldav's Calendar role, which opts CalendarAttachments /
///   SendInvitations / SharedCollections in). A shared/service-account login an admin bound to
///   Contacts is therefore disclosed to a non-admin portal caller who has no self-service surface
///   for that role at all — credential-adjacent topology, not the credential itself, but exactly
///   the class of leak <c>EndpointHelpers.MaskSecretSettings</c> already guards for the settings
///   dictionary next to it.
/// </summary>
public sealed class PortalMeUserNameDisclosureTests
{
	private static Dictionary<string, string?> ContactsRole => new()
	{
		["ActiveSync:Backends:Contacts:Provider"] = "carddav",
		["ActiveSync:Backends:Contacts:BaseUrl"] = "https://dav.example.com"
	};

	[Fact]
	public async Task Me_DoesNotEchoUserName_ForARoleWithNoSelfServiceFields()
	{
		await using WebUiHost host = await WebUiHost.StartAsync(
			WebUiHost.Users(("bob", new AccountOptions { MailAddress = "bob@example.com" })), ContactsRole);

		// The admin bound a shared service-account login to Contacts — bob never set this himself
		// and (carddav offering no SelfServiceEditable field for the role) never could.
		AccountStore store = new(host.Factory);
		await store.UpsertAsync("bob", new AccountOptions
		{
			MailAddress = "bob@example.com",
			Backends = new Dictionary<string, BackendRoleOverride>(StringComparer.OrdinalIgnoreCase)
			{
				["Contacts"] = new BackendRoleOverride { UserName = "svc-shared-contacts@internal.example.com" }
			}
		}, CancellationToken.None);

		using HttpClient client = await host.SignInAsync("bob", admin: false);
		HttpResponseMessage response = await client.GetAsync("/user/api/me");

		string raw = await response.Content.ReadAsStringAsync();
		Assert.DoesNotContain("svc-shared-contacts", raw, StringComparison.Ordinal);

		JsonElement contacts = (await host.ReadJsonAsync(response)).GetProperty("backends").GetProperty("Contacts");
		Assert.Equal(JsonValueKind.Null, contacts.GetProperty("userName").ValueKind);
	}

	[Fact]
	public async Task Me_StillEchoesUserName_ForARoleWithSelfServiceFields()
	{
		// The gate must not blank EVERY role's userName — only ones the caller has no self-service
		// surface for at all. Calendar (caldav) opts several fields in, so it stays visible: it is
		// exactly the kind of credential the portal exists to let a user see and change.
		Dictionary<string, string?> settings = new()
		{
			["ActiveSync:Backends:Calendar:Provider"] = "caldav",
			["ActiveSync:Backends:Calendar:BaseUrl"] = "https://dav.example.com"
		};
		await using WebUiHost host = await WebUiHost.StartAsync(
			WebUiHost.Users(("bob", new AccountOptions { MailAddress = "bob@example.com" })), settings);

		AccountStore store = new(host.Factory);
		await store.UpsertAsync("bob", new AccountOptions
		{
			MailAddress = "bob@example.com",
			Backends = new Dictionary<string, BackendRoleOverride>(StringComparer.OrdinalIgnoreCase)
			{
				["Calendar"] = new BackendRoleOverride { UserName = "bob.dav" }
			}
		}, CancellationToken.None);

		using HttpClient client = await host.SignInAsync("bob", admin: false);
		HttpResponseMessage response = await client.GetAsync("/user/api/me");

		JsonElement calendar = (await host.ReadJsonAsync(response)).GetProperty("backends").GetProperty("Calendar");
		Assert.Equal("bob.dav", calendar.GetProperty("userName").GetString());
	}
}

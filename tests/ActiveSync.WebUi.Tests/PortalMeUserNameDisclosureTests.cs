using System.Net.Http.Json;
using System.Text.Json;
using ActiveSync.Core.Accounts;
using ActiveSync.Core.Options;

namespace ActiveSync.WebUi.Tests;

/// <summary>
///   Re-evaluated as a follow-up. The original fix withheld
///   <c>GET /user/api/me</c>'s <c>userName</c> for a role whose PROVIDER opts no field into
///   <c>SelfServiceEditable</c> (the SETTINGS surface — connection knobs like Host/BaseUrl). But
///   that gate governs the settings dictionary only; backend CREDENTIALS (userName/password) are a
///   separate, unconditional self-service surface per this handler's own header comment and
///   docs/webui.md — <c>PUT /user/api/backends/{role}</c> lets a caller set <c>userName</c> for ANY
///   role with no <c>SelfServiceEditable</c> check at all (only the <c>settings</c> dictionary is
///   gated there). Withholding the read for a value the write side never restricted broke the
///   round trip: a caller who set their own <c>Contacts</c> userName could no longer read it back
///   (see <c>WebUiPortalTests.Saving_RefusesAdministeredSettings_AndLeavesThemAlone</c> /
///   <c>SelfService_IsIsolated_AndPreservesAdminOnlyFields</c> in the integration suite, which
///   caught this live). <c>userName</c> is now echoed unconditionally again, like every other field
///   on the DTO — consistent with the PUT side that was never gated in the first place.
/// </summary>
public sealed class PortalMeUserNameDisclosureTests
{
	private static Dictionary<string, string?> ContactsRole => new()
	{
		["ActiveSync:Backends:Contacts:Provider"] = "carddav",
		["ActiveSync:Backends:Contacts:BaseUrl"] = "https://dav.example.com"
	};

	[Fact]
	public async Task Me_EchoesUserName_ForARoleWithNoSelfServiceSettingsFields()
	{
		await using WebUiHost host = await WebUiHost.StartAsync(
			WebUiHost.Users(("bob", new UserOptions { MailAddress = "bob@example.com" })), ContactsRole);

		// carddav's Contacts role opts no field into the SETTINGS self-service surface, but bob can
		// still PUT his own Contacts userName unconditionally (the credential surface is separate
		// and always available) — so GET must echo back whatever is currently stored, admin-set or
		// self-set, exactly as the PUT handler lets him overwrite it either way.
		UserStore store = new(host.Factory);
		await store.UpsertAsync("bob", new UserOptions
		{
			MailAddress = "bob@example.com",
			Backends = new Dictionary<string, BackendRoleOverride>(StringComparer.OrdinalIgnoreCase)
			{
				["Contacts"] = new BackendRoleOverride { UserName = "svc-shared-contacts@internal.example.com" }
			}
		}, CancellationToken.None);

		using HttpClient client = await host.SignInAsync("bob", admin: false);
		HttpResponseMessage response = await client.GetAsync("/user/api/me");

		JsonElement contacts = (await host.ReadJsonAsync(response)).GetProperty("backends").GetProperty("Contacts");
		Assert.Equal("svc-shared-contacts@internal.example.com", contacts.GetProperty("userName").GetString());
	}

	[Fact]
	public async Task Me_StillEchoesUserName_ForARoleWithSelfServiceFields()
	{
		// A role whose provider DOES opt settings fields in behaves the same way — userName is
		// visible regardless, since the settings-surface gate never applied to credentials at all.
		Dictionary<string, string?> settings = new()
		{
			["ActiveSync:Backends:Calendar:Provider"] = "caldav",
			["ActiveSync:Backends:Calendar:BaseUrl"] = "https://dav.example.com"
		};
		await using WebUiHost host = await WebUiHost.StartAsync(
			WebUiHost.Users(("bob", new UserOptions { MailAddress = "bob@example.com" })), settings);

		UserStore store = new(host.Factory);
		await store.UpsertAsync("bob", new UserOptions
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

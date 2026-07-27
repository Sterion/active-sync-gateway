using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ActiveSync.Core.Accounts;
using ActiveSync.Core.Options;
using ActiveSync.Core.State;

namespace ActiveSync.WebUi.Tests;

/// <summary>
///   Item 6b of docs/design/db-restructure.md, admin-API half. The design is explicit that the
///   guards must hold on BOTH surfaces — "guarding only the CLI leaves the API able to write a
///   shape the schema no longer has" — so the rename immutability guard, the collision check and
///   the confirm-and-cascade delete are asserted here as well as in the CLI/store tests.
/// </summary>
public sealed class UserLifecycleApiTests
{
	private static Dictionary<string, UserOptions> Admin() =>
		WebUiHost.Users(("alice", new UserOptions { Admin = true }));

	/// <summary>A database-declared user with a device and some irreplaceable local content.</summary>
	private static async Task<int> SeedUserAsync(WebUiHost host, string login, int contacts)
	{
		UserStore store = new(host.Factory);
		await store.UpsertAsync(login, new UserOptions { MailAddress = $"{login}@example.com" },
			CancellationToken.None);
		int userId = (await store.FindUserIdAsync(login, CancellationToken.None))!.Value;

		await using SyncDbContext db = host.Factory.CreateDbContext();
#pragma warning disable VSTHRD103
		db.Devices.Add(new Device
		{
			UserId = userId, DeviceId = "PHONE1", DeviceType = "Test",
			CreatedUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow,
		});
		for (int i = 0; i < contacts; i++)
			db.LocalItems.Add(new LocalItem
			{
				UserId = userId, Collection = "contacts", Uid = $"c-{i}",
				Content = "BEGIN:VCARD\r\nEND:VCARD", Version = 1, LastModifiedUtc = DateTime.UtcNow,
			});
#pragma warning restore VSTHRD103
		await db.SaveChangesAsync(CancellationToken.None);
		return userId;
	}

	// ---- full-replacement update ----

	[Fact]
	public async Task Update_PreservesTheFieldsTheAdminScreenCannotSee()
	{
		// The PUT replaces the row wholesale from a DTO that does not model every column, so an
		// edit to something unrelated (here: the mail address) used to silently destroy the rest —
		// a stored DefaultBackendPassword, breaking every backend for that user; the OIDC subject
		// binding that stops the login being claimed by someone else; the auto-provisioned marker.
		await using WebUiHost host = await WebUiHost.StartAsync(Admin());
		UserStore store = new(host.Factory);
		await store.UpsertAsync("erin", new UserOptions
		{
			Password = "phone-pw",                       // required alongside the backend secret
			MailAddress = "erin@example.com",
			DefaultBackendLogin = "backend-erin",
			DefaultBackendPassword = "backend-secret",
			OidcSubject = "idp-subject-123",
			AutoProvisioned = true,
		}, CancellationToken.None);

		using HttpClient client = await host.SignInAsync("alice", admin: true);
		HttpResponseMessage response = await client.PutAsJsonAsync(
			"/admin/api/users/erin", new { mailAddress = "erin.new@example.com" });
		Assert.True(response.IsSuccessStatusCode, $"update failed: {response.StatusCode}");

		UserOptions? row = await store.GetAsync("erin", CancellationToken.None);
		Assert.NotNull(row);
		Assert.Equal("erin.new@example.com", row!.MailAddress);   // the edit landed
		Assert.Equal("backend-erin", row.DefaultBackendLogin);    // ...and nothing else was lost
		Assert.Equal("backend-secret", row.DefaultBackendPassword);
		Assert.Equal("idp-subject-123", row.OidcSubject);
		Assert.True(row.AutoProvisioned);
	}

	// ---- rename ----

	[Fact]
	public async Task Rename_KeepsTheIdentity_SoStateSurvives()
	{
		await using WebUiHost host = await WebUiHost.StartAsync(Admin());
		int userId = await SeedUserAsync(host, "dana", contacts: 0);
		using HttpClient client = await host.SignInAsync("alice", admin: true);

		HttpResponseMessage response = await client.PostAsJsonAsync(
			"/admin/api/users/dana/rename", new { newLogin = "dana.new" });

		Assert.True(response.IsSuccessStatusCode, $"rename failed: {response.StatusCode}");
		UserStore store = new(host.Factory);
		Assert.Null(await store.FindUserIdAsync("dana", CancellationToken.None));
		Assert.Equal(userId, await store.FindUserIdAsync("dana.new", CancellationToken.None));
		await using SyncDbContext db = host.Factory.CreateDbContext();
		Assert.Equal(1, await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
			.CountAsync(db.Devices, d => d.UserId == userId, CancellationToken.None));
	}

	[Fact]
	public async Task Rename_OfAConfigDeclaredUser_IsRefused_PointingAtConfiguration()
	{
		// The guard that makes matching config↔database BY LOGIN safe: the only mutable side is
		// the one configuration does not own.
		await using WebUiHost host = await WebUiHost.StartAsync(WebUiHost.Users(
			("alice", new UserOptions { Admin = true }),
			("configured", new UserOptions { MailAddress = "c@example.com" })));
		using HttpClient client = await host.SignInAsync("alice", admin: true);

		HttpResponseMessage response = await client.PostAsJsonAsync(
			"/admin/api/users/configured/rename", new { newLogin = "renamed" });

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		string body = await response.Content.ReadAsStringAsync();
		Assert.Contains("declared in configuration", body);
		Assert.Contains("ActiveSync:Users", body);
	}

	[Fact]
	public async Task Rename_OntoATakenLogin_IsRefused()
	{
		await using WebUiHost host = await WebUiHost.StartAsync(Admin());
		await SeedUserAsync(host, "dana", contacts: 0);
		await SeedUserAsync(host, "erin", contacts: 0);
		using HttpClient client = await host.SignInAsync("alice", admin: true);

		HttpResponseMessage response = await client.PostAsJsonAsync(
			"/admin/api/users/dana/rename", new { newLogin = "erin" });

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Contains("already taken", await response.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task Rename_OntoAConfigDeclaredLogin_IsRefused()
	{
		// Renaming onto a config login would let configuration start shadowing this user the
		// moment it is next read.
		await using WebUiHost host = await WebUiHost.StartAsync(WebUiHost.Users(
			("alice", new UserOptions { Admin = true }),
			("configured", new UserOptions())));
		await SeedUserAsync(host, "dana", contacts: 0);
		using HttpClient client = await host.SignInAsync("alice", admin: true);

		HttpResponseMessage response = await client.PostAsJsonAsync(
			"/admin/api/users/dana/rename", new { newLogin = "configured" });

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Contains("already declared in configuration", await response.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task Rename_RefusesAMalformedLogin()
	{
		await using WebUiHost host = await WebUiHost.StartAsync(Admin());
		await SeedUserAsync(host, "dana", contacts: 0);
		using HttpClient client = await host.SignInAsync("alice", admin: true);

		HttpResponseMessage response = await client.PostAsJsonAsync(
			"/admin/api/users/dana/rename", new { newLogin = "bad:login" });

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	// ---- delete ----

	[Fact]
	public async Task DeletionImpact_ReportsWhatWouldBeLost_WithoutDeletingAnything()
	{
		await using WebUiHost host = await WebUiHost.StartAsync(Admin());
		await SeedUserAsync(host, "dana", contacts: 4);
		using HttpClient client = await host.SignInAsync("alice", admin: true);

		JsonElement impact = await host.ReadJsonAsync(
			await client.GetAsync("/admin/api/users/dana/deletion-impact"));

		Assert.True(impact.GetProperty("destroysContent").GetBoolean());
		Assert.Contains("4 contacts", impact.GetProperty("summary").GetString());
		// Nothing was deleted by asking.
		Assert.NotNull(await new UserStore(host.Factory).FindUserIdAsync("dana", CancellationToken.None));
	}

	[Fact]
	public async Task Delete_WithoutTheTypedEcho_IsRefused_AndNamesTheLoss()
	{
		await using WebUiHost host = await WebUiHost.StartAsync(Admin());
		await SeedUserAsync(host, "dana", contacts: 4);
		using HttpClient client = await host.SignInAsync("alice", admin: true);

		HttpResponseMessage response = await client.PostAsJsonAsync(
			"/admin/api/users/dana/delete", new { confirm = "wrong" });

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		string body = await response.Content.ReadAsStringAsync();
		Assert.Contains("confirm must echo 'dana'", body);
		Assert.Contains("4 contacts", body);
		Assert.Contains("nowhere else", body);
		// ...and the user is still there.
		Assert.NotNull(await new UserStore(host.Factory).FindUserIdAsync("dana", CancellationToken.None));
	}

	[Fact]
	public async Task Delete_WithTheTypedEcho_CascadesEverything()
	{
		await using WebUiHost host = await WebUiHost.StartAsync(Admin());
		int userId = await SeedUserAsync(host, "dana", contacts: 4);
		using HttpClient client = await host.SignInAsync("alice", admin: true);

		HttpResponseMessage response = await client.PostAsJsonAsync(
			"/admin/api/users/dana/delete", new { confirm = "dana" });

		Assert.True(response.IsSuccessStatusCode, $"delete failed: {response.StatusCode}");
		Assert.Null(await new UserStore(host.Factory).FindUserIdAsync("dana", CancellationToken.None));
		await using SyncDbContext db = host.Factory.CreateDbContext();
		Assert.Equal(0, await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
			.CountAsync(db.LocalItems, i => i.UserId == userId, CancellationToken.None));
		Assert.Equal(0, await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
			.CountAsync(db.Devices, d => d.UserId == userId, CancellationToken.None));
	}

	[Fact]
	public async Task Delete_OfAContentlessUser_StillNeedsTheEcho_ButDoesNotWarn()
	{
		// Graduated friction: sync state alone rebuilds, so the refusal is plain.
		await using WebUiHost host = await WebUiHost.StartAsync(Admin());
		await SeedUserAsync(host, "erin", contacts: 0);
		using HttpClient client = await host.SignInAsync("alice", admin: true);

		HttpResponseMessage refused = await client.PostAsJsonAsync(
			"/admin/api/users/erin/delete", new { confirm = "" });
		Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
		Assert.DoesNotContain("nowhere else", await refused.Content.ReadAsStringAsync());

		HttpResponseMessage done = await client.PostAsJsonAsync(
			"/admin/api/users/erin/delete", new { confirm = "erin" });
		Assert.True(done.IsSuccessStatusCode);
	}

	[Fact]
	public async Task Delete_OfAConfigDeclaredUser_SaysItWillComeBack()
	{
		// Configuration is not the gateway's to edit, so deleting a config-declared user is
		// honest about the row returning rather than pretending the deletion is final.
		await using WebUiHost host = await WebUiHost.StartAsync(WebUiHost.Users(
			("alice", new UserOptions { Admin = true }),
			("configured", new UserOptions())));
		await SeedUserAsync(host, "configured", contacts: 0);
		using HttpClient client = await host.SignInAsync("alice", admin: true);

		JsonElement body = await host.ReadJsonAsync(await client.PostAsJsonAsync(
			"/admin/api/users/configured/delete", new { confirm = "configured" }));

		Assert.True(body.GetProperty("configFallback").GetBoolean());
	}

	[Fact]
	public async Task Delete_And_Rename_AreAdminOnly()
	{
		await using WebUiHost host = await WebUiHost.StartAsync(WebUiHost.Users(
			("alice", new UserOptions { Admin = true }),
			("bob", new UserOptions())));
		await SeedUserAsync(host, "dana", contacts: 1);
		using HttpClient holder = await host.SignInAsync("bob", admin: false);

		HttpResponseMessage rename = await holder.PostAsJsonAsync(
			"/admin/api/users/dana/rename", new { newLogin = "hijacked" });
		HttpResponseMessage delete = await holder.PostAsJsonAsync(
			"/admin/api/users/dana/delete", new { confirm = "dana" });

		Assert.True(rename.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized);
		Assert.True(delete.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized);
		Assert.NotNull(await new UserStore(host.Factory).FindUserIdAsync("dana", CancellationToken.None));
	}
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ActiveSync.Core.Accounts;
using ActiveSync.Core.Options;

namespace ActiveSync.WebUi.Tests;

/// <summary>
///   Nothing stopped an admin from removing the last admin, or from disabling their own
///   account, leaving the web interface with no way back in. Recovery is CLI-only, which is a
///   legitimate escape hatch but not something the UI ever warned about.
/// </summary>
public sealed class LastAdminTests
{
	private static Dictionary<string, UserOptions> OneAdmin()
	{
		return WebUiHost.Users(
			("alice", new UserOptions { Admin = true }),
			("bob", new UserOptions()));
	}

	private static Dictionary<string, UserOptions> TwoAdmins()
	{
		return WebUiHost.Users(
			("alice", new UserOptions { Admin = true }),
			("carol", new UserOptions { Admin = true }));
	}

	[Fact]
	public async Task ClearingTheAdminFlagOnTheLastAdmin_IsRefused()
	{
		await using WebUiHost host = await WebUiHost.StartAsync(OneAdmin());
		using HttpClient client = await host.SignInAsync("alice", admin: true);

		HttpResponseMessage response = await client.PutAsJsonAsync(
			"/admin/api/users/alice", new { admin = false, enabled = true });

		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
	}

	[Fact]
	public async Task DisablingTheLastAdmin_IsRefused()
	{
		await using WebUiHost host = await WebUiHost.StartAsync(OneAdmin());
		using HttpClient client = await host.SignInAsync("alice", admin: true);

		HttpResponseMessage response = await client.PostAsJsonAsync(
			"/admin/api/users/alice/disable", new { });

		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
	}

	[Fact]
	public async Task DeletingTheLastAdminsDatabaseRow_IsRefused()
	{
		// A delete only drops the database row. Here the sole admin exists ONLY as a row — no
		// config entry underneath — so the account, and the last admin flag with it, would
		// disappear. (A row shadowing an admin config entry is fine: the entry takes over.)
		await using WebUiHost host = await WebUiHost.StartAsync(
			WebUiHost.Users(("bob", new UserOptions())));
		UserStore store = new(host.Factory);
		await store.UpsertAsync("dave", new UserOptions { Admin = true }, CancellationToken.None);

		using HttpClient client = await host.SignInAsync("dave", admin: true);
		HttpResponseMessage response = await client.DeleteAsync("/admin/api/users/dave");
		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
	}

	[Fact]
	public async Task DeletingTheLastAdminThroughTheDeleteRoute_IsRefused()
	{
		// Unlike PUT/DELETE/disable, POST /delete (the confirm-and-cascade route) ran no
		// LastAdminProblemAsync check at all, so the sole admin could destroy their own account
		// outright and lock every operator out of /admin — recovery from there is CLI-only.
		await using WebUiHost host = await WebUiHost.StartAsync(OneAdmin());
		using HttpClient client = await host.SignInAsync("alice", admin: true);

		HttpResponseMessage response = await client.PostAsJsonAsync(
			"/admin/api/users/alice/delete", new { confirm = "alice" });

		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
		// ...and the account is still there.
		Assert.NotNull(await new UserStore(host.Factory).FindUserIdAsync("alice", CancellationToken.None));
	}

	[Fact]
	public async Task DeletingARowThatShadowsAnAdminConfigEntry_IsAllowed()
	{
		await using WebUiHost host = await WebUiHost.StartAsync(
			WebUiHost.Users(("alice", new UserOptions { Admin = true })));
		UserStore store = new(host.Factory);
		await store.UpsertAsync("alice", new UserOptions { Admin = true }, CancellationToken.None);

		using HttpClient client = await host.SignInAsync("alice", admin: true);
		HttpResponseMessage response = await client.DeleteAsync("/admin/api/users/alice");
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	[Fact]
	public async Task WithASecondAdmin_TheSameWritesSucceed()
	{
		await using WebUiHost host = await WebUiHost.StartAsync(TwoAdmins());
		using HttpClient client = await host.SignInAsync("alice", admin: true);

		HttpResponseMessage demote = await client.PutAsJsonAsync(
			"/admin/api/users/carol", new { admin = false, enabled = true });
		Assert.Equal(HttpStatusCode.OK, demote.StatusCode);
	}

	[Fact]
	public async Task DisablingYourOwnAccount_WarnsButIsAllowed_WhenAnotherAdminRemains()
	{
		await using WebUiHost host = await WebUiHost.StartAsync(TwoAdmins());
		using HttpClient client = await host.SignInAsync("alice", admin: true);

		HttpResponseMessage response = await client.PostAsJsonAsync(
			"/admin/api/users/alice/disable", new { });

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		JsonElement body = await host.ReadJsonAsync(response);
		Assert.Contains("your own", body.GetProperty("warning").GetString(), StringComparison.OrdinalIgnoreCase);
	}
}

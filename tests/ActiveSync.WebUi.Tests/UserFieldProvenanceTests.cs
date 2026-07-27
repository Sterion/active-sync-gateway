using System.Text.Json;
using ActiveSync.Core.Accounts;
using ActiveSync.Core.Options;

namespace ActiveSync.WebUi.Tests;

/// <summary>
///   C8 — `GET /admin/api/users` advertises per-field provenance it does not carry.
///   `docs/webui.md` documents "declared users with per-field provenance (each value tagged with
///   the level that supplied it)", and `MergedUser.Sources` already computes exactly that, but
///   `UsersEndpoints.ToDto` never read it and emitted only one coarse `Origin` string for the
///   whole user — which is also what makes C2's freeze invisible to the operator.
/// </summary>
public sealed class UserFieldProvenanceTests
{
	[Fact]
	public async Task Get_ReportsWhichLevelSuppliedEachField()
	{
		await using WebUiHost host = await WebUiHost.StartAsync(WebUiHost.Users(
			("alice", new UserOptions { Admin = true }),
			// MailAddress from config, Admin overridden in the database.
			("configured", new UserOptions { MailAddress = "c@example.com" })));
		UserStore store = new(host.Factory);
		await store.UpsertAsync("configured", new UserOptions { Admin = true }, CancellationToken.None);

		using HttpClient client = await host.SignInAsync("alice", admin: true);
		JsonElement user = await host.ReadJsonAsync(await client.GetAsync("/admin/api/users/configured"));

		JsonElement sources = user.GetProperty("sources");
		Assert.Equal("config", sources.GetProperty("MailAddress").GetString());
		Assert.Equal("db", sources.GetProperty("Admin").GetString());
	}
}

using System.Net.Http.Json;
using ActiveSync.Core.Accounts;
using ActiveSync.Core.Options;

namespace ActiveSync.WebUi.Tests;

/// <summary>
///   The admin Users editor round-trips the MERGED (config over database) view back into the
///   database row. `GET /admin/api/users` reports config-supplied fields as part of the effective
///   entry, `users.js` pre-fills the editor from them, and a save that resubmits them UNCHANGED used
///   to write them straight into the database row — freezing every config-supplied field as a
///   permanent override. This is exactly the trap per-field resolution is meant to avoid: loading a
///   starting entry used to clone config values into the row, which is right for whole-entry
///   replacement but wrong once resolution is per field, since it freezes config values as
///   overrides. A later edit to the same key in configuration would then silently stop reaching
///   the user.
/// </summary>
public sealed class UsersMergedViewWriteBackTests
{
	private static Dictionary<string, UserOptions> Users() => WebUiHost.Users(
		("alice", new UserOptions { Admin = true }),
		("configured", new UserOptions { MailAddress = "c@example.com", Admin = true }));

	[Fact]
	public async Task Update_ResubmittingTheConfigValues_DoesNotFreezeThemAsADatabaseOverride()
	{
		await using WebUiHost host = await WebUiHost.StartAsync(Users());
		using HttpClient client = await host.SignInAsync("alice", admin: true);

		// Exactly what GET /admin/api/users/configured would have returned — the admin never
		// touched anything.
		HttpResponseMessage response = await client.PutAsJsonAsync("/admin/api/users/configured", new
		{
			mailAddress = "c@example.com",
			admin = true,
			enabled = true,
		});
		Assert.True(response.IsSuccessStatusCode, $"update failed: {response.StatusCode}");

		UserStore store = new(host.Factory);
		UserOptions? row = await store.GetAsync("configured", CancellationToken.None);
		Assert.NotNull(row);
		// Neither field should have been written to the row: both still come from configuration,
		// and a database copy here would permanently shadow any later config edit.
		Assert.Null(row!.MailAddress);
		Assert.Null(row.Admin);
	}

	[Fact]
	public async Task Update_ActuallyChangingAField_StillRecordsARealOverride()
	{
		// The elision must not swallow a genuine deviation.
		await using WebUiHost host = await WebUiHost.StartAsync(Users());
		using HttpClient client = await host.SignInAsync("alice", admin: true);

		HttpResponseMessage response = await client.PutAsJsonAsync("/admin/api/users/configured", new
		{
			mailAddress = "overridden@example.com",
			admin = true,
			enabled = true,
		});
		Assert.True(response.IsSuccessStatusCode, $"update failed: {response.StatusCode}");

		UserStore store = new(host.Factory);
		UserOptions? row = await store.GetAsync("configured", CancellationToken.None);
		Assert.Equal("overridden@example.com", row!.MailAddress);
	}
}

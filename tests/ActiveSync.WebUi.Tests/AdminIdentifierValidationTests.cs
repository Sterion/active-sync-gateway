using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ActiveSync.Core.Options;
using ActiveSync.Core.State;

namespace ActiveSync.WebUi.Tests;

/// <summary>
///   C16 — device blocks and share grants accepted whatever arrived. Only
///   <c>IsNullOrWhiteSpace</c> on the login and <c>StartsWith('/')</c> on the href, so a login
///   carrying ':' or a control character (neither can survive Basic auth or the session/watcher
///   key separator, which is why every other write path rejects them) and an href like
///   <c>/../../etc</c> persisted as rows that can never match anything.
///
///   NOTE on what is deliberately still allowed: a block naming a login that is not declared.
///   Pass-through authentication means most users have no declared entry, and a block placed
///   before a device first syncs is a legitimate pre-emptive action — so the response reports
///   <c>knownUser</c> for the UI to warn on instead of refusing the write.
/// </summary>
public sealed class AdminIdentifierValidationTests
{
	private static async Task<WebUiHost> AdminHostAsync()
	{
		return await WebUiHost.StartAsync(
			WebUiHost.Users(("alice", new AccountOptions { Admin = true })));
	}

	[Theory]
	[InlineData("bob:evil")]
	[InlineData("bob\nevil")]
	[InlineData("   ")]
	public async Task DeviceBlock_RefusesAMalformedLogin(string login)
	{
		await using WebUiHost host = await AdminHostAsync();
		using HttpClient client = await host.SignInAsync("alice", admin: true);

		HttpResponseMessage response = await client.PostAsJsonAsync(
			"/admin/api/devices/block", new { user = login, deviceId = "phone1" });

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		await using SyncDbContext db = host.Factory.CreateDbContext();
		Assert.Empty(db.LoginBlocks);
	}

	[Fact]
	public async Task DeviceBlock_ReportsWhetherTheLoginIsDeclared()
	{
		await using WebUiHost host = await AdminHostAsync();
		using HttpClient client = await host.SignInAsync("alice", admin: true);

		JsonElement unknown = await host.ReadJsonAsync(await client.PostAsJsonAsync(
			"/admin/api/devices/block", new { user = "typo", deviceId = "phone1" }));
		Assert.False(unknown.GetProperty("knownUser").GetBoolean());

		JsonElement known = await host.ReadJsonAsync(await client.PostAsJsonAsync(
			"/admin/api/devices/block", new { user = "alice", deviceId = "phone1" }));
		Assert.True(known.GetProperty("knownUser").GetBoolean());
	}

	[Theory]
	[InlineData("/dav/../../etc/passwd")]
	[InlineData("/dav/..")]
	// A raw control character: unusable in an href, rejected everywhere else.
	[InlineData("/dav/\u0007/")]
	[InlineData("relative/path/")]
	public async Task Share_RefusesAMalformedHref(string href)
	{
		await using WebUiHost host = await AdminHostAsync();
		using HttpClient client = await host.SignInAsync("alice", admin: true);

		HttpResponseMessage response = await client.PostAsJsonAsync(
			"/admin/api/shares", new { user = "alice", collectionHref = href, readOnly = true });

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		await using SyncDbContext db = host.Factory.CreateDbContext();
		Assert.Empty(db.SharedCalendarGrants);
	}

	[Fact]
	public async Task Share_RefusesAMalformedLogin()
	{
		await using WebUiHost host = await AdminHostAsync();
		using HttpClient client = await host.SignInAsync("alice", admin: true);

		HttpResponseMessage response = await client.PostAsJsonAsync(
			"/admin/api/shares", new { user = "bob:evil", collectionHref = "/dav/cal/", readOnly = false });

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task Share_AcceptsAnOrdinaryCollection()
	{
		await using WebUiHost host = await AdminHostAsync();
		using HttpClient client = await host.SignInAsync("alice", admin: true);

		HttpResponseMessage response = await client.PostAsJsonAsync(
			"/admin/api/shares",
			new { user = "alice", collectionHref = "/dav/cal/family/", readOnly = true });

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		JsonElement body = await host.ReadJsonAsync(response);
		Assert.Equal("/dav/cal/family/", body.GetProperty("collectionHref").GetString());
		Assert.True(body.GetProperty("knownUser").GetBoolean());
	}
}

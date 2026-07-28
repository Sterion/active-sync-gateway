using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ActiveSync.Core.Options;
using ActiveSync.Core.State;

namespace ActiveSync.WebUi.Tests;

/// <summary>
///   Device blocks and share grants accepted whatever arrived. Only
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
			WebUiHost.Users(("alice", new UserOptions { Admin = true })));
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
		// A block FKs to a real partnership, so both users need a device to block.
		await SeedDeviceAsync(host, "typo", "phone1");
		await SeedDeviceAsync(host, "alice", "phone1");

		JsonElement unknown = await host.ReadJsonAsync(await client.PostAsJsonAsync(
			"/admin/api/devices/block", new { user = "typo", deviceId = "phone1" }));
		Assert.False(unknown.GetProperty("knownUser").GetBoolean());

		JsonElement known = await host.ReadJsonAsync(await client.PostAsJsonAsync(
			"/admin/api/devices/block", new { user = "alice", deviceId = "phone1" }));
		Assert.True(known.GetProperty("knownUser").GetBoolean());
	}

	[Fact]
	public async Task Block_WithoutADevice_IsRefused_PointingAtDisablingTheUser()
	{
		// The admin API must enforce device-scoping exactly like the CLI — both go through
		// DeviceAdminService, which is where decision 19 lives.
		await using WebUiHost host = await AdminHostAsync();
		using HttpClient client = await host.SignInAsync("alice", admin: true);

		HttpResponseMessage response = await client.PostAsJsonAsync(
			"/admin/api/devices/block", new { user = "alice" });

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Contains("deviceId is required", await response.Content.ReadAsStringAsync());
	}

	private static async Task SeedDeviceAsync(WebUiHost host, string login, string deviceId)
	{
		(int userId, _) = await new ActiveSync.Core.Accounts.UserStore(host.Factory)
			.GetOrCreateUserAsync(login, null, CancellationToken.None);
		await using SyncDbContext db = host.Factory.CreateDbContext();
#pragma warning disable VSTHRD103
		db.Devices.Add(new Device
		{
			UserId = userId, DeviceId = deviceId, DeviceType = "Test",
			CreatedUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow,
		});
#pragma warning restore VSTHRD103
		await db.SaveChangesAsync(CancellationToken.None);
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
	public async Task Share_Delete_RefusesAMalformedLogin()
	{
		// The POST verb validates the login via AdminIdentifiers.LoginProblem (see
		// Share_RefusesAMalformedLogin above); the DELETE verb validated neither shape nor
		// whitespace, so the two write surfaces disagreed on what a storable login looks like.
		await using WebUiHost host = await AdminHostAsync();
		using HttpClient client = await host.SignInAsync("alice", admin: true);

		HttpResponseMessage response = await client.DeleteAsync(
			$"/admin/api/shares?user={Uri.EscapeDataString("bob:evil")}&collectionHref={Uri.EscapeDataString("/dav/cal/")}");

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

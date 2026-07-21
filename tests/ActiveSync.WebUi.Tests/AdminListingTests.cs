using System.Text.Json;
using ActiveSync.Core.Options;
using ActiveSync.Core.State;

namespace ActiveSync.WebUi.Tests;

/// <summary>
///   C10 — <c>GET /admin/api/devices</c> and <c>/shares</c> materialized their whole tables per
///   request: no Take, no pagination, while the sibling logs endpoint clamps to 500. Tens of
///   thousands of devices is a repeatable memory spike an admin can trigger by refreshing, and
///   the in-memory <c>blocks.Any(...)</c> cross-join made the devices listing O(devices×blocks)
///   on top.
/// </summary>
public sealed class AdminListingTests
{
	private static async Task SeedDevicesAsync(
		ISyncDbContextFactory factory, int count, string user = "alice")
	{
		await using SyncDbContext db = factory.CreateDbContext();
		for (int i = 0; i < count; i++)
			// DbSet.Add is synchronous and local (no I/O).
#pragma warning disable VSTHRD103
			db.Devices.Add(new Device
			{
				UserName = user,
				DeviceId = $"device{i:0000}",
				DeviceType = "Test",
				CreatedUtc = DateTime.UtcNow,
				LastSeenUtc = DateTime.UtcNow
			});
#pragma warning restore VSTHRD103
		await db.SaveChangesAsync(CancellationToken.None);
	}

	private static async Task SeedSharesAsync(ISyncDbContextFactory factory, int count)
	{
		await using SyncDbContext db = factory.CreateDbContext();
		for (int i = 0; i < count; i++)
#pragma warning disable VSTHRD103
			db.SharedCalendarGrants.Add(new SharedCalendarGrant
			{
				UserName = "alice",
				CollectionHref = $"/dav/shared/{i:0000}/",
				CreatedUtc = DateTime.UtcNow
			});
#pragma warning restore VSTHRD103
		await db.SaveChangesAsync(CancellationToken.None);
	}

	private static async Task<WebUiHost> AdminHostAsync()
	{
		return await WebUiHost.StartAsync(
			WebUiHost.Users(("alice", new AccountOptions { Admin = true })));
	}

	[Fact]
	public async Task Devices_AreCappedAndReportTheTotal()
	{
		await using WebUiHost host = await AdminHostAsync();
		await SeedDevicesAsync(host.Factory, 600);
		using HttpClient client = await host.SignInAsync("alice", admin: true);

		JsonElement body = await host.ReadJsonAsync(await client.GetAsync("/admin/api/devices"));
		Assert.Equal(200, body.GetProperty("entries").GetArrayLength());
		Assert.Equal(600, body.GetProperty("total").GetInt32());
	}

	[Fact]
	public async Task Devices_HonourLimitAndOffset_ClampedLikeTheLogsEndpoint()
	{
		await using WebUiHost host = await AdminHostAsync();
		await SeedDevicesAsync(host.Factory, 600);
		using HttpClient client = await host.SignInAsync("alice", admin: true);

		JsonElement page = await host.ReadJsonAsync(
			await client.GetAsync("/admin/api/devices?limit=5&offset=10"));
		JsonElement[] entries = [.. page.GetProperty("entries").EnumerateArray()];
		Assert.Equal(5, entries.Length);
		Assert.Equal("device0010", entries[0].GetProperty("deviceId").GetString());

		// A caller asking for everything gets the same ceiling as /logs, not the table.
		JsonElement greedy = await host.ReadJsonAsync(
			await client.GetAsync("/admin/api/devices?limit=100000"));
		Assert.Equal(500, greedy.GetProperty("entries").GetArrayLength());
	}

	[Fact]
	public async Task Shares_AreCappedAndReportTheTotal()
	{
		await using WebUiHost host = await AdminHostAsync();
		await SeedSharesAsync(host.Factory, 600);
		using HttpClient client = await host.SignInAsync("alice", admin: true);

		JsonElement body = await host.ReadJsonAsync(await client.GetAsync("/admin/api/shares"));
		Assert.Equal(200, body.GetProperty("entries").GetArrayLength());
		Assert.Equal(600, body.GetProperty("total").GetInt32());
	}

	[Fact]
	public async Task Devices_StillReportBlockState()
	{
		// The block cross-join is what the O(n×m) scan was for — replacing it with set lookups
		// must not change the answer.
		await using WebUiHost host = await AdminHostAsync();
		await SeedDevicesAsync(host.Factory, 3);
		await using (SyncDbContext db = host.Factory.CreateDbContext())
		{
#pragma warning disable VSTHRD103
			db.LoginBlocks.Add(new LoginBlock
			{
				UserName = "alice", DeviceId = "device0001", CreatedUtc = DateTime.UtcNow
			});
#pragma warning restore VSTHRD103
			await db.SaveChangesAsync(CancellationToken.None);
		}

		using HttpClient client = await host.SignInAsync("alice", admin: true);
		JsonElement body = await host.ReadJsonAsync(await client.GetAsync("/admin/api/devices"));
		Dictionary<string, bool> blocked = body.GetProperty("entries").EnumerateArray()
			.ToDictionary(
				d => d.GetProperty("deviceId").GetString()!,
				d => d.GetProperty("blocked").GetBoolean());

		Assert.False(blocked["device0000"]);
		Assert.True(blocked["device0001"]);
		Assert.False(blocked["device0002"]);
	}

	[Fact]
	public async Task Devices_UserLevelBlock_CoversEveryDeviceOfThatUser()
	{
		// Blocked on a DIFFERENT login than the one signing in — a user-level block also refuses
		// the web session of the account it names.
		await using WebUiHost host = await AdminHostAsync();
		await SeedDevicesAsync(host.Factory, 3, user: "carol");
		await using (SyncDbContext db = host.Factory.CreateDbContext())
		{
#pragma warning disable VSTHRD103
			db.LoginBlocks.Add(new LoginBlock { UserName = "carol", CreatedUtc = DateTime.UtcNow });
#pragma warning restore VSTHRD103
			await db.SaveChangesAsync(CancellationToken.None);
		}

		using HttpClient client = await host.SignInAsync("alice", admin: true);
		JsonElement body = await host.ReadJsonAsync(await client.GetAsync("/admin/api/devices"));
		Assert.All(body.GetProperty("entries").EnumerateArray(), device =>
		{
			Assert.True(device.GetProperty("blocked").GetBoolean());
			Assert.True(device.GetProperty("userBlocked").GetBoolean());
		});
	}
}

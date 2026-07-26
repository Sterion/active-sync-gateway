using System.Net;
using System.Xml.Linq;
using ActiveSync.Core.State;
using ActiveSync.Integration.Tests.Infrastructure;
using ActiveSync.Protocol.Wbxml;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ActiveSync.Integration.Tests.Scenarios;

/// <summary>
///   The two distinct refusal mechanisms both answer 403 after a valid login: disabling the USER
///   (`eas user disable`) refuses every device, while an operator BLOCK (`eas block`) cuts off
///   exactly one device and leaves the others syncing. Both lift as soon as they are removed.
/// </summary>
[Collection("gateway")]
[Trait("Category", "Integration")]
public class LoginBlockTests(GatewayFixture gateway)
{
	/// <summary>
	///   Blocks or unblocks ONE DEVICE (blocks are per-device only now — the whole-user switch is
	///   the user's Enabled flag, exercised separately). The device must already exist, which it
	///   does once the client has synced.
	/// </summary>
	private async Task SetDeviceBlockAsync(string userName, string deviceId, bool blocked)
	{
		using IServiceScope scope = gateway.Factory.Services.CreateScope();
		SyncDbContext db = scope.ServiceProvider.GetRequiredService<SyncDbContext>();
		Device? device = await db.Devices
			.FirstOrDefaultAsync(d => d.User.Login == userName.ToLowerInvariant() && d.DeviceId == deviceId);
		Assert.NotNull(device);
		LoginBlock? existing = await db.LoginBlocks.FirstOrDefaultAsync(b => b.DeviceKey == device!.Id);
		if (blocked && existing is null)
		{
			// DbSet.Add is synchronous and local (no I/O) — AddAsync exists only to support
			// async value generators (e.g. HiLo/Cosmos), which this project doesn't use.
#pragma warning disable VSTHRD103
			db.LoginBlocks.Add(new LoginBlock { DeviceKey = device!.Id, CreatedUtc = DateTime.UtcNow });
#pragma warning restore VSTHRD103
		}
		else if (!blocked && existing is not null)
			db.LoginBlocks.Remove(existing);
		await db.SaveChangesAsync();
	}

	/// <summary>Turns the whole USER off/on (the mechanism that replaced a user-level block).</summary>
	private async Task SetUserEnabledAsync(string userName, bool enabled)
	{
		using IServiceScope scope = gateway.Factory.Services.CreateScope();
		ActiveSync.Core.Accounts.UserStore users =
			scope.ServiceProvider.GetRequiredService<ActiveSync.Core.Accounts.UserStore>();
		ActiveSync.Core.Options.UserOptions entry =
			await users.GetAsync(userName, CancellationToken.None) ?? new ActiveSync.Core.Options.UserOptions();
		entry.Enabled = enabled ? null : false;
		await users.UpsertAsync(userName, entry, CancellationToken.None);
		await scope.ServiceProvider.GetRequiredService<ActiveSync.Core.Accounts.UserResolver>()
			.EnsureFreshAsync(true, CancellationToken.None);
	}

	private static Task<HttpResponseMessage> FolderSyncRawAsync(EasTestClient client)
	{
		XDocument body = new(
			new XElement(EasNamespaces.FolderHierarchy + "FolderSync",
				new XElement(EasNamespaces.FolderHierarchy + "SyncKey", "0")));
		return client.PostRawAsync("FolderSync", body);
	}

	[BackendFact]
	public async Task DisablingTheUser_Returns403_OnEveryDevice_UntilReEnabled()
	{
		EasTestClient client = gateway.CreateEasClient(TestBackend.User2);
		EasTestClient second = gateway.CreateEasClient(TestBackend.User2);
		try
		{
			using HttpResponseMessage before = await FolderSyncRawAsync(client);
			Assert.Equal(HttpStatusCode.OK, before.StatusCode);

			await SetUserEnabledAsync(TestBackend.User2, false);
			using HttpResponseMessage blocked = await FolderSyncRawAsync(client);
			Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
			// Disabling is the WHOLE-USER switch: a second device is refused too.
			using HttpResponseMessage blockedSecond = await FolderSyncRawAsync(second);
			Assert.Equal(HttpStatusCode.Forbidden, blockedSecond.StatusCode);

			await SetUserEnabledAsync(TestBackend.User2, true);
			using HttpResponseMessage after = await FolderSyncRawAsync(client);
			Assert.Equal(HttpStatusCode.OK, after.StatusCode);
		}
		finally
		{
			await SetUserEnabledAsync(TestBackend.User2, true);
		}
	}

	[BackendFact]
	public async Task DeviceBlock_OnlyBlocksThatDevice()
	{
		EasTestClient blockedClient = gateway.CreateEasClient(TestBackend.User2);
		EasTestClient otherClient = gateway.CreateEasClient(TestBackend.User2);
		try
		{
			// Both partnerships must exist before a device can be blocked (the block FKs to the
			// device row), which one sync each establishes.
			using (HttpResponseMessage seedBlocked = await FolderSyncRawAsync(blockedClient))
				Assert.Equal(HttpStatusCode.OK, seedBlocked.StatusCode);
			using (HttpResponseMessage seedOther = await FolderSyncRawAsync(otherClient))
				Assert.Equal(HttpStatusCode.OK, seedOther.StatusCode);

			await SetDeviceBlockAsync(TestBackend.User2, blockedClient.DeviceId, true);

			using HttpResponseMessage refused = await FolderSyncRawAsync(blockedClient);
			Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

			using HttpResponseMessage allowed = await FolderSyncRawAsync(otherClient);
			Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

			await SetDeviceBlockAsync(TestBackend.User2, blockedClient.DeviceId, false);
			using HttpResponseMessage lifted = await FolderSyncRawAsync(blockedClient);
			Assert.Equal(HttpStatusCode.OK, lifted.StatusCode);
		}
		finally
		{
			await SetDeviceBlockAsync(TestBackend.User2, blockedClient.DeviceId, false);
		}
	}
}

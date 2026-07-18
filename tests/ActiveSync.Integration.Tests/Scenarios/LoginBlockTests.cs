using System.Net;
using System.Xml.Linq;
using ActiveSync.Core.State;
using ActiveSync.Integration.Tests.Infrastructure;
using ActiveSync.Protocol.Wbxml;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ActiveSync.Integration.Tests.Scenarios;

/// <summary>
///   Operator login blocks (the CLI `block`/`unblock` rows) refuse authenticated requests
///   with 403 — user-wide or for a single device — and disappear once removed.
/// </summary>
[Collection("gateway")]
[Trait("Category", "Integration")]
public class LoginBlockTests(GatewayFixture gateway)
{
	private async Task SetUserBlockAsync(string userName, string? deviceId, bool blocked)
	{
		using IServiceScope scope = gateway.Factory.Services.CreateScope();
		SyncDbContext db = scope.ServiceProvider.GetRequiredService<SyncDbContext>();
		LoginBlock? existing = await db.LoginBlocks.FirstOrDefaultAsync(
			b => b.UserName == userName && b.DeviceId == deviceId);
		if (blocked && existing is null)
		{
			// DbSet.Add is synchronous and local (no I/O) — AddAsync exists only to support
			// async value generators (e.g. HiLo/Cosmos), which this project doesn't use.
#pragma warning disable VSTHRD103
			db.LoginBlocks.Add(new LoginBlock { UserName = userName, DeviceId = deviceId, CreatedUtc = DateTime.UtcNow });
#pragma warning restore VSTHRD103
		}
		else if (!blocked && existing is not null)
			db.LoginBlocks.Remove(existing);
		await db.SaveChangesAsync();
	}

	private static Task<HttpResponseMessage> FolderSyncRawAsync(EasTestClient client)
	{
		XDocument body = new(
			new XElement(EasNamespaces.FolderHierarchy + "FolderSync",
				new XElement(EasNamespaces.FolderHierarchy + "SyncKey", "0")));
		return client.PostRawAsync("FolderSync", body);
	}

	[BackendFact]
	public async Task UserBlock_Returns403_UntilUnblocked()
	{
		EasTestClient client = gateway.CreateEasClient(TestBackend.User2);
		try
		{
			using HttpResponseMessage before = await FolderSyncRawAsync(client);
			Assert.Equal(HttpStatusCode.OK, before.StatusCode);

			await SetUserBlockAsync(TestBackend.User2, null, true);
			using HttpResponseMessage blocked = await FolderSyncRawAsync(client);
			Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);

			await SetUserBlockAsync(TestBackend.User2, null, false);
			using HttpResponseMessage after = await FolderSyncRawAsync(client);
			Assert.Equal(HttpStatusCode.OK, after.StatusCode);
		}
		finally
		{
			await SetUserBlockAsync(TestBackend.User2, null, false);
		}
	}

	[BackendFact]
	public async Task DeviceBlock_OnlyBlocksThatDevice()
	{
		EasTestClient blockedClient = gateway.CreateEasClient(TestBackend.User2);
		EasTestClient otherClient = gateway.CreateEasClient(TestBackend.User2);
		try
		{
			await SetUserBlockAsync(TestBackend.User2, blockedClient.DeviceId, true);

			using HttpResponseMessage refused = await FolderSyncRawAsync(blockedClient);
			Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

			using HttpResponseMessage allowed = await FolderSyncRawAsync(otherClient);
			Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
		}
		finally
		{
			await SetUserBlockAsync(TestBackend.User2, blockedClient.DeviceId, false);
		}
	}
}

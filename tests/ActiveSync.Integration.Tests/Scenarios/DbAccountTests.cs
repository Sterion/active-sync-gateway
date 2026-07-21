using System.Net;
using ActiveSync.Core.Accounts;
using ActiveSync.Core.Options;
using ActiveSync.Core.Security;
using ActiveSync.Integration.Tests.Infrastructure;
using ActiveSync.Protocol;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ActiveSync.Integration.Tests.Scenarios;

/// <summary>
///   Database-declared accounts (eas user ...): they authenticate and sync exactly like
///   config entries, grant RequireDeclaredUsers allowlist entries, and edits apply to the
///   running gateway via the stamp check without a restart.
/// </summary>
[Collection("gateway")]
[Trait("Category", "Integration")]
public class DbAccountTests(GatewayFixture gateway)
{
	private const string GatewayPassword = "topsecret-db-pw";

	private static EasTestClient CreateClient(WebApplicationFactory<Program> factory, string login, string password)
	{
		return new EasTestClient(
			factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }),
			login, password, $"DEV{Guid.NewGuid():N}"[..16].ToUpperInvariant());
	}

	private static async Task AssertSyncsInboxAsync(EasTestClient client)
	{
		await client.HandshakeAsync();
		string inbox = client.FolderOfType(EasFolderType.Inbox).ServerId;
		await client.InitialSyncAsync(inbox);
		Assert.NotNull(await client.PullAllAsync(inbox));
	}

	private static async Task AssertUnauthorizedAsync(EasTestClient client)
	{
		using HttpResponseMessage denied = await client.PostRawAsync("FolderSync", null);
		Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
	}

	[BackendFact]
	public async Task DbDeclaredUser_WithHashedGatewayAndSealedImapPassword_SyncsMail()
	{
		using WebApplicationFactory<Program> factory = gateway.CreateIsolatedFactory(
			new Dictionary<string, string?> { ["ActiveSync:Auth:UsersRefreshSeconds"] = "0" });
		AccountStore store = factory.Services.GetRequiredService<AccountStore>();
		await store.UpsertAsync("dbphone@gw.local", new AccountOptions
		{
			Password = GatewayPasswordHasher.Hash(GatewayPassword),
			MailAddress = TestBackend.User1,
			Backends = new Dictionary<string, BackendRoleOverride>
			{
				["MailStore"] = new()
				{
					UserName = TestBackend.User1,
					Password = SecretValue.Seal(
						TestBackend.Password, Convert.FromBase64String(GatewayFixture.TestEncryptionKey)),
				},
			},
		}, CancellationToken.None);

		// The gateway login exists ONLY in the database; a working sync proves the whole
		// chain (stamp refresh -> snapshot -> unsealed backend credentials).
		await AssertSyncsInboxAsync(CreateClient(factory, "dbphone@gw.local", GatewayPassword));
		await AssertUnauthorizedAsync(CreateClient(factory, "dbphone@gw.local", "wrong-password"));
	}

	[BackendFact]
	public async Task RequireDeclaredUsers_DbGrantAdmits_UndeclaredStays401()
	{
		using WebApplicationFactory<Program> factory = gateway.CreateIsolatedFactory(
			new Dictionary<string, string?>
			{
				["ActiveSync:RequireDeclaredUsers"] = "true",
				["ActiveSync:Auth:UsersRefreshSeconds"] = "0",
			});

		// No users anywhere yet: even valid IMAP credentials are rejected.
		await AssertUnauthorizedAsync(CreateClient(factory, TestBackend.User1, TestBackend.Password));

		// An empty database entry is a pure allowlist grant — auth probes IMAP as usual.
		AccountStore store = factory.Services.GetRequiredService<AccountStore>();
		await store.UpsertAsync(TestBackend.User1, new AccountOptions(), CancellationToken.None);

		await AssertSyncsInboxAsync(CreateClient(factory, TestBackend.User1, TestBackend.Password));
		await AssertUnauthorizedAsync(CreateClient(factory, TestBackend.User2, TestBackend.Password));
	}

	[BackendFact]
	public async Task AutoProvisionUsers_CreatesAutoMarkedRow_OnFirstSuccessfulSync()
	{
		using WebApplicationFactory<Program> factory = gateway.CreateIsolatedFactory(
			new Dictionary<string, string?>
			{
				["ActiveSync:AutoProvisionUsers"] = "true",
				["ActiveSync:Auth:UsersRefreshSeconds"] = "0",
			});
		AccountStore store = factory.Services.GetRequiredService<AccountStore>();

		// Pure pass-through to begin with: no declared row for this login.
		Assert.Null(await store.GetAsync(TestBackend.User1, CancellationToken.None));

		// A normal pass-through sync (credentials verified against the backend) provisions the user.
		await AssertSyncsInboxAsync(CreateClient(factory, TestBackend.User1, TestBackend.Password));

		AccountOptions? row = await store.GetAsync(TestBackend.User1, CancellationToken.None);
		Assert.NotNull(row);
		Assert.True(row!.AutoProvisioned);
		Assert.Null(row.Password); // no gateway password — auth still probes the backend

		// A second sync must not create a duplicate; the login is now declared.
		await AssertSyncsInboxAsync(CreateClient(factory, TestBackend.User1, TestBackend.Password));
		Assert.Single(await store.ListAsync(CancellationToken.None));
	}

	[BackendFact]
	public async Task AutoProvisionUsers_Off_LeavesPassThroughUnpersisted()
	{
		// Auto-provisioning is on by default, so this leg turns it OFF explicitly.
		using WebApplicationFactory<Program> factory = gateway.CreateIsolatedFactory(
			new Dictionary<string, string?>
			{
				["ActiveSync:AutoProvisionUsers"] = "false",
				["ActiveSync:Auth:UsersRefreshSeconds"] = "0",
			});
		AccountStore store = factory.Services.GetRequiredService<AccountStore>();

		await AssertSyncsInboxAsync(CreateClient(factory, TestBackend.User1, TestBackend.Password));

		Assert.Null(await store.GetAsync(TestBackend.User1, CancellationToken.None));
	}

	[BackendFact]
	public async Task DisabledAccount_Refuses403_ThenReEnableRestores()
	{
		using WebApplicationFactory<Program> factory = gateway.CreateIsolatedFactory(
			new Dictionary<string, string?> { ["ActiveSync:Auth:UsersRefreshSeconds"] = "0" });
		AccountStore store = factory.Services.GetRequiredService<AccountStore>();

		// An enabled (empty) declared account syncs normally.
		await store.UpsertAsync(TestBackend.User1, new AccountOptions(), CancellationToken.None);
		await AssertSyncsInboxAsync(CreateClient(factory, TestBackend.User1, TestBackend.Password));

		// Disable it: valid credentials now get 403 (not 401 — auth still succeeds) on every device.
		await store.UpsertAsync(TestBackend.User1, new AccountOptions { Enabled = false }, CancellationToken.None);
		using HttpResponseMessage refused = await CreateClient(factory, TestBackend.User1, TestBackend.Password)
			.PostRawAsync("FolderSync", null);
		Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

		// Re-enabling restores access on the next request (no restart).
		await store.UpsertAsync(TestBackend.User1, new AccountOptions(), CancellationToken.None);
		await AssertSyncsInboxAsync(CreateClient(factory, TestBackend.User1, TestBackend.Password));
	}

	[BackendFact]
	public async Task PasswordEdit_AppliesToTheNextRequest_WithoutRestart()
	{
		using WebApplicationFactory<Program> factory = gateway.CreateIsolatedFactory(
			new Dictionary<string, string?> { ["ActiveSync:Auth:UsersRefreshSeconds"] = "0" });
		AccountStore store = factory.Services.GetRequiredService<AccountStore>();

		AccountOptions entry = new()
		{
			Password = GatewayPasswordHasher.Hash("first-password"),
			Backends = new Dictionary<string, BackendRoleOverride> { ["MailStore"] = new() { UserName = TestBackend.User1, Password = TestBackend.Password } },
		};
		await store.UpsertAsync("rotating@gw.local", entry, CancellationToken.None);
		await AssertSyncsInboxAsync(CreateClient(factory, "rotating@gw.local", "first-password"));

		// Rotate the gateway password through the store (what `eas user password` does):
		// the stamp bump + SnapshotChanged cache reset must apply on the very next request,
		// despite the 5-minute success cache the old password just earned.
		entry.Password = GatewayPasswordHasher.Hash("second-password");
		await store.UpsertAsync("rotating@gw.local", entry, CancellationToken.None);

		await AssertUnauthorizedAsync(CreateClient(factory, "rotating@gw.local", "first-password"));
		await AssertSyncsInboxAsync(CreateClient(factory, "rotating@gw.local", "second-password"));
	}
}

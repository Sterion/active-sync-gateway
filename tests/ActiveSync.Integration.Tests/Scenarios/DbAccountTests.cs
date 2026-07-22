using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using ActiveSync.Core.Accounts;
using ActiveSync.Core.Options;
using ActiveSync.Core.Security;
using ActiveSync.Crypto;
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
	public async Task DisabledAccount_IsAlsoRefusedByAutodiscover()
	{
		// E14: Autodiscover shared the EAS auth prologue but only checked operator BLOCKS,
		// so `eas user disable` refused every device on /Microsoft-Server-ActiveSync while
		// Autodiscover kept handing the same account a service document.
		using WebApplicationFactory<Program> factory = gateway.CreateIsolatedFactory(
			new Dictionary<string, string?> { ["ActiveSync:Auth:UsersRefreshSeconds"] = "0" });
		AccountStore store = factory.Services.GetRequiredService<AccountStore>();
		using HttpClient http = factory.CreateClient(
			new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

		await store.UpsertAsync(TestBackend.User1, new AccountOptions(), CancellationToken.None);
		using (HttpResponseMessage enabled = await http.SendAsync(AutodiscoverRequest()))
			Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);

		await store.UpsertAsync(
			TestBackend.User1, new AccountOptions { Enabled = false }, CancellationToken.None);
		using (HttpResponseMessage disabled = await http.SendAsync(AutodiscoverRequest()))
			Assert.Equal(HttpStatusCode.Forbidden, disabled.StatusCode);

		// Re-enabling restores it on the next request, like the EAS path.
		await store.UpsertAsync(TestBackend.User1, new AccountOptions(), CancellationToken.None);
		using (HttpResponseMessage restored = await http.SendAsync(AutodiscoverRequest()))
			Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
	}

	private static HttpRequestMessage AutodiscoverRequest()
	{
		const string ns = "http://schemas.microsoft.com/exchange/autodiscover/mobilesync/requestschema/2006";
		XDocument body = new(
			new XElement(XName.Get("Autodiscover", ns),
				new XElement(XName.Get("Request", ns),
					new XElement(XName.Get("EMailAddress", ns), TestBackend.User1),
					new XElement(XName.Get("AcceptableResponseSchema", ns),
						"http://schemas.microsoft.com/exchange/autodiscover/mobilesync/responseschema/2006"))));
		HttpRequestMessage request = new(HttpMethod.Post, "/autodiscover/autodiscover.xml")
		{
			Content = new StringContent(body.ToString(), Encoding.UTF8, "text/xml")
		};
		request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
			Convert.ToBase64String(Encoding.UTF8.GetBytes($"{TestBackend.User1}:{TestBackend.Password}")));
		return request;
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

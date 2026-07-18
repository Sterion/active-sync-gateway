using System.Net;
using System.Xml.Linq;
using ActiveSync.Core.Security;
using ActiveSync.Integration.Tests.Infrastructure;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ActiveSync.Integration.Tests.Scenarios;

/// <summary>
///   Per-user overrides on top of the pass-through baseline. The declared gateway logins
///   ("phone1@gw.local"...) do not exist on the Stalwart stack — their entries map to the
///   real user1/user2 backend accounts, so a working sync proves the mapping — while
///   undeclared logins keep authenticating straight against IMAP on the same gateway.
/// </summary>
[Collection("gateway")]
[Trait("Category", "Integration")]
public class UserOverrideTests(GatewayFixture gateway) : IDisposable
{
	private const string GatewayPassword = "topsecret-gw-pw";

	private static readonly XNamespace ST = EasNamespaces.Settings;

	private readonly WebApplicationFactory<Program> _factory = gateway.CreateIsolatedFactory(
		new Dictionary<string, string?>
		{
			// phone1: explicit gateway password (rule 1) + full IMAP credential override.
			["ActiveSync:Users:phone1@gw.local:Password"] = GatewayPassword,
			["ActiveSync:Users:phone1@gw.local:MailAddress"] = TestBackend.User1,
			["ActiveSync:Users:phone1@gw.local:Backends:MailStore:UserName"] = TestBackend.User1,
			["ActiveSync:Users:phone1@gw.local:Backends:MailStore:Password"] = TestBackend.Password,
			// phone2: pbkdf2 gateway password + enc:v1:-sealed backend password, both
			// computed here so nothing bakes in and goes stale.
			["ActiveSync:Users:phone2@gw.local:Password"] = GatewayPasswordHasher.Hash(GatewayPassword),
			["ActiveSync:Users:phone2@gw.local:MailAddress"] = TestBackend.User2,
			["ActiveSync:Users:phone2@gw.local:Backends:MailStore:UserName"] = TestBackend.User2,
			["ActiveSync:Users:phone2@gw.local:Backends:MailStore:Password"] = SecretValue.Seal(
				TestBackend.Password, Convert.FromBase64String(GatewayFixture.TestEncryptionKey)),
			// phone3: only a user-name override — the phone must present the real IMAP
			// password, validated by a probe against the overridden identity (rule 3).
			["ActiveSync:Users:phone3@gw.local:Backends:MailStore:UserName"] = TestBackend.User2,
			// pinned: a configured Imap:Password pins the phone password to it (rule 2).
			["ActiveSync:Users:pinned@gw.local:Backends:MailStore:UserName"] = TestBackend.User1,
			["ActiveSync:Users:pinned@gw.local:Backends:MailStore:Password"] = TestBackend.Password
		});

	public void Dispose()
	{
		_factory.Dispose();
	}

	private EasTestClient CreateClient(string login, string password, WebApplicationFactory<Program>? factory = null)
	{
		return new EasTestClient(
			(factory ?? _factory).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }),
			login, password, $"DEV{Guid.NewGuid():N}"[..16].ToUpperInvariant());
	}

	private static async Task AssertSyncsInboxAsync(EasTestClient client)
	{
		await client.HandshakeAsync();
		string inbox = client.FolderOfType(EasFolderType.Inbox).ServerId;
		await client.InitialSyncAsync(inbox);
		Assert.NotNull(await client.PullAllAsync(inbox));
	}

	[BackendFact]
	public async Task GatewayPasswordOverride_MapsToBackendAccount_AndSyncsMail()
	{
		await AssertSyncsInboxAsync(CreateClient("phone1@gw.local", GatewayPassword));
	}

	[BackendFact]
	public async Task Settings_ReturnsTheConfiguredMailAddress_NotTheGatewayLogin()
	{
		EasTestClient client = CreateClient("phone1@gw.local", GatewayPassword);
		await client.HandshakeAsync();

		XDocument? response = await client.PostAsync("Settings", new XDocument(
			new XElement(ST + "Settings",
				new XElement(ST + "UserInformation",
					new XElement(ST + "Get")))));
		string? address = response?.Root?
			.Element(ST + "UserInformation")?.Element(ST + "Get")?
			.Element(ST + "EmailAddresses")?.Element(ST + "SMTPAddress")?.Value;
		Assert.Equal(TestBackend.User1, address);
	}

	[BackendFact]
	public async Task HashedGatewayPassword_AndSealedBackendPassword_Work()
	{
		await AssertSyncsInboxAsync(CreateClient("phone2@gw.local", GatewayPassword));
	}

	[BackendFact]
	public async Task ImapUserNameOverride_ProbesWithThePresentedPassword()
	{
		// No passwords configured for phone3 — presenting user2's REAL IMAP password must
		// authenticate via the probe against the overridden identity...
		await AssertSyncsInboxAsync(CreateClient("phone3@gw.local", TestBackend.Password));

		// ...and a wrong one is rejected by that same probe.
		EasTestClient wrong = CreateClient("phone3@gw.local", "not-the-imap-password");
		using HttpResponseMessage denied = await wrong.PostRawAsync("FolderSync", null);
		Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
	}

	[BackendFact]
	public async Task ConfiguredImapPassword_PinsThePhonePassword()
	{
		// Rule 2: presented password must equal the configured Imap:Password.
		await AssertSyncsInboxAsync(CreateClient("pinned@gw.local", TestBackend.Password));

		EasTestClient wrong = CreateClient("pinned@gw.local", "anything-else");
		using HttpResponseMessage denied = await wrong.PostRawAsync("FolderSync", null);
		Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
	}

	[BackendFact]
	public async Task UndeclaredUser_KeepsPlainPassThrough_OnTheSameGateway()
	{
		await AssertSyncsInboxAsync(CreateClient(TestBackend.User1, TestBackend.Password));
	}

	[BackendFact]
	public async Task WrongOrForeignPasswords_Get401()
	{
		EasTestClient wrongPassword = CreateClient("phone1@gw.local", "not-the-password");
		using HttpResponseMessage denied = await wrongPassword.PostRawAsync("FolderSync", null);
		Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

		// The backend password must never work as the gateway password when a gateway
		// Password override is set — the identities are decoupled by design.
		EasTestClient backendPassword = CreateClient("phone1@gw.local", TestBackend.Password);
		using HttpResponseMessage backendDenied = await backendPassword.PostRawAsync("FolderSync", null);
		Assert.Equal(HttpStatusCode.Unauthorized, backendDenied.StatusCode);
	}

	[BackendFact]
	public async Task RequireDeclaredUsers_RejectsUndeclared_EvenWithValidImapCredentials()
	{
		using WebApplicationFactory<Program> allowlistGateway = gateway.CreateIsolatedFactory(
			new Dictionary<string, string?>
			{
				["ActiveSync:RequireDeclaredUsers"] = "true",
				// An (effectively) empty entry is a pure allowlist grant — auth still runs
				// through the normal IMAP probe. (In-memory config needs one subkey to
				// materialize the entry; a JSON users file can use a literal {}.)
				[$"ActiveSync:Users:{TestBackend.User1}:MailAddress"] = TestBackend.User1
			});

		await AssertSyncsInboxAsync(CreateClient(TestBackend.User1, TestBackend.Password, allowlistGateway));

		// user2's IMAP credentials are perfectly valid — but user2 has no entry.
		EasTestClient undeclared = CreateClient(TestBackend.User2, TestBackend.Password, allowlistGateway);
		using HttpResponseMessage denied = await undeclared.PostRawAsync("FolderSync", null);
		Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
	}
}

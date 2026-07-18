using System.Net;
using System.Xml.Linq;
using ActiveSync.Integration.Tests.Infrastructure;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;

namespace ActiveSync.Integration.Tests.Scenarios;

[Collection("gateway")]
[Trait("Category", "Integration")]
public class HandshakeTests(GatewayFixture gateway)
{
	[BackendFact]
	public async Task Options_AdvertisesProtocolVersionsAndCommands()
	{
		EasTestClient client = gateway.CreateEasClient(TestBackend.User1);
		using HttpResponseMessage response = await client.OptionsAsync();

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Contains("14.1", response.Headers.GetValues("MS-ASProtocolVersions").Single());
		string commands = response.Headers.GetValues("MS-ASProtocolCommands").Single();
		Assert.Contains("Sync", commands);
		Assert.Contains("SendMail", commands);
		Assert.Contains("Ping", commands);
	}

	[BackendFact]
	public async Task Provision_TwoPhase_IssuesFinalPolicyKey()
	{
		EasTestClient client = gateway.CreateEasClient(TestBackend.User1);
		await client.ProvisionAsync();
		Assert.NotEqual(0u, client.PolicyKey);
	}

	[BackendFact]
	public async Task FolderSync_WorksWithPlainQueryForm()
	{
		// Legacy (pre-12.1) clients use ?Cmd=...&User=... — the endpoint must accept both forms.
		EasTestClient client = gateway.CreateEasClient(TestBackend.User1);
		XDocument body = new(
			new XElement(
				EasNamespaces.FolderHierarchy + "FolderSync",
				new XElement(
					EasNamespaces.FolderHierarchy + "SyncKey", "0")));
		using HttpResponseMessage response = await client.PostRawAsync("FolderSync", body, usePlainQuery: true);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		byte[] bytes = await response.Content.ReadAsByteArrayAsync();
		Assert.True(bytes.Length > 0);
	}

	[BackendFact]
	public async Task Handshake_YieldsWellKnownFolders()
	{
		EasTestClient client = gateway.CreateEasClient(TestBackend.User1);
		await client.HandshakeAsync();

		Assert.Contains(client.Folders, f => f.Type == EasFolderType.Inbox);
		// Special folders exist on both supported stacks
		Assert.Contains(client.Folders, f => f.Type is EasFolderType.SentItems or EasFolderType.UserMail);
		Assert.Contains(client.Folders, f => f.Type is EasFolderType.DeletedItems or EasFolderType.UserMail);
	}
}

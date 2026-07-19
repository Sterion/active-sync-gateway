using System.Xml.Linq;
using ActiveSync.Integration.Tests.Infrastructure;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;

namespace ActiveSync.Integration.Tests.Scenarios;

/// <summary>Read-only gateway: silent revert for item writes, honest errors for sends/folder ops.</summary>
[Collection("gateway")]
[Trait("Category", "Integration")]
public class ReadOnlyTests(GatewayFixture gateway)
{
	private static readonly XNamespace Email = EasNamespaces.Email;

	// Seed directly via SMTP — the read-only gateway itself refuses to send.
	private static Task<string> SeedMailAsync(string toUser, string subject)
	{
		return MailSeeder.SeedMailAsync(TestBackend.User2, toUser, subject);
	}

	[BackendFact]
	public async Task SendMail_IsRejected_WithStatus120()
	{
		EasTestClient client = gateway.CreateEasClient(TestBackend.User1, readOnly: true);
		await client.HandshakeAsync();

		XDocument? response = await client.SendMailAsync(EasTestClient.BuildMime(
			TestBackend.User1, TestBackend.User2, $"ro-{Guid.NewGuid():N}", "should not send"));

		Assert.NotNull(response); // failure = WBXML body (success would be empty)
		Assert.Equal("120", response.Root?.Element(EasNamespaces.ComposeMail + "Status")?.Value);
	}

	[BackendFact]
	public async Task FolderCreate_IsRejected_WithStatus3()
	{
		EasTestClient client = gateway.CreateEasClient(TestBackend.User1, readOnly: true);
		await client.HandshakeAsync();
		(string status, _) = await client.FolderCreateAsync($"ro-folder-{Guid.NewGuid():N}"[..16]);
		Assert.Equal("3", status);
	}

	[SmtpSubmissionFact]
	public async Task Delete_IsSilentlyReverted_ItemComesBack()
	{
		EasTestClient client = gateway.CreateEasClient(TestBackend.User1, readOnly: true);
		await client.HandshakeAsync();
		string inbox = client.FolderOfType(EasFolderType.Inbox).ServerId;
		await client.InitialSyncAsync(inbox);
		await client.PullAllAsync(inbox);

		string subject = await SeedMailAsync(TestBackend.User1, $"ro-del-{Guid.NewGuid():N}");
		SyncItem item = await WaitUntil.ResultAsync(async () =>
			{
				SyncResult pull = await client.PullAllAsync(inbox);
				return pull.Adds.FirstOrDefault(a =>
					a.ApplicationData.Element(Email + "Subject")?.Value == subject);
			}, $"delivery of '{subject}'");

		// Delete is accepted on the wire (no error) ...
		SyncResult delete = await client.DeleteItemAsync(inbox, item.ServerId);
		Assert.Equal("1", delete.Status);

		// ... but the server re-Adds the item (silent revert). The diff runs after client
		// commands, so the revert usually arrives in the very same Sync response.
		SyncItem restored = delete.Adds.FirstOrDefault(a =>
			                    a.ApplicationData.Element(Email + "Subject")?.Value == subject)
		                    ?? await WaitUntil.ResultAsync(async () =>
			                    {
				                    SyncResult pull = await client.PullAllAsync(inbox);
				                    return pull.Adds.FirstOrDefault(a =>
					                    a.ApplicationData.Element(Email + "Subject")?.Value == subject);
			                    }, $"silent revert of deleted '{subject}'", TimeSpan.FromSeconds(20));
		Assert.Equal(item.ServerId, restored.ServerId);
	}

	[SmtpSubmissionFact]
	public async Task FlagChange_IsSilentlyReverted()
	{
		EasTestClient client = gateway.CreateEasClient(TestBackend.User1, readOnly: true);
		await client.HandshakeAsync();
		string inbox = client.FolderOfType(EasFolderType.Inbox).ServerId;
		await client.InitialSyncAsync(inbox);
		await client.PullAllAsync(inbox);

		string subject = await SeedMailAsync(TestBackend.User1, $"ro-flag-{Guid.NewGuid():N}");
		SyncItem item = await WaitUntil.ResultAsync(async () =>
			{
				SyncResult pull = await client.PullAllAsync(inbox);
				return pull.Adds.FirstOrDefault(a =>
					a.ApplicationData.Element(Email + "Subject")?.Value == subject);
			}, $"delivery of '{subject}'");
		Assert.Equal("0", item.ApplicationData.Element(Email + "Read")?.Value);

		SyncResult change = await client.ChangeItemAsync(inbox, item.ServerId,
			new XElement(Email + "Read", "1"));
		Assert.Equal("1", change.Status); // accepted on the wire

		// The server pushes its version back (Read still 0) — usually in the same response.
		// Depending on backend timing the revert may arrive as a Change or a re-Add.
		static SyncItem? Revert(SyncResult result, string serverId)
		{
			return result.Changes.FirstOrDefault(c => c.ServerId == serverId)
			       ?? result.Adds.FirstOrDefault(a => a.ServerId == serverId);
		}

		SyncItem reverted = Revert(change, item.ServerId)
		                    ?? await WaitUntil.ResultAsync(async () =>
			                    {
				                    SyncResult pull = await client.PullAllAsync(inbox);
				                    return Revert(pull, item.ServerId);
			                    }, $"silent revert of read flag on '{subject}'", TimeSpan.FromSeconds(30));
		Assert.Equal("0", reverted.ApplicationData.Element(Email + "Read")?.Value);
	}
}

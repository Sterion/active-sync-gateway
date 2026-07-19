using System.Xml.Linq;
using ActiveSync.Integration.Tests.Infrastructure;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;
using MailKit;

namespace ActiveSync.Integration.Tests.Scenarios;

/// <summary>
///   Mail end-to-end over the JMAP provider: FolderSync, two-client send/receive/reply, and
///   read-flag + delete — with cross-protocol verification via IMAP (Stalwart serves the same
///   store over both), proving the JMAP mail path matches the IMAP one.
/// </summary>
[Collection("gateway")]
[Trait("Category", "Integration")]
public class JmapMailFlowTests(GatewayFixture gateway)
{
	private static readonly XNamespace Email = EasNamespaces.Email;

	[JmapMailFact]
	public async Task Jmap_FolderSync_MapsSpecialFolders()
	{
		EasTestClient client = gateway.CreateJmapEasClient(TestBackend.User1);
		await client.HandshakeAsync();

		// The JMAP Mailbox roles must map to the EAS special folder types.
		Assert.NotNull(client.FolderOfType(EasFolderType.Inbox));
		Assert.NotNull(client.FolderOfType(EasFolderType.SentItems));
		Assert.NotNull(client.FolderOfType(EasFolderType.DeletedItems));
		Assert.NotNull(client.FolderOfType(EasFolderType.Drafts));
	}

	[JmapMailFact]
	public async Task Jmap_User1SendsMail_User2ReceivesIt_RepliesBack()
	{
		EasTestClient clientA = gateway.CreateJmapEasClient(TestBackend.User1);
		EasTestClient clientB = gateway.CreateJmapEasClient(TestBackend.User2);
		await clientA.HandshakeAsync();
		await clientB.HandshakeAsync();

		string inboxA = clientA.FolderOfType(EasFolderType.Inbox).ServerId;
		string inboxB = clientB.FolderOfType(EasFolderType.Inbox).ServerId;
		await clientA.InitialSyncAsync(inboxA);
		await clientB.InitialSyncAsync(inboxB);
		await clientA.PullAllAsync(inboxA);
		await clientB.PullAllAsync(inboxB);

		string subject = $"jmap-e2e-{Guid.NewGuid():N}";
		XDocument? sendResult = await clientA.SendMailAsync(EasTestClient.BuildMime(
			TestBackend.User1, TestBackend.User2, subject, "Hello from client A over JMAP!"));
		Assert.Null(sendResult);

		SyncItem received = await WaitUntil.ResultAsync(async () =>
			{
				SyncResult pull = await clientB.PullAllAsync(inboxB);
				return pull.Adds.FirstOrDefault(a =>
					a.ApplicationData.Element(Email + "Subject")?.Value == subject);
			}, $"client B receiving '{subject}' over JMAP");

		Assert.Contains(TestBackend.User1, received.ApplicationData.Element(Email + "From")?.Value ?? "");
		string? body = received.ApplicationData
			.Element(EasNamespaces.AirSyncBase + "Body")?
			.Element(EasNamespaces.AirSyncBase + "Data")?.Value;
		Assert.Contains("Hello from client A", body);

		XDocument? replyResult = await clientB.SmartReplyAsync(
			EasTestClient.BuildMime(TestBackend.User2, TestBackend.User1, $"Re: {subject}", "Reply from B"),
			inboxB, received.ServerId);
		Assert.Null(replyResult);

		SyncItem reply = await WaitUntil.ResultAsync(async () =>
			{
				SyncResult pull = await clientA.PullAllAsync(inboxA);
				return pull.Adds.FirstOrDefault(a =>
					a.ApplicationData.Element(Email + "Subject")?.Value == $"Re: {subject}");
			}, $"client A receiving reply to '{subject}' over JMAP");

		string replyBody = reply.ApplicationData
			.Element(EasNamespaces.AirSyncBase + "Body")?
			.Element(EasNamespaces.AirSyncBase + "Data")?.Value ?? "";
		Assert.Contains("Reply from B", replyBody);
	}

	[JmapMailFact]
	public async Task Jmap_ReadFlag_LandsOnBackend_AndDeleteMovesToTrash()
	{
		EasTestClient clientB = gateway.CreateJmapEasClient(TestBackend.User2);
		await clientB.HandshakeAsync();
		string inboxB = clientB.FolderOfType(EasFolderType.Inbox).ServerId;
		await clientB.InitialSyncAsync(inboxB);
		await clientB.PullAllAsync(inboxB);

		EasTestClient clientA = gateway.CreateJmapEasClient(TestBackend.User1);
		await clientA.HandshakeAsync();
		string subject = $"jmap-flags-{Guid.NewGuid():N}";
		await clientA.SendMailAsync(EasTestClient.BuildMime(
			TestBackend.User1, TestBackend.User2, subject, "flag test"));

		SyncItem item = await WaitUntil.ResultAsync(async () =>
			{
				SyncResult pull = await clientB.PullAllAsync(inboxB);
				return pull.Adds.FirstOrDefault(a =>
					a.ApplicationData.Element(Email + "Subject")?.Value == subject);
			}, $"delivery of '{subject}'");

		// Mark read via EAS(JMAP), verify \Seen via direct IMAP (same underlying store).
		SyncResult change = await clientB.ChangeItemAsync(inboxB, item.ServerId,
			new XElement(Email + "Read", "1"));
		Assert.Equal("1", change.Status);
		await WaitUntil.TrueAsync(
			() => ImapProbe.MessageHasFlagAsync(TestBackend.User2, "INBOX", subject, MessageFlags.Seen),
			$"\\Seen flag on '{subject}' via JMAP");

		// Delete via EAS(JMAP) → message must leave INBOX (gateway moves it to Trash).
		await clientB.DeleteItemAsync(inboxB, item.ServerId);
		await WaitUntil.TrueAsync(
			async () => !await ImapProbe.MessageExistsAsync(TestBackend.User2, "INBOX", subject),
			$"'{subject}' leaving INBOX via JMAP");
	}
}

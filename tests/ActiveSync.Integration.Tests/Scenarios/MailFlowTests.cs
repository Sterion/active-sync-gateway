using System.Xml.Linq;
using ActiveSync.Integration.Tests.Infrastructure;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;
using MailKit;

namespace ActiveSync.Integration.Tests.Scenarios;

/// <summary>
///   The flagship end-to-end scenario: two independent EAS clients (two users, two devices)
///   exchanging mail through the gateway → SMTP → backend delivery → IMAP → gateway → EAS.
/// </summary>
[Collection("gateway")]
[Trait("Category", "Integration")]
public class MailFlowTests(GatewayFixture gateway)
{
	private static readonly XNamespace Email = EasNamespaces.Email;

	[BackendFact]
	public async Task User1SendsMail_User2ReceivesIt_RepliesBack()
	{
		EasTestClient clientA = gateway.CreateEasClient(TestBackend.User1);
		EasTestClient clientB = gateway.CreateEasClient(TestBackend.User2);
		await clientA.HandshakeAsync();
		await clientB.HandshakeAsync();

		string inboxA = clientA.FolderOfType(EasFolderType.Inbox).ServerId;
		string inboxB = clientB.FolderOfType(EasFolderType.Inbox).ServerId;
		await clientA.InitialSyncAsync(inboxA);
		await clientB.InitialSyncAsync(inboxB);
		await clientA.PullAllAsync(inboxA); // drain any pre-existing mail
		await clientB.PullAllAsync(inboxB);

		// --- A → B ---
		string subject = $"e2e-{Guid.NewGuid():N}";
		XDocument? sendResult = await clientA.SendMailAsync(EasTestClient.BuildMime(
			TestBackend.User1, TestBackend.User2, subject, "Hello from client A over ActiveSync!"));
		Assert.Null(sendResult); // empty 200 = success

		SyncItem received = await WaitUntil.ResultAsync(async () =>
			{
				SyncResult pull = await clientB.PullAllAsync(inboxB);
				return pull.Adds.FirstOrDefault(a =>
					a.ApplicationData.Element(Email + "Subject")?.Value == subject);
			}, $"client B receiving '{subject}'");

		Assert.Contains(TestBackend.User1,
			received.ApplicationData.Element(Email + "From")?.Value ?? "");
		string? body = received.ApplicationData
			.Element(EasNamespaces.AirSyncBase + "Body")?
			.Element(EasNamespaces.AirSyncBase + "Data")?.Value;
		Assert.Contains("Hello from client A", body);

		// --- B replies (SmartReply references the received item) ---
		XDocument? replyResult = await clientB.SmartReplyAsync(
			EasTestClient.BuildMime(TestBackend.User2, TestBackend.User1, $"Re: {subject}", "Reply from B"),
			inboxB, received.ServerId);
		Assert.Null(replyResult);

		SyncItem reply = await WaitUntil.ResultAsync(async () =>
			{
				SyncResult pull = await clientA.PullAllAsync(inboxA);
				return pull.Adds.FirstOrDefault(a =>
					a.ApplicationData.Element(Email + "Subject")?.Value == $"Re: {subject}");
			}, $"client A receiving reply to '{subject}'");

		string replyBody = reply.ApplicationData
			.Element(EasNamespaces.AirSyncBase + "Body")?
			.Element(EasNamespaces.AirSyncBase + "Data")?.Value ?? "";
		Assert.Contains("Reply from B", replyBody);
		// SmartReply appends the original message below the reply text
		Assert.Contains("Hello from client A", replyBody);
	}

	[BackendFact]
	public async Task ReadFlag_SetViaEas_LandsOnImap_AndDeleteMovesToTrash()
	{
		EasTestClient clientB = gateway.CreateEasClient(TestBackend.User2);
		await clientB.HandshakeAsync();
		string inboxB = clientB.FolderOfType(EasFolderType.Inbox).ServerId;
		await clientB.InitialSyncAsync(inboxB);
		await clientB.PullAllAsync(inboxB);

		// Seed a message via a second gateway client (full pipeline, no shortcuts).
		EasTestClient clientA = gateway.CreateEasClient(TestBackend.User1);
		await clientA.HandshakeAsync();
		string subject = $"flags-{Guid.NewGuid():N}";
		await clientA.SendMailAsync(EasTestClient.BuildMime(
			TestBackend.User1, TestBackend.User2, subject, "flag test"));

		SyncItem item = await WaitUntil.ResultAsync(async () =>
			{
				SyncResult pull = await clientB.PullAllAsync(inboxB);
				return pull.Adds.FirstOrDefault(a =>
					a.ApplicationData.Element(Email + "Subject")?.Value == subject);
			}, $"delivery of '{subject}'");

		// Mark read via EAS, verify \Seen via direct IMAP.
		SyncResult change = await clientB.ChangeItemAsync(inboxB, item.ServerId,
			new XElement(Email + "Read", "1"));
		Assert.Equal("1", change.Status);

		await WaitUntil.TrueAsync(
			() => ImapProbe.MessageHasFlagAsync(TestBackend.User2, "INBOX", subject, MessageFlags.Seen),
			$"\\Seen flag on '{subject}'");

		// Delete via EAS → message must leave INBOX (gateway moves it to Trash).
		await clientB.DeleteItemAsync(inboxB, item.ServerId);
		await WaitUntil.TrueAsync(
			async () => !await ImapProbe.MessageExistsAsync(TestBackend.User2, "INBOX", subject),
			$"'{subject}' leaving INBOX");
	}

	[BackendFact]
	public async Task Delete_WithDeletesAsMovesZero_ExpungesInsteadOfMovingToTrash()
	{
		EasTestClient recipient = gateway.CreateEasClient(TestBackend.User2);
		await recipient.HandshakeAsync();
		string inbox = recipient.FolderOfType(EasFolderType.Inbox).ServerId;
		await recipient.InitialSyncAsync(inbox);
		await recipient.PullAllAsync(inbox);

		EasTestClient sender = gateway.CreateEasClient(TestBackend.User1);
		await sender.HandshakeAsync();
		string subject = $"perm-del-{Guid.NewGuid():N}";
		await sender.SendMailAsync(EasTestClient.BuildMime(
			TestBackend.User1, TestBackend.User2, subject, "permanent delete test"));

		SyncItem item = await WaitUntil.ResultAsync(async () =>
			{
				SyncResult pull = await recipient.PullAllAsync(inbox);
				return pull.Adds.FirstOrDefault(a =>
					a.ApplicationData.Element(Email + "Subject")?.Value == subject);
			}, $"delivery of '{subject}'");

		// DeletesAsMoves=0 asks for a permanent delete — the message must leave INBOX and
		// must NOT reappear in Trash.
		await recipient.DeleteItemAsync(inbox, item.ServerId, deletesAsMoves: false);
		await WaitUntil.TrueAsync(
			async () => !await ImapProbe.MessageExistsAsync(TestBackend.User2, "INBOX", subject),
			$"'{subject}' leaving INBOX");
		Assert.False(await ImapProbe.MessageExistsAsync(TestBackend.User2, "Trash", subject),
			"permanently deleted message must not be in Trash");
	}
}

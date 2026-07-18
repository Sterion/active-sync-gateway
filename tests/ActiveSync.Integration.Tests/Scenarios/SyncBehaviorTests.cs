using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using ActiveSync.Integration.Tests.Infrastructure;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;

namespace ActiveSync.Integration.Tests.Scenarios;

[Collection("gateway")]
[Trait("Category", "Integration")]
public class SyncBehaviorTests(GatewayFixture gateway)
{
	private static readonly XNamespace Email = EasNamespaces.Email;

	[BackendFact]
	public async Task EmptySync_FreshDevice_GetsStatus13()
	{
		EasTestClient client = gateway.CreateEasClient(TestBackend.User1);
		await client.HandshakeAsync();
		SyncResult result = await client.EmptySyncAsync(client.FolderOfType(EasFolderType.Inbox).ServerId);
		Assert.Equal("13", result.Status);
	}

	[BackendFact]
	public async Task EmptySync_AfterFullSync_ReplaysCachedRequest()
	{
		EasTestClient client = gateway.CreateEasClient(TestBackend.User1);
		await client.HandshakeAsync();
		string inbox = client.FolderOfType(EasFolderType.Inbox).ServerId;
		await client.InitialSyncAsync(inbox);
		await client.PullAllAsync(inbox);

		// Full Sync with a short heartbeat primes the request cache.
		await client.SyncAsync(inbox, heartbeatSeconds: 5);

		// Empty request must be replayed (empty 200 or a real response) — never status 13.
		SyncResult replayed = await client.EmptySyncAsync(inbox);
		Assert.NotEqual("13", replayed.Status);
	}

	[BackendFact]
	public async Task Ping_ReturnsChangedFolder_WhenMailArrives()
	{
		EasTestClient clientB = gateway.CreateEasClient(TestBackend.User2);
		await clientB.HandshakeAsync();
		string inboxB = clientB.FolderOfType(EasFolderType.Inbox).ServerId;
		await clientB.InitialSyncAsync(inboxB);
		await clientB.PullAllAsync(inboxB);

		EasTestClient clientA = gateway.CreateEasClient(TestBackend.User1);
		await clientA.HandshakeAsync();

		// Start the long-poll, then trigger a delivery while it is pending.
		Task<(string Status, List<string> ChangedFolders)> pingTask = clientB.PingAsync(30, inboxB);
		await Task.Delay(TimeSpan.FromSeconds(2));
		string subject = $"ping-{Guid.NewGuid():N}";
		await clientA.SendMailAsync(EasTestClient.BuildMime(
			TestBackend.User1, TestBackend.User2, subject, "wake up"));

		(string status, List<string> changed) = await pingTask;
		Assert.Equal("2", status);
		Assert.Contains(inboxB, changed);
	}

	[BackendFact]
	public async Task Ping_WakesAcrossConsecutiveRequests()
	{
		// The shared per-user IDLE watcher must survive request boundaries and re-arm
		// for the next Ping without a fresh connection setup.
		EasTestClient clientB = gateway.CreateEasClient(TestBackend.User2);
		await clientB.HandshakeAsync();
		string inboxB = clientB.FolderOfType(EasFolderType.Inbox).ServerId;
		await clientB.InitialSyncAsync(inboxB);
		await clientB.PullAllAsync(inboxB);

		EasTestClient clientA = gateway.CreateEasClient(TestBackend.User1);
		await clientA.HandshakeAsync();

		for (int cycle = 1; cycle <= 2; cycle++)
		{
			Task<(string Status, List<string> ChangedFolders)> pingTask = clientB.PingAsync(30, inboxB);
			await Task.Delay(TimeSpan.FromSeconds(2));
			string subject = $"cycle{cycle}-{Guid.NewGuid():N}";
			await clientA.SendMailAsync(EasTestClient.BuildMime(
				TestBackend.User1, TestBackend.User2, subject, "wake up again"));

			(string status, List<string> changed) = await pingTask;
			Assert.Equal("2", status);
			Assert.Contains(inboxB, changed);
			await clientB.PullAllAsync(inboxB); // drain so the next cycle starts clean
		}
	}

	[BackendFact]
	public async Task Ping_WakesOnNonInboxFolder()
	{
		// A Ping without INBOX gets a persistent IDLE watcher for its priority folder.
		EasTestClient client = gateway.CreateEasClient(TestBackend.User1);
		await client.HandshakeAsync();
		string drafts = client.FolderOfType(EasFolderType.Drafts).ServerId;
		await client.InitialSyncAsync(drafts);
		await client.PullAllAsync(drafts);

		Stopwatch stopwatch = Stopwatch.StartNew();
		Task<(string Status, List<string> ChangedFolders)> pingTask = client.PingAsync(60, drafts);
		await Task.Delay(TimeSpan.FromSeconds(2));
		await MailSeeder.AppendDraftAsync(TestBackend.User1, $"draft-{Guid.NewGuid():N}");

		(string status, List<string> changed) = await pingTask;
		stopwatch.Stop();

		Assert.Equal("2", status);
		Assert.Contains(drafts, changed);
		// The STATUS poll first ticks at 30 s; a wake well before that proves IDLE did it.
		Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20),
			$"Drafts ping should wake via IDLE, took {stopwatch.Elapsed}");
	}


	[BackendFact]
	public async Task MoveItems_ToTrash_ReturnsNewServerId()
	{
		EasTestClient clientB = gateway.CreateEasClient(TestBackend.User2);
		await clientB.HandshakeAsync();
		string inbox = clientB.FolderOfType(EasFolderType.Inbox).ServerId;
		string trash = clientB.FolderOfType(EasFolderType.DeletedItems).ServerId;
		await clientB.InitialSyncAsync(inbox);
		await clientB.PullAllAsync(inbox);

		EasTestClient clientA = gateway.CreateEasClient(TestBackend.User1);
		await clientA.HandshakeAsync();
		string subject = $"move-{Guid.NewGuid():N}";
		await clientA.SendMailAsync(EasTestClient.BuildMime(
			TestBackend.User1, TestBackend.User2, subject, "move me"));

		SyncItem item = await WaitUntil.ResultAsync(async () =>
			{
				SyncResult pull = await clientB.PullAllAsync(inbox);
				return pull.Adds.FirstOrDefault(a =>
					a.ApplicationData.Element(Email + "Subject")?.Value == subject);
			}, $"delivery of '{subject}'");

		XDocument? response = await clientB.MoveItemsAsync(item.ServerId, inbox, trash);
		XNamespace M = EasNamespaces.Move;
		XElement? move = response?.Root?.Element(M + "Response");
		Assert.NotNull(move);
		Assert.Equal("3", move.Element(M + "Status")?.Value); // 3 = success for MoveItems
		string? dstMsgId = move.Element(M + "DstMsgId")?.Value;
		Assert.False(string.IsNullOrEmpty(dstMsgId));
		Assert.StartsWith($"{trash}:", dstMsgId);
	}

	[BackendFact]
	public async Task Attachment_FetchedViaItemOperations_AndLegacyGetAttachment()
	{
		EasTestClient clientB = gateway.CreateEasClient(TestBackend.User2);
		await clientB.HandshakeAsync();
		string inbox = clientB.FolderOfType(EasFolderType.Inbox).ServerId;
		await clientB.InitialSyncAsync(inbox);
		await clientB.PullAllAsync(inbox);

		string payload = "attachment-payload-" + Guid.NewGuid().ToString("N");
		string subject = $"att-{Guid.NewGuid():N}";
		string boundary = "b" + Guid.NewGuid().ToString("N");
		string mime =
			$"From: {TestBackend.User1}\r\nTo: {TestBackend.User2}\r\nSubject: {subject}\r\n" +
			$"MIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=\"{boundary}\"\r\n\r\n" +
			$"--{boundary}\r\nContent-Type: text/plain\r\n\r\nsee attachment\r\n" +
			$"--{boundary}\r\nContent-Type: application/octet-stream; name=\"data.bin\"\r\n" +
			$"Content-Disposition: attachment; filename=\"data.bin\"\r\n" +
			$"Content-Transfer-Encoding: base64\r\n\r\n" +
			Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)) +
			$"\r\n--{boundary}--\r\n";

		EasTestClient clientA = gateway.CreateEasClient(TestBackend.User1);
		await clientA.HandshakeAsync();
		await clientA.SendMailAsync(mime);

		XNamespace ASB = EasNamespaces.AirSyncBase;
		SyncItem item = await WaitUntil.ResultAsync(async () =>
			{
				SyncResult pull = await clientB.PullAllAsync(inbox);
				return pull.Adds.FirstOrDefault(a =>
					a.ApplicationData.Element(Email + "Subject")?.Value == subject);
			}, $"delivery of '{subject}'");

		string? fileReference = item.ApplicationData
			.Element(ASB + "Attachments")?
			.Element(ASB + "Attachment")?
			.Element(ASB + "FileReference")?.Value;
		Assert.False(string.IsNullOrEmpty(fileReference), "attachment missing FileReference");

		(string status, string? contentType, byte[]? data) = await clientB.FetchAttachmentAsync(fileReference!);
		Assert.Equal("1", status);
		Assert.NotNull(data);
		Assert.Equal(payload, Encoding.UTF8.GetString(data));
		Assert.Contains("octet-stream", contentType);

		byte[] legacyBytes = await clientB.GetAttachmentAsync(fileReference!);
		Assert.Equal(payload, Encoding.UTF8.GetString(legacyBytes));
	}
}

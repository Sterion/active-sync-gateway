using System.Diagnostics;
using System.Xml.Linq;
using ActiveSync.Integration.Tests.Infrastructure;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;

namespace ActiveSync.Integration.Tests.Scenarios;

/// <summary>
///   Proves the exact pending-change watchdog catches backend changes that the IDLE/STATUS
///   watchers cannot see: changes that predate the watch (baseline race) and changes the
///   server never announces (missed IDLE + STATUS-counter-blind flag flips).
/// </summary>
[Collection("gateway")]
[Trait("Category", "Integration")]
public class WatchdogTests(GatewayFixture gateway)
{
	private static readonly XNamespace Email = EasNamespaces.Email;

	[BackendFact]
	public async Task Ping_DetectsFlagChangeThatPredatesTheWatch()
	{
		EasTestClient client = gateway.CreateEasClient(TestBackend.User1);
		await client.HandshakeAsync();
		string inbox = client.FolderOfType(EasFolderType.Inbox).ServerId;
		await client.InitialSyncAsync(inbox);
		await client.PullAllAsync(inbox);

		// Seed a message and sync it, so the device snapshot records it as unseen.
		string subject = $"wd-entry-{Guid.NewGuid():N}";
		EasTestClient sender = gateway.CreateEasClient(TestBackend.User2);
		await sender.HandshakeAsync();
		await sender.SendMailAsync(EasTestClient.BuildMime(
			TestBackend.User2, TestBackend.User1, subject, "watchdog entry check"));
		await WaitUntil.ResultAsync(async () =>
			{
				SyncResult pull = await client.PullAllAsync(inbox);
				return pull.Adds.FirstOrDefault(a =>
					a.ApplicationData.Element(Email + "Subject")?.Value == subject);
			}, $"delivery of '{subject}'");

		// Flip the flag BEFORE the Ping starts: the watchers baseline "now", so only the
		// exact entry re-check can see this change. Without it the Ping would sit for the
		// full heartbeat.
		await ImapProbe.SetSeenAsync(TestBackend.User1, subject, true);

		Stopwatch stopwatch = Stopwatch.StartNew();
		(string status, List<string> changed) = await client.PingAsync(60, inbox);
		stopwatch.Stop();

		Assert.Equal("2", status);
		Assert.Contains(inbox, changed);
		Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
			$"entry check should answer immediately, took {stopwatch.Elapsed}");
	}

	[BackendFact]
	public async Task Ping_WatchdogCatchesStatusBlindChange()
	{
		// This gateway instance has IDLE disabled and a 15 s watchdog.
		EasTestClient client = gateway.CreateWatchdogEasClient(TestBackend.User2);
		await client.HandshakeAsync();
		string inbox = client.FolderOfType(EasFolderType.Inbox).ServerId;
		await client.InitialSyncAsync(inbox);
		await client.PullAllAsync(inbox);

		// Seed two messages; mark B seen up front so the flips below cancel out.
		string subjectA = $"wd-blind-a-{Guid.NewGuid():N}";
		string subjectB = $"wd-blind-b-{Guid.NewGuid():N}";
		EasTestClient sender = gateway.CreateEasClient(TestBackend.User1);
		await sender.HandshakeAsync();
		HashSet<string> seen = new();
		await sender.SendMailAsync(EasTestClient.BuildMime(
			TestBackend.User1, TestBackend.User2, subjectA, "watchdog blind A"));
		await sender.SendMailAsync(EasTestClient.BuildMime(
			TestBackend.User1, TestBackend.User2, subjectB, "watchdog blind B"));
		await WaitUntil.TrueAsync(async () =>
		{
			SyncResult pull = await client.PullAllAsync(inbox);
			foreach (SyncItem add in pull.Adds)
			{
				string? s = add.ApplicationData.Element(Email + "Subject")?.Value;
				if (s is not null)
					seen.Add(s);
			}

			return seen.Contains(subjectA) && seen.Contains(subjectB);
		}, "delivery of both watchdog messages");

		await ImapProbe.SetSeenAsync(TestBackend.User2, subjectB, true);
		// Pull until the B-seen change lands in the device snapshot, so the flips below
		// are the only pending difference once the Ping starts.
		await WaitUntil.TrueAsync(async () =>
		{
			SyncResult pull = await client.PullAllAsync(inbox);
			return pull.Changes.Any(c =>
				c.ApplicationData.Element(Email + "Subject")?.Value == subjectB);
		}, "B seen-flag to reach the device snapshot");

		Stopwatch stopwatch = Stopwatch.StartNew();
		Task<(string Status, List<string> ChangedFolders)> pingTask = client.PingAsync(60, inbox);
		await Task.Delay(TimeSpan.FromSeconds(2));

		// A → seen and B → unseen: message count, UIDNEXT and unread count are all
		// unchanged, so the STATUS poller is blind and IDLE is off. Only the watchdog's
		// exact revision diff can detect this.
		await ImapProbe.SetSeenAsync(TestBackend.User2, subjectA, true);
		await ImapProbe.SetSeenAsync(TestBackend.User2, subjectB, false);

		(string status, List<string> changed) = await pingTask;
		stopwatch.Stop();

		Assert.Equal("2", status);
		Assert.Contains(inbox, changed);
		// Watchdog ticks at 15 s; the (blind) STATUS poll first runs at 30 s. Finishing
		// well before 30 s proves the watchdog did the detecting.
		Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(28),
			$"watchdog should fire at ~15 s, took {stopwatch.Elapsed}");
	}
}

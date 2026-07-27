using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Core.State;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;
using ActiveSync.Server.Eas;
using ActiveSync.Server.Eas.Handlers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Server.Tests;

/// <summary>
///   Round-3 review item 1 — F2/F3/K2: a server→client item whose render fails must never be
///   recorded into the persisted snapshot as delivered (F3/K2), and a collection that skipped an
///   item this round must not be offered to the long-poll wait, which would otherwise spin the
///   watchdog against the same permanently-failing item every interval (F2).
/// </summary>
public sealed class SyncLostChangeTests : IDisposable
{
	private static readonly XNamespace AS = EasNamespaces.AirSync;
	private static readonly XNamespace ASB = EasNamespaces.AirSyncBase;

	private readonly EasHandlerHarness _harness = new();

	public void Dispose()
	{
		_harness.Dispose();
	}

	private SyncHandler NewSyncHandler()
	{
		return new SyncHandler(
			_harness.Folders,
			TestOptionsMonitor.SnapshotOf(_harness.Options),
			new StubLifetime(),
			new MeetingInvitationService(NullLogger<MeetingInvitationService>.Instance),
			NullLogger<SyncHandler>.Instance);
	}

	private async Task<UserFolder> RegisterInboxAsync()
	{
		List<UserFolder> folders = await _harness.RegisterFoldersAsync(
			new BackendFolder("imap:INBOX", "Inbox", null, EasFolderType.Inbox, EasClass.Email));
		return folders.Single(f => f.BackendKey == "imap:INBOX");
	}

	private static XDocument SyncRequest(string collectionId, string syncKey)
	{
		return new XDocument(new XElement(AS + "Sync",
			new XElement(AS + "Collections",
				new XElement(AS + "Collection",
					new XElement(AS + "SyncKey", syncKey),
					new XElement(AS + "CollectionId", collectionId)))));
	}

	// ---- F3/K2: a Change whose render fails must not be recorded as delivered ----
	[Fact]
	public async Task F3_ChangeRenderFailure_IsReofferedOnNextRound_NotLostForever()
	{
		UserFolder inbox = await RegisterInboxAsync();
		// The default ItemApplicationData (an airsync:Subject) has no WBXML token and is deliberately
		// unencodable (see EasHandlerHarness); this test round-trips the response through WBXML, so
		// it needs an encodable body.
		_harness.Session.Store.ItemApplicationData = _ =>
			[new XElement(ASB + "Body", new XElement(ASB + "Type", "1"), new XElement(ASB + "Data", "preview"))];
		SyncHandler handler = NewSyncHandler();

		Device device = await _harness.State.GetOrCreateDeviceAsync(
			_harness.UserId, "TESTDEVICE01", "TestClient", CancellationToken.None);
		(_, CollectionState? state) = await _harness.State.ValidateSyncKeyAsync(
			device, inbox.ServerId, "0", CancellationToken.None);
		await _harness.State.CommitCollectionStateAsync(
			state!, new Dictionary<string, string> { ["10"] = "old10", ["20"] = "old20" }, 0,
			SyncKeyValidation.Initial, CancellationToken.None);

		// Both items changed on the backend this round; "20"'s render fails (VanishedKeys stands in
		// for BuildItemElementAsync returning null — an unparseable message, a converter throw, …).
		_harness.Session.Store.Revisions["10"] = "new10";
		_harness.Session.Store.Revisions["20"] = "new20";
		_harness.Session.Store.VanishedKeys.Add("20");

		XDocument? round1 = await _harness.RunAsync(handler, "Sync", SyncRequest(inbox.ServerId, "1"));
		Assert.NotNull(round1);
		XElement collection1 = round1!.Root!.Element(AS + "Collections")!.Element(AS + "Collection")!;
		List<XElement> changes1 = collection1.Element(AS + "Commands")?.Elements(AS + "Change").ToList() ?? [];
		// Only "10" actually rendered this round — "20" must be silently skipped, not sent broken.
		Assert.Single(changes1);
		Assert.EndsWith(":10", changes1[0].Element(AS + "ServerId")!.Value);

		string nextKey = collection1.Element(AS + "SyncKey")!.Value;

		// The transient failure clears; the backend's revision of "20" is unchanged from round 1 —
		// if round 1 wrongly recorded "20" as delivered, round 2's diff will see no difference and
		// never re-offer it.
		_harness.Session.Store.VanishedKeys.Remove("20");

		XDocument? round2 = await _harness.RunAsync(handler, "Sync", SyncRequest(inbox.ServerId, nextKey));

		// F3 (unfixed): round 1 recorded newSnapshot["20"] = "new20" even though no Change was ever
		// sent, so round 2's diff considers "20" already synced and round2 is the canonical empty
		// "no changes" answer (null). Fixed: "20" is still pending and gets (re-)offered here.
		Assert.NotNull(round2);
		XElement collection2 = round2!.Root!.Element(AS + "Collections")!.Element(AS + "Collection")!;
		XElement? change20 = collection2.Element(AS + "Commands")?.Elements(AS + "Change")
			.FirstOrDefault(c => c.Element(AS + "ServerId")!.Value.EndsWith(":20"));
		Assert.NotNull(change20);
	}

	// ---- F2: a collection with a skipped item must not be offered to the long-poll wait ----
	[Fact]
	public async Task F2_ItemSkippedThisRound_CollectionNotOfferedToLongPollWait()
	{
		UserFolder inbox = await RegisterInboxAsync();
		SyncHandler handler = NewSyncHandler();

		Device device = await _harness.State.GetOrCreateDeviceAsync(
			_harness.UserId, "TESTDEVICE01", "TestClient", CancellationToken.None);
		(_, CollectionState? state) = await _harness.State.ValidateSyncKeyAsync(
			device, inbox.ServerId, "0", CancellationToken.None);
		await _harness.State.CommitCollectionStateAsync(
			state!, new Dictionary<string, string>(), 0, SyncKeyValidation.Initial, CancellationToken.None);

		// The backend reports one new item whose render permanently fails: the round has nothing to
		// report (no payload) AND an item was skipped.
		_harness.Session.Store.Revisions["10"] = "r1";
		_harness.Session.Store.VanishedKeys.Add("10");

		int waitCalls = 0;
		// Reports "changed" the instant it is asked — if the collection is (wrongly) handed to the
		// long-poll wait, this resolves practically instantly rather than hanging the test for the
		// full heartbeat, so a red run fails fast rather than timing out.
		_harness.Session.Store.WaitForChanges = keys =>
		{
			waitCalls++;
			return keys;
		};

		XDocument request = new(new XElement(AS + "Sync",
			new XElement(AS + "HeartbeatInterval", "60"),
			new XElement(AS + "Collections",
				new XElement(AS + "Collection",
					new XElement(AS + "SyncKey", "1"),
					new XElement(AS + "CollectionId", inbox.ServerId)))));

		await _harness.RunAsync(handler, "Sync", request);

		// F2 (unfixed): ProcessCollectionAsync still marks this collection Waitable even though an
		// item was skipped, so SyncHandler hands it to WaitWithWatchdogAsync, which immediately calls
		// WaitForChangesAsync against the backend (and would do so every interval in production,
		// forever, against an item that can never succeed). Fixed: a collection with a skipped item
		// is never handed to the waiter.
		Assert.Equal(0, waitCalls);
	}

	private sealed class StubLifetime : IHostApplicationLifetime
	{
		public CancellationToken ApplicationStarted => CancellationToken.None;
		public CancellationToken ApplicationStopping => CancellationToken.None;
		public CancellationToken ApplicationStopped => CancellationToken.None;

		public void StopApplication()
		{
		}
	}
}

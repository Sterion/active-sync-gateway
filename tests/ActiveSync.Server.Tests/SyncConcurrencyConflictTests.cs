using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Core.State;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;
using ActiveSync.Server.Eas;
using ActiveSync.Server.Eas.Handlers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Server.Tests;

/// <summary>
///   A concurrent commit race on one collection's <see cref="CollectionState" /> row
///   (two pipelined Sync requests for the same device/collection) must answer that ONE collection
///   with a retryable status, not escape as an unhandled exception that discards every OTHER
///   collection's already-computed response in the same request.
/// </summary>
public sealed class SyncConcurrencyConflictTests : IDisposable
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

	[Fact]
	public async Task Sync_ConcurrentCollectionCommitRace_ReturnsStatus5ForThatCollection_SiblingsUnaffected()
	{
		List<UserFolder> registry = await _harness.RegisterFoldersAsync(
			new BackendFolder("imap:INBOX", "Inbox", null, EasFolderType.Inbox, EasClass.Email),
			new BackendFolder("imap:Sent", "Sent", null, EasFolderType.SentItems, EasClass.Email));
		UserFolder inbox = registry.Single(f => f.BackendKey == "imap:INBOX");
		UserFolder sent = registry.Single(f => f.BackendKey == "imap:Sent");

		SyncHandler handler = NewSyncHandler();

		// Prime both collections to key 1 (initial sync, empty snapshot).
		await _harness.RunAsync(handler, "Sync", TwoCollectionRequest(inbox.ServerId, "0", sent.ServerId, "0"));

		// Simulate a pipelined sibling request that already committed a new generation for the
		// INBOX collection's CollectionState row, off the same connection but a different
		// DbContext — the exact race CommitCollectionStateAsync's DbUpdateConcurrencyException
		// guard exists to catch.
		Device device = await _harness.State.GetOrCreateDeviceAsync(
			_harness.UserId, "TESTDEVICE01", "TestClient", CancellationToken.None);
		await using SqliteSyncDbContext racer = _harness.NewDbContext();
		CollectionState racedState = await racer.CollectionStates
			.FirstAsync(s => s.DeviceKey == device.Id && s.CollectionId == inbox.ServerId);
		EntityEntry<CollectionState> entry = racer.Entry(racedState);
		entry.State = EntityState.Modified; // force a real UPDATE so the token is re-stamped
		await racer.SaveChangesAsync();

		// A genuine change on BOTH collections this round, so both attempt CommitCollectionStateAsync
		// (a collection with nothing to report never reaches the commit at all). An encodable body,
		// since the sibling collection's Add is expected to render and go out over WBXML this time.
		_harness.Session.Store.ItemApplicationData = _ =>
			[new XElement(ASB + "Body", new XElement(ASB + "Type", "1"), new XElement(ASB + "Data", "preview"))];
		_harness.Session.Store.Revisions["10"] = "a";

		XDocument? response = await _harness.RunAsync(handler, "Sync",
			TwoCollectionRequest(inbox.ServerId, "1", sent.ServerId, "1"));

		Assert.NotNull(response);
		List<XElement> collections = response!.Root!.Element(AS + "Collections")!
			.Elements(AS + "Collection").ToList();

		// The raced collection: a retryable status, not a 500-worthy unhandled exception — and the
		// client keeps its current key (status 5 is not the "restart from 0" status 3).
		XElement inboxResponse = collections.Single(c => c.Element(AS + "CollectionId")?.Value == inbox.ServerId);
		Assert.Equal("5", inboxResponse.Element(AS + "Status")?.Value);
		Assert.Equal("1", inboxResponse.Element(AS + "SyncKey")?.Value);

		// The sibling collection's own response must still be present — the race on INBOX must not
		// have discarded it.
		XElement sentResponse = collections.Single(c => c.Element(AS + "CollectionId")?.Value == sent.ServerId);
		Assert.Equal("1", sentResponse.Element(AS + "Status")?.Value);
	}

	private static XDocument TwoCollectionRequest(
		string collectionId1, string syncKey1, string collectionId2, string syncKey2)
	{
		return new XDocument(new XElement(AS + "Sync",
			new XElement(AS + "Collections",
				new XElement(AS + "Collection",
					new XElement(AS + "SyncKey", syncKey1),
					new XElement(AS + "CollectionId", collectionId1)),
				new XElement(AS + "Collection",
					new XElement(AS + "SyncKey", syncKey2),
					new XElement(AS + "CollectionId", collectionId2)))));
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

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
///   GetItemEstimate (MS-ASCMD 2.2.1.9) status-code conformance: an unprimed collection (key 0)
///   is status 3 (SYNCSTATENOTPRIMED) without estimating; a stale/mismatched/unparseable key is
///   status 4 (INVALIDSYNCKEY) — a previous version had the two swapped, see the note on
///   <see cref="StaleOrMismatchedSyncKey_ReportsStatus4" />; a flaky backend during the revision
///   listing degrades that one collection to status 2 rather than 500-ing the whole request.
/// </summary>
public sealed class GetItemEstimateTests : IDisposable
{
	private static readonly XNamespace GIE = EasNamespaces.GetItemEstimate;
	private static readonly XNamespace AS = EasNamespaces.AirSync;

	private readonly EasHandlerHarness _harness = new();

	public void Dispose()
	{
		_harness.Dispose();
	}

	// The collection has never completed a Sync round (key 0) — there is no
	// snapshot to diff against, so MS-ASCMD Status 3 (SYNCSTATENOTPRIMED) tells the client to run
	// an initial Sync first, rather than silently estimating against an empty baseline (which
	// would report every item on the backend as "new" — a meaningless number for a client that
	// has never synced).
	[Fact]
	public async Task UnprimedInitialKey_ReportsStatus3_WithoutEstimating()
	{
		UserFolder inbox = await InboxAsync();
		_harness.Session.Store.Revisions["10"] = "a";
		_harness.Session.Store.Revisions["11"] = "b";

		XDocument? response = await RunAsync(inbox.ServerId, "0");

		XElement? resp = response?.Root?.Element(GIE + "Response");
		Assert.Equal("3", resp?.Element(GIE + "Status")?.Value);
		Assert.Null(resp?.Element(GIE + "Estimate"));
	}

	// Corrects a previous version that read MS-ASCMD's GetItemEstimate
	// status table by analogy to the unrelated Sync command's table and landed the two swapped:
	// a stale/mismatched/unparseable key was answered 3, and this test used to assert exactly
	// that. Confirmed against the published MS-ASCMD GetItemEstimate Status element: 3 is
	// SYNCSTATENOTPRIMED (see the Initial test above), 4 is INVALIDSYNCKEY — this case.
	[Fact]
	public async Task StaleOrMismatchedSyncKey_ReportsStatus4()
	{
		UserFolder inbox = await InboxAsync();

		XDocument? response = await RunAsync(inbox.ServerId, "5"); // nonzero key, no primed state

		XElement? resp = response?.Root?.Element(GIE + "Response");
		Assert.Equal("4", resp?.Element(GIE + "Status")?.Value);
	}

	// A single flaky store must not take down the multi-collection
	// request. The endpoint has a catch-all that would turn an unguarded throw into HTTP 500; the
	// handler must catch it and report status 2 for that collection instead (matching
	// SyncHandler's per-collection path).
	[Fact]
	public async Task BackendListingFailure_ReportsStatus2_DoesNotThrow()
	{
		UserFolder inbox = await InboxAsync();

		// GetItemEstimate's own key-0 handling now short-circuits to Status 3
		// without ever reaching the listing, so exercising the listing failure needs a real,
		// primed (Current) key — commit one via an actual Sync round first.
		await _harness.RunAsync(NewSyncHandler(), "Sync", new XDocument(
			new XElement(AS + "Sync",
				new XElement(AS + "Collections",
					new XElement(AS + "Collection",
						new XElement(AS + "SyncKey", "0"),
						new XElement(AS + "CollectionId", inbox.ServerId))))));

		_harness.Session.Store.GetRevisionsFailWith = () => new BackendException("listing blew up");

		XDocument? response = await RunAsync(inbox.ServerId, "1"); // primed key → reaches the listing

		XElement? resp = response?.Root?.Element(GIE + "Response");
		Assert.Equal("2", resp?.Element(GIE + "Status")?.Value);
	}

	// A 12.1 client identifies a collection by Class + CollectionId (mirroring the
	// deliberate EchoClassIfLegacy handling in SyncHandler.Collection.cs), but the response never
	// carried Class at all.
	[Fact]
	public async Task LegacyClient_ReportsClassAlongsideCollectionId()
	{
		UserFolder inbox = await InboxAsync();
		_harness.Session.Store.Revisions["10"] = "a";

		// Prime the collection with a real Sync round first (key 0 -> 1) so GetItemEstimate reaches
		// the estimate path rather than the unprimed Status 3 short-circuit.
		await _harness.RunAsync(NewSyncHandler(), "Sync", new XDocument(
			new XElement(AS + "Sync",
				new XElement(AS + "Collections",
					new XElement(AS + "Collection",
						new XElement(AS + "SyncKey", "0"),
						new XElement(AS + "CollectionId", inbox.ServerId))))));

		XDocument? response = await _harness.RunAsync(
			new GetItemEstimateHandler(_harness.Folders, NullLogger<GetItemEstimateHandler>.Instance),
			"GetItemEstimate",
			new XDocument(new XElement(GIE + "GetItemEstimate",
				new XElement(GIE + "Collections",
					new XElement(GIE + "Collection",
						new XElement(AS + "SyncKey", "1"),
						new XElement(GIE + "CollectionId", inbox.ServerId))))),
			protocolVersion: "12.1");

		XElement? collection = response?.Root?.Element(GIE + "Response")?.Element(GIE + "Collection");
		Assert.Equal("1", response?.Root?.Element(GIE + "Response")?.Element(GIE + "Status")?.Value);
		Assert.Equal(EasClass.Email, collection?.Element(GIE + "Class")?.Value);
	}

	private async Task<UserFolder> InboxAsync()
	{
		List<UserFolder> registry = await _harness.RegisterFoldersAsync(
			new BackendFolder { BackendKey = "imap:INBOX", DisplayName = "Inbox", Type = FolderType.Inbox, EasClass = EasClass.Email });
		return registry.Single();
	}

	private Task<XDocument?> RunAsync(string collectionId, string syncKey)
	{
		return _harness.RunAsync(
			new GetItemEstimateHandler(_harness.Folders, NullLogger<GetItemEstimateHandler>.Instance),
			"GetItemEstimate",
			new XDocument(new XElement(GIE + "GetItemEstimate",
				new XElement(GIE + "Collections",
					new XElement(GIE + "Collection",
						new XElement(AS + "SyncKey", syncKey),
						new XElement(AS + "CollectionId", collectionId))))));
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

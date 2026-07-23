using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Core.State;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;
using ActiveSync.Server.Eas;
using ActiveSync.Server.Eas.Handlers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Server.Tests;

/// <summary>
///   Item 32 — Sync command protocol conformance (Area F: F1, F4, F5, F6, F7, F8, F9). Drives the
///   whole Sync handler through the production WBXML codec so the request each test posts is the
///   one a device would send.
/// </summary>
public sealed class SyncConformanceTests : IDisposable
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

	/// <summary>A minimal Sync request for one collection at the given sync key.</summary>
	private static XDocument SyncRequest(string collectionId, string syncKey, params XElement[] extra)
	{
		XElement collection = new(AS + "Collection",
			new XElement(AS + "SyncKey", syncKey),
			new XElement(AS + "CollectionId", collectionId));
		collection.Add(extra);
		return new XDocument(new XElement(AS + "Sync", new XElement(AS + "Collections", collection)));
	}

	private async Task<UserFolder> RegisterInboxAsync()
	{
		List<UserFolder> folders = await _harness.RegisterFoldersAsync(
			new BackendFolder("imap:INBOX", "Inbox", null, EasFolderType.Inbox, EasClass.Email));
		return folders.Single(f => f.BackendKey == "imap:INBOX");
	}

	// ---- F1: <Sync/> carrying a root but no Collections must replay, not answer Status 13 ----
	[Fact]
	public async Task F1_SyncWithRootButNoCollections_Replays_DoesNotReturnStatus13()
	{
		UserFolder inbox = await RegisterInboxAsync();
		SyncHandler handler = NewSyncHandler();

		// Prime: an initial Sync (key 0, GetChanges suppressed) creates the collection state
		// (→ key 1) and caches the replayable request shape on the Device row.
		await _harness.RunAsync(handler, "Sync",
			SyncRequest(inbox.ServerId, "0", new XElement(AS + "GetChanges", "0")));

		// The client re-sends the documented "repeat my previous request" idiom, but as a valid
		// WBXML body that is just the <Sync/> root with no <Collections> — the shape F1 rejects.
		XDocument? response = await _harness.RunAsync(handler, "Sync", new XDocument(new XElement(AS + "Sync")));

		// Before the fix this is a bare <Sync><Status>13</Status></Sync>. After the fix the cached
		// request is replayed; with no changes to report the collection round is silent, which is
		// the canonical empty-body "no changes" answer — i.e. NOT Status 13.
		Assert.Null(response);
	}

	// A control proving the assertion above discriminates: with no cache primed, the same
	// no-Collections body legitimately still answers Status 13 ("no cache available").
	[Fact]
	public async Task F1_SyncWithRootButNoCollections_NoCache_StillReturnsStatus13()
	{
		await RegisterInboxAsync();
		SyncHandler handler = NewSyncHandler();

		XDocument? response = await _harness.RunAsync(handler, "Sync", new XDocument(new XElement(AS + "Sync")));

		Assert.NotNull(response);
		Assert.Equal("13", response!.Root!.Element(AS + "Status")?.Value);
	}

	// ---- F4: Status 3 must reset the client's sync key to 0, not echo the rejected one ----
	[Fact]
	public async Task F4_InvalidSyncKey_Status3_ResetsSyncKeyToZero()
	{
		UserFolder inbox = await RegisterInboxAsync();
		SyncHandler handler = NewSyncHandler();

		// Prime the collection so it exists at key 1; then present a key that is neither current
		// nor the one-behind replay key → SyncKeyValidation.Invalid → Status 3.
		await _harness.RunAsync(handler, "Sync",
			SyncRequest(inbox.ServerId, "0", new XElement(AS + "GetChanges", "0")));

		XDocument? response = await _harness.RunAsync(handler, "Sync", SyncRequest(inbox.ServerId, "99"));

		Assert.NotNull(response);
		XElement collection = response!.Root!.Element(AS + "Collections")!.Element(AS + "Collection")!;
		Assert.Equal("3", collection.Element(AS + "Status")?.Value);
		// Echoing "99" back would make a trusting client resend the same rejected key (resync loop);
		// Exchange resets it to 0 so the client restarts from an initial sync.
		Assert.Equal("0", collection.Element(AS + "SyncKey")?.Value);
	}

	// ---- F5: an out-of-range HeartbeatInterval is answered with Status 14 + Limit, not clamped ----
	[Fact]
	public async Task F5_HeartbeatBelowMinimum_ReturnsStatus14AndLimit()
	{
		UserFolder inbox = await RegisterInboxAsync();
		SyncHandler handler = NewSyncHandler();
		await _harness.RunAsync(handler, "Sync",
			SyncRequest(inbox.ServerId, "0", new XElement(AS + "GetChanges", "0")));

		// So that the UNMODIFIED code's long-poll wait (which clamps 30s → 60s and would otherwise
		// block for a full minute) resolves instantly instead: report a change the moment it waits.
		_harness.Session.Store.WaitForChanges = keys => keys;

		// HeartbeatInterval 30 is below the 60 s minimum.
		XDocument request = new(new XElement(AS + "Sync",
			new XElement(AS + "HeartbeatInterval", "30"),
			new XElement(AS + "Collections",
				new XElement(AS + "Collection",
					new XElement(AS + "SyncKey", "1"),
					new XElement(AS + "CollectionId", inbox.ServerId),
					new XElement(AS + "GetChanges", "0")))));

		XDocument? response = await _harness.RunAsync(handler, "Sync", request);

		Assert.NotNull(response);
		Assert.Equal("14", response!.Root!.Element(AS + "Status")?.Value);
		// Limit teaches the client the correct bound, in the unit it used (HeartbeatInterval = seconds).
		Assert.Equal("60", response.Root.Element(AS + "Limit")?.Value);
	}

	// ---- F6: MIMESupport=2 must select the Type-4 (MIME) body, not downgrade to HTML ----
	[Fact]
	public void F6_MimeSupportAlways_SelectsMimeBodyOverHtml()
	{
		// The client offers both HTML (2) and MIME (4) and asks for MIME on every message.
		XElement optionsElement = new(AS + "Options",
			new XElement(AS + "MIMESupport", "2"),
			new XElement(ASB + "BodyPreference", new XElement(ASB + "Type", "2")),
			new XElement(ASB + "BodyPreference", new XElement(ASB + "Type", "4")));

		SyncCollectionOptions resolved = SyncCollectionOptions.Resolve(optionsElement, new CollectionState { CollectionId = "1" });

		// The HTML-first ladder would pick 2 and S/MIME could never be verified on-device.
		Assert.Equal(4, resolved.BodyType);
		// The MIMESupport value is threaded through so it persists with the body type.
		Assert.Equal(2, resolved.MimeSupport);
	}

	// MIMESupport=0 (never) keeps the historical HTML-first behaviour even when MIME is offered.
	[Fact]
	public void F6_MimeSupportNever_KeepsHtmlLadder()
	{
		XElement optionsElement = new(AS + "Options",
			new XElement(ASB + "BodyPreference", new XElement(ASB + "Type", "2")),
			new XElement(ASB + "BodyPreference", new XElement(ASB + "Type", "4")));

		SyncCollectionOptions resolved = SyncCollectionOptions.Resolve(optionsElement, new CollectionState { CollectionId = "1" });

		Assert.Equal(2, resolved.BodyType);
		Assert.Equal(0, resolved.MimeSupport);
	}

	// ---- F7: a client Change to an item the backend moved on must return Status 7, not overwrite ----
	[Fact]
	public async Task F7_ClientChangeAgainstMovedBackend_ReturnsStatus7_DoesNotOverwrite()
	{
		UserFolder inbox = await RegisterInboxAsync();
		SyncHandler handler = NewSyncHandler();

		// Prime the collection at key 1 with the client's last-acked revision of item "10".
		Device device = await _harness.State.GetOrCreateDeviceAsync(
			EasHandlerHarness.UserName, "TESTDEVICE01", "TestClient", CancellationToken.None);
		(_, CollectionState? state) = await _harness.State.ValidateSyncKeyAsync(
			device, inbox.ServerId, "0", CancellationToken.None);
		await _harness.State.CommitCollectionStateAsync(
			state!, new Dictionary<string, string> { ["10"] = "old" }, 0, SyncKeyValidation.Initial,
			CancellationToken.None);

		// The backend has since moved on: its current revision of item "10" differs from what the
		// client last acked → a concurrent edit.
		_harness.Session.Store.Revisions["10"] = "new";

		XDocument request = new(new XElement(AS + "Sync",
			new XElement(AS + "Collections",
				new XElement(AS + "Collection",
					new XElement(AS + "SyncKey", "1"),
					new XElement(AS + "CollectionId", inbox.ServerId),
					new XElement(AS + "GetChanges", "0"),
					new XElement(AS + "Commands",
						new XElement(AS + "Change",
							new XElement(AS + "ServerId", $"{inbox.ServerId}:10"),
							new XElement(AS + "ApplicationData",
								new XElement(EasNamespaces.Email + "Read", "1"))))))));

		XDocument? response = await _harness.RunAsync(handler, "Sync", request);

		Assert.NotNull(response);
		XElement collection = response!.Root!.Element(AS + "Collections")!.Element(AS + "Collection")!;
		XElement? change = collection.Element(AS + "Responses")?.Element(AS + "Change");
		Assert.NotNull(change);
		Assert.Equal("7", change!.Element(AS + "Status")?.Value);
		// Server-wins: the client's edit is rejected, not blindly written over the concurrent change.
		Assert.Empty(_harness.Session.Store.Updated);
	}

	// ---- F8: the Collection Class is echoed as the first child on a 12.1 response ----
	[Fact]
	public async Task F8_InitialSync_Eas121_EchoesClassFirst()
	{
		UserFolder inbox = await RegisterInboxAsync();
		SyncHandler handler = NewSyncHandler();

		XDocument? response = await _harness.RunAsync(handler, "Sync",
			SyncRequest(inbox.ServerId, "0"), protocolVersion: "12.1");

		XElement collection = response!.Root!.Element(AS + "Collections")!.Element(AS + "Collection")!;
		XElement? first = collection.Elements().FirstOrDefault();
		Assert.NotNull(first);
		Assert.Equal("Class", first!.Name.LocalName);
		Assert.Equal(EasClass.Email, first.Value);
	}

	[Fact]
	public async Task F8_SteadyStateSync_Eas121_EchoesClassFirst()
	{
		UserFolder inbox = await RegisterInboxAsync();
		SyncHandler handler = NewSyncHandler();

		// A collection at key 1 whose snapshot holds an item the backend no longer has → a Delete
		// command, so the round emits a real Collection response (the steady-state path).
		Device device = await _harness.State.GetOrCreateDeviceAsync(
			EasHandlerHarness.UserName, "TESTDEVICE01", "TestClient", CancellationToken.None);
		(_, CollectionState? state) = await _harness.State.ValidateSyncKeyAsync(
			device, inbox.ServerId, "0", CancellationToken.None);
		await _harness.State.CommitCollectionStateAsync(
			state!, new Dictionary<string, string> { ["10"] = "x" }, 0, SyncKeyValidation.Initial,
			CancellationToken.None);

		XDocument? response = await _harness.RunAsync(handler, "Sync",
			SyncRequest(inbox.ServerId, "1"), protocolVersion: "12.1");

		XElement collection = response!.Root!.Element(AS + "Collections")!.Element(AS + "Collection")!;
		Assert.Equal("Class", collection.Elements().First().Name.LocalName);
		Assert.Equal(EasClass.Email, collection.Elements().First().Value);
		// Sanity: this really is the steady-state path (a Delete was emitted).
		Assert.NotNull(collection.Element(AS + "Commands")?.Element(AS + "Delete"));
	}

	// Gating: 14.1 (and above) must NOT emit Class — the 14.1 wire form stays byte-identical.
	[Fact]
	public async Task F8_InitialSync_Eas141_OmitsClass()
	{
		UserFolder inbox = await RegisterInboxAsync();
		SyncHandler handler = NewSyncHandler();

		XDocument? response = await _harness.RunAsync(handler, "Sync",
			SyncRequest(inbox.ServerId, "0"), protocolVersion: "14.1");

		XElement collection = response!.Root!.Element(AS + "Collections")!.Element(AS + "Collection")!;
		Assert.Null(collection.Element(AS + "Class"));
	}

	// ---- F9: MoreAvailable is emitted right after Status, before Commands/Responses ----
	[Fact]
	public async Task F9_MoreAvailable_PrecedesCommands()
	{
		UserFolder inbox = await RegisterInboxAsync();
		SyncHandler handler = NewSyncHandler();

		// Two items known to the client, both gone from the backend → two Deletes; a WindowSize of 1
		// truncates to one and sets MoreAvailable, so the response carries both Commands and MoreAvailable.
		Device device = await _harness.State.GetOrCreateDeviceAsync(
			EasHandlerHarness.UserName, "TESTDEVICE01", "TestClient", CancellationToken.None);
		(_, CollectionState? state) = await _harness.State.ValidateSyncKeyAsync(
			device, inbox.ServerId, "0", CancellationToken.None);
		await _harness.State.CommitCollectionStateAsync(
			state!, new Dictionary<string, string> { ["10"] = "x", ["20"] = "y" }, 0, SyncKeyValidation.Initial,
			CancellationToken.None);

		XDocument? response = await _harness.RunAsync(handler, "Sync",
			SyncRequest(inbox.ServerId, "1", new XElement(AS + "WindowSize", "1")));

		XElement collection = response!.Root!.Element(AS + "Collections")!.Element(AS + "Collection")!;
		List<XElement> children = collection.Elements().ToList();
		int moreAt = children.FindIndex(e => e.Name.LocalName == "MoreAvailable");
		int commandsAt = children.FindIndex(e => e.Name.LocalName == "Commands");
		Assert.True(moreAt >= 0, "MoreAvailable should be present");
		Assert.True(commandsAt >= 0, "Commands should be present");
		// Exchange/Z-Push emit MoreAvailable immediately after Status; order matters for strict parsers.
		Assert.True(moreAt < commandsAt, $"MoreAvailable (index {moreAt}) must precede Commands (index {commandsAt})");
	}

	// ---- F12: the replay rollback must not be persisted until the request commits ----
	[Fact]
	public async Task F12_ReplayRollback_IsNotPersistedBeforeCommit()
	{
		UserFolder inbox = await RegisterInboxAsync();
		Device device = await _harness.State.GetOrCreateDeviceAsync(
			EasHandlerHarness.UserName, "TESTDEVICE01", "TestClient", CancellationToken.None);

		// Advance the collection to SyncKey 2 with a live previous (SyncKey-1) generation.
		(_, CollectionState? state) = await _harness.State.ValidateSyncKeyAsync(
			device, inbox.ServerId, "0", CancellationToken.None);
		await _harness.State.CommitCollectionStateAsync(
			state!, new Dictionary<string, string> { ["a"] = "1" }, 0, SyncKeyValidation.Initial,
			CancellationToken.None);
		await _harness.State.CommitCollectionStateAsync(
			state!, new Dictionary<string, string> { ["a"] = "1", ["b"] = "1" }, 0, SyncKeyValidation.Current,
			CancellationToken.None);

		// The client re-sends the one-behind key → Replay. The rollback is DEFERRED to the round's own
		// commit (A1): validation does not touch the tracked entity, so nothing is persisted until the
		// round commits.
		(SyncKeyValidation validation, _) = await _harness.State.ValidateSyncKeyAsync(
			device, inbox.ServerId, "1", CancellationToken.None);
		Assert.Equal(SyncKeyValidation.Replay, validation);

		// Read what is actually committed to the database, independently of the tracked entity.
		await using SqliteSyncDbContext fresh = _harness.NewDbContext();
		CollectionState persisted = await fresh.CollectionStates.AsNoTracking()
			.SingleAsync(c => c.CollectionId == inbox.ServerId);

		// Before the fix, ValidateSyncKeyAsync SaveChanges'd the rollback immediately: the DB would
		// show SyncKey 1 with the replay generation already discarded. A commit failure after that
		// leaves the client's key re-validating as Current against the rolled-back snapshot.
		Assert.Equal(2, persisted.SyncKey);
		Assert.NotNull(persisted.PreviousSnapshotCompressed);
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

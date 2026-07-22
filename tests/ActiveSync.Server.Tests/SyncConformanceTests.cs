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

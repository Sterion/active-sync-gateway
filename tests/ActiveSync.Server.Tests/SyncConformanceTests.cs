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

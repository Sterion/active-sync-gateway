using System.Diagnostics.Metrics;
using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Core.Observability;
using ActiveSync.Core.State;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;
using ActiveSync.Server.Eas;
using ActiveSync.Server.Eas.Handlers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveSync.Server.Tests;

/// <summary>
///   Item 35 — Sync response hot path: batched item fetch (F13) and accurate
///   server→client item metrics (F14).
/// </summary>
public sealed class SyncHotPathTests : IDisposable
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

	// F13: a window of several Add/Change items is fetched in ONE batched GetItemsAsync call,
	// not a backend round trip per item.
	[Fact]
	public async Task F13_WindowIsFetchedInOneBatch_NotPerItem()
	{
		UserFolder inbox = await RegisterInboxAsync();
		_harness.Session.Store.ItemApplicationData = _ =>
			[new XElement(ASB + "Body", new XElement(ASB + "Type", "1"), new XElement(ASB + "Data", "preview"))];
		SyncHandler handler = NewSyncHandler();
		// Prime: initial sync (key 0) creates the collection state at key 1 with an empty snapshot.
		await _harness.RunAsync(handler, "Sync", SyncRequest(inbox.ServerId, "0"));

		// Three items the client has never seen → three server Adds this round.
		_harness.Session.Store.Revisions["10"] = "a";
		_harness.Session.Store.Revisions["11"] = "b";
		_harness.Session.Store.Revisions["12"] = "c";

		XDocument? response = await _harness.RunAsync(handler, "Sync", SyncRequest(inbox.ServerId, "1"));

		XElement collection = response!.Root!.Element(AS + "Collections")!.Element(AS + "Collection")!;
		Assert.Equal(3, collection.Element(AS + "Commands")!.Elements(AS + "Add").Count());

		// The whole window went through a single batched fetch carrying all three keys.
		Assert.Single(_harness.Session.Store.BatchFetched);
		Assert.Equal(["10", "11", "12"], _harness.Session.Store.BatchFetched[0].OrderBy(k => k));
	}

	// F14: an Add whose item vanished mid-sync (fetch returns null) is not sent, and must NOT be
	// counted as a delivered item — the server→client "add" metric must equal what was sent.
	[Fact]
	public async Task F14_VanishedItem_IsNotCountedAsSent()
	{
		UserFolder inbox = await RegisterInboxAsync();
		_harness.Session.Store.ItemApplicationData = _ =>
			[new XElement(ASB + "Body", new XElement(ASB + "Type", "1"), new XElement(ASB + "Data", "preview"))];
		SyncHandler handler = NewSyncHandler();
		// Prime: initial sync (key 0) creates the collection state at key 1 with an empty snapshot.
		await _harness.RunAsync(handler, "Sync", SyncRequest(inbox.ServerId, "0"));

		// Two Adds this round; one vanishes between the revision listing and the fetch.
		_harness.Session.Store.Revisions["10"] = "a";
		_harness.Session.Store.Revisions["11"] = "b";
		_harness.Session.Store.VanishedKeys.Add("10");

		int addsRecorded = 0;
		using MeterListener listener = new();
		listener.InstrumentPublished = (instrument, l) =>
		{
			if (instrument.Meter.Name == GatewayMetrics.MeterName &&
			    instrument.Name == "activesync_sync_items")
				l.EnableMeasurementEvents(instrument);
		};
		listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
		{
			string? direction = null, operation = null;
			foreach (KeyValuePair<string, object?> tag in tags)
			{
				if (tag.Key == "direction") direction = tag.Value?.ToString();
				if (tag.Key == "operation") operation = tag.Value?.ToString();
			}

			if (direction == "server_to_client" && operation == "add")
				addsRecorded += (int)measurement;
		});
		listener.Start();

		XDocument? response = await _harness.RunAsync(handler, "Sync", SyncRequest(inbox.ServerId, "1"));
		listener.Dispose();

		XElement collection = response!.Root!.Element(AS + "Collections")!.Element(AS + "Collection")!;
		int sentAdds = collection.Element(AS + "Commands")!.Elements(AS + "Add").Count();

		Assert.Equal(1, sentAdds);          // only item 11 was actually sent
		Assert.Equal(sentAdds, addsRecorded); // the metric counts what was sent, not what was diffed
	}

	// F15: a steady-state poll whose replayable request shape is unchanged must not re-write the
	// Device row for the cache (the LastSeenUtc write is the only one that should happen).
	[Fact]
	public async Task F15_UnchangedReplayShape_DoesNotRewriteTheDeviceRow()
	{
		UserFolder inbox = await RegisterInboxAsync();
		SyncHandler handler = NewSyncHandler();

		// Prime: initial sync (key 0 → 1) caches the request shape once.
		await _harness.RunAsync(handler, "Sync", SyncRequest(inbox.ServerId, "0"));
		// A second non-empty Sync with the same shape (idle, no changes) — the cache is identical.
		await _harness.RunAsync(handler, "Sync", SyncRequest(inbox.ServerId, "1"));

		// Count SaveChanges during a THIRD identical poll. GetOrCreateDevice always writes
		// LastSeenUtc (one save); the cache write must be elided because the shape is unchanged.
		int saves = 0;
		void OnSaved(object? sender, Microsoft.EntityFrameworkCore.SavedChangesEventArgs e) => saves++;
		_harness.Db.SavedChanges += OnSaved;
		await _harness.RunAsync(handler, "Sync", SyncRequest(inbox.ServerId, "1"));
		_harness.Db.SavedChanges -= OnSaved;

		Assert.Equal(1, saves);
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

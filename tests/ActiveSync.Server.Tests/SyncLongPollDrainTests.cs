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
///   Sync's long-poll wait races per-store watchers (<c>WaitForAnyChangeAsync</c>)
///   via <c>Task.WhenAny</c> and must drain every losing wait before returning — its
///   <see cref="IBackendSession" /> lease is released the moment <c>HandleAsync</c> returns, so an
///   abandoned watcher keeps running (or faults) against a session no longer valid, unobserved.
/// </summary>
public sealed class SyncLongPollDrainTests : IDisposable
{
	private static readonly XNamespace AS = EasNamespaces.AirSync;

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
	public async Task Sync_MultiStoreWait_DrainsTheLosingWait_BeforeReturning()
	{
		_harness.Options.Eas.MinHeartbeatSeconds = 1;
		_harness.Options.Eas.WatchdogSeconds = 0; // isolate the watcher race from the watchdog re-check

		EasHandlerHarness.RecordingStore calendar = new()
		{
			EasClass = EasClass.Calendar,
			KeyPrefix = "caldav:"
		};
		_harness.Session.SecondaryStore = calendar;

		List<UserFolder> registry = await _harness.RegisterFoldersAsync(
			new BackendFolder("imap:INBOX", "Inbox", null, EasFolderType.Inbox, EasClass.Email),
			new BackendFolder("caldav:Cal", "Calendar", null, EasFolderType.UserCalendar, EasClass.Calendar));
		UserFolder inbox = registry.Single(f => f.BackendKey == "imap:INBOX");
		UserFolder cal = registry.Single(f => f.BackendKey == "caldav:Cal");

		SyncHandler handler = NewSyncHandler();

		// Prime both collections to key 1 with an empty snapshot (initial sync, no diff payload).
		await _harness.RunAsync(handler, "Sync", TwoCollectionRequest(inbox.ServerId, "0", cal.ServerId, "0"));

		// The mail store's wait resolves immediately with a change — the winner of the race.
		_harness.Session.Store.WaitForChanges = keys => keys;

		// The calendar store's wait deliberately ignores cancellation — a real backend still takes
		// a moment to unwind (closing a socket, finishing an in-flight read) even after being asked
		// to stop. If the race abandons it instead of draining it, the Sync response returns long
		// before this completes.
		DateTime? calendarWaitCompletedUtc = null;
		calendar.WaitForChangesAsyncOverride = async (_, _) =>
		{
			await Task.Delay(TimeSpan.FromMilliseconds(250), CancellationToken.None);
			calendarWaitCompletedUtc = DateTime.UtcNow;
			return [];
		};

		await _harness.RunAsync(handler, "Sync",
			TwoCollectionRequest(inbox.ServerId, "1", cal.ServerId, "1", waitSeconds: 30));
		DateTime returnedUtc = DateTime.UtcNow;

		// The response must not be written until the losing calendar wait actually completed —
		// proving it was drained, not abandoned with an unobserved task still running against a
		// released session lease.
		Assert.NotNull(calendarWaitCompletedUtc);
		Assert.True(returnedUtc >= calendarWaitCompletedUtc,
			$"Sync returned at {returnedUtc:O}, before the losing wait completed at {calendarWaitCompletedUtc:O}");
	}

	private static XDocument TwoCollectionRequest(
		string collectionId1, string syncKey1, string collectionId2, string syncKey2, int? waitSeconds = null)
	{
		XElement root = new(AS + "Sync",
			new XElement(AS + "Collections",
				new XElement(AS + "Collection",
					new XElement(AS + "SyncKey", syncKey1),
					new XElement(AS + "CollectionId", collectionId1)),
				new XElement(AS + "Collection",
					new XElement(AS + "SyncKey", syncKey2),
					new XElement(AS + "CollectionId", collectionId2))));
		if (waitSeconds is { } wait)
			root.AddFirst(new XElement(AS + "HeartbeatInterval", wait.ToString()));
		return new XDocument(root);
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

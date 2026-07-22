using System.Text.Json;
using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Core.Options;
using ActiveSync.Core.State;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;
using Microsoft.Extensions.Options;

namespace ActiveSync.Server.Eas.Handlers;

/// <summary>Sync (MS-ASCMD 2.2.1.21): item-level synchronization for all content classes.</summary>
/// <remarks>
///   The implementation is split across partials: this file holds the top-level request flow
///   (<see cref="HandleAsync" />); <c>.Collection</c> the per-collection diff; <c>.ClientCommands</c>
///   the client→server command application; <c>.ServerCommands</c> the item rendering;
///   <c>.LongPoll</c> the Wait/HeartbeatInterval scaffolding; and <c>.Cache</c> the empty-request
///   replay. <see cref="SyncCollectionOptions" /> and <see cref="ClientCommandLedger" /> are the
///   two extracted collaborators.
/// </remarks>
public sealed partial class SyncHandler(
	FolderService folders,
	IOptionsSnapshot<ActiveSyncOptions> options,
	IHostApplicationLifetime lifetime,
	MeetingInvitationService invitations,
	ILogger<SyncHandler> logger) : IEasCommandHandler
{
	/// <summary>
	///   Bogus revision written to the snapshot for suppressed read-only writes: it never
	///   matches a real backend revision, so the next diff re-sends the server's version.
	/// </summary>
	private const string ReadOnlyRevertRevision = "!ro";

	private static readonly XNamespace AS = EasNamespaces.AirSync;
	private static readonly XNamespace ASB = EasNamespaces.AirSyncBase;
	private static readonly XNamespace Cal = EasNamespaces.Calendar;
	private static readonly XNamespace E2 = EasNamespaces.Email2;

	public string Command => "Sync";

	public async Task HandleAsync(EasContext context, CancellationToken ct)
	{
		XDocument? request = await context.ReadRequestAsync();
		int? waitSeconds = null;
		int globalWindow = options.Value.Eas.DefaultWindowSize;
		List<XElement> collectionElements;

		// A request carrying real <Collection> elements is a full request; a body that is just the
		// <Sync/> root with no collections is the same "repeat my previous request" idiom as a
		// zero-length body (F1) — both take the replay path below, and Status 13 is reserved for
		// "no cache available".
		List<XElement>? requested = request?.Root?
			.Element(AS + "Collections")?.Elements(AS + "Collection").ToList();
		if (requested is { Count: > 0 })
		{
			XElement root = request!.Root!;
			collectionElements = requested;

			if (int.TryParse(root.Element(AS + "Wait")?.Value, out int waitMinutes))
				waitSeconds = waitMinutes * 60;
			else if (int.TryParse(root.Element(AS + "HeartbeatInterval")?.Value, out int heartbeat))
				waitSeconds = heartbeat;
			if (int.TryParse(root.Element(AS + "WindowSize")?.Value, out int gw))
				globalWindow = gw;

			// Cache the replayable request shape for subsequent empty Sync requests (MS-ASCMD:
			// an empty request means "repeat my previous request").
			CachedSyncRequest cache = new(waitSeconds, globalWindow, collectionElements
				.Select(c => new CachedSyncCollection(
					c.Element(AS + "CollectionId")?.Value ?? "",
					c.Element(AS + "GetChanges")?.Value != "0",
					int.TryParse(c.Element(AS + "WindowSize")?.Value, out int cw) ? cw : null))
				.Where(c => c.CollectionId.Length > 0)
				.ToList());
			context.Device.LastSyncRequestJson = JsonSerializer.Serialize(cache);
			await context.State.PersistAsync(ct);
		}
		else
		{
			// Empty Sync: replay the cached request. Without a usable cache, status 13 tells
			// the client to resend the full request.
			List<CachedSyncCollection>? replayed = BuildReplayedCollections(context, out waitSeconds, out globalWindow);
			if (replayed is null)
			{
				await WriteStatusAsync(context, "13");
				return;
			}

			collectionElements = new List<XElement>();
			foreach (CachedSyncCollection cached in replayed)
			{
				CollectionState? state = await context.State.GetCollectionStateAsync(context.Device, cached.CollectionId, ct);
				if (state is null || state.SyncKey == 0)
				{
					// Collection never completed a real sync — the cache is stale.
					await WriteStatusAsync(context, "13");
					return;
				}

				XElement synthetic = new(AS + "Collection",
					new XElement(AS + "SyncKey", state.SyncKey.ToString()),
					new XElement(AS + "CollectionId", cached.CollectionId));
				if (!cached.GetChanges)
					synthetic.Add(new XElement(AS + "GetChanges", "0"));
				if (cached.WindowSize is { } cachedWindow)
					synthetic.Add(new XElement(AS + "WindowSize", cachedWindow.ToString()));
				collectionElements.Add(synthetic);
			}
		}

		List<XElement> responses = new();
		bool anyPayload = false;
		List<WaitableCollection> pendingWaitCollections = new();

		foreach (XElement collectionElement in collectionElements)
		{
			CollectionResult result = await ProcessCollectionAsync(context, collectionElement, globalWindow, ct);
			if (result.Response is not null)
			{
				responses.Add(result.Response);
				anyPayload |= result.HadPayload;
			}

			if (result.Waitable is { } waitable)
				pendingWaitCollections.Add(waitable);
		}

		// Long poll: nothing to report and the client asked to wait.
		if (!anyPayload && responses.Count == 0 && waitSeconds is { } wait && pendingWaitCollections.Count > 0)
		{
			wait = Math.Clamp(wait, options.Value.Eas.MinHeartbeatSeconds, options.Value.Eas.MaxHeartbeatSeconds);
			using IDisposable longPoll =
				Core.Observability.GatewayMetrics.TrackLongPoll(context.Device.UserName);
			bool changed = await WaitWithWatchdogAsync(
				context, pendingWaitCollections, TimeSpan.FromSeconds(wait), ct);
			if (changed)
				// F11: re-processing each waitable and appending its response is duplication-free by
				// invariant, not by coincidence. This block is reached only when `responses` is empty
				// (guarded above), and a WaitableCollection is produced by ProcessCollectionAsync ONLY
				// when that first pass emitted no Response (see CollectionResult: "empty response ⇒
				// waitable"). So every collection re-processed here contributed nothing on the first
				// pass — re-emitting its now-non-empty response cannot double-count one already in the
				// list. Re-running against the same request element is safe: it re-diffs current state.
				foreach (WaitableCollection waitable in pendingWaitCollections)
				{
					CollectionResult result = await ProcessCollectionAsync(context, waitable.Element, globalWindow, ct);
					if (result.Response is not null && result.HadPayload)
					{
						responses.Add(result.Response);
						anyPayload = true;
					}
				}
		}

		if (responses.Count == 0)
		{
			await context.WriteEmptyAsync(); // canonical "no changes" answer
			return;
		}

		await context.WriteResponseAsync(new XDocument(
			new XElement(AS + "Sync",
				new XElement(AS + "Collections", responses))));
	}

	private static Task WriteStatusAsync(EasContext context, string status)
	{
		return context.WriteResponseAsync(new XDocument(
			new XElement(AS + "Sync", new XElement(AS + "Status", status))));
	}
}

/// <summary>A collection awaiting the long-poll wait: its request element, folder and store.</summary>
internal readonly record struct WaitableCollection(XElement Element, UserFolder Folder, IContentStore Store);

/// <summary>
///   The outcome of processing one &lt;Collection&gt;: the response element to emit (null when the
///   collection produced nothing this round), whether that response carried real payload, and —
///   only when it produced nothing — the handle to wait on for a long poll. Naming the triple is
///   what makes the "empty response ⇒ waitable" invariant expressible in the type.
/// </summary>
internal readonly record struct CollectionResult(XElement? Response, bool HadPayload, WaitableCollection? Waitable);

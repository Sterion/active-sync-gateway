using System.Text.Json;
using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Core.Options;
using ActiveSync.Core.State;
using ActiveSync.Protocol.Wbxml;
using Microsoft.Extensions.Options;

namespace ActiveSync.Server.Eas.Handlers;

/// <summary>Ping (MS-ASCMD 2.2.1.13): long-poll push notification.</summary>
public sealed class PingHandler(
	FolderService folders,
	IOptionsSnapshot<ActiveSyncOptions> options,
	IHostApplicationLifetime lifetime,
	ILogger<PingHandler> logger) : IEasCommandHandler
{
	private static readonly XNamespace P = EasNamespaces.Ping;

	public string Command => "Ping";

	public async Task HandleAsync(EasContext context, CancellationToken ct)
	{
		XDocument? request = await context.ReadRequestAsync();
		PingParams? parameters = null;

		if (request?.Root is { } root)
		{
			List<string>? folderIds = root.Element(P + "Folders")?
				.Elements(P + "Folder")
				.Select(f => f.Element(P + "Id")?.Value)
				.Where(id => !string.IsNullOrEmpty(id))
				.Select(id => id!)
				.ToList();
			int heartbeat = int.TryParse(root.Element(P + "HeartbeatInterval")?.Value, out int hb) ? hb : 0;

			if (folderIds is { Count: > 0 } && heartbeat > 0)
			{
				parameters = new PingParams(heartbeat, folderIds);
				context.Device.PingParamsJson = JsonSerializer.Serialize(parameters);
				await context.State.PersistAsync(ct);
			}
			else if (heartbeat > 0 && context.Device.PingParamsJson is not null)
			{
				PingParams? cached = JsonSerializer.Deserialize<PingParams>(context.Device.PingParamsJson);
				if (cached is not null)
					parameters = cached with { HeartbeatSeconds = heartbeat };
			}
		}
		else if (context.Device.PingParamsJson is not null)
		{
			parameters = JsonSerializer.Deserialize<PingParams>(context.Device.PingParamsJson);
		}

		if (parameters is null)
		{
			await WriteStatusAsync(context, "3"); // missing parameters — client must send full request
			return;
		}

		EasOptions eas = options.Value.Eas;
		if (parameters.HeartbeatSeconds < eas.MinHeartbeatSeconds ||
		    parameters.HeartbeatSeconds > eas.MaxHeartbeatSeconds)
		{
			await context.WriteResponseAsync(new XDocument(
				new XElement(P + "Ping",
					new XElement(P + "Status", "5"),
					new XElement(P + "HeartbeatInterval",
						Math.Clamp(parameters.HeartbeatSeconds, eas.MinHeartbeatSeconds, eas.MaxHeartbeatSeconds)
							.ToString()))));
			return;
		}

		// Resolve folders to stores.
		Dictionary<IContentStore, List<(string CollectionId, UserFolder Folder)>> byStore = new();
		foreach (string collectionId in parameters.FolderIds)
		{
			(UserFolder Folder, IContentStore Store)? resolved = await folders.ResolveCollectionAsync(
				context.Session, context.Device.UserName, collectionId, ct);
			if (resolved is null)
			{
				await WriteStatusAsync(context, "7"); // hierarchy changed — client must FolderSync
				return;
			}

			List<(string CollectionId, UserFolder Folder)> list =
				byStore.TryGetValue(resolved.Value.Store, out List<(string CollectionId, UserFolder Folder)>? existing)
					? existing
					: [];
			list.Add((collectionId, resolved.Value.Folder));
			byStore[resolved.Value.Store] = list;
		}

		logger.LogDebug("Ping: watching {Count} folders for {User} (heartbeat {Heartbeat}s)",
			parameters.FolderIds.Count, context.Device.UserName, parameters.HeartbeatSeconds);

		List<(string CollectionId, UserFolder Folder, IContentStore Store)> watched = byStore
			.SelectMany(kv => kv.Value.Select(v => (v.CollectionId, v.Folder, Store: kv.Key)))
			.DistinctBy(v => v.CollectionId)
			.ToList();
		Dictionary<string, string> folderNames = watched.ToDictionary(v => v.CollectionId, v => v.Folder.DisplayName);

		string Describe(IEnumerable<string> ids)
		{
			return string.Join(", ",
				ids.Distinct().Select(id => folderNames.TryGetValue(id, out string? name) ? $"\"{name}\"" : id));
		}

		async Task<List<string>> CheckPendingAsync(CancellationToken token)
		{
			List<string> found = new();
			foreach ((string collectionId, UserFolder folder, IContentStore store) in watched)
				if (await PendingChangeDetector.HasPendingChangesAsync(
					    context, collectionId, folder, store, logger, token))
					found.Add(collectionId);
			return found;
		}

		Task WriteChangesAsync(IReadOnlyCollection<string> ids)
		{
			return context.WriteResponseAsync(new XDocument(
				new XElement(P + "Ping",
					new XElement(P + "Status", "2"),
					new XElement(P + "Folders",
						ids.Distinct().Select(id => new XElement(P + "Folder", id))))));
		}

		// Entry check: a change that landed before this Ping started is already inside the
		// watchers' baselines and would otherwise sit invisible for the whole heartbeat.
		// This is a normal arrival path (no watcher was running yet), hence Info.
		List<string> pending = await CheckPendingAsync(ct);
		if (pending.Count > 0)
		{
			logger.LogInformation(
				"Ping: pending changes in {Folders} for {User} at start (arrived between requests)",
				Describe(pending), context.Device.UserName);
			await WriteChangesAsync(pending);
			return;
		}

		TimeSpan timeout = TimeSpan.FromSeconds(parameters.HeartbeatSeconds);
		DateTime deadline = DateTime.UtcNow + timeout;
		using IDisposable longPoll =
			Core.Observability.GatewayMetrics.TrackLongPoll(context.Device.UserName);
		// Also observe host shutdown: an active long-poll must never delay process exit.
		using CancellationTokenSource cts =
			CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.ApplicationStopping);
		List<Task<List<string>>> watchers = byStore
			.Select(async kv =>
			{
				IReadOnlyList<string> changedKeys = await kv.Key.WaitForChangesAsync(
					kv.Value.Select(v => v.Folder.BackendKey).Distinct().ToList(), timeout, cts.Token);
				return changedKeys
					.SelectMany(key => kv.Value.Where(v => v.Folder.BackendKey == key))
					.Select(v => v.CollectionId)
					.Distinct()
					.ToList();
			})
			.ToList();

		// Watchdog: IDLE/STATUS notifications are best-effort, so re-run the exact check
		// at a fixed cadence. Quiet ticks stay silent.
		int watchdogSeconds = eas.WatchdogSeconds;
		Task<List<string>>? watchdogTask = watchdogSeconds > 0 ? WatchdogAsync(cts.Token) : null;

		async Task<List<string>> WatchdogAsync(CancellationToken token)
		{
			TimeSpan interval = TimeSpan.FromSeconds(watchdogSeconds);
			while (true)
			{
				TimeSpan remaining = deadline - DateTime.UtcNow;
				if (remaining <= TimeSpan.Zero)
					return [];
				await Task.Delay(remaining < interval ? remaining : interval, token);
				List<string> found = await CheckPendingAsync(token);
				if (found.Count > 0)
					return found;
			}
		}

		LongPollWatchdog.Outcome<List<string>> outcome = await LongPollWatchdog.RaceAsync(
			watchers, watchdogTask, changed => changed.Count > 0, new List<string>(), cts, ct);
		List<string> changedCollections = outcome.Result;

		if (changedCollections.Count == 0)
		{
			await WriteStatusAsync(context, "1"); // heartbeat expired, no changes
			return;
		}

		if (outcome.FoundByWatchdog)
			logger.LogWarning(
				"Watchdog: pending changes in {Folders} for {User} found by re-check (missed by the backend watcher)",
				Describe(changedCollections), context.Device.UserName);
		else
			logger.LogInformation("Ping: changes in {Folders} for {User}",
				Describe(changedCollections), context.Device.UserName);

		await WriteChangesAsync(changedCollections);
	}

	private static Task WriteStatusAsync(EasContext context, string status)
	{
		return context.WriteResponseAsync(new XDocument(
			new XElement(P + "Ping", new XElement(P + "Status", status))));
	}

	private sealed record PingParams(int HeartbeatSeconds, List<string> FolderIds);
}

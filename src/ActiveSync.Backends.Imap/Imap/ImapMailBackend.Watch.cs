using MailKit;
using Microsoft.Extensions.Logging;

namespace ActiveSync.Backends.Imap;

// The push/watch engine: races a persistent IMAP IDLE watcher against STATUS polling to
// detect backend changes during a Ping/Sync long-poll (see WaitForChangesAsync).
public sealed partial class ImapMailBackend
{
	public async Task<IReadOnlyList<string>> WaitForChangesAsync(
		IReadOnlyList<string> folderBackendKeys, TimeSpan timeout, CancellationToken ct)
	{
		DateTime watchStartUtc = DateTime.UtcNow;
		DateTime deadline = watchStartUtc + timeout;
		Dictionary<string, string> baseline = await SnapshotStatusAsync(folderBackendKeys, ct).ConfigureAwait(false);

		// Sub-second push: a shared per-(user, folder) IDLE watcher covers the priority
		// folder (INBOX when watched, else the first watched folder) across requests —
		// latched events included — while all folders keep STATUS polling. IDLE
		// unavailability degrades to pure polling transparently.
		string? idleKey = folderBackendKeys.Count == 0
			? null
			: folderBackendKeys.FirstOrDefault(k =>
				  ImapSession.FromBackendKey(k).Equals("INBOX", StringComparison.OrdinalIgnoreCase))
			  ?? folderBackendKeys[0];
		ImapIdleWatcher? watcher = idleKey is null ? null : idleWatcherProvider(ImapSession.FromBackendKey(idleKey));

		logger.LogDebug(
			"IMAP watch: {Folders} for {User} (timeout {Timeout}, idle on {IdleFolder})",
			string.Join(", ", folderBackendKeys.Select(ImapSession.FromBackendKey)),
			session.UserName, timeout,
			watcher is null ? "(none, STATUS polling only)" : ImapSession.FromBackendKey(idleKey!));

		if (watcher is null)
			return await PollForChangesAsync(folderBackendKeys, baseline, deadline, ct).ConfigureAwait(false);

		using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
		Task<bool?> idleTask = watcher.WaitForChangeAsync(watchStartUtc, timeout, cts.Token);
		Task<IReadOnlyList<string>> pollTask = PollForChangesAsync(folderBackendKeys, baseline, deadline, cts.Token);
		try
		{
			Task finished = await Task.WhenAny(idleTask, pollTask).ConfigureAwait(false);
			if (finished == idleTask && await idleTask.ConfigureAwait(false) == true)
				return [idleKey!]; // non-null whenever a watcher was resolved
			// IDLE timed out or is unavailable — let polling run out the clock.
			return await pollTask.ConfigureAwait(false);
		}
		finally
		{
			await cts.CancelAsync().ConfigureAwait(false);
			await ObserveAsync(idleTask).ConfigureAwait(false);
			await ObserveAsync(pollTask).ConfigureAwait(false);
		}
	}

	private static async Task ObserveAsync(Task task)
	{
		try
		{
			// The awaited task is always created in the same method scope (WhenAny loser
			// drain); there is no cross-context deadlock risk here.
#pragma warning disable VSTHRD003
			await task.ConfigureAwait(false);
#pragma warning restore VSTHRD003
		}
		catch
		{
			// cancelled loser or already-logged failure — nothing to do
		}
	}

	private async Task<IReadOnlyList<string>> PollForChangesAsync(
		IReadOnlyList<string> folderBackendKeys,
		Dictionary<string, string> baseline,
		DateTime deadline,
		CancellationToken ct)
	{
		// The semaphore inside RunAsync is held only per snapshot so Sync requests can
		// interleave with a long-running Ping.
		while (DateTime.UtcNow < deadline)
		{
			TimeSpan remaining = deadline - DateTime.UtcNow;
			TimeSpan delay = TimeSpan.FromSeconds(Math.Min(30, Math.Max(1, remaining.TotalSeconds)));
			await Task.Delay(delay, ct).ConfigureAwait(false);

			Dictionary<string, string> current = await SnapshotStatusAsync(folderBackendKeys, ct).ConfigureAwait(false);
			List<string> changed = folderBackendKeys
				.Where(k => baseline.GetValueOrDefault(k) != current.GetValueOrDefault(k))
				.ToList();
			if (changed.Count > 0)
			{
				foreach (string key in changed)
					logger.LogInformation(
						"IMAP STATUS: \"{Folder}\" changed for {User} (count:uidnext:unread {Baseline} -> {Current})",
						ImapSession.FromBackendKey(key), session.UserName,
						baseline.GetValueOrDefault(key, "?"), current.GetValueOrDefault(key, "?"));
				return changed;
			}
		}

		logger.LogDebug("IMAP watch: no backend changes in {Folders} for {User} within the heartbeat",
			string.Join(", ", folderBackendKeys.Select(ImapSession.FromBackendKey)), session.UserName);
		return [];
	}

	private Task<Dictionary<string, string>> SnapshotStatusAsync(
		IReadOnlyList<string> folderBackendKeys, CancellationToken ct)
	{
		return session.RunAsync(async client =>
		{
			Dictionary<string, string> map = new(StringComparer.Ordinal);
			foreach (string key in folderBackendKeys)
				try
				{
					IMailFolder folder = await client.GetFolderAsync(ImapSession.FromBackendKey(key), ct).ConfigureAwait(false);
					await folder.StatusAsync(
						StatusItems.Count | StatusItems.UidNext | StatusItems.Unread, ct).ConfigureAwait(false);
					map[key] = $"{folder.Count}:{folder.UidNext}:{folder.Unread}";
				}
				catch (Exception ex) when (ex is FolderNotFoundException)
				{
					map[key] = "gone";
				}

			return map;
		}, ct);
	}
}

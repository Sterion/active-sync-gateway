using System.Text.Json;
using ActiveSync.Contracts;

namespace ActiveSync.Backends.Jmap;

// The push/watch engine: WaitForChangesAsync races the shared per-user JMAP EventSource push
// (when available) against polling folder/account state tokens — Mailbox counts plus the
// account-wide Email state — to detect backend changes during a Ping/Sync long-poll. The token
// plumbing keeps its internal string mailbox ids; the contract's typed keys are wrapped at the
// boundary (the ImapMailBackend.Watch.cs pattern).
public sealed partial class JmapMailStore
{
	/// <inheritdoc />
	public async Task<IReadOnlyList<FolderKey>> WaitForChangesAsync(
		IReadOnlyList<FolderKey> folders, TimeSpan timeout, CancellationToken ct)
	{
		string account = await AccountAsync(ct).ConfigureAwait(false);
		string[] ids = folders.Select(f => FromKey(f.Value)).ToArray();
		Dictionary<string, string> baseline = await FolderTokensAsync(account, ids, ct).ConfigureAwait(false);
		DateTime deadline = DateTime.UtcNow + timeout;
		int delaySeconds = 1;
		int ceiling = Math.Max(1, pollSeconds);
		while (DateTime.UtcNow < deadline)
		{
			TimeSpan remaining = deadline - DateTime.UtcNow;
			TimeSpan delay = TimeSpan.FromSeconds(Math.Min(delaySeconds, ceiling));
			if (delay > remaining)
				delay = remaining;
			// The EventSource push, when available, wakes the wait as soon as the server
			// signals a change; the poll (token diff below) stays the correctness backstop.
			DateTime since = DateTime.UtcNow;
			if (delay > TimeSpan.Zero)
			{
				if (waitForPush is not null)
				{
					using CancellationTokenSource race = CancellationTokenSource.CreateLinkedTokenSource(ct);
					await Task.WhenAny(Task.Delay(delay, race.Token), waitForPush(since, race.Token)).ConfigureAwait(false);
					await race.CancelAsync().ConfigureAwait(false);
				}
				else
				{
					await Task.Delay(delay, ct).ConfigureAwait(false);
				}
			}

			delaySeconds = Math.Min(delaySeconds * 2, ceiling);

			Dictionary<string, string> current = await FolderTokensAsync(account, ids, ct).ConfigureAwait(false);
			List<FolderKey> changed = folders
				.Where(key => baseline.GetValueOrDefault(FromKey(key.Value)) !=
				              current.GetValueOrDefault(FromKey(key.Value)))
				.ToList();
			if (changed.Count > 0)
				return changed;
		}

		return [];
	}

	private async Task<Dictionary<string, string>> FolderTokensAsync(string account, string[] mailboxIds, CancellationToken ct)
	{
		if (mailboxIds.Length == 0)
			return new Dictionary<string, string>();
		// Mailbox counts (total:unread) alone miss a flag-only change (e.g. $flagged/$answered/a
		// category, which move no counter) and an equal add+delete (the counts net out). The
		// account-level Email state advances on ANY email create/update/destroy, so fold it into
		// every folder's token to catch those. Both are fetched in one request; Email/get with
		// an empty id list returns just the current state. NOTE: the state is account-wide, so a
		// change in one folder shifts every watched folder's token - Ping over-notifies rather than
		// misses, which is the safe direction (the client resyncs and finds nothing new).
		IReadOnlyList<JmapCall> calls =
		[
			new JmapCall("Mailbox/get", new Dictionary<string, object?>
			{
				["accountId"] = account,
				["ids"] = mailboxIds,
				["properties"] = new[] { "id", "totalEmails", "unreadEmails" }
			}, "0"),
			new JmapCall("Email/get", new Dictionary<string, object?>
			{
				["accountId"] = account,
				["ids"] = Array.Empty<string>()
			}, "1")
		];
		using JmapResponse response = await client.InvokeAsync(CapMail, calls, ct).ConfigureAwait(false);
		JsonElement emailArgs = response.Arguments("1");
		string emailState = emailArgs.TryGetProperty("state", out JsonElement es) ? es.GetString() ?? "" : "";
		Dictionary<string, string> tokens = new(StringComparer.Ordinal);
		foreach (JsonElement mailbox in response.Arguments("0").GetProperty("list").EnumerateArray())
		{
			string id = mailbox.GetProperty("id").GetString()!;
			long total = mailbox.TryGetProperty("totalEmails", out JsonElement t) ? t.GetInt64() : 0;
			long unread = mailbox.TryGetProperty("unreadEmails", out JsonElement u) ? u.GetInt64() : 0;
			tokens[id] = $"{total}:{unread}:{emailState}";
		}

		return tokens;
	}
}

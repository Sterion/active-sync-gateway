using ActiveSync.Contracts;

namespace ActiveSync.TestPlugin;

/// <summary>
///   A REAL notes store, in memory — the fixture's whole point. The plugin contract's claim is
///   that one package is enough to write a working backend; a fixture that implements no store and
///   throws from <c>CreateConnectionAsync</c> exercises registration and nothing else, which is
///   how the gap this contract redesign closed went unnoticed for so long.
///   <para>
///     Notes is the class that proves the claim most sharply: <see cref="NoteItem" /> is typed, so
///     this file references <c>ActiveSync.Contracts</c> and the BCL and nothing whatsoever besides
///     — no MIME, iCalendar or vCard library, and no gateway assembly.
///   </para>
///   <para>
///     It also HONOURS the optional <c>expected</c> precondition (it has a per-item version
///     counter, exactly like the gateway's own local stores), so the conformance kit's precondition
///     check exercises the throwing path rather than reporting a skip.
///   </para>
/// </summary>
public sealed class TestNotesStore : INotesStore
{
	/// <summary>The store's single folder. Its own key space, prefixed like every other store's.</summary>
	public static readonly FolderKey Folder = new("testplugin:notes");

	private readonly Lock _gate = new();
	private readonly Dictionary<string, Entry> _notes = new(StringComparer.Ordinal);
	private long _nextId;
	private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

	public bool OwnsKey(FolderKey key) => key == Folder;

	public Task<IReadOnlyList<BackendFolder>> ListFoldersAsync(CancellationToken ct) =>
		Task.FromResult<IReadOnlyList<BackendFolder>>(
		[
			new BackendFolder
			{
				Key = Folder,
				DisplayName = "Plugin Notes",
				Type = FolderType.Notes
			}
		]);

	public Task<IReadOnlyDictionary<ItemKey, ItemRevision>> GetItemRevisionsAsync(
		FolderKey folder, ContentFilter filter, CancellationToken ct)
	{
		RequireOwnFolder(folder);
		lock (_gate)
		{
			// The whole collection, every time: the engine diffs this map against its snapshot, so a
			// partial map reads as deletions.
			IReadOnlyDictionary<ItemKey, ItemRevision> revisions = _notes.ToDictionary(
				pair => new ItemKey(pair.Key),
				pair => new ItemRevision(pair.Value.Revision));
			return Task.FromResult(revisions);
		}
	}

	public Task<NoteItem?> GetItemAsync(FolderKey folder, ItemKey item, CancellationToken ct)
	{
		RequireOwnFolder(folder);
		lock (_gate)
			return Task.FromResult(_notes.TryGetValue(item.Value, out Entry? entry) ? entry.Note : null);
	}

	public Task<(ItemKey Key, ItemRevision Revision)> CreateItemAsync(
		FolderKey folder, NoteItem item, CancellationToken ct)
	{
		RequireOwnFolder(folder);
		lock (_gate)
		{
			string key = "note-" + (++_nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);
			Entry entry = new(item with { LastModified = DateTimeOffset.UtcNow }, 1);
			_notes[key] = entry;
			SignalChange();
			return Task.FromResult((new ItemKey(key), new ItemRevision(entry.Revision)));
		}
	}

	public Task<ItemRevision> UpdateItemAsync(
		FolderKey folder, ItemKey item, NoteItem value, ItemRevision? expected, CancellationToken ct)
	{
		RequireOwnFolder(folder);
		lock (_gate)
		{
			if (!_notes.TryGetValue(item.Value, out Entry? current))
				throw new BackendItemNotFoundException($"Note '{item.Value}' no longer exists.");

			// The host merged the client's partial update onto the current payload already and
			// handed over a COMPLETE note; a store never sees a patch. `expected` is what closes the
			// window between that read and this write.
			if (expected is { } precondition && precondition.Value != current.Revision)
				throw new BackendPreconditionFailedException(
					$"Note '{item.Value}' is at revision '{current.Revision}', not '{precondition.Value}'.");

			Entry updated = new(value with { LastModified = DateTimeOffset.UtcNow }, current.Version + 1);
			_notes[item.Value] = updated;
			SignalChange();
			return Task.FromResult(new ItemRevision(updated.Revision));
		}
	}

	public Task DeleteItemAsync(FolderKey folder, ItemKey item, bool permanent, CancellationToken ct)
	{
		RequireOwnFolder(folder);
		lock (_gate)
		{
			// `permanent` is a mail distinction (Trash vs. gone); a notes store has nowhere to move
			// an item to, so both mean delete — the same choice the gateway's own local stores make.
			if (_notes.Remove(item.Value))
				SignalChange();
		}

		return Task.CompletedTask;
	}

	public async Task<IReadOnlyList<FolderKey>> WaitForChangesAsync(
		IReadOnlyList<FolderKey> folders, TimeSpan timeout, CancellationToken ct)
	{
		if (!folders.Contains(Folder))
			return [];

		Task changed;
		lock (_gate)
			changed = _changed.Task;

		// Linked CTS so the losing wait is cancelled rather than left pending for the whole timeout —
		// a long-poll path must never accumulate abandoned timers.
		using CancellationTokenSource losers = CancellationTokenSource.CreateLinkedTokenSource(ct);
		Task delay = Task.Delay(timeout, losers.Token);
		Task first = await Task.WhenAny(changed, delay).ConfigureAwait(false);
		await losers.CancelAsync().ConfigureAwait(false);

		return first == changed ? [Folder] : [];
	}

	private static void RequireOwnFolder(FolderKey folder)
	{
		if (folder != Folder)
			throw new BackendException($"Folder '{folder.Value}' does not belong to the test plugin's notes store.");
	}

	/// <summary>Releases anyone waiting and arms the latch for the next change. Called under the lock.</summary>
	private void SignalChange()
	{
		TaskCompletionSource waiting = _changed;
		_changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		waiting.TrySetResult();
	}

	/// <summary>One stored note and its version counter, which IS its revision.</summary>
	private sealed record Entry(NoteItem Note, long Version)
	{
		public string Revision { get; } =
			"v" + Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
	}
}

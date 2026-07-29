using ActiveSync.Contracts;
using ActiveSync.Protocol.Sync;

namespace ActiveSync.Core.State;

/// <summary>
///   One item's entry in a collection snapshot: the revision the device acknowledged, plus whether
///   a client write we suppressed still owes that device a revert.
/// </summary>
/// <remarks>
///   The flag used to be a sentinel revision ("!ro") written into the revision value space, which
///   made a host concern indistinguishable from something a backend could legitimately return. A
///   revision is the backend's word alone (<see cref="ItemRevision" /> is opaque and only ever
///   compared); host state lives BESIDE it.
/// </remarks>
/// <param name="Revision">The revision the device last acknowledged for this item.</param>
/// <param name="PendingReadOnlyRevert">
///   Whether the next diff must re-offer this item as a Change even if the backend has not moved —
///   set when a client Change was silently reverted (read-only mode, a read-only shared calendar,
///   or a server-wins conflict) so the client's local edit is overwritten with the server's copy.
/// </param>
public readonly record struct SnapshotEntry(ItemRevision Revision, bool PendingReadOnlyRevert)
{
	/// <summary>The ordinary entry: an acknowledged revision with nothing owed.</summary>
	/// <param name="revision">The revision the device acknowledged.</param>
	public SnapshotEntry(string revision) : this(new ItemRevision(revision), false)
	{
	}

	/// <summary>This entry, marked as owing the client a revert on the next diff.</summary>
	public SnapshotEntry AsPendingRevert() => this with { PendingReadOnlyRevert = true };
}

/// <summary>
///   Bridges the persisted, typed collection snapshot to <see cref="CollectionDiff" />, which is
///   deliberately BCL-only (it lives in <c>ActiveSync.Protocol</c>, which references no other
///   project — see DependencyRuleTests). The projection here is the one place the two shapes meet:
///   the diff sees plain id → revision strings plus the set of items owing a revert, and its result
///   is re-married with the host flags on the way back.
/// </summary>
public static class CollectionSnapshot
{
	/// <summary>An empty snapshot, keyed the way every snapshot in the engine is.</summary>
	/// <returns>A fresh, ordinal-keyed empty snapshot.</returns>
	public static Dictionary<string, SnapshotEntry> Empty() => new(StringComparer.Ordinal);

	/// <summary>Marks an item as owing the client a revert, whether or not it is already known.</summary>
	/// <param name="snapshot">The collection snapshot to patch in place.</param>
	/// <param name="itemKey">The item whose suppressed change must be reverted.</param>
	public static void MarkPendingRevert(Dictionary<string, SnapshotEntry> snapshot, string itemKey)
	{
		// An unknown item gets an empty revision: the flag alone forces the Change, so the revision
		// value is never consulted for it — and inventing one would be a sentinel again.
		snapshot[itemKey] = snapshot.TryGetValue(itemKey, out SnapshotEntry entry)
			? entry.AsPendingRevert()
			: new SnapshotEntry(new ItemRevision(string.Empty), true);
	}

	/// <summary>
	///   Diffs a typed snapshot against the backend's current revision map and returns both the
	///   windowed changes and the snapshot to persist. Items still owing a revert are forced into
	///   the Changes list; the flag clears only for the ones actually charged to this window, so an
	///   item that did not fit re-offers next round.
	/// </summary>
	/// <param name="snapshot">The last acknowledged snapshot.</param>
	/// <param name="current">The backend's current item key → revision map.</param>
	/// <param name="windowSize">The combined budget for deletes+changes+adds this round.</param>
	/// <returns>The diff, and the (mutable) snapshot to persist once the round is rendered.</returns>
	public static (CollectionChanges Changes, Dictionary<string, SnapshotEntry> NewSnapshot) Diff(
		IReadOnlyDictionary<string, SnapshotEntry> snapshot,
		IReadOnlyDictionary<string, string> current,
		int windowSize)
	{
		Dictionary<string, string> revisions = new(snapshot.Count, StringComparer.Ordinal);
		HashSet<string>? pendingReverts = null;
		foreach ((string itemKey, SnapshotEntry entry) in snapshot)
		{
			revisions[itemKey] = entry.Revision.Value;
			if (entry.PendingReadOnlyRevert)
				(pendingReverts ??= new HashSet<string>(StringComparer.Ordinal)).Add(itemKey);
		}

		CollectionChanges changes = CollectionDiff.Compute(revisions, current, windowSize, pendingReverts);

		HashSet<string>? sentChanges = pendingReverts is null
			? null
			: new HashSet<string>(changes.Changes.Select(c => c.ServerId), StringComparer.Ordinal);
		Dictionary<string, SnapshotEntry> newSnapshot = new(changes.NewSnapshot.Count, StringComparer.Ordinal);
		foreach ((string itemKey, string revision) in changes.NewSnapshot)
			newSnapshot[itemKey] = new SnapshotEntry(
				new ItemRevision(revision),
				pendingReverts?.Contains(itemKey) == true && sentChanges?.Contains(itemKey) != true);

		return (changes, newSnapshot);
	}
}

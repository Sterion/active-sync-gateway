// Copyright (c) 2026 Ruben Andersen
// SPDX-License-Identifier: MIT

using System.Globalization;

namespace ActiveSync.Protocol.Sync;

/// <summary>One item's identity and revision stamp within a <see cref="CollectionDiff.Compute" /> result.</summary>
/// <param name="ServerId">The item's id within the collection (backend-defined; always compared with <see cref="StringComparer.Ordinal" />).</param>
/// <param name="Revision">An opaque backend revision/ETag token; a value that differs from the snapshotted one means the item changed.</param>
public sealed record ItemChange(string ServerId, string Revision);

/// <summary>
///   <see cref="NewSnapshot" /> is deliberately a mutable <see cref="Dictionary{TKey, TValue}" />,
///   not <see cref="IReadOnlyList{T}" /> like its siblings — callers (e.g. the sync handler's echo
///   suppression) patch it in place before persisting. It is always keyed with
///   <see cref="StringComparer.Ordinal" />, regardless of what comparer either input to
///   <see cref="CollectionDiff.Compute" /> used — do not rely on any other comparer surviving
///   into it.
/// </summary>
public sealed record CollectionChanges(
	IReadOnlyList<ItemChange> Adds,
	IReadOnlyList<ItemChange> Changes,
	IReadOnlyList<string> Deletes,
	bool MoreAvailable,
	Dictionary<string, string> NewSnapshot);

/// <summary>
///   Differential sync: compares the last acknowledged snapshot (ServerId → revision) with the
///   current backend revision map and produces windowed changes plus the snapshot to persist.
///   Items beyond the window are left out of the new snapshot so they surface on the next round.
///   Deletes, changes and adds share one window budget and are charged in that order, so a
///   device that lost items drains its tombstones before the window fills with new mail.
/// </summary>
public static class CollectionDiff
{
	/// <param name="snapshot">
	///   The previously-acknowledged id → revision map. Ids are always compared with
	///   <see cref="StringComparer.Ordinal" /> regardless of the comparer the supplied dictionary
	///   happens to use — a non-ordinal comparer here would make id lookups agree with
	///   <paramref name="current" /> but disagree with the persisted <see cref="CollectionChanges.NewSnapshot" />
	///   (itself always ordinal), which forks one logical item into two permanent entries across
	///   rounds. Normalized internally; callers do not need to pre-normalize, but should not
	///   rely on any other comparer's semantics (e.g. case-insensitivity) being honored.
	/// </param>
	/// <param name="current">The backend's current id → revision map. Same comparer note as <paramref name="snapshot" />.</param>
	/// <param name="windowSize">
	///   The combined budget for deletes+changes+adds sent this round (see the class summary for
	///   the charge order). Clamped to at least 1, so a non-positive value still makes progress
	///   instead of producing an empty round forever.
	/// </param>
	/// <param name="forceChanged">
	///   Ids that must be reported as Changes even when their revision still matches the snapshot —
	///   the host's read-only/conflict silent revert, where the backend never moved but the CLIENT's
	///   copy must be overwritten with the server's. It is passed as its own set precisely so the
	///   host does not have to poison the revision value space with a sentinel that "can never match
	///   a real revision"; a revision is the backend's word alone. Ids not present in
	///   <paramref name="snapshot" /> are ignored (an unknown id is an Add or nothing).
	/// </param>
	public static CollectionChanges Compute(
		IReadOnlyDictionary<string, string> snapshot,
		IReadOnlyDictionary<string, string> current,
		int windowSize,
		IReadOnlySet<string>? forceChanged = null)
	{
		snapshot = AsOrdinal(snapshot);
		current = AsOrdinal(current);

		List<ItemChange> adds = new();
		List<ItemChange> changes = new();
		List<string> deletes = new();

		foreach ((string id, string revision) in current)
			if (!snapshot.TryGetValue(id, out string? known))
				adds.Add(new ItemChange(id, revision));
			else if (!string.Equals(known, revision, StringComparison.Ordinal) ||
			         forceChanged?.Contains(id) == true)
				changes.Add(new ItemChange(id, revision));

		foreach (string id in snapshot.Keys)
			if (!current.ContainsKey(id))
				deletes.Add(id);

		// Deterministic order: newest-looking ids last so initial sync fills oldest-first
		// within a window; string ordinal keeps this stable across rounds. Deletes are sorted
		// too — an unsent tombstone must come back in the same place on the next round.
		adds.Sort(static (a, b) => CompareIds(a.ServerId, b.ServerId));
		changes.Sort(static (a, b) => CompareIds(a.ServerId, b.ServerId));
		deletes.Sort(CompareIds);

		Dictionary<string, string> newSnapshot = new(snapshot, StringComparer.Ordinal);

		int budget = Math.Max(1, windowSize);
		List<string> sentDeletes = new();
		List<ItemChange> sentChanges = new();
		List<ItemChange> sentAdds = new();

		// Tombstones first: they are the cheapest thing to send and the client cannot
		// reconcile anything else until they are gone. An unsent delete keeps its snapshot
		// entry, which is exactly what makes it reappear as a delete next round.
		foreach (string id in deletes)
		{
			if (budget == 0)
				break;

			sentDeletes.Add(id);
			newSnapshot.Remove(id);
			budget--;
		}

		foreach (ItemChange change in changes)
		{
			if (budget == 0)
				break;

			sentChanges.Add(change);
			newSnapshot[change.ServerId] = change.Revision;
			budget--;
		}

		foreach (ItemChange add in adds)
		{
			if (budget == 0)
				break;

			sentAdds.Add(add);
			newSnapshot[add.ServerId] = add.Revision;
			budget--;
		}

		bool more = sentDeletes.Count < deletes.Count
			|| sentChanges.Count < changes.Count
			|| sentAdds.Count < adds.Count;

		return new CollectionChanges(sentAdds, sentChanges, sentDeletes, more, newSnapshot);
	}

	private static int CompareIds(string a, string b)
	{
		// Numeric ids (IMAP UIDs, DAV short ids) compare numerically so windows fill in
		// ascending id order; fall back to ordinal for anything else. Comparing "na vs nb
		// when both parse, else ordinal(a, b)" is NOT a total order -- "9" < "10" (numeric),
		// "10" < "1a" (ordinal), but "9" > "1a" (ordinal) is a genuine cycle, which makes
		// List.Sort's result depend on the ids' original order rather than their values. Make
		// it total by comparing (isNumeric, value) as one ordered pair: every numeric id sorts
		// strictly before every non-numeric id, and numeric-vs-numeric / non-numeric-vs-non-
		// numeric each fall back to their own transitive comparison. NumberStyles.None +
		// InvariantCulture (rather than the default culture-sensitive style) so " 5"/"+5" are
		// not silently treated as numeric.
		bool aNumeric = long.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out long na);
		bool bNumeric = long.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out long nb);

		if (aNumeric && bNumeric)
			return na.CompareTo(nb);
		if (aNumeric != bNumeric)
			return aNumeric ? -1 : 1;
		return string.CompareOrdinal(a, b);
	}

	/// <summary>
	///   Normalizes to an ordinal-keyed dictionary so every lookup in <see cref="Compute" /> and
	///   the persisted <see cref="CollectionChanges.NewSnapshot" /> agree on comparer, regardless
	///   of what the caller happened to pass. A dictionary already keyed on
	///   <see cref="StringComparer.Ordinal" /> is returned as-is to avoid the copy.
	/// </summary>
	private static IReadOnlyDictionary<string, string> AsOrdinal(IReadOnlyDictionary<string, string> source)
	{
		return source is Dictionary<string, string> { Comparer: StringComparer comparer } dictionary
		       && ReferenceEquals(comparer, StringComparer.Ordinal)
			? dictionary
			: new Dictionary<string, string>(source, StringComparer.Ordinal);
	}
}

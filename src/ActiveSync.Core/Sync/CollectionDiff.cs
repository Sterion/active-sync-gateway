namespace ActiveSync.Core.Sync;

public sealed record ItemChange(string ServerId, string Revision);

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
/// </summary>
public static class CollectionDiff
{
	public static CollectionChanges Compute(
		IReadOnlyDictionary<string, string> snapshot,
		IReadOnlyDictionary<string, string> current,
		int windowSize)
	{
		List<ItemChange> adds = new();
		List<ItemChange> changes = new();
		List<string> deletes = new();

		foreach ((string id, string revision) in current)
			if (!snapshot.TryGetValue(id, out string? known))
				adds.Add(new ItemChange(id, revision));
			else if (!string.Equals(known, revision, StringComparison.Ordinal))
				changes.Add(new ItemChange(id, revision));

		foreach (string id in snapshot.Keys)
			if (!current.ContainsKey(id))
				deletes.Add(id);

		// Deterministic order: newest-looking ids last so initial sync fills oldest-first
		// within a window; string ordinal keeps this stable across rounds.
		adds.Sort(static (a, b) => CompareIds(a.ServerId, b.ServerId));
		changes.Sort(static (a, b) => CompareIds(a.ServerId, b.ServerId));

		Dictionary<string, string> newSnapshot = new(snapshot, StringComparer.Ordinal);
		foreach (string id in deletes)
			newSnapshot.Remove(id);

		int budget = Math.Max(1, windowSize);
		List<ItemChange> sentAdds = new();
		List<ItemChange> sentChanges = new();
		bool more = false;

		foreach (ItemChange change in changes)
		{
			if (budget == 0)
			{
				more = true;
				break;
			}

			sentChanges.Add(change);
			newSnapshot[change.ServerId] = change.Revision;
			budget--;
		}

		if (!more)
			foreach (ItemChange add in adds)
			{
				if (budget == 0)
				{
					more = true;
					break;
				}

				sentAdds.Add(add);
				newSnapshot[add.ServerId] = add.Revision;
				budget--;
			}
		else
			more = true;

		if (!more && (sentAdds.Count < adds.Count || sentChanges.Count < changes.Count))
			more = true;

		return new CollectionChanges(sentAdds, sentChanges, deletes, more, newSnapshot);
	}

	private static int CompareIds(string a, string b)
	{
		// Numeric ids (IMAP UIDs, DAV short ids) compare numerically so windows fill in
		// ascending id order; fall back to ordinal for anything else.
		if (long.TryParse(a, out long na) && long.TryParse(b, out long nb))
			return na.CompareTo(nb);
		return string.CompareOrdinal(a, b);
	}
}

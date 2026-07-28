using ActiveSync.Protocol.Sync;

namespace ActiveSync.Core.Tests;

public class CollectionDiffTests
{
	private static Dictionary<string, string> Map(params (string Id, string Rev)[] items)
	{
		return items.ToDictionary(i => i.Id, i => i.Rev);
	}

	[Fact]
	public void InitialSync_AllItemsAreAdds()
	{
		CollectionChanges result = CollectionDiff.Compute(Map(), Map(("1", "a"), ("2", "b")), 100);

		Assert.Equal(2, result.Adds.Count);
		Assert.Empty(result.Changes);
		Assert.Empty(result.Deletes);
		Assert.False(result.MoreAvailable);
		Assert.Equal(2, result.NewSnapshot.Count);
	}

	[Fact]
	public void ChangedRevision_IsReportedAsChange()
	{
		CollectionChanges result = CollectionDiff.Compute(
			Map(("1", "a"), ("2", "b")),
			Map(("1", "a"), ("2", "B")), 100);

		Assert.Empty(result.Adds);
		Assert.Single(result.Changes);
		Assert.Equal("2", result.Changes[0].ServerId);
		Assert.Equal("B", result.NewSnapshot["2"]);
	}

	[Fact]
	public void MissingItem_IsReportedAsDelete()
	{
		CollectionChanges result = CollectionDiff.Compute(
			Map(("1", "a"), ("2", "b")),
			Map(("1", "a")), 100);

		Assert.Single(result.Deletes);
		Assert.Equal("2", result.Deletes[0]);
		Assert.False(result.NewSnapshot.ContainsKey("2"));
	}

	[Fact]
	public void WindowSize_LimitsAddsAndSetsMoreAvailable()
	{
		Dictionary<string, string> current = Map(Enumerable.Range(1, 10).Select(i => (i.ToString(), "r")).ToArray());
		CollectionChanges result = CollectionDiff.Compute(Map(), current, 4);

		Assert.Equal(4, result.Adds.Count);
		Assert.True(result.MoreAvailable);
		// Unsent items must stay out of the snapshot so they arrive next round.
		Assert.Equal(4, result.NewSnapshot.Count);

		// Next round picks up where we left off.
		CollectionChanges second = CollectionDiff.Compute(result.NewSnapshot, current, 4);
		Assert.Equal(4, second.Adds.Count);
		Assert.True(second.MoreAvailable);
		CollectionChanges third = CollectionDiff.Compute(second.NewSnapshot, current, 4);
		Assert.Equal(2, third.Adds.Count);
		Assert.False(third.MoreAvailable);
	}

	[Fact]
	public void NumericIds_FillWindowInAscendingOrder()
	{
		Dictionary<string, string> current = Map(("10", "r"), ("2", "r"), ("30", "r"));
		CollectionChanges result = CollectionDiff.Compute(Map(), current, 2);

		Assert.Equal(["2", "10"], result.Adds.Select(a => a.ServerId).ToArray());
	}

	[Fact]
	public void Deletes_AreChargedToTheWindow_AndDrainAcrossRounds()
	{
		// F2/A21: emptying a 50k folder must not produce one response with 50k <Delete>
		// elements. Deletes are charged to the same budget as adds/changes, unsent ones stay
		// in the snapshot so they resurface, and truncation sets MoreAvailable.
		Dictionary<string, string> snapshot = Map(Enumerable.Range(1, 20).Select(i => (i.ToString(), "r")).ToArray());

		CollectionChanges first = CollectionDiff.Compute(snapshot, Map(), 5);
		Assert.Equal(5, first.Deletes.Count);
		Assert.True(first.MoreAvailable);
		Assert.Equal(15, first.NewSnapshot.Count);

		CollectionChanges second = CollectionDiff.Compute(first.NewSnapshot, Map(), 5);
		Assert.Equal(5, second.Deletes.Count);
		Assert.True(second.MoreAvailable);
		Assert.Equal(10, second.NewSnapshot.Count);

		// No id is reported twice, and the last round drains cleanly.
		Assert.Empty(first.Deletes.Intersect(second.Deletes));
		CollectionChanges third = CollectionDiff.Compute(second.NewSnapshot, Map(), 5);
		CollectionChanges fourth = CollectionDiff.Compute(third.NewSnapshot, Map(), 5);
		Assert.Equal(5, fourth.Deletes.Count);
		Assert.False(fourth.MoreAvailable);
		Assert.Empty(fourth.NewSnapshot);
	}

	[Fact]
	public void Deletes_DrainBeforeChangesAndAdds()
	{
		// Tombstones are charged first so a device that lost items catches up on removals
		// before the window fills with new mail.
		Dictionary<string, string> snapshot = Map(("1", "a"), ("2", "b"), ("3", "c"));
		Dictionary<string, string> current = Map(("3", "C"), ("4", "d"), ("5", "e"));

		CollectionChanges result = CollectionDiff.Compute(snapshot, current, 2);

		Assert.Equal(["1", "2"], result.Deletes.ToArray());
		Assert.Empty(result.Changes);
		Assert.Empty(result.Adds);
		Assert.True(result.MoreAvailable);
	}

	[Fact]
	public void UnwindowedDeletes_StayOutOfTheSentSet_ButKeepTheirSnapshotEntry()
	{
		Dictionary<string, string> snapshot = Map(("1", "a"), ("2", "b"), ("3", "c"));

		CollectionChanges result = CollectionDiff.Compute(snapshot, Map(), 1);

		Assert.Single(result.Deletes);
		// The two undelivered tombstones must remain in the snapshot; dropping them here is
		// what makes them invisible on the next round.
		Assert.Equal(2, result.NewSnapshot.Count);
		Assert.DoesNotContain(result.Deletes[0], result.NewSnapshot.Keys);
	}

	[Fact]
	public void MixedNumericAndNonNumericIds_SortOrderIsIndependentOfInputOrder()
	{
		// W3: CompareIds computed "9" < "10" (numeric), "10" < "1a" (ordinal fallback), but
		// "9" > "1a" (ordinal fallback) -- an intransitive comparator. List.Sort has no
		// obligation to produce the same result for the same *set* of ids when the comparator
		// is not a total order; empirically it produces a different order depending purely on
		// the ids' original enumeration order, which is exactly the "silently reshuffling which
		// items a windowed device receives across rounds" defect the finding describes. A
		// correct (total-order) comparer must sort the same three ids identically no matter
		// which order the backend happened to hand them back in.
		Dictionary<string, string> currentA = new() { ["9"] = "r", ["10"] = "r", ["1a"] = "r" };
		Dictionary<string, string> currentB = new() { ["1a"] = "r", ["9"] = "r", ["10"] = "r" };
		Dictionary<string, string> currentC = new() { ["10"] = "r", ["1a"] = "r", ["9"] = "r" };

		string[] orderA = CollectionDiff.Compute(Map(), currentA, 100).Adds.Select(a => a.ServerId).ToArray();
		string[] orderB = CollectionDiff.Compute(Map(), currentB, 100).Adds.Select(a => a.ServerId).ToArray();
		string[] orderC = CollectionDiff.Compute(Map(), currentC, 100).Adds.Select(a => a.ServerId).ToArray();

		Assert.Equal(orderA, orderB);
		Assert.Equal(orderA, orderC);
		// And the numeric ids must still sort as a block ahead of the non-numeric one.
		Assert.Equal(["9", "10", "1a"], orderA);
	}

	[Fact]
	public void CallerSuppliedCaseInsensitiveComparer_DoesNotForkTheSnapshot()
	{
		// W17: Compute reads membership via snapshot.TryGetValue/current.ContainsKey using EACH
		// dictionary's OWN comparer, then always materializes NewSnapshot with
		// StringComparer.Ordinal -- so a caller passing an OrdinalIgnoreCase map gets a diff
		// computed case-insensitively but a snapshot persisted case-sensitively.
		//
		// Round 1: "ABC" is genuinely new -- one Add, one snapshot entry.
		Dictionary<string, string> round1Current = new(StringComparer.OrdinalIgnoreCase) { ["ABC"] = "r1" };
		CollectionChanges round1 = CollectionDiff.Compute(Map(), round1Current, 100);
		Assert.Single(round1.Adds);
		Assert.Single(round1.NewSnapshot);

		// Round 2: the backend hands the SAME item back with different casing (same revision) --
		// under the OrdinalIgnoreCase comparer the caller consistently uses, this is the same
		// logical item, not a new one.
		Dictionary<string, string> round2Current = new(StringComparer.OrdinalIgnoreCase) { ["abc"] = "r1" };
		CollectionChanges round2 = CollectionDiff.Compute(round1.NewSnapshot, round2Current, 100);

		// The persisted snapshot must never fork into two entries ("ABC" AND "abc") for one
		// logical item -- either it is recognized as unchanged (0 adds, 0 deletes) or, if the
		// engine deliberately treats every id ordinally throughout, it is a clean delete-of-old
		// plus add-of-new. What must not happen is the OLD entry surviving un-deleted while a
		// SECOND entry is added alongside it.
		Assert.Single(round2.NewSnapshot);
	}
}

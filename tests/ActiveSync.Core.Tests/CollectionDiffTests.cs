using ActiveSync.Core.Sync;

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
}

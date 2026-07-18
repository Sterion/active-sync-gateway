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
	public void DeletesAreNotWindowed()
	{
		Dictionary<string, string> snapshot = Map(Enumerable.Range(1, 20).Select(i => (i.ToString(), "r")).ToArray());
		CollectionChanges result = CollectionDiff.Compute(snapshot, Map(), 5);

		Assert.Equal(20, result.Deletes.Count);
		Assert.Empty(result.NewSnapshot);
		Assert.False(result.MoreAvailable);
	}
}

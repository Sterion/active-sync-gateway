using ActiveSync.Contracts;
using ActiveSync.Core.State;
using ActiveSync.Protocol.Sync;

namespace ActiveSync.Core.Tests;

/// <summary>
///   The typed collection snapshot: a revision the device acknowledged plus the host's own
///   read-only revert marker BESIDE it, where the sentinel revision "!ro" used to live inside the
///   revision value space. These pin the behaviour that sentinel provided — a suppressed client
///   write is re-offered until the client actually receives the server's copy — now that it is
///   carried as a flag the diff is told about explicitly.
/// </summary>
public class CollectionSnapshotTests
{
	/// <summary>The backend's current revision map, in the store contract's typed key space.</summary>
	private static Dictionary<ItemKey, ItemRevision> Current(params (string Id, string Rev)[] items) =>
		items.ToDictionary(i => new ItemKey(i.Id), i => new ItemRevision(i.Rev));

	[Fact]
	public void PendingRevert_ForcesAChange_WhenTheBackendHasNotMoved()
	{
		Dictionary<string, SnapshotEntry> snapshot = CollectionSnapshot.Empty();
		snapshot["1"] = new SnapshotEntry("a");
		CollectionSnapshot.MarkPendingRevert(snapshot, "1");

		(CollectionChanges changes, Dictionary<string, SnapshotEntry> newSnapshot) =
			CollectionSnapshot.Diff(snapshot, Current(("1", "a")), 100);

		Assert.Equal(["1"], changes.Changes.Select(c => c.ServerId).ToArray());
		// Delivered this round, so nothing is owed any more — and the entry carries the backend's
		// own revision, never a host-invented one.
		Assert.Equal(new SnapshotEntry("a"), newSnapshot["1"]);
	}

	[Fact]
	public void PendingRevert_SurvivesAFullWindow_AndIsReofferedNextRound()
	{
		Dictionary<string, SnapshotEntry> snapshot = CollectionSnapshot.Empty();
		snapshot["1"] = new SnapshotEntry("a");
		snapshot["2"] = new SnapshotEntry("b");
		CollectionSnapshot.MarkPendingRevert(snapshot, "1");
		CollectionSnapshot.MarkPendingRevert(snapshot, "2");

		(CollectionChanges first, Dictionary<string, SnapshotEntry> afterFirst) =
			CollectionSnapshot.Diff(snapshot, Current(("1", "a"), ("2", "b")), 1);

		Assert.Single(first.Changes);
		Assert.True(first.MoreAvailable);
		string sent = first.Changes[0].ServerId;
		string unsent = sent == "1" ? "2" : "1";
		Assert.False(afterFirst[sent].PendingReadOnlyRevert);
		Assert.True(afterFirst[unsent].PendingReadOnlyRevert);

		(CollectionChanges second, Dictionary<string, SnapshotEntry> afterSecond) =
			CollectionSnapshot.Diff(afterFirst, Current(("1", "a"), ("2", "b")), 100);

		Assert.Equal([unsent], second.Changes.Select(c => c.ServerId).ToArray());
		Assert.False(afterSecond[unsent].PendingReadOnlyRevert);
	}

	[Fact]
	public void MarkPendingRevert_OnAnUnknownItem_StillForcesTheChange()
	{
		// An item the snapshot does not know gets an empty revision plus the marker: the marker
		// alone drives the re-offer, so no value has to stand in for "never matches".
		Dictionary<string, SnapshotEntry> snapshot = CollectionSnapshot.Empty();
		CollectionSnapshot.MarkPendingRevert(snapshot, "1");

		Assert.Equal(string.Empty, snapshot["1"].Revision.Value);
		Assert.True(snapshot["1"].PendingReadOnlyRevert);

		(CollectionChanges changes, _) = CollectionSnapshot.Diff(snapshot, Current(("1", "a")), 100);

		Assert.Equal(["1"], changes.Changes.Select(c => c.ServerId).ToArray());
	}

	[Fact]
	public void MarkPendingRevert_KeepsTheAckedRevision()
	{
		// The revision the client last saw must survive the marking: a later round that decides
		// NOT to send the revert (the item was deleted on the backend, say) must not have lost it.
		Dictionary<string, SnapshotEntry> snapshot = CollectionSnapshot.Empty();
		snapshot["1"] = new SnapshotEntry("a");
		CollectionSnapshot.MarkPendingRevert(snapshot, "1");

		Assert.Equal(new ItemRevision("a"), snapshot["1"].Revision);
	}

	[Fact]
	public void Codec_RoundTripsRevisionsAndPendingReverts()
	{
		Dictionary<string, SnapshotEntry> snapshot = CollectionSnapshot.Empty();
		snapshot["1"] = new SnapshotEntry("a");
		snapshot["2"] = new SnapshotEntry(new ItemRevision("b"), true);

		Dictionary<string, SnapshotEntry> read = SnapshotCodec.Decompress(SnapshotCodec.Compress(snapshot))!;

		Assert.Equal(new SnapshotEntry("a"), read["1"]);
		Assert.Equal(new SnapshotEntry(new ItemRevision("b"), true), read["2"]);
		Assert.Same(StringComparer.Ordinal, read.Comparer);
	}

	[Fact]
	public void Codec_RejectsASnapshotWrittenInTheOldShape()
	{
		// The pre-typed-entry blob was a bare {id: revision} map (which also carried the "!ro"
		// sentinel). It is deliberately NOT converted: null tells the caller to make the device
		// resynchronize from SyncKey 0 rather than diff against something it cannot interpret.
		byte[] legacy = Gzip("""{"1":"a","2":"!ro"}""");

		Assert.Null(SnapshotCodec.Decompress(legacy));
	}

	[Fact]
	public void Codec_ReadsAnAbsentBlobAsAnEmptySnapshot()
	{
		// A never-committed row is not a format problem — it is simply empty, exactly as before.
		Assert.Empty(SnapshotCodec.Decompress(null)!);
		Assert.Empty(SnapshotCodec.Decompress([])!);
	}

	[Fact]
	public void Codec_RejectsACorruptBlobInsteadOfThrowing()
	{
		Assert.Null(SnapshotCodec.Decompress([0x1f, 0x8b, 0x00, 0x01, 0x02]));
	}

	private static byte[] Gzip(string json)
	{
		using MemoryStream buffer = new();
		using (System.IO.Compression.GZipStream gzip =
		       new(buffer, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
		{
			byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);
			gzip.Write(bytes, 0, bytes.Length);
		}

		return buffer.ToArray();
	}
}

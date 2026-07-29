using System.IO.Compression;
using System.Text.Json;
using ActiveSync.Contracts;

namespace ActiveSync.Core.State;

/// <summary>
///   Gzip codec for collection snapshots. A snapshot is {ServerId → revision} for every item ever
///   sent to a device; on a 50k-item mailbox its JSON runs 2–3 MB, and the row keeps it twice
///   (current + previous). Persisting it gzipped keeps that bulk off disk and out of every
///   request's read/write path — the dominant steady-state sync cost. The in-memory shape
///   stays a plain <see cref="Dictionary{TKey,TValue}" />; only the stored column bytes are
///   compressed, and this is the one place that (de)serializes them.
///   <para>
///     The stored document is versioned. Version 2 carries the revisions as the same flat map as
///     before, plus a sidecar list of the items owing a read-only revert (omitted when empty, which
///     is the overwhelmingly common case) — so the typed <see cref="SnapshotEntry" /> costs nothing
///     per item on disk. A blob written in ANY other shape — notably the bare {id → revision} map
///     that also carried the "!ro" sentinel INSIDE the revision — is deliberately not converted: it
///     reads as <c>null</c>, and the caller restarts that device's collection from SyncKey 0, which
///     is the announced consequence of the shape change.
///   </para>
/// </summary>
internal static class SnapshotCodec
{
	/// <summary>The shape this codec writes. A blob declaring anything else is not converted.</summary>
	private const int CurrentVersion = 2;

	private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

	/// <summary>Serializes a snapshot to gzipped UTF-8 JSON.</summary>
	public static byte[] Compress(Dictionary<string, SnapshotEntry> snapshot)
	{
		Dictionary<string, string> revisions = new(snapshot.Count, StringComparer.Ordinal);
		List<string>? pendingReverts = null;
		foreach ((string itemKey, SnapshotEntry entry) in snapshot)
		{
			revisions[itemKey] = entry.Revision.Value;
			if (entry.PendingReadOnlyRevert)
				(pendingReverts ??= []).Add(itemKey);
		}

		byte[] json = JsonSerializer.SerializeToUtf8Bytes(
			new SnapshotDocument { V = CurrentVersion, Items = revisions, PendingReverts = pendingReverts },
			JsonOpts);
		using MemoryStream buffer = new();
		using (GZipStream gzip = new(buffer, CompressionLevel.Fastest, leaveOpen: true))
			gzip.Write(json, 0, json.Length);
		return buffer.ToArray();
	}

	/// <summary>
	///   Inflates a snapshot column back to the item map. A null or empty blob — a never-committed
	///   row, or one defaulted by the column-type migration — reads as an empty snapshot, so the
	///   client simply re-syncs rather than the read throwing. A blob that is present but NOT in the
	///   current shape (a pre-typed-entry snapshot, or a corrupt one) reads as <c>null</c>: the
	///   caller must treat that collection's sync key as invalid rather than silently diffing
	///   against an empty snapshot, which would re-Add every item the device already holds.
	/// </summary>
	public static Dictionary<string, SnapshotEntry>? Decompress(byte[]? compressed)
	{
		if (compressed is null || compressed.Length == 0)
			return CollectionSnapshot.Empty();

		SnapshotDocument? document;
		try
		{
			using MemoryStream input = new(compressed);
			using GZipStream gzip = new(input, CompressionMode.Decompress);
			using MemoryStream output = new();
			gzip.CopyTo(output);
			document = JsonSerializer.Deserialize<SnapshotDocument>(output.ToArray(), JsonOpts);
		}
		catch (Exception ex) when (ex is JsonException or InvalidDataException)
		{
			return null;
		}

		if (document is not { V: CurrentVersion, Items: not null })
			return null;

		// JsonSerializer.Deserialize builds the dictionary with the default (non-explicit) string
		// comparer; re-key with StringComparer.Ordinal so every snapshot map in the diff engine
		// (FolderRegistry/DavItemMap already build theirs this way) uses the SAME comparer.
		Dictionary<string, SnapshotEntry> snapshot = new(document.Items.Count, StringComparer.Ordinal);
		foreach ((string itemKey, string revision) in document.Items)
			snapshot[itemKey] = new SnapshotEntry(revision);
		foreach (string itemKey in document.PendingReverts ?? [])
			snapshot[itemKey] = snapshot.TryGetValue(itemKey, out SnapshotEntry entry)
				? entry.AsPendingRevert()
				: new SnapshotEntry(new ItemRevision(string.Empty), true);

		return snapshot;
	}

	/// <summary>The persisted shape: a version stamp, the flat revision map, and the revert sidecar.</summary>
	private sealed record SnapshotDocument
	{
		public int V { get; init; }
		public Dictionary<string, string>? Items { get; init; }
		public List<string>? PendingReverts { get; init; }
	}
}

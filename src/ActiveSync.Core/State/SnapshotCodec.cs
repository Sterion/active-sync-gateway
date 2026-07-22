using System.IO.Compression;
using System.Text.Json;

namespace ActiveSync.Core.State;

/// <summary>
///   Gzip codec for collection snapshots. A snapshot is {ServerId → revision} for every item ever
///   sent to a device; on a 50k-item mailbox its JSON runs 2–3 MB, and the row keeps it twice
///   (current + previous). Persisting it gzipped keeps that bulk off disk and out of every
///   request's read/write path — the dominant steady-state sync cost (A4). The in-memory shape
///   stays a plain <see cref="Dictionary{TKey,TValue}" />; only the stored column bytes are
///   compressed, and this is the one place that (de)serializes them.
/// </summary>
internal static class SnapshotCodec
{
	private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

	/// <summary>Serializes a snapshot to gzipped UTF-8 JSON.</summary>
	public static byte[] Compress(Dictionary<string, string> snapshot)
	{
		byte[] json = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOpts);
		using MemoryStream buffer = new();
		using (GZipStream gzip = new(buffer, CompressionLevel.Fastest, leaveOpen: true))
			gzip.Write(json, 0, json.Length);
		return buffer.ToArray();
	}

	/// <summary>
	///   Inflates a snapshot column back to the item map. A null or empty blob — a never-committed
	///   row, or one defaulted by the column-type migration — reads as an empty snapshot, so the
	///   client simply re-syncs rather than the read throwing.
	/// </summary>
	public static Dictionary<string, string> Decompress(byte[]? compressed)
	{
		if (compressed is null || compressed.Length == 0)
			return new Dictionary<string, string>();
		using MemoryStream input = new(compressed);
		using GZipStream gzip = new(input, CompressionMode.Decompress);
		using MemoryStream output = new();
		gzip.CopyTo(output);
		return JsonSerializer.Deserialize<Dictionary<string, string>>(output.ToArray(), JsonOpts)
			?? new Dictionary<string, string>();
	}
}

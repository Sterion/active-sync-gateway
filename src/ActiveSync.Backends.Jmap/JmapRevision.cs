using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;

namespace ActiveSync.Backends.Jmap;

/// <summary>
///   Computes a content revision for a JMAP object (contact card / calendar event) that the diff
///   engine can treat as the whole truth (H16).
/// </summary>
/// <remarks>
///   H5: a naive <c>SHA-256(element.GetRawText())</c> is sensitive to JSON member ORDER and
///   whitespace, both of which a server may vary between two <c>*/get</c> calls (a JSON object is
///   an unordered set — RFC 8259 §4). When it does, every item's revision flips and the diff
///   re-sends the entire collection. Hashing a CANONICAL form instead — object members sorted by
///   name, array order preserved, scalars written verbatim, no insignificant whitespace — makes
///   the revision depend only on the logical content, so a re-serialization no longer churns.
/// </remarks>
internal static class JmapRevision
{
	/// <summary>Stable 8-byte (16 hex chars) revision over the canonical serialization of <paramref name="element" />.</summary>
	public static string Compute(JsonElement element)
	{
		ArrayBufferWriter<byte> buffer = new();
		using (Utf8JsonWriter writer = new(buffer))
			WriteCanonical(element, writer);
		return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan), 0, 8);
	}

	private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
	{
		switch (element.ValueKind)
		{
			case JsonValueKind.Object:
				writer.WriteStartObject();
				// Sort members by name so a server re-ordering the same properties hashes identically.
				foreach (JsonProperty property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
				{
					writer.WritePropertyName(property.Name);
					WriteCanonical(property.Value, writer);
				}

				writer.WriteEndObject();
				break;
			case JsonValueKind.Array:
				// Array order is semantically significant in JSCard/JSCalendar — preserve it.
				writer.WriteStartArray();
				foreach (JsonElement item in element.EnumerateArray())
					WriteCanonical(item, writer);
				writer.WriteEndArray();
				break;
			default:
				// Scalars (string/number/true/false/null) are written verbatim.
				element.WriteTo(writer);
				break;
		}
	}
}

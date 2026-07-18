using System.Text;

namespace ActiveSync.Backends.Converters;

/// <summary>
///   Plain-text body preparation shared by the converters: UTF-8-aware truncation to a byte
///   budget. Truncating by character count (as the calendar/task/note converters used to) can
///   overrun the client's requested TruncationSize for multi-byte text and even split a code
///   point; this backs off to a code-point boundary so the result is always valid UTF-8 and
///   within the limit.
/// </summary>
public static class BodyText
{
	/// <summary>
	///   Returns the (possibly truncated) body, whether it was truncated, and the FULL UTF-8
	///   byte size (which the client uses to decide whether to re-fetch untruncated).
	/// </summary>
	public static (string Data, bool Truncated, long EstimatedBytes) ForBody(string text, long? limitBytes)
	{
		long estimated = Encoding.UTF8.GetByteCount(text);
		if (limitBytes is { } max && estimated > max)
			return (TruncateUtf8(text, max), true, estimated);
		return (text, false, estimated);
	}

	/// <summary>Truncates to at most <paramref name="maxBytes" /> UTF-8 bytes on a code-point boundary.</summary>
	public static string TruncateUtf8(string content, long maxBytes)
	{
		if (Encoding.UTF8.GetByteCount(content) <= maxBytes)
			return content;
		byte[] bytes = Encoding.UTF8.GetBytes(content);
		int len = (int)Math.Min(maxBytes, bytes.Length); // < bytes.Length after the guard above
		// Only back off when the cut lands INSIDE a code point — i.e. the first byte being
		// removed is a continuation byte. A cut exactly on a boundary keeps the whole prefix
		// (backing off unconditionally would drop a fully valid trailing character).
		if ((bytes[len] & 0xC0) == 0x80)
		{
			while (len > 0 && (bytes[len - 1] & 0xC0) == 0x80)
				len--;
			if (len > 0)
				len--; // the lead byte of the split code point
		}

		return Encoding.UTF8.GetString(bytes, 0, len);
	}
}

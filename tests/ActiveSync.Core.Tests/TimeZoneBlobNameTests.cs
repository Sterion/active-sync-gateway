using ActiveSync.Backends.Common.Converters;

namespace ActiveSync.Core.Tests;

/// <summary>
///   TimeZoneBlob.WriteName truncates the 32-UTF-16-unit StandardName/DaylightName field at
///   an arbitrary byte offset (62 of the 64-byte field, reserving 2 bytes). When the cut lands
///   between the two UTF-16 code units of a surrogate pair (an astral character, e.g. an emoji),
///   the written blob ends in a lone high surrogate with no matching low surrogate — invalid
///   UTF-16. Driven directly against the internal method (via InternalsVisibleTo): a real
///   TimeZoneInfo.StandardName can never actually carry a surrogate pair through this test's
///   normal decode path either — Encoding.Unicode.GetString itself silently replaces an unpaired
///   surrogate with U+FFFD on decode, which would hide the defect — so this reads the raw UTF-16
///   code units directly off the byte buffer instead of decoding through Encoding.Unicode.
/// </summary>
public class TimeZoneBlobNameTests
{
	[Fact]
	public void WriteName_NeverEndsInAnUnpairedHighSurrogate()
	{
		// 30 ASCII characters (30 UTF-16 units) followed by an astral emoji (a surrogate PAIR,
		// 2 units) = 32 units = exactly the 64-byte field width. The 62-byte cut keeps the first
		// 31 units: the 30 ASCII characters plus the emoji's high surrogate alone.
		string name = new string('A', 30) + "\U0001F600"; // U+1F600 GRINNING FACE
		byte[] destination = new byte[64];

		TimeZoneBlob.WriteName(destination, name);

		for (int offset = 0; offset + 1 < destination.Length; offset += 2)
		{
			char unit = (char)BitConverter.ToUInt16(destination, offset);
			if (unit == '\0')
				break; // reached the unwritten tail

			if (!char.IsHighSurrogate(unit))
				continue;

			bool hasMatchingLowSurrogate = offset + 3 < destination.Length &&
				char.IsLowSurrogate((char)BitConverter.ToUInt16(destination, offset + 2));
			Assert.True(hasMatchingLowSurrogate,
				$"WriteName split a surrogate pair: a lone high surrogate (0x{(int)unit:X4}) " +
				$"was written at byte offset {offset} with no matching low surrogate after it.");
		}
	}
}

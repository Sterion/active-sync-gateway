using System.Security.Cryptography;

namespace ActiveSync.Plugin.Local;

/// <summary>
///   Mints the store's own item keys: 16 characters of lowercase base32 over 48 bits of
///   millisecond timestamp and 32 random bits, so keys sort by creation time and never collide in
///   practice.
/// </summary>
/// <remarks>
///   Fixed width and a closed alphabet are both load-bearing for mail, where the key is parsed back
///   out of a file name BY POSITION rather than by splitting on a delimiter (a delimiter split
///   cannot survive "Smith, John — invoice.eml"), and where the key travels onto the wire inside an
///   EAS ServerId, which is length-capped. A key derived from an arbitrary file name would satisfy
///   neither.
/// </remarks>
internal static class ItemKeyMint
{
	/// <summary>Characters a minted key is made of: base32 without the vowel-and-lookalike noise.</summary>
	private const string Alphabet = "0123456789abcdefghijklmnopqrstuv";

	/// <summary>The exact length of a minted key.</summary>
	public const int Length = 16;

	/// <summary>Mints a fresh, time-ordered key.</summary>
	public static string Mint()
	{
		Span<byte> bytes = stackalloc byte[10];
		long milliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		bytes[0] = (byte)(milliseconds >> 40);
		bytes[1] = (byte)(milliseconds >> 32);
		bytes[2] = (byte)(milliseconds >> 24);
		bytes[3] = (byte)(milliseconds >> 16);
		bytes[4] = (byte)(milliseconds >> 8);
		bytes[5] = (byte)milliseconds;
		RandomNumberGenerator.Fill(bytes[6..]);

		Span<char> key = stackalloc char[Length];
		for (int group = 0; group < 2; group++)
		{
			// 5 bytes -> 40 bits -> 8 base32 characters, most significant first.
			long block = 0;
			for (int index = 0; index < 5; index++)
				block = (block << 8) | bytes[group * 5 + index];
			for (int index = 0; index < 8; index++)
				key[group * 8 + index] = Alphabet[(int)((block >> (35 - index * 5)) & 0x1F)];
		}

		return new string(key);
	}

	/// <summary>Whether a value is one of this store's minted keys (and not, say, a hand-chosen file name).</summary>
	public static bool IsMinted(ReadOnlySpan<char> value)
	{
		if (value.Length != Length)
			return false;
		foreach (char character in value)
			if (!Alphabet.Contains(character, StringComparison.Ordinal))
				return false;
		return true;
	}
}

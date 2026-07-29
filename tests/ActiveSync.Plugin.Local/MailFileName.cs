using System.Text;
using ActiveSync.Contracts;

namespace ActiveSync.Plugin.Local;

/// <summary>
///   A message's file name: <c>&lt;key&gt;[.&lt;flags&gt;[.&lt;categories&gt;]].eml</c>. Mail's
///   metadata lives IN the name because the store keeps no sidecar file — a flag change is a
///   rename, and the message bytes are never rewritten.
/// </summary>
/// <remarks>
///   <para>
///     The key occupies a fixed-width, closed-alphabet slot at the front and is read back BY
///     POSITION. Deriving it by splitting on a delimiter would break on the first file called
///     "Smith, John — invoice.eml", and two dropped files whose stems collided would silently
///     become one item; the key also travels onto the wire inside an EAS ServerId, which is
///     length-capped, so an arbitrary file name could not serve as one.
///   </para>
///   <para>
///     A rename changes the FILE NAME, never the key — the host's snapshot and echo suppression
///     are keyed on the item key alone, so marking a message read is invisible to them.
///   </para>
/// </remarks>
internal readonly record struct MailFileName
{
	/// <summary>The extension every message file carries.</summary>
	public const string Extension = ".eml";

	/// <summary>Flag letters, in the order they are always written.</summary>
	private const string FlagLetters = "SFARD";

	/// <summary>
	///   How many bytes of encoded categories a name may carry. NAME_MAX is 255 bytes, so an
	///   unbounded set of free-text EAS categories would make an ordinary Sync Change throw
	///   PathTooLong; overflow is DROPPED (and the revision reports what was actually stored),
	///   which is the same stance the IMAP store takes on keywords it cannot represent.
	/// </summary>
	private const int MaxCategoryBytes = 120;

	private MailFileName(string key, MailFlags flags, IReadOnlyList<string> categories)
	{
		Key = key;
		Flags = flags;
		Categories = categories;
	}

	/// <summary>The item key — the minted token at the front of the name.</summary>
	public string Key { get; }

	/// <summary>The message's flags, as encoded in the name.</summary>
	public MailFlags Flags { get; }

	/// <summary>The message's user categories, as encoded in the name.</summary>
	public IReadOnlyList<string> Categories { get; }

	/// <summary>
	///   Parses a file name this store owns. Returns false for anything else — a hand-dropped
	///   file, an editor swap file — which the caller adopts by renaming it once.
	/// </summary>
	public static bool TryParse(string fileName, out MailFileName parsed)
	{
		parsed = default;
		if (!fileName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
			return false;

		string stem = fileName[..^Extension.Length];
		if (stem.Length < ItemKeyMint.Length || !ItemKeyMint.IsMinted(stem.AsSpan(0, ItemKeyMint.Length)))
			return false;

		string key = stem[..ItemKeyMint.Length];
		string rest = stem[ItemKeyMint.Length..];
		if (rest.Length == 0)
		{
			parsed = new MailFileName(key, new MailFlags(), []);
			return true;
		}

		if (rest[0] != '.')
			return false;

		string[] segments = rest[1..].Split('.');
		if (segments.Length > 2)
			return false;

		MailFlags flags = new()
		{
			Seen = segments[0].Contains('S', StringComparison.Ordinal),
			Flagged = segments[0].Contains('F', StringComparison.Ordinal),
			Answered = segments[0].Contains('A', StringComparison.Ordinal),
			Forwarded = segments[0].Contains('R', StringComparison.Ordinal),
			Draft = segments[0].Contains('D', StringComparison.Ordinal)
		};
		foreach (char letter in segments[0])
			if (!FlagLetters.Contains(letter, StringComparison.Ordinal))
				return false;

		parsed = new MailFileName(key, flags, segments.Length == 2 ? DecodeCategories(segments[1]) : []);
		return true;
	}

	/// <summary>
	///   Composes the file name for a key, flags and categories, dropping categories that do not
	///   fit. <paramref name="storedCategories" /> reports what actually made it in — the caller
	///   MUST build the revision from that, not from what it asked for, or an over-long set would
	///   re-send the message on every sync round forever.
	/// </summary>
	public static string Compose(
		string key, MailFlags flags, IReadOnlyList<string> categories,
		out IReadOnlyList<string> storedCategories)
	{
		string letters = Encode(flags);
		string encoded = EncodeCategories(categories, out storedCategories);
		if (encoded.Length == 0)
			return letters.Length == 0
				? key + Extension
				: $"{key}.{letters}{Extension}";

		return $"{key}.{letters}.{encoded}{Extension}";
	}

	/// <summary>
	///   The revision of a message: its name-carried metadata plus the file's size and write time.
	///   Content is not hashed — message bytes are immutable except for a draft rewrite, where the
	///   size and timestamp both move, and hashing a whole mailbox on every Ping poll would not be
	///   affordable.
	/// </summary>
	public static string RevisionOf(
		MailFlags flags, IReadOnlyList<string> categories, long length, long writeTicks)
	{
		return $"{Encode(flags)}|{EncodeCategories(categories, out _)}|{length:x}|{writeTicks:x}";
	}

	/// <summary>The flag letters, always in the same order so the name and the revision are stable.</summary>
	private static string Encode(MailFlags flags)
	{
		StringBuilder letters = new(FlagLetters.Length);
		if (flags.Seen)
			letters.Append('S');
		if (flags.Flagged)
			letters.Append('F');
		if (flags.Answered)
			letters.Append('A');
		if (flags.Forwarded)
			letters.Append('R');
		if (flags.Draft)
			letters.Append('D');
		return letters.ToString();
	}

	/// <summary>Percent-encodes and '+'-joins the categories, dropping whatever exceeds the byte cap.</summary>
	private static string EncodeCategories(
		IReadOnlyList<string> categories, out IReadOnlyList<string> storedCategories)
	{
		if (categories.Count == 0)
		{
			storedCategories = [];
			return "";
		}

		List<string> stored = [];
		StringBuilder encoded = new();
		foreach (string category in categories)
		{
			if (string.IsNullOrWhiteSpace(category))
				continue;
			// '.' is unreserved, so EscapeDataString leaves it — and it is this name's own
			// segment separator, so it has to go too.
			string piece = Uri.EscapeDataString(category.Trim())
				.Replace(".", "%2E", StringComparison.Ordinal);
			if (encoded.Length + piece.Length + 1 > MaxCategoryBytes)
				continue;
			if (encoded.Length > 0)
				encoded.Append('+');
			encoded.Append(piece);
			stored.Add(category.Trim());
		}

		storedCategories = stored;
		return encoded.ToString();
	}

	private static IReadOnlyList<string> DecodeCategories(string encoded)
	{
		if (encoded.Length == 0)
			return [];

		List<string> categories = [];
		foreach (string piece in encoded.Split('+', StringSplitOptions.RemoveEmptyEntries))
			try
			{
				categories.Add(Uri.UnescapeDataString(piece));
			}
			catch (UriFormatException)
			{
				categories.Add(piece);
			}

		return categories;
	}
}

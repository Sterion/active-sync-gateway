namespace ActiveSync.Backends.Common.Converters;

/// <summary>
///   Which of a message's IMAP/JMAP keywords count as user CATEGORIES — a mail store's own
///   classification, shared because the imap and jmap stores must agree on it: the same list
///   goes into the item revision string, into the category writes, and over the contract as
///   <c>MailItem.Categories</c>. Nothing here touches EAS.
/// </summary>
public static class MailKeywords
{
	// Managed/system keywords that must never surface as user categories (nor be removed
	// by a client clearing its category list). Everything backslash-prefixed is an IMAP
	// system flag by definition.
	private static readonly HashSet<string> SystemKeywords = new(StringComparer.OrdinalIgnoreCase)
	{
		"$Forwarded", "$MDNSent", "$SubmitPending", "$Submitted",
		"$Junk", "$NotJunk", "Junk", "NonJunk", "$Phishing"
	};

	/// <summary>
	///   The category-relevant subset of a message's keywords: system keywords filtered out,
	///   sorted for stable revision strings.
	/// </summary>
	public static IReadOnlyList<string> CategoryKeywords(IEnumerable<string>? keywords)
	{
		if (keywords is null)
			return [];
		return keywords
			.Where(k => k.Length > 0 && k[0] != '\\' && !SystemKeywords.Contains(k))
			.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
			.ToList();
	}
}

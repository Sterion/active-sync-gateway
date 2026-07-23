using ActiveSync.Backends.Imap;

namespace ActiveSync.Core.Tests;

/// <summary>
///   D6: EAS categories are free text but IMAP keywords are RFC 3501 atoms. The old
///   char-by-char '_' substitution collapsed distinct categories ("a b" and "a_b") onto the same
///   keyword, so the server-derived category could never match the client's original and the mail
///   revision string thrashed every Sync. A non-atom category is now DROPPED (returns empty, which
///   the caller filters) rather than mangled, so two distinct categories can no longer collide.
/// </summary>
public class ImapKeywordSanitizeTests
{
	[Fact]
	public void ValidAtom_PassesThroughUnchanged()
	{
		Assert.Equal("Work", ImapMailBackend.SanitizeKeyword("Work"));
		Assert.Equal("a_b", ImapMailBackend.SanitizeKeyword("a_b"));
	}

	[Fact]
	public void NonAtomCategory_IsDropped_NotMangled()
	{
		// A space is not an atom char: the category is dropped (empty), not turned into "a_b".
		Assert.Equal("", ImapMailBackend.SanitizeKeyword("a b"));
	}

	[Fact]
	public void DistinctCategories_DoNotCollide()
	{
		// The heart of D6: "a b" (non-atom) and "a_b" (valid atom) must not sanitize to the same
		// keyword, or the two categories fold together and churn the revision.
		string spaced = ImapMailBackend.SanitizeKeyword("a b");
		string underscored = ImapMailBackend.SanitizeKeyword("a_b");
		Assert.NotEqual(spaced, underscored);
	}

	[Fact]
	public void CategoryWithSpecials_IsDropped()
	{
		// Any atom-special (RFC 3501) makes the whole category un-round-trippable → dropped.
		Assert.Equal("", ImapMailBackend.SanitizeKeyword("re[view]"));
		Assert.Equal("", ImapMailBackend.SanitizeKeyword("50%"));
	}
}

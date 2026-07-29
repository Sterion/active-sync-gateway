using ActiveSync.Backends.Dav;
using ActiveSync.Contracts;

namespace ActiveSync.Core.Tests;

/// <summary>
///   Shared-calendar entry parsing ("href|ro") and config validation — the syntax lives with the
///   caldav provider that reads the setting, never in the plugin contract, which carries only the
///   typed <see cref="SharedCollection" /> result.
/// </summary>
public class SharedCollectionEntryTests
{
	[Theory]
	[InlineData("/dav/cal/team/", "/dav/cal/team/", false)]
	[InlineData("/dav/cal/team/|ro", "/dav/cal/team/", true)]
	[InlineData(" /cal/x |RO", "/cal/x", true)]
	[InlineData("https://dav.example.com/cal/y/|rw", "https://dav.example.com/cal/y/", false)]
	public void Parse_SplitsHrefAndMode(string entry, string expectedHref, bool expectedReadOnly)
	{
		SharedCollection parsed = SharedCollectionEntry.Parse(entry);
		Assert.Equal(expectedHref, parsed.Href);
		Assert.Equal(expectedReadOnly, parsed.ReadOnly);
	}

	[Theory]
	[InlineData("/dav/cal/team/")]
	[InlineData("/dav/cal/team/|ro")]
	[InlineData("https://dav.example.com/cal/y/")]
	public void Validate_AcceptsPathsAndSameHostUrls(string entry)
	{
		Assert.Null(SharedCollectionEntry.Validate(entry, "https://dav.example.com"));
	}

	[Theory]
	[InlineData("relative/path")] // not absolute
	[InlineData("ftp://dav.example.com/cal/")] // wrong scheme
	[InlineData("https://other.example.com/cal/")] // different host than BaseUrl
	[InlineData("/cal/x|banana")] // unknown mode suffix
	public void Validate_RejectsUnusableEntries(string entry)
	{
		Assert.NotNull(SharedCollectionEntry.Validate(entry, "https://dav.example.com"));
	}

	// The unknown-mode-suffix error told the operator "|ro" is the only recognized suffix,
	// even though Parse (and Validate's own check two lines above the message) accepts "|rw" too
	// — so an operator who mistyped "|rw" as "|rww" was never told the suffix they meant exists.
	[Fact]
	public void Validate_UnknownModeSuffix_MentionsBothRecognizedSuffixes()
	{
		string? message = SharedCollectionEntry.Validate("/cal/x|rww", "https://dav.example.com");

		Assert.NotNull(message);
		Assert.Contains("\"|ro\"", message);
		Assert.Contains("\"|rw\"", message);
	}

	// Parse is the RUNTIME path (CalDavBackendProvider parses configured SharedCollections
	// with it). It used to fail OPEN — any mode suffix that was not "ro" produced a read-WRITE
	// grant, so a typo like "|read-only" or "|r" silently handed a shared collection full write
	// access. A present-but-unrecognized EXACT "ro"/"rw" suffix must fail CLOSED (read-only):
	// read-write is only ever granted by an explicit "|rw" or by no suffix at all (a plain href is
	// the owner's own).
	[Theory]
	[InlineData("/cal/team/|ro")]
	[InlineData("/cal/team/|RO")]
	public void Parse_RecognizedReadOnlySuffix_FailsClosedAsReadOnly(string entry)
	{
		Assert.True(SharedCollectionEntry.Parse(entry).ReadOnly);
	}

	// Behaviour change (replaces an earlier, narrower version of this test): a trailing "|xxx" segment is a
	// mode delimiter ONLY when xxx is exactly "ro"/"rw". These entries used to be misparsed as
	// href=truncated-before-the-pipe + readOnly=true — an href-corruption bug; Validate() is the
	// layer that rejects a typo'd suffix outright — Parse() itself must not guess at one, so it now
	// treats the whole string as a literal href (default read-write, exactly like a plain href with
	// no suffix at all).
	[Theory]
	[InlineData("/cal/team/|banana")]
	[InlineData("/cal/team/|r")]
	[InlineData("/cal/team/|read-only")]
	[InlineData("/cal/team/|")]
	public void Parse_UnrecognizedTrailingSegment_IsKeptAsLiteralHref(string entry)
	{
		SharedCollection parsed = SharedCollectionEntry.Parse(entry);
		Assert.Equal(entry, parsed.Href);
		Assert.False(parsed.ReadOnly);
	}

	// DAV hrefs may legitimately contain '|'. Only a trailing segment that is EXACTLY
	// "ro"/"rw" is a mode suffix; anything else (including a bare href with a '|' in the middle
	// of a path segment) is part of the href and must survive Parse verbatim, not get truncated
	// at the last '|' and reinterpreted as a mode.
	[Theory]
	[InlineData("/cal/a|b/", "/cal/a|b/", false)]
	[InlineData("https://dav.example.com/cal/x|y/", "https://dav.example.com/cal/x|y/", false)]
	public void Parse_HrefContainingPipe_IsNotTruncated(string entry, string expectedHref, bool expectedReadOnly)
	{
		SharedCollection parsed = SharedCollectionEntry.Parse(entry);
		Assert.Equal(expectedHref, parsed.Href);
		Assert.Equal(expectedReadOnly, parsed.ReadOnly);
	}

	// The cross-host guard used `if (Uri.TryCreate(baseUrl, ...) && hostsDiffer)`, so an
	// UNPARSEABLE BaseUrl made the whole condition false and an absolute href to an attacker host
	// validated. A malformed BaseUrl must fail CLOSED — an absolute URL cannot be admitted when
	// there is no base host to compare it against.
	[Theory]
	[InlineData("https://evil.example.com/cal/", "not-a-url")]
	[InlineData("https://evil.example.com/cal/", "")]
	[InlineData("https://evil.example.com/cal/", "dav.example.com")] // no scheme → not absolute
	public void Validate_AbsoluteUrl_WithUnparseableBaseUrl_IsRejected(string entry, string baseUrl)
	{
		Assert.NotNull(SharedCollectionEntry.Validate(entry, baseUrl));
	}

	// Guard: a same-host absolute URL against a parseable BaseUrl must still validate — the
	// fail-closed change must not reject the legitimate case.
	[Fact]
	public void Validate_AbsoluteSameHostUrl_WithParseableBaseUrl_IsAccepted()
	{
		Assert.Null(SharedCollectionEntry.Validate("https://dav.example.com/cal/", "https://dav.example.com/dav/"));
	}
}

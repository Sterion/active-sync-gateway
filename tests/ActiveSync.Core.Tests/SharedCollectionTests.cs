using ActiveSync.Contracts;
using ActiveSync.Core.Backend;

namespace ActiveSync.Core.Tests;

/// <summary>Shared-calendar entry parsing ("href|ro") and config validation.</summary>
public class SharedCollectionTests
{
	[Theory]
	[InlineData("/dav/cal/team/", "/dav/cal/team/", false)]
	[InlineData("/dav/cal/team/|ro", "/dav/cal/team/", true)]
	[InlineData(" /cal/x |RO", "/cal/x", true)]
	[InlineData("https://dav.example.com/cal/y/|rw", "https://dav.example.com/cal/y/", false)]
	public void Parse_SplitsHrefAndMode(string entry, string expectedHref, bool expectedReadOnly)
	{
		SharedCollection parsed = SharedCollection.Parse(entry);
		Assert.Equal(expectedHref, parsed.Href);
		Assert.Equal(expectedReadOnly, parsed.ReadOnly);
	}

	[Theory]
	[InlineData("/dav/cal/team/")]
	[InlineData("/dav/cal/team/|ro")]
	[InlineData("https://dav.example.com/cal/y/")]
	public void Validate_AcceptsPathsAndSameHostUrls(string entry)
	{
		Assert.Null(SharedCollection.Validate(entry, "https://dav.example.com"));
	}

	[Theory]
	[InlineData("relative/path")] // not absolute
	[InlineData("ftp://dav.example.com/cal/")] // wrong scheme
	[InlineData("https://other.example.com/cal/")] // different host than BaseUrl
	[InlineData("/cal/x|banana")] // unknown mode suffix
	public void Validate_RejectsUnusableEntries(string entry)
	{
		Assert.NotNull(SharedCollection.Validate(entry, "https://dav.example.com"));
	}

	// K62: Parse is the RUNTIME path (CalDavBackendProvider parses configured SharedCollections
	// with it). It used to fail OPEN — any mode suffix that was not "ro" produced a read-WRITE
	// grant, so a typo like "|read-only" or "|r" silently handed a shared collection full write
	// access. A present-but-unrecognized suffix must fail CLOSED (read-only): read-write is only
	// ever granted by an explicit "|rw" or by no suffix at all (a plain href is the owner's own).
	[Theory]
	[InlineData("/cal/team/|banana")]
	[InlineData("/cal/team/|r")]
	[InlineData("/cal/team/|read-only")]
	[InlineData("/cal/team/|")]
	public void Parse_UnknownModeSuffix_FailsClosedAsReadOnly(string entry)
	{
		Assert.True(SharedCollection.Parse(entry).ReadOnly);
	}

	// K64: the cross-host guard used `if (Uri.TryCreate(baseUrl, ...) && hostsDiffer)`, so an
	// UNPARSEABLE BaseUrl made the whole condition false and an absolute href to an attacker host
	// validated. A malformed BaseUrl must fail CLOSED — an absolute URL cannot be admitted when
	// there is no base host to compare it against.
	[Theory]
	[InlineData("https://evil.example.com/cal/", "not-a-url")]
	[InlineData("https://evil.example.com/cal/", "")]
	[InlineData("https://evil.example.com/cal/", "dav.example.com")] // no scheme → not absolute
	public void Validate_AbsoluteUrl_WithUnparseableBaseUrl_IsRejected(string entry, string baseUrl)
	{
		Assert.NotNull(SharedCollection.Validate(entry, baseUrl));
	}

	// K64 guard: a same-host absolute URL against a parseable BaseUrl must still validate — the
	// fail-closed change must not reject the legitimate case.
	[Fact]
	public void Validate_AbsoluteSameHostUrl_WithParseableBaseUrl_IsAccepted()
	{
		Assert.Null(SharedCollection.Validate("https://dav.example.com/cal/", "https://dav.example.com/dav/"));
	}
}

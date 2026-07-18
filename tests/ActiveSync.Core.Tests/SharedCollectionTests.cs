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
}

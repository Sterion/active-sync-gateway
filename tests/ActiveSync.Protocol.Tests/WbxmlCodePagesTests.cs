using ActiveSync.Protocol.Wbxml;

namespace ActiveSync.Protocol.Tests;

/// <summary>
///   Guards the hand-transcribed MS-ASWBXML token tables against the one mistake that turns a
///   table edit into a total, permanent outage: a duplicate tag NAME within a page. <see
///   cref="WbxmlCodePages.CodePage.Reverse" /> builds a name→token dictionary via
///   <c>ToDictionary</c>, which throws <see cref="ArgumentException" /> on a duplicate value; that
///   call runs inside the record constructor invoked from the <c>static readonly Pages</c>
///   field initializer, so the very first touch of <see cref="WbxmlCodePages" /> anywhere in the
///   process caches a <see cref="TypeInitializationException" /> and rethrows it on every
///   subsequent access for the life of the process — every EAS request 500s (W4).
/// </summary>
public class WbxmlCodePagesTests
{
	[Fact]
	public void EveryCodePage_HasNoDuplicateTagNames()
	{
		foreach (WbxmlCodePages.CodePage page in WbxmlCodePages.Pages)
		{
			int distinctNames = page.Tokens.Values.Distinct(StringComparer.Ordinal).Count();
			Assert.True(distinctNames == page.Tokens.Count,
				$"Code page {page.Index} ({page.Namespace}) has a duplicate tag name — this " +
				"crashes CodePage.Reverse's static initializer for the life of the process.");
		}
	}

	[Fact]
	public void EveryCodePage_IndexMatchesItsPositionInPages()
	{
		for (int i = 0; i < WbxmlCodePages.Pages.Count; i++)
			Assert.Equal(i, WbxmlCodePages.Pages[i].Index);
	}
}

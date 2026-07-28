using System.Xml.Linq;
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

	/// <summary>
	///   W7: Find (page 25) assigns MaxPictures/MaxSize/Picture in the reverse order of every
	///   sibling page (Search page 15, ResolveRecipients page 10 both put Picture first) — a
	///   shape internal consistency alone flagged as needing a human with the actual MS-ASWBXML
	///   text rather than a guess. Verified against the published spec, revision 24.0
	///   (2025-05-20), section 2.1.2.1.26 "Code Page 25: Find": the byte assignment below is
	///   exactly what the spec lists. This is coverage that pins the answer, not a red-first
	///   proof of a bug — the table was already correct; only the uncertainty was the defect.
	/// </summary>
	[Fact]
	public void FindPage25_PictureTripletOrder_MatchesMsAswbxmlSpec()
	{
		WbxmlCodePages.CodePage find = WbxmlCodePages.ForIndex(25)!;
		Assert.Equal("MaxPictures", find.Tokens[0x20]);
		Assert.Equal("MaxSize", find.Tokens[0x21]);
		Assert.Equal("Picture", find.Tokens[0x22]);

		XDocument doc = new(
			new XElement(EasNamespaces.Find + "Find",
				new XElement(EasNamespaces.Find + "ExecuteSearch",
					new XElement(EasNamespaces.Find + "GalSearchCriterion",
						new XElement(EasNamespaces.Find + "Options",
							new XElement(EasNamespaces.Find + "Picture",
								new XElement(EasNamespaces.Find + "MaxSize", "0"),
								new XElement(EasNamespaces.Find + "MaxPictures", "0")))))));

		byte[] bytes = WbxmlEncoder.Encode(doc);

		// Header (4) + SWITCH_PAGE to 25 (2) + Find(0x45) + ExecuteSearch(0x47) +
		// GalSearchCriterion(0x59) + Options(0x4C) + Picture-with-content(0x62) +
		// MaxSize-with-content(0x61) + STR_I "0" + END + MaxPictures-with-content(0x60) +
		// STR_I "0" + END + END(Picture) + END(Options) + END(GalSearchCriterion) +
		// END(ExecuteSearch) + END(Find).
		byte[] expected =
		[
			0x03, 0x01, 0x6A, 0x00, // WBXML header
			0x00, 0x19, // SWITCH_PAGE 25
			0x45, // Find, with content
			0x47, // ExecuteSearch, with content
			0x59, // GalSearchCriterion, with content
			0x4C, // Options, with content
			0x62, // Picture, with content
			0x61, // MaxSize, with content
			0x03, (byte)'0', 0x00, // STR_I "0"
			0x01, // END MaxSize
			0x60, // MaxPictures, with content
			0x03, (byte)'0', 0x00, // STR_I "0"
			0x01, // END MaxPictures
			0x01, // END Picture
			0x01, // END Options
			0x01, // END GalSearchCriterion
			0x01, // END ExecuteSearch
			0x01 // END Find
		];
		Assert.Equal(expected, bytes);
	}
}

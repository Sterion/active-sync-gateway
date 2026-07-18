using System.Xml.Linq;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;

namespace ActiveSync.Protocol.Tests;

/// <summary>Version parsing/ordering and the 16.x WBXML additions (Find page 25 round-trip).</summary>
public sealed class EasVersionTests
{
	[Theory]
	[InlineData("12.1", 12, 1)]
	[InlineData("14.1", 14, 1)]
	[InlineData("16.0", 16, 0)]
	[InlineData("16.1", 16, 1)]
	public void Parse_ReadsMajorMinor(string input, int major, int minor)
	{
		Assert.Equal(new EasVersion(major, minor), EasVersion.Parse(input));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("garbage")]
	public void Parse_Unparsable_FallsBackTo141(string? input)
	{
		Assert.Equal(EasVersion.V141, EasVersion.Parse(input));
	}

	[Fact]
	public void Ordering_GatesWork()
	{
		Assert.True(EasVersion.V161 > EasVersion.V160);
		Assert.True(EasVersion.V160 > EasVersion.V141);
		Assert.True(EasVersion.Parse("16.1") >= EasVersion.V160);
		Assert.False(EasVersion.Parse("14.1") >= EasVersion.V160);
	}

	[Fact]
	public void FindPage_RoundTripsThroughWbxml()
	{
		XNamespace f = EasNamespaces.Find;
		XDocument request = new(new XElement(f + "Find",
			new XElement(f + "SearchId", "42E25C08-4A50-4F3B-B056-11A8C1E52C05"),
			new XElement(f + "ExecuteSearch",
				new XElement(f + "GalSearchCriterion",
					new XElement(f + "Query", new XElement(f + "FreeText", "alice")),
					new XElement(f + "Options",
						new XElement(f + "Range", "0-9"),
						new XElement(f + "Picture", new XElement(f + "MaxPictures", "5")))))));

		XDocument decoded = WbxmlDecoder.Decode(WbxmlEncoder.Encode(request));
		Assert.Equal(request.ToString(), decoded.ToString());
	}

	[Fact]
	public void SixteenXTokens_RoundTripAcrossPages()
	{
		XNamespace asb = EasNamespaces.AirSyncBase;
		XNamespace cal = EasNamespaces.Calendar;
		XNamespace e2 = EasNamespaces.Email2;
		XNamespace cm = EasNamespaces.ComposeMail;
		XNamespace pv = EasNamespaces.Provision;
		XDocument doc = new(new XElement(cal + "Recurrence", // any real container tag works as the root
			new XElement(cal + "ClientUid", "client-uid-1"),
			new XElement(asb + "Location", new XElement(asb + "DisplayName", "HQ")),
			new XElement(asb + "InstanceId", "20260720T100000Z"),
			new XElement(e2 + "IsDraft", "1"),
			new XElement(e2 + "Send"),
			new XElement(cm + "Forwardees",
				new XElement(cm + "Forwardee",
					new XElement(cm + "Name", "R"),
					new XElement(cm + "Email", "r@example.com"))),
			new XElement(pv + "AccountOnlyRemoteWipe")));

		XDocument decoded = WbxmlDecoder.Decode(WbxmlEncoder.Encode(doc));
		Assert.Equal(doc.ToString(), decoded.ToString());
	}
}

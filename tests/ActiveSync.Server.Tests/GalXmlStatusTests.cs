using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;
using ActiveSync.Server.Eas.Content;

namespace ActiveSync.Server.Tests;

/// <summary>
///   The HOST half of the GAL photo rules: the store reports a typed
///   <see cref="GalPictureStatus" /> (it enforced the limits) and the host maps it onto the
///   MS-ASCMD Picture Status wire values — 1 + Data / 173 / 174 / 175. ResolveRecipients projects
///   its own RR-namespace Picture from the same record, so the two must stay in step.
/// </summary>
public class GalXmlStatusTests
{
	private static readonly XNamespace Gal = EasNamespaces.Gal;
	private static readonly byte[] PhotoBytes = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4];

	private static XElement Picture(GalPictureStatus status, GalPicture? photo = null)
	{
		List<XElement> properties = GalXml.ToGalProperties(new GalEntry
		{
			DisplayName = "Photo Person",
			Picture = new GalPictureResult { Status = status, Picture = photo }
		});
		return properties.Single(e => e.Name == Gal + "Picture");
	}

	[Fact]
	public void Available_YieldsStatus1AndData()
	{
		XElement picture = Picture(GalPictureStatus.Available,
			new GalPicture { Data = PhotoBytes, ContentType = "image/jpeg" });

		Assert.Equal("1", picture.Element(Gal + "Status")?.Value);
		Assert.Equal(PhotoBytes, Convert.FromBase64String(picture.Element(Gal + "Data")!.Value));
	}

	[Theory]
	[InlineData(GalPictureStatus.None, "173")]
	[InlineData(GalPictureStatus.OverSizeLimit, "174")]
	[InlineData(GalPictureStatus.OverCountLimit, "175")]
	public void EveryOtherStatus_YieldsItsWireValue_WithoutData(GalPictureStatus status, string wire)
	{
		XElement picture = Picture(status);

		Assert.Equal(wire, picture.Element(Gal + "Status")?.Value);
		Assert.Null(picture.Element(Gal + "Data"));
	}

	[Fact]
	public void NoPictureRequested_EmitsNoPictureElementAtAll()
	{
		List<XElement> properties = GalXml.ToGalProperties(new GalEntry { DisplayName = "Plain Person" });

		Assert.DoesNotContain(properties, e => e.Name == Gal + "Picture");
	}
}

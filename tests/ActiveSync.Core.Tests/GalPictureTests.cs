using System.Xml.Linq;
using ActiveSync.Backends.Converters;
using ActiveSync.Protocol.Wbxml;

namespace ActiveSync.Core.Tests;

/// <summary>The MS-ASCMD GAL photo rules implemented by ContactConverter.AppendGalPicture.</summary>
public sealed class GalPictureTests
{
	private static readonly XNamespace Gal = EasNamespaces.Gal;
	private static readonly byte[] PhotoBytes = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4];

	private static readonly string VcardWithPhoto =
		"BEGIN:VCARD\r\nVERSION:3.0\r\nUID:p1\r\nFN:Photo Person\r\n" +
		$"PHOTO;ENCODING=b;TYPE=JPEG:{Convert.ToBase64String(PhotoBytes)}\r\nEND:VCARD\r\n";

	private const string VcardWithoutPhoto =
		"BEGIN:VCARD\r\nVERSION:3.0\r\nUID:p2\r\nFN:Plain Person\r\nEND:VCARD\r\n";

	[Fact]
	public void PhotoPresent_YieldsStatus1AndData()
	{
		List<XElement> entry = new();
		bool granted = ContactConverter.AppendGalPicture(entry, VcardWithPhoto, null, false);
		Assert.True(granted);
		XElement picture = Assert.Single(entry);
		Assert.Equal("1", picture.Element(Gal + "Status")?.Value);
		Assert.Equal(PhotoBytes, Convert.FromBase64String(picture.Element(Gal + "Data")!.Value));
	}

	[Fact]
	public void NoPhoto_YieldsStatus173()
	{
		List<XElement> entry = new();
		Assert.False(ContactConverter.AppendGalPicture(entry, VcardWithoutPhoto, null, false));
		Assert.Equal("173", Assert.Single(entry).Element(Gal + "Status")?.Value);
	}

	[Fact]
	public void PhotoOverMaxSize_YieldsStatus174_WithoutData()
	{
		List<XElement> entry = new();
		Assert.False(ContactConverter.AppendGalPicture(entry, VcardWithPhoto, PhotoBytes.Length - 1, false));
		XElement picture = Assert.Single(entry);
		Assert.Equal("174", picture.Element(Gal + "Status")?.Value);
		Assert.Null(picture.Element(Gal + "Data"));
	}

	[Fact]
	public void LimitReached_YieldsStatus175_EvenWhenAPhotoExists()
	{
		List<XElement> entry = new();
		Assert.False(ContactConverter.AppendGalPicture(entry, VcardWithPhoto, null, true));
		XElement picture = Assert.Single(entry);
		Assert.Equal("175", picture.Element(Gal + "Status")?.Value);
		Assert.Null(picture.Element(Gal + "Data"));
	}
}

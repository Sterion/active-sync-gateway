using ActiveSync.Backends.Common.Converters;
using ActiveSync.Contracts;

namespace ActiveSync.Core.Tests;

/// <summary>
///   The GAL photo rules as the STORE now reports them: a typed
///   <see cref="GalPictureResult" /> whose <see cref="GalPictureStatus" /> distinguishes "has
///   none" from "over a limit" (a bare nullable photo could not). The MS-ASCMD wire statuses the
///   host maps these onto — 1 / 173 / 174 / 175 — are asserted host-side in
///   <c>GalXmlStatusTests</c>.
/// </summary>
public sealed class GalPictureTests
{
	private static readonly byte[] PhotoBytes = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4];

	private static readonly string VcardWithPhoto =
		"BEGIN:VCARD\r\nVERSION:3.0\r\nUID:p1\r\nFN:Photo Person\r\n" +
		$"PHOTO;ENCODING=b;TYPE=JPEG:{Convert.ToBase64String(PhotoBytes)}\r\nEND:VCARD\r\n";

	private const string VcardWithoutPhoto =
		"BEGIN:VCARD\r\nVERSION:3.0\r\nUID:p2\r\nFN:Plain Person\r\nEND:VCARD\r\n";

	private static (GalPictureResult Picture, bool Granted) Build(
		string vcf, int? maxPhotoBytes, bool limitReached)
	{
		GalEntry? entry = ContactConverter.BuildGalEntry(
			vcf, "Person", wantPhoto: true, maxPhotoBytes, limitReached, out bool granted);
		Assert.NotNull(entry);
		Assert.NotNull(entry!.Picture);
		return (entry.Picture!, granted);
	}

	[Fact]
	public void PhotoPresent_YieldsAvailableWithData()
	{
		(GalPictureResult picture, bool granted) = Build(VcardWithPhoto, null, false);

		Assert.True(granted);
		Assert.Equal(GalPictureStatus.Available, picture.Status);
		Assert.Equal(PhotoBytes, picture.Picture!.Data.ToArray());
		Assert.Equal("image/jpeg", picture.Picture.ContentType);
	}

	[Fact]
	public void NoPhoto_YieldsNone()
	{
		(GalPictureResult picture, bool granted) = Build(VcardWithoutPhoto, null, false);

		Assert.False(granted);
		Assert.Equal(GalPictureStatus.None, picture.Status);
		Assert.Null(picture.Picture);
	}

	[Fact]
	public void PhotoOverMaxSize_YieldsOverSizeLimit_WithoutData()
	{
		(GalPictureResult picture, bool granted) = Build(VcardWithPhoto, PhotoBytes.Length - 1, false);

		Assert.False(granted);
		Assert.Equal(GalPictureStatus.OverSizeLimit, picture.Status);
		Assert.Null(picture.Picture);
	}

	[Fact]
	public void LimitReached_YieldsOverCountLimit_EvenWhenAPhotoExists()
	{
		(GalPictureResult picture, bool granted) = Build(VcardWithPhoto, null, true);

		Assert.False(granted);
		Assert.Equal(GalPictureStatus.OverCountLimit, picture.Status);
		Assert.Null(picture.Picture);
	}

	[Fact]
	public void PhotosNotRequested_LeavesThePictureUnset()
	{
		// A null Picture means "the client did not ask", which is distinct from every status
		// above — the host then emits no gal:Picture element at all.
		GalEntry? entry = ContactConverter.BuildGalEntry(
			VcardWithPhoto, "Person", wantPhoto: false, null, false, out bool granted);

		Assert.NotNull(entry);
		Assert.False(granted);
		Assert.Null(entry!.Picture);
	}
}

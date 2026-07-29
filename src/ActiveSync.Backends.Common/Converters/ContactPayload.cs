using ActiveSync.Contracts;
using FolkerKinzel.VCards;
using FolkerKinzel.VCards.Extensions;
using FolkerKinzel.VCards.Models;
using FolkerKinzel.VCards.Models.Properties;

namespace ActiveSync.Backends.Common.Converters;

/// <summary>
///   What a contact STORE needs from a vCard payload it already owns — no EAS anywhere: name a
///   resource from the card's own UID, and project a card into the contract's typed
///   <see cref="GalEntry" /> for <c>IDirectoryOperations.SearchGalAsync</c>. Shared because
///   CardDAV and the local contact store both serve the GAL. The vCard ⇄ ApplicationData
///   conversion is host-side, in ActiveSync.Eas.Conversion.
/// </summary>
public static class ContactPayload
{
	/// <summary>The card's UID, used to name the stored resource after its own document.</summary>
	public static string? ExtractUid(string vcf)
	{
		ContactIDProperty? id = Vcf.Parse(vcf).FirstOrDefault()?.ContactID;
		return id?.Value?.String ?? id?.Value?.Guid?.ToString() ?? id?.Value?.Uri?.ToString();
	}

	/// <summary>
	///   Parses the vCard ONCE and produces the typed GAL entry plus (optionally) its photo
	///   outcome. Returns null when the card is unparsable or does not match the query. The STORE
	///   enforces the photo limits here (it holds the request and counts grants across the whole
	///   result set); the HOST maps <see cref="GalPictureStatus" /> to the MS-ASCMD wire statuses.
	///   Returns the entry with <see cref="GalEntry.Picture" /> null when
	///   <paramref name="wantPhoto" /> is false (the client did not request pictures at all).
	/// </summary>
	public static GalEntry? BuildGalEntry(
		string vcf, string query, bool wantPhoto, int? maxPhotoBytes, bool photoLimitReached, out bool photoGranted)
	{
		photoGranted = false;
		if (Vcf.Parse(vcf).FirstOrDefault() is not { } vcard)
			return null;
		GalEntry? entry = ToGalEntry(vcard, query);
		if (entry is null)
			return null;
		if (wantPhoto)
		{
			GalPictureResult picture = BuildGalPicture(vcard, maxPhotoBytes, photoLimitReached);
			photoGranted = picture.Status == GalPictureStatus.Available;
			entry = entry with { Picture = picture };
		}

		return entry;
	}

	private static GalEntry? ToGalEntry(VCard vcard, string query)
	{
		string display = vcard.DisplayNames?.FirstOrDefault(d => d is not null)?.Value ?? "";
		string email = vcard.EMails.OrderByPref().FirstOrDefault(e => e?.Value is not null)?.Value ?? "";
		Name? name = vcard.NameViews?.FirstOrDefault(n => n is not null)?.Value;
		string first = name?.Given.FirstOrDefault() ?? "";
		string last = name?.Surnames.FirstOrDefault() ?? "";

		bool matches = new[] { display, email, first, last }
			.Any(v => v.Contains(query, StringComparison.OrdinalIgnoreCase));
		if (!matches)
			return null;

		string? phone = vcard.Phones.OrderByPref().FirstOrDefault(p => p?.Value is not null)?.Value;
		string? company = vcard.Organizations?.FirstOrDefault(o => o is not null)?.Value?.Name;
		return new GalEntry
		{
			DisplayName = display,
			EmailAddress = string.IsNullOrEmpty(email) ? null : email,
			FirstName = string.IsNullOrEmpty(first) ? null : first,
			LastName = string.IsNullOrEmpty(last) ? null : last,
			Phone = phone,
			Company = company
		};
	}

	/// <summary>
	///   The photo outcome per the MS-ASCMD photo rules, typed: the count limit outranks
	///   everything (the request's budget is spent), then "no photo", then the size limit —
	///   data is carried exactly when the status says so.
	/// </summary>
	private static GalPictureResult BuildGalPicture(VCard? vcard, int? maxSizeBytes, bool limitReached)
	{
		if (limitReached)
			return new GalPictureResult { Status = GalPictureStatus.OverCountLimit };

		byte[]? photo = vcard?.Photos?.FirstOrDefault(p => p is not null)?.Value?.Bytes;
		if (photo is not { Length: > 0 })
			return new GalPictureResult { Status = GalPictureStatus.None };

		if (maxSizeBytes is { } maxSize && photo.Length > maxSize)
			return new GalPictureResult { Status = GalPictureStatus.OverSizeLimit };

		return new GalPictureResult
		{
			Status = GalPictureStatus.Available,
			Picture = new GalPicture { Data = photo, ContentType = SniffImageContentType(photo) }
		};
	}

	/// <summary>Cheap magic-byte sniff for the photo's MIME type (vCard PHOTO carries no reliable one).</summary>
	private static string SniffImageContentType(ReadOnlySpan<byte> bytes)
	{
		return bytes switch
		{
			[0xFF, 0xD8, ..] => "image/jpeg",
			[0x89, 0x50, 0x4E, 0x47, ..] => "image/png",
			[0x47, 0x49, 0x46, ..] => "image/gif",
			_ => "application/octet-stream"
		};
	}
}

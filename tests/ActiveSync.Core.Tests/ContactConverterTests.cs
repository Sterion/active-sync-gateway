using System.Globalization;
using System.Text;
using System.Xml.Linq;
using ActiveSync.Backends.Common.Converters;
using ActiveSync.Contracts;
using ActiveSync.Protocol.Wbxml;

namespace ActiveSync.Core.Tests;

public class ContactConverterTests
{
	private static readonly XNamespace Contacts = EasNamespaces.Contacts;
	private static readonly XNamespace AirSyncBase = EasNamespaces.AirSyncBase;

	private const string ExistingVcard =
		"BEGIN:VCARD\r\n" +
		"VERSION:3.0\r\n" +
		"UID:c-1\r\n" +
		"N:Person;Test;;;\r\n" +
		"FN:Test Person\r\n" +
		"EMAIL;TYPE=INTERNET:one@example.com\r\n" +
		"EMAIL;TYPE=INTERNET:two@example.com\r\n" +
		"EMAIL;TYPE=INTERNET:three@example.com\r\n" +
		"EMAIL;TYPE=INTERNET:four@example.com\r\n" +
		"TEL;TYPE=CELL:+4512345678\r\n" +
		"ADR;TYPE=HOME:;;Main Street 1;Copenhagen;;2100;DK\r\n" +
		"ORG:Contoso;Research\r\n" +
		"TITLE:Engineer\r\n" +
		"URL:https://example.com/test\r\n" +
		"BDAY:1980-04-05\r\n" +
		"NOTE:Met at the conference.\r\n" +
		"CATEGORIES:Friends,Work\r\n" +
		"PHOTO;ENCODING=b;TYPE=JPEG:/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAEB\r\n" +
		"X-SPOUSE:Alex\r\n" +
		"IMPP:xmpp:test@jabber.example.com\r\n" +
		"item1.X-ABLABEL:_$!<HomePage>!$_\r\n" +
		"GEO:55.676;12.568\r\n" +
		"END:VCARD\r\n";

	private static XElement AppData(params XElement[] elements)
	{
		return new XElement("ApplicationData", elements);
	}

	[Fact]
	public void Update_PreservesVcardDataEasCannotExpress()
	{
		// Editing the name must not erase X- properties, IMPP, GEO or the 4th email —
		// none of which the EAS Contacts class can round-trip.
		string updated = ContactConverter.FromApplicationData(AppData(
			new XElement(Contacts + "FirstName", "Renamed"),
			new XElement(Contacts + "LastName", "Person"),
			new XElement(Contacts + "Email1Address", "one@example.com"),
			new XElement(Contacts + "Email2Address", "two@example.com"),
			new XElement(Contacts + "Email3Address", "three@example.com"),
			new XElement(Contacts + "MobilePhoneNumber", "+4512345678")), "c-1", ExistingVcard);

		Assert.Contains("N:Person;Renamed", updated);
		Assert.Contains("X-SPOUSE:Alex", updated);
		Assert.Contains("IMPP:xmpp:test@jabber.example.com", updated);
		Assert.Contains("GEO:55.676;12.568", updated);
		Assert.Contains("item1.X-ABLABEL", updated);
		// The surplus (4th) email survives; the first three come from the payload.
		Assert.Contains("four@example.com", updated);
		Assert.Equal(4, updated.Split("EMAIL").Length - 1);
	}

	[Fact]
	public void Update_PresentElementsWin_OverTheStoredValue()
	{
		string updated = ContactConverter.FromApplicationData(AppData(
			new XElement(Contacts + "FirstName", "Solo"),
			new XElement(Contacts + "Email1Address", "new@example.com")), "c-1", ExistingVcard);

		// A present element is authoritative: it replaces the stored value for that slot.
		Assert.Contains("new@example.com", updated);
		Assert.DoesNotContain("one@example.com", updated);
		Assert.Contains("four@example.com", updated);
		// Omitted slots are ghosted, not erased (D4) — Email2/3 and the mobile survive.
		Assert.Contains("two@example.com", updated);
		Assert.Contains("+4512345678", updated);
	}

	[Fact]
	public void GhostedChange_DoesNotEraseOmittedManagedProperties()
	{
		// D4 — a Sync Change carrying only <MobilePhoneNumber> used to rebuild the card from
		// the payload alone, wiping name, emails, address, company, note, photo and
		// categories. MS-ASCMD ghosting: an omitted element means "leave as is".
		string updated = ContactConverter.FromApplicationData(AppData(
			new XElement(Contacts + "MobilePhoneNumber", "+4599999999")), "c-1", ExistingVcard);

		Assert.Contains("+4599999999", updated);
		Assert.DoesNotContain("+4512345678", updated);

		Assert.Contains("N:Person;Test", updated);
		Assert.Contains("FN:Test Person", updated);
		Assert.Contains("one@example.com", updated);
		Assert.Contains("Main Street 1", updated);
		Assert.Contains("ORG:Contoso;Research", updated);
		Assert.Contains("TITLE:Engineer", updated);
		Assert.Contains("URL:https://example.com/test", updated);
		Assert.Contains("BDAY:1980-04-05", updated);
		Assert.Contains("NOTE:Met at the conference.", updated);
		Assert.Contains("CATEGORIES:Friends", updated);
		Assert.Contains("PHOTO", updated);
		Assert.Contains("/9j/4AAQ", updated.Replace("\r\n ", ""));
	}

	[Fact]
	public void EmptyElement_ClearsTheProperty_GhostingIsPresenceNotValue()
	{
		// Clearing stays expressible: the element is PRESENT with an empty value, so the
		// stored value does not come back.
		string updated = ContactConverter.FromApplicationData(AppData(
			new XElement(Contacts + "MobilePhoneNumber", ""),
			new XElement(Contacts + "JobTitle", "")), "c-1", ExistingVcard);

		Assert.DoesNotContain("+4512345678", updated);
		Assert.DoesNotContain("TITLE:Engineer", updated);
		// Untouched managed properties are still ghosted through.
		Assert.Contains("ORG:Contoso;Research", updated);
	}

	/// <summary>
	///   D24 — Escape only replaced "\r\n" and "\n"; a bare "\r" (not part of a CRLF pair) survived
	///   into the stored property value unescaped. Some vCard parsers treat a lone CR as a line
	///   terminator, so this could truncate the NOTE property and turn the remainder into a
	///   spurious continuation line.
	/// </summary>
	[Fact]
	public void Escape_ConvertsABareCarriageReturn_NoRawCrSurvives()
	{
		string created = ContactConverter.FromApplicationData(AppData(
			new XElement(Contacts + "FirstName", "Fresh"),
			new XElement(AirSyncBase + "Body",
				new XElement(AirSyncBase + "Data", "line one\rline two"))), "c-3", null);

		Assert.False(HasBareCarriageReturn(created), $"raw CR survived into the vCard: {created}");
	}

	/// <summary>A CR not immediately followed by LF — the exact shape Escape's own replaces miss.</summary>
	private static bool HasBareCarriageReturn(string value)
	{
		for (int i = 0; i < value.Length; i++)
			if (value[i] == '\r' && (i + 1 >= value.Length || value[i + 1] != '\n'))
				return true;
		return false;
	}

	[Fact]
	public void Create_WithoutExistingCard_HasNoPreservedLines()
	{
		string created = ContactConverter.FromApplicationData(AppData(
			new XElement(Contacts + "FirstName", "Fresh"),
			new XElement(Contacts + "LastName", "Start")), "c-2", null);

		Assert.Contains("N:Start;Fresh", created);
		Assert.DoesNotContain("X-SPOUSE", created);
		Assert.DoesNotContain("EMAIL", created);
	}

	[Fact]
	public void Picture_CannotInjectVcardLines()
	{
		// D6 — <Picture> is client-supplied text. It used to be interpolated raw into
		// PHOTO;ENCODING=b;TYPE=JPEG:{picture.Trim()}, and Trim() strips only LEADING and
		// TRAILING whitespace, so an embedded CRLF wrote arbitrary properties into the
		// stored card — and, via CardDAV, onto the DAV server.
		string injected = "/9j/4AAQSkZJRg==\r\nEMAIL;TYPE=INTERNET:attacker@evil.example\r\nX-INJECTED:pwned";

		string card = ContactConverter.FromApplicationData(AppData(
			new XElement(Contacts + "FirstName", "Victim"),
			new XElement(Contacts + "Picture", injected)), "c-9", null);

		Assert.DoesNotContain("attacker@evil.example", card);
		Assert.DoesNotContain("X-INJECTED", card);
	}

	[Fact]
	public void Picture_ValidBase64_IsEmittedWithATypeFromTheDecodedBytes()
	{
		byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01];
		string card = ContactConverter.FromApplicationData(AppData(
			new XElement(Contacts + "Picture", Convert.ToBase64String(png))), "c-10", null);

		Assert.Contains("PHOTO;ENCODING=b;TYPE=PNG:", card);
		Assert.Contains(Convert.ToBase64String(png), card.Replace("\r\n ", ""));
	}

	[Fact]
	public void Picture_Unparsable_IsSkipped_NotEmitted()
	{
		string card = ContactConverter.FromApplicationData(AppData(
			new XElement(Contacts + "FirstName", "Victim"),
			new XElement(Contacts + "Picture", "not base64 at all!!")), "c-11", null);

		Assert.DoesNotContain("PHOTO", card);
	}

	[Fact]
	public void Folding_CountsOctets_AndNeverSplitsASurrogatePair()
	{
		// D23 — RFC 6350 §3.2 specifies 75 OCTETS. Folding at 75 CHARS produces over-long
		// lines for any non-ASCII, and `sb.Append(line, 0, width)` could cut between the two
		// halves of a surrogate pair, leaving an unpaired surrogate that corrupts the UTF-8
		// encoding of the stored card. Emoji are 2 UTF-16 units / 4 UTF-8 octets, so a run of
		// them straddles the boundary at both.
		string note = string.Concat(Enumerable.Repeat("😀", 60));
		string card = ContactConverter.FromApplicationData(AppData(
			new XElement(AirSyncBase + "Body", new XElement(AirSyncBase + "Data", note))), "c-12", null);

		foreach (string line in card.Split("\r\n"))
		{
			Assert.True(Encoding.UTF8.GetByteCount(line) <= 75,
				$"line is {Encoding.UTF8.GetByteCount(line)} octets: {line}");
			// An unpaired surrogate does not survive a UTF-8 round trip (it encodes to '?').
			Assert.Equal(line, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(line)));
		}

		// Unfolding must give the note back byte-for-byte — no dropped or mangled code points.
		string unfolded = card.Replace("\r\n ", "");
		Assert.Contains($"NOTE:{note}", unfolded);
	}

	[Fact]
	public void Read_UnparsableCard_ReturnsNull_LikeEverySiblingConverter()
	{
		// D22 — CalendarConverter/TasksConverter/NotesConverter all return null so
		// LocalStoreBase skips the item. Throwing here fails the ENTIRE Sync response,
		// leaving the folder permanently unsyncable over one corrupt card.
		Assert.Null(ContactConverter.ToApplicationData("this is not a vCard", BodyPreference.PlainText));
	}

	[Fact]
	public void Update_PreservedEmail_DoesNotDuplicateAnEmittedOne_WhenPrefReordersTheTop3()
	{
		// D3: Email1-3 are picked from vcard.EMails.OrderByPref() (used by ToApplicationData,
		// which Ghost() calls to backfill an omitted Email1-3 from the stored card). The old
		// AppendPreserved instead walked the RAW FILE order and re-emitted every EMAIL past the
		// 3rd *file position* — correct only when file order already matches pref order. Here an
		// explicit PREF=1 on the 4th line in the file promotes it to Email1, so pref order is
		// [d, a, b, c] while file order is [a, b, c, d]: the file-position-3 address (c) is
		// wrongly treated as "already written" (and dropped), while the file-position-4 address
		// (d) — which WAS already written as Email1 — is wrongly re-preserved as a duplicate.
		const string vcard =
			"BEGIN:VCARD\r\n" +
			"VERSION:3.0\r\n" +
			"UID:c-20\r\n" +
			"N:Person;Pref;;;\r\n" +
			"FN:Pref Person\r\n" +
			"EMAIL;TYPE=INTERNET:a@example.com\r\n" +
			"EMAIL;TYPE=INTERNET:b@example.com\r\n" +
			"EMAIL;TYPE=INTERNET:c@example.com\r\n" +
			"EMAIL;TYPE=INTERNET;PREF=1:d@example.com\r\n" +
			"END:VCARD\r\n";

		// The client touches an unrelated field only, so Email1-3 are entirely ghosted from the
		// stored card's own pref-ordered view.
		string updated = ContactConverter.FromApplicationData(AppData(
			new XElement(Contacts + "FirstName", "Pref")), "c-20", vcard);

		int dCount = updated.Split("d@example.com").Length - 1;
		Assert.Equal(1, dCount); // d must appear exactly once, not duplicated as a "surplus" line
		Assert.Contains("c@example.com", updated); // c must survive as the genuine surplus address
	}

	[Fact]
	public void Update_UnfoldsContinuationLines_BeforeClassifying()
	{
		// A folded NOTE (managed) must be dropped as one logical line, and a folded X-
		// property must be preserved as one logical line.
		string folded =
			"BEGIN:VCARD\r\nVERSION:3.0\r\nUID:c-3\r\nFN:F\r\n" +
			"NOTE:first part of a long managed note\r\n that continues folded\r\n" +
			"X-CUSTOM:first part of a custom value\r\n  that also continues\r\n" +
			"END:VCARD\r\n";

		// The payload governs NOTE (present, empty → cleared), so nothing ghosts it back and
		// the assertion still proves the folded managed line was consumed as ONE unit.
		string updated = ContactConverter.FromApplicationData(AppData(
			new XElement(Contacts + "FirstName", "F"),
			new XElement(AirSyncBase + "Body", new XElement(AirSyncBase + "Data", ""))), "c-3", folded);

		Assert.DoesNotContain("long managed note", updated);
		Assert.DoesNotContain("that continues folded", updated);
		Assert.Contains("X-CUSTOM:first part of a custom value that also continues", updated);
	}

	[Fact]
	public void Update_PreservesUriPhoto_WhenAnUnrelatedFieldChanges()
	{
		// D4 — PHOTO is a "managed" property, so AppendPreserved never copies the stored line
		// verbatim; the only source of a re-emitted PHOTO is V("Picture"), filled by
		// ToApplicationData from decoded bytes. A URI-valued PHOTO (verified against
		// FolkerKinzel.VCards 8.2.0: Value.Bytes is null for PHOTO;VALUE=URI) produces no
		// Picture element at all, so Ghost has nothing to carry forward and the stored PHOTO
		// line is silently dropped on the very next unrelated edit.
		const string existingWithUriPhoto =
			"BEGIN:VCARD\r\n" +
			"VERSION:3.0\r\n" +
			"UID:c-photo\r\n" +
			"N:Person;Test;;;\r\n" +
			"FN:Test Person\r\n" +
			"TEL;TYPE=CELL:+4512345678\r\n" +
			"PHOTO;VALUE=URI:https://example.com/photo.jpg\r\n" +
			"END:VCARD\r\n";

		string updated = ContactConverter.FromApplicationData(AppData(
			new XElement(Contacts + "MobilePhoneNumber", "+4599999999")), "c-photo", existingWithUriPhoto);

		Assert.Contains("PHOTO;VALUE=URI:https://example.com/photo.jpg", updated);
	}

	[Fact]
	public void Update_ClearingAByteBackedPhoto_StillWorks_EvenWithTheUriPreservationGuard()
	{
		// The URI-preservation fix (D4) must not defeat an explicit clear of a REAL (byte-backed)
		// stored photo: the guard only preserves PHOTO when the stored card carries no decodable
		// bytes, so a byte photo the client omits keeps ghosting through Picture as before, and an
		// explicit empty <Picture/> still clears it.
		string updated = ContactConverter.FromApplicationData(AppData(
			new XElement(Contacts + "FirstName", "Solo"),
			new XElement(Contacts + "Picture", "")), "c-1", ExistingVcard);

		Assert.DoesNotContain("PHOTO", updated);
	}

	[Fact]
	public void Read_HomeFaxAndCarNumbers_RoundTripToTheSameSlotTheyWereWrittenTo()
	{
		// D9 — the read side is not the inverse of the write side. The writer emits
		// TEL;TYPE=HOME,FAX for HomeFaxNumber and TEL;TYPE=CAR for CarPhoneNumber, but the
		// reader had no branch for either: a HOME+FAX number fell through to the generic Fax
		// branch (BusinessFaxNumber) and a CAR number fell through to the untyped fallback
		// (HomePhoneNumber, since the CELL slot was free). Ghost() rebuilds the payload from
		// this same lossy read, so the very next unrelated edit permanently migrates both
		// numbers to the wrong EAS slot on the CardDAV server.
		const string vcard =
			"BEGIN:VCARD\r\n" +
			"VERSION:3.0\r\n" +
			"UID:c-tel\r\n" +
			"N:Person;Test;;;\r\n" +
			"FN:Test Person\r\n" +
			"TEL;TYPE=HOME,FAX:+4511110000\r\n" +
			"TEL;TYPE=CAR:+4522220000\r\n" +
			"END:VCARD\r\n";

		List<XElement>? data = ContactConverter.ToApplicationData(vcard, BodyPreference.PlainText);

		Assert.NotNull(data);
		Assert.Equal("+4511110000", data!.FirstOrDefault(e => e.Name == Contacts + "HomeFaxNumber")?.Value);
		Assert.Equal("+4522220000", data.FirstOrDefault(e => e.Name == Contacts + "CarPhoneNumber")?.Value);
		Assert.Null(data.FirstOrDefault(e => e.Name == Contacts + "BusinessFaxNumber"));
		Assert.Null(data.FirstOrDefault(e => e.Name == Contacts + "HomePhoneNumber"));
	}

	[Fact]
	public void Read_TwoWorkAddresses_EmitOnlyOneSetOfBusinessAddressElements()
	{
		// D10 — the address loop used the plain `Add` local (unconditional append), unlike the
		// phone loop's AddFirst, so a card with two WORK addresses produced two
		// BusinessStreet/BusinessCity/... elements in one ApplicationData. MS-ASCNTC declares
		// these single-instance and iOS is strict about repeated elements.
		const string vcard =
			"BEGIN:VCARD\r\n" +
			"VERSION:3.0\r\n" +
			"UID:c-adr\r\n" +
			"N:Person;Test;;;\r\n" +
			"FN:Test Person\r\n" +
			"ADR;TYPE=WORK:;;First Street 1;Copenhagen;;2100;DK\r\n" +
			"ADR;TYPE=WORK:;;Second Street 2;Aarhus;;8000;DK\r\n" +
			"END:VCARD\r\n";

		List<XElement>? data = ContactConverter.ToApplicationData(vcard, BodyPreference.PlainText);

		Assert.NotNull(data);
		Assert.Single(data!, e => e.Name == Contacts + "BusinessStreet");
		Assert.Single(data, e => e.Name == Contacts + "BusinessCity");
		Assert.Equal("First Street 1", data.First(e => e.Name == Contacts + "BusinessStreet").Value);
	}

	[Fact]
	public void Read_LargeNote_IsTruncatedToTheRequestedBodyPreference()
	{
		// D11 — bodyPreference was accepted and discarded: the note was always written in full
		// (AirSyncBodyWriter.Build(..., truncated: false, ...) hard-coded), against the size
		// budget the device asked for, and the device was told the body was complete.
		string longNote = string.Concat(Enumerable.Repeat("0123456789", 200)); // 2000 chars
		string vcard =
			"BEGIN:VCARD\r\nVERSION:3.0\r\nUID:c-note\r\nN:Person;Test;;;\r\nFN:Test Person\r\n" +
			$"NOTE:{longNote}\r\nEND:VCARD\r\n";

		List<XElement>? data = ContactConverter.ToApplicationData(vcard, new BodyPreference(1, 256, false));

		Assert.NotNull(data);
		XElement body = data!.Single(e => e.Name == AirSyncBase + "Body");
		string truncatedFlag = body.Element(AirSyncBase + "Truncated")!.Value;
		string sent = body.Element(AirSyncBase + "Data")!.Value;

		Assert.Equal("1", truncatedFlag);
		Assert.True(Encoding.UTF8.GetByteCount(sent) <= 256);
	}

	[Fact]
	public void Update_Birthday_IsFormattedInvariantly_RegardlessOfTheServerCulture()
	{
		// D12 — AppendLine(sb, "BDAY", bday.ToString("yyyy-MM-dd")) used no
		// CultureInfo.InvariantCulture, so DateTime.ToString honoured the thread's calendar. On a
		// host whose culture defaults to a non-Gregorian calendar (th-TH -> ThaiBuddhist) the
		// emitted BDAY is off by centuries.
		CultureInfo original = CultureInfo.CurrentCulture;
		try
		{
			CultureInfo.CurrentCulture = new CultureInfo("th-TH");
			string updated = ContactConverter.FromApplicationData(AppData(
				new XElement(Contacts + "FirstName", "Cal"),
				new XElement(Contacts + "Birthday", "1980-04-05T12:00:00.000Z")), "c-cal", null);

			Assert.Contains("BDAY:1980-04-05", updated);
		}
		finally
		{
			CultureInfo.CurrentCulture = original;
		}
	}
}

using System.Xml.Linq;
using ActiveSync.Backends.Converters;
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
	public void Read_UnparsableCard_ReturnsNull_LikeEverySiblingConverter()
	{
		// D22 — CalendarConverter/TasksConverter/NotesConverter all return null so
		// LocalStoreBase skips the item. Throwing here fails the ENTIRE Sync response,
		// leaving the folder permanently unsyncable over one corrupt card.
		Assert.Null(ContactConverter.ToApplicationData("this is not a vCard", BodyPreference.PlainText));
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
}

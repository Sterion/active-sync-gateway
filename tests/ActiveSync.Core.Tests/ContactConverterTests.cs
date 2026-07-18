using System.Xml.Linq;
using ActiveSync.Backends.Converters;
using ActiveSync.Protocol.Wbxml;

namespace ActiveSync.Core.Tests;

public class ContactConverterTests
{
	private static readonly XNamespace Contacts = EasNamespaces.Contacts;

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
	public void Update_ManagedFieldsComeFromThePayload_NotTheOldCard()
	{
		string updated = ContactConverter.FromApplicationData(AppData(
			new XElement(Contacts + "FirstName", "Solo"),
			new XElement(Contacts + "Email1Address", "new@example.com")), "c-1", ExistingVcard);

		// Managed properties are replace-semantics: the payload's single email wins over
		// the old first three (only the EAS-inexpressible 4th is carried over).
		Assert.Contains("new@example.com", updated);
		Assert.DoesNotContain("one@example.com", updated);
		Assert.Contains("four@example.com", updated);
		// The old mobile number is managed and was omitted → gone (full-item semantics).
		Assert.DoesNotContain("+4512345678", updated);
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
	public void Update_UnfoldsContinuationLines_BeforeClassifying()
	{
		// A folded NOTE (managed) must be dropped as one logical line, and a folded X-
		// property must be preserved as one logical line.
		string folded =
			"BEGIN:VCARD\r\nVERSION:3.0\r\nUID:c-3\r\nFN:F\r\n" +
			"NOTE:first part of a long managed note\r\n that continues folded\r\n" +
			"X-CUSTOM:first part of a custom value\r\n  that also continues\r\n" +
			"END:VCARD\r\n";

		string updated = ContactConverter.FromApplicationData(AppData(
			new XElement(Contacts + "FirstName", "F")), "c-3", folded);

		Assert.DoesNotContain("long managed note", updated);
		Assert.DoesNotContain("that continues folded", updated);
		Assert.Contains("X-CUSTOM:first part of a custom value that also continues", updated);
	}
}

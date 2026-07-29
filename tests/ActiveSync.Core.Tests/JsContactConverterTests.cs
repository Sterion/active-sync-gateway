using System.Text.Json;
using ActiveSync.Backends.Jmap;
using ActiveSync.Contracts;

namespace ActiveSync.Core.Tests;

/// <summary>
///   JSContact (RFC 9553) ⇄ vCard round-trips, built on the shape Stalwart emits. The bridge was
///   retargeted with the typed item currency: the contract's contacts payload is vCard, so this
///   converter produces and consumes vCard instead of EAS XML — the EAS half is host-side. Every
///   case below is the pre-contract assertion carried over onto the new format.
/// </summary>
public class JsContactConverterTests
{
	// A real Stalwart ContactCard (trimmed).
	private const string CardJson = """
	{
	  "@type": "Card", "version": "1.0", "kind": "individual", "id": "abc",
	  "name": { "full": "John Q Doe", "components": [
	    { "kind": "given", "value": "John" }, { "kind": "surname", "value": "Doe" } ] },
	  "organizations": { "o": { "name": "Acme Inc", "units": [ { "name": "R&D" } ] } },
	  "titles": { "t": { "name": "Engineer", "kind": "title" } },
	  "emails": { "e": { "address": "john@acme.com", "contexts": { "work": true } } },
	  "phones": { "p": { "number": "+1-555-1234", "features": { "mobile": true } } },
	  "nicknames": { "n": { "name": "JD" } },
	  "keywords": { "vip": true },
	  "x-custom": "preserve-me"
	}
	""";

	private static JsonElement Card => JsonDocument.Parse(CardJson).RootElement;

	/// <summary>The unfolded property lines of a vCard, so an assertion can name one exactly.</summary>
	private static IReadOnlyList<string> Lines(string vcf) =>
		vcf.Replace("\r\n ", "").Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

	private static string? Line(string vcf, string property) =>
		Lines(vcf).FirstOrDefault(l =>
			l.StartsWith(property + ":", StringComparison.Ordinal) ||
			l.StartsWith(property + ";", StringComparison.Ordinal));

	private static string? Value(string vcf, string property)
	{
		string? line = Line(vcf, property);
		int colon = line?.IndexOf(':') ?? -1;
		return colon >= 0 ? line![(colon + 1)..] : null;
	}

	[Fact]
	public void ToVCard_MapsCommonFields()
	{
		string vcf = JsContactConverter.ToVCard(Card);

		Assert.StartsWith("BEGIN:VCARD\r\nVERSION:3.0\r\n", vcf);
		Assert.Equal("Doe;John;;;", Value(vcf, "N"));
		Assert.Equal("John Q Doe", Value(vcf, "FN"));
		Assert.Equal("Acme Inc;R&D", Value(vcf, "ORG"));
		Assert.Equal("Engineer", Value(vcf, "TITLE"));
		Assert.Equal("john@acme.com", Value(vcf, "EMAIL"));
		Assert.Equal("+1-555-1234", Value(vcf, "TEL"));
		Assert.Equal("TEL;TYPE=CELL:+1-555-1234", Line(vcf, "TEL"));
		Assert.Equal("JD", Value(vcf, "NICKNAME"));
		Assert.Equal("vip", Value(vcf, "CATEGORIES"));
		// The card's own uid names the contact — the host reads it back to name the DAV/local
		// resource, so it must always be present.
		Assert.Equal("abc", Value(vcf, "UID"));
	}

	[Fact]
	public void FromVCard_BuildsJsContact_AndPreservesUnknownMembers()
	{
		const string vcf =
			"BEGIN:VCARD\r\nVERSION:3.0\r\nUID:ada\r\nN:Lovelace;Ada;;;\r\nFN:Ada Lovelace\r\n" +
			"EMAIL;TYPE=INTERNET:ada@example.com\r\nORG:Analytical Engines;\r\n" +
			"TEL;TYPE=CELL:+1-555-0100\r\nNICKNAME:Countess\r\nEND:VCARD\r\n";

		Dictionary<string, object?> card = JsContactConverter.FromVCard(vcf, Card);
		JsonElement rebuilt = JsonSerializer.SerializeToElement(card);

		Assert.Equal("Card", rebuilt.GetProperty("@type").GetString());
		Assert.Equal("Ada", rebuilt.GetProperty("name").GetProperty("components")[0].GetProperty("value").GetString());
		Assert.Equal("ada@example.com",
			rebuilt.GetProperty("emails").EnumerateObject().First().Value.GetProperty("address").GetString());
		// Unknown member from the existing card survives the rewrite.
		Assert.Equal("preserve-me", rebuilt.GetProperty("x-custom").GetString());
	}

	// The birthday was written into anniversaries/b/date/utc and read back out of
	// anniversaries/b/date/date, a member nothing ever wrote, so it silently never appeared again.
	[Fact]
	public void Birthday_SurvivesTheRoundTrip()
	{
		const string vcf =
			"BEGIN:VCARD\r\nVERSION:3.0\r\nUID:ada\r\nN:Lovelace;Ada;;;\r\nFN:Ada Lovelace\r\n" +
			"BDAY:1815-12-10\r\nEND:VCARD\r\n";

		Dictionary<string, object?> card = JsContactConverter.FromVCard(vcf, null);
		string back = JsContactConverter.ToVCard(JsonSerializer.SerializeToElement(card));

		Assert.Equal("1815-12-10", Value(back, "BDAY"));
	}

	// Both JSContact date shapes must be readable: RFC 9553 allows a PartialDate as well as a
	// Timestamp, and a server may hand back either.
	[Theory]
	[InlineData("""{ "@type": "Timestamp", "utc": "1815-12-10T00:00:00Z" }""")]
	[InlineData("""{ "@type": "PartialDate", "year": 1815, "month": 12, "day": 10 }""")]
	public void Birthday_IsReadFromEitherDateShape(string dateJson)
	{
		string cardJson = $$"""
		{ "@type": "Card", "version": "1.0", "kind": "individual", "uid": "b1",
		  "anniversaries": { "b": { "@type": "Anniversary", "kind": "birth", "date": {{dateJson}} } } }
		""";

		string vcf = JsContactConverter.ToVCard(JsonDocument.Parse(cardJson).RootElement);

		Assert.Equal("1815-12-10", Value(vcf, "BDAY"));
	}

	// "anniversaries" is a Managed top-level member (rewritten from the payload on every edit),
	// but the writer only ever produces the "birth" entry from the vCard BDAY — any other kind
	// already on the card (e.g. a wedding anniversary, which this bridge does not read/write) was
	// silently dropped on every edit instead of being carried over.
	[Fact]
	public void FromVCard_PreservesNonBirthAnniversaries_OnUpdate()
	{
		const string existingCardJson = """
		{
		  "@type": "Card", "version": "1.0", "kind": "individual",
		  "anniversaries": {
		    "b": { "@type": "Anniversary", "kind": "birth",
		           "date": { "@type": "Timestamp", "utc": "1815-12-10T00:00:00Z" } },
		    "w": { "@type": "Anniversary", "kind": "wedding",
		           "date": { "@type": "Timestamp", "utc": "1840-06-01T00:00:00Z" } }
		  }
		}
		""";
		JsonElement existingCard = JsonDocument.Parse(existingCardJson).RootElement;

		// An edit to an unrelated field; the host's merged vCard carries the unchanged BDAY too, so
		// only "anniversaries" itself is at risk of being clobbered.
		const string vcf =
			"BEGIN:VCARD\r\nVERSION:3.0\r\nUID:ada\r\nN:Lovelace;Ada;;;\r\nFN:Ada Lovelace\r\n" +
			"BDAY:1815-12-10\r\nEND:VCARD\r\n";

		Dictionary<string, object?> card = JsContactConverter.FromVCard(vcf, existingCard);
		JsonElement rebuilt = JsonSerializer.SerializeToElement(card);

		JsonElement anniversaries = rebuilt.GetProperty("anniversaries");
		Assert.Equal("wedding", anniversaries.GetProperty("w").GetProperty("kind").GetString());
		Assert.Equal("birth", anniversaries.GetProperty("b").GetProperty("kind").GetString());
	}

	// A blank company AND department (a present-but-cleared ORG, distinct from an absent one) must
	// leave "organizations" unset, not produce an org with an empty name.
	[Fact]
	public void FromVCard_EmptyCompanyNameAndDepartment_LeavesOrganizationsUnset()
	{
		const string vcf =
			"BEGIN:VCARD\r\nVERSION:3.0\r\nUID:ada\r\nN:;Ada;;;\r\nFN:Ada\r\nORG:;\r\nEND:VCARD\r\n";

		Dictionary<string, object?> card = JsContactConverter.FromVCard(vcf, null);

		Assert.False(card.ContainsKey("organizations"),
			"a cleared ORG must not produce an organization with an empty name");
	}

	// A malformed angle-bracket email (an unmatched '<' with no closing '>') kept the WHOLE
	// original string — including the display-name text and the stray '<' — as the JMAP "address"
	// member, which isn't a valid email address at all.
	[Fact]
	public void FromVCard_MalformedAngleBracketEmail_ExtractsTheAddressPart()
	{
		const string vcf =
			"BEGIN:VCARD\r\nVERSION:3.0\r\nUID:x\r\nFN:X\r\nEMAIL;TYPE=INTERNET:Display Name <notanemail\r\nEND:VCARD\r\n";

		Dictionary<string, object?> card = JsContactConverter.FromVCard(vcf, null);
		JsonElement rebuilt = JsonSerializer.SerializeToElement(card);

		string? address = rebuilt.GetProperty("emails").GetProperty("e1").GetProperty("address").GetString();
		Assert.Equal("notanemail", address);
	}

	[Fact]
	public void RoundTrip_VCardToJsContactToVCard_PreservesFields()
	{
		const string vcf =
			"BEGIN:VCARD\r\nVERSION:3.0\r\nUID:grace\r\nN:Hopper;Grace;;;\r\nFN:Grace Hopper\r\n" +
			"EMAIL;TYPE=INTERNET:grace@navy.mil\r\nTITLE:Rear Admiral\r\n" +
			"TEL;TYPE=WORK,VOICE:+1-555-0199\r\nEND:VCARD\r\n";

		Dictionary<string, object?> card = JsContactConverter.FromVCard(vcf, null);
		string back = JsContactConverter.ToVCard(JsonSerializer.SerializeToElement(card));

		Assert.Equal("Hopper;Grace;;;", Value(back, "N"));
		Assert.Equal("Grace Hopper", Value(back, "FN"));
		Assert.Equal("grace@navy.mil", Value(back, "EMAIL"));
		Assert.Equal("Rear Admiral", Value(back, "TITLE"));
		Assert.Equal("TEL;TYPE=WORK,VOICE:+1-555-0199", Line(back, "TEL"));
	}

	[Fact]
	public void ToGalEntry_ProjectsTheDirectoryFields()
	{
		GalEntry entry = JsContactConverter.ToGalEntry(Card);

		Assert.Equal("John Q Doe", entry.DisplayName);
		Assert.Equal("john@acme.com", entry.EmailAddress);
		Assert.Equal("John", entry.FirstName);
		Assert.Equal("Doe", entry.LastName);
		Assert.Equal("+1-555-1234", entry.Phone);
		Assert.Equal("Acme Inc", entry.Company);
		// Photos are not read from "media" — the store reports the typed None status instead.
		Assert.Null(entry.Picture);
	}
}

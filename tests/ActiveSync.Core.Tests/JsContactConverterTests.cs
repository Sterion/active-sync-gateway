using System.Text.Json;
using System.Xml.Linq;
using ActiveSync.Backends.Jmap;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Protocol.Wbxml;

namespace ActiveSync.Core.Tests;

/// <summary>JSContact (RFC 9553) ⇄ EAS Contacts round-trips, built on the shape Stalwart emits.</summary>
public class JsContactConverterTests
{
	private static readonly XNamespace C = EasNamespaces.Contacts;
	private static readonly XNamespace C2 = EasNamespaces.Contacts2;

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

	[Fact]
	public void ToApplicationData_MapsCommonFields()
	{
		List<XElement> data = JsContactConverter.ToApplicationData(Card, BodyPreference.PlainText);

		string? V(string local) => data.FirstOrDefault(e => e.Name.LocalName == local)?.Value;
		Assert.Equal("John", V("FirstName"));
		Assert.Equal("Doe", V("LastName"));
		Assert.Equal("John Q Doe", V("FileAs"));
		Assert.Equal("Acme Inc", V("CompanyName"));
		Assert.Equal("R&D", V("Department"));
		Assert.Equal("Engineer", V("JobTitle"));
		Assert.Equal("john@acme.com", V("Email1Address"));
		Assert.Equal("+1-555-1234", V("MobilePhoneNumber"));
		Assert.Equal("JD", V("NickName"));
		Assert.Contains("vip", data.FirstOrDefault(e => e.Name.LocalName == "Categories")?
			.Elements(C + "Category").Select(c => c.Value) ?? []);
	}

	[Fact]
	public void FromApplicationData_BuildsJsContact_AndPreservesUnknownMembers()
	{
		XElement app = new("ApplicationData",
			new XElement(C + "FirstName", "Ada"),
			new XElement(C + "LastName", "Lovelace"),
			new XElement(C + "Email1Address", "ada@example.com"),
			new XElement(C + "CompanyName", "Analytical Engines"),
			new XElement(C + "MobilePhoneNumber", "+1-555-0100"),
			new XElement(C2 + "NickName", "Countess"));

		Dictionary<string, object?> card = JsContactConverter.FromApplicationData(app, Card);
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
		XElement app = new("ApplicationData",
			new XElement(C + "FirstName", "Ada"),
			new XElement(C + "LastName", "Lovelace"),
			new XElement(C + "Birthday", "1815-12-10T00:00:00.000Z"));

		Dictionary<string, object?> card = JsContactConverter.FromApplicationData(app, null);
		List<XElement> back =
			JsContactConverter.ToApplicationData(JsonSerializer.SerializeToElement(card), BodyPreference.PlainText);

		string? birthday = back.FirstOrDefault(e => e.Name.LocalName == "Birthday")?.Value;
		Assert.NotNull(birthday);
		Assert.StartsWith("1815-12-10", birthday);
	}

	// Both JSContact date shapes must be readable: RFC 9553 allows a PartialDate as well as a
	// Timestamp, and a server may hand back either.
	[Theory]
	[InlineData("""{ "@type": "Timestamp", "utc": "1815-12-10T00:00:00Z" }""")]
	[InlineData("""{ "@type": "PartialDate", "year": 1815, "month": 12, "day": 10 }""")]
	public void Birthday_IsReadFromEitherDateShape(string dateJson)
	{
		string cardJson = $$"""
		{ "@type": "Card", "version": "1.0", "kind": "individual",
		  "anniversaries": { "b": { "@type": "Anniversary", "kind": "birth", "date": {{dateJson}} } } }
		""";
		List<XElement> data =
			JsContactConverter.ToApplicationData(JsonDocument.Parse(cardJson).RootElement, BodyPreference.PlainText);
		string? birthday = data.FirstOrDefault(e => e.Name.LocalName == "Birthday")?.Value;
		Assert.NotNull(birthday);
		Assert.StartsWith("1815-12-10", birthday);
	}

	// "anniversaries" is a Managed top-level member (rewritten from the payload on every
	// edit), but the writer only ever produces the "birth" entry from EAS Birthday — any other
	// kind already on the card (e.g. a wedding anniversary; EAS carries it as
	// contacts2:Anniversary, which this bridge does not read/write) was silently dropped on
	// every edit instead of being carried over.
	[Fact]
	public void FromApplicationData_PreservesNonBirthAnniversaries_OnUpdate()
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

		// An edit to an unrelated field; the client resends the unchanged Birthday too (managed
		// fields are full-replace), so only "anniversaries" itself is at risk of being clobbered.
		XElement app = new("ApplicationData",
			new XElement(C + "FirstName", "Ada"),
			new XElement(C + "LastName", "Lovelace"),
			new XElement(C + "Birthday", "1815-12-10T00:00:00.000Z"));

		Dictionary<string, object?> card = JsContactConverter.FromApplicationData(app, existingCard);
		JsonElement rebuilt = JsonSerializer.SerializeToElement(card);

		JsonElement anniversaries = rebuilt.GetProperty("anniversaries");
		Assert.Equal("wedding", anniversaries.GetProperty("w").GetProperty("kind").GetString());
		Assert.Equal("birth", anniversaries.GetProperty("b").GetProperty("kind").GetString());
	}

	// `if (V("CompanyName") is { } company || V("Department") is { } department1)` used a
	// `{ }` pattern, which matches any NON-NULL string — including an empty one. An empty
	// <CompanyName/> element (present but cleared, distinct from absent) must leave
	// "organizations" unset, not produce an org with an empty name.
	[Fact]
	public void FromApplicationData_EmptyCompanyNameAndDepartment_LeavesOrganizationsUnset()
	{
		XElement app = new("ApplicationData",
			new XElement(C + "FirstName", "Ada"),
			new XElement(C + "CompanyName", ""),
			new XElement(C + "Department", ""));

		Dictionary<string, object?> card = JsContactConverter.FromApplicationData(app, null);

		Assert.False(card.ContainsKey("organizations"),
			"an empty CompanyName/Department element must not produce an organization with an empty name");
	}

	// A malformed angle-bracket EAS email (an unmatched '<' with no closing '>') kept the
	// WHOLE original string — including the display-name text and the stray '<' — as the JMAP
	// "address" member, which isn't a valid email address at all.
	[Fact]
	public void FromApplicationData_MalformedAngleBracketEmail_ExtractsTheAddressPart()
	{
		XElement app = new("ApplicationData",
			new XElement(C + "Email1Address", "Display Name <notanemail"));

		Dictionary<string, object?> card = JsContactConverter.FromApplicationData(app, null);
		JsonElement rebuilt = JsonSerializer.SerializeToElement(card);

		string? address = rebuilt.GetProperty("emails").GetProperty("e1").GetProperty("address").GetString();
		Assert.Equal("notanemail", address);
	}

	[Fact]
	public void RoundTrip_EasToJsContactToEas_PreservesFields()
	{
		XElement app = new("ApplicationData",
			new XElement(C + "FirstName", "Grace"),
			new XElement(C + "LastName", "Hopper"),
			new XElement(C + "Email1Address", "grace@navy.mil"),
			new XElement(C + "JobTitle", "Rear Admiral"),
			new XElement(C + "BusinessPhoneNumber", "+1-555-0199"));

		Dictionary<string, object?> card = JsContactConverter.FromApplicationData(app, null);
		JsonElement asJson = JsonSerializer.SerializeToElement(card);
		List<XElement> back = JsContactConverter.ToApplicationData(asJson, BodyPreference.PlainText);

		string? V(string local) => back.FirstOrDefault(e => e.Name.LocalName == local)?.Value;
		Assert.Equal("Grace", V("FirstName"));
		Assert.Equal("Hopper", V("LastName"));
		Assert.Equal("grace@navy.mil", V("Email1Address"));
		Assert.Equal("Rear Admiral", V("JobTitle"));
		Assert.Equal("+1-555-0199", V("BusinessPhoneNumber"));
	}
}

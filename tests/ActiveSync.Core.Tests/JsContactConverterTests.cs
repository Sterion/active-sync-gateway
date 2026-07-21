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

	// H6 — the birthday was written into anniversaries/b/date/utc and read back out of
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

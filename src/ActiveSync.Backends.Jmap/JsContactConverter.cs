using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using ActiveSync.Backends.Converters;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;

namespace ActiveSync.Backends.Jmap;

/// <summary>
///   JSContact (RFC 9553) ⇄ EAS Contacts-class ApplicationData (MS-ASCNTC). Covers the fields
///   the EAS Contacts class carries (name parts, file-as, up to three emails, typed phones,
///   home/work addresses, organization/department/title, nickname, birthday, note, categories,
///   photo). On write, unknown JSContact members of an existing card are preserved so editing
///   one EAS field never drops data the Contacts class cannot express.
/// </summary>
public static class JsContactConverter
{
	private static readonly XNamespace Contacts = EasNamespaces.Contacts;
	private static readonly XNamespace Contacts2 = EasNamespaces.Contacts2;
	private static readonly XNamespace AirSyncBase = EasNamespaces.AirSyncBase;

	// EAS-managed JSContact top-level members: rewritten from the payload on every change; any
	// other member of an existing card is preserved verbatim.
	private static readonly string[] Managed =
	[
		"name", "nicknames", "organizations", "titles", "emails", "phones", "addresses",
		"anniversaries", "notes", "keywords", "media"
	];

	// JMAP `*/set update` values are PatchObjects (RFC 8620 §5.3) — a member absent from the patch
	// is left untouched, it is not cleared. Verified against Stalwart 0.16: omitting "titles" from
	// an update leaves the old job title in place. EAS Change payloads carry the complete managed
	// set, so a field the client cleared arrives as an *absent* element; it therefore has to be
	// sent as an explicit null or the clear never reaches the server. This is also correct under
	// full-replace semantics, where an explicit null and an absent member mean the same thing.
	//
	// "media" is deliberately not in this set: the EAS Contacts view neither reads nor writes the
	// photo, so nulling it on every edit would destroy a picture the client never saw.
	private static readonly string[] ClearedOnUpdate =
	[
		"name", "nicknames", "organizations", "titles", "emails", "phones", "addresses",
		"anniversaries", "notes", "keywords"
	];

	public static List<XElement> ToApplicationData(JsonElement card, BodyPreference bodyPreference)
	{
		List<XElement> data = new();

		void Add(XName name, string? value)
		{
			if (!string.IsNullOrWhiteSpace(value))
				data.Add(new XElement(name, value));
		}

		// name.components (kind → EAS field) + name.full → FileAs.
		if (card.TryGetProperty("name", out JsonElement name))
		{
			foreach (JsonElement c in Array(name, "components"))
			{
				string kind = Str(c, "kind") ?? "";
				string? value = Str(c, "value");
				switch (kind)
				{
					case "given": Add(Contacts + "FirstName", value); break;
					case "given2": Add(Contacts + "MiddleName", value); break;
					case "surname": Add(Contacts + "LastName", value); break;
					case "credential": Add(Contacts + "Suffix", value); break;
					case "title": Add(Contacts + "Title", value); break;
				}
			}

			Add(Contacts + "FileAs", Str(name, "full"));
		}

		int emailIndex = 1;
		foreach (JsonElement email in Values(card, "emails"))
		{
			if (Str(email, "address") is { } address && emailIndex <= 3)
				Add(Contacts + $"Email{emailIndex++}Address", address);
		}

		foreach (JsonElement phone in Values(card, "phones"))
		{
			if (Str(phone, "number") is not { } number)
				continue;
			bool work = Bool(phone, "contexts", "work");
			bool home = Bool(phone, "contexts", "private");
			if (Bool(phone, "features", "mobile"))
				AddFirst(data, Contacts + "MobilePhoneNumber", number);
			else if (Bool(phone, "features", "fax"))
				AddFirst(data, Contacts + (work ? "BusinessFaxNumber" : "HomeFaxNumber"), number);
			else if (Bool(phone, "features", "pager"))
				AddFirst(data, Contacts + "PagerNumber", number);
			else if (work)
			{
				if (!AddFirst(data, Contacts + "BusinessPhoneNumber", number))
					AddFirst(data, Contacts + "Business2PhoneNumber", number);
			}
			else
			{
				_ = home;
				if (!AddFirst(data, Contacts + "HomePhoneNumber", number))
					AddFirst(data, Contacts + "Home2PhoneNumber", number);
			}
		}

		foreach (JsonElement address in Values(card, "addresses"))
		{
			string prefix = Bool(address, "contexts", "work") ? "Business" : "Home";
			if (data.Any(e => e.Name == Contacts + $"{prefix}Street" || e.Name == Contacts + $"{prefix}City"))
				continue; // one address per context
			string? street = Component(address, "name") ?? Component(address, "street");
			Add(Contacts + $"{prefix}Street", street);
			Add(Contacts + $"{prefix}City", Component(address, "locality"));
			Add(Contacts + $"{prefix}State", Component(address, "region"));
			Add(Contacts + $"{prefix}PostalCode", Component(address, "postcode"));
			Add(Contacts + $"{prefix}Country", Component(address, "country"));
		}

		JsonElement org = Values(card, "organizations").FirstOrDefault();
		if (org.ValueKind == JsonValueKind.Object)
		{
			Add(Contacts + "CompanyName", Str(org, "name"));
			Add(Contacts + "Department", Array(org, "units").Select(u => Str(u, "name")).FirstOrDefault(u => u is not null));
		}

		Add(Contacts + "JobTitle", Values(card, "titles").Select(t => Str(t, "name")).FirstOrDefault(t => t is not null));
		Add(Contacts2 + "NickName", Values(card, "nicknames").Select(n => Str(n, "name")).FirstOrDefault(n => n is not null));

		foreach (JsonElement anniversary in Values(card, "anniversaries"))
			if (Str(anniversary, "kind") == "birth" &&
			    anniversary.TryGetProperty("date", out JsonElement date) &&
			    AnniversaryDate(date) is { } parsed)
				data.Add(new XElement(Contacts + "Birthday", EasDateTime.ToLong(parsed)));

		string? note = Values(card, "notes").Select(n => Str(n, "note")).FirstOrDefault(n => !string.IsNullOrEmpty(n));
		if (!string.IsNullOrEmpty(note))
			data.Add(AirSyncBodyWriter.Build(Encoding.UTF8.GetByteCount(note), false, note));

		if (card.TryGetProperty("keywords", out JsonElement keywords) && keywords.ValueKind == JsonValueKind.Object)
		{
			List<XElement> categories = keywords.EnumerateObject()
				.Where(k => k.Value.ValueKind == JsonValueKind.True)
				.Select(k => new XElement(Contacts + "Category", k.Name))
				.ToList();
			if (categories.Count > 0)
				data.Add(new XElement(Contacts + "Categories", categories));
		}

		_ = bodyPreference;
		return data;
	}

	/// <summary>
	///   Builds a JSContact Card object from client ApplicationData. Managed members are
	///   rewritten; every other member of <paramref name="existing" /> is carried over.
	/// </summary>
	public static Dictionary<string, object?> FromApplicationData(XElement applicationData, JsonElement? existing)
	{
		string? V(string localName) => applicationData.Element(Contacts + localName)?.Value;

		Dictionary<string, object?> card = new()
		{
			["@type"] = "Card",
			["version"] = "1.0",
			["kind"] = "individual"
		};

		// Preserve unknown members from the existing card.
		if (existing is { ValueKind: JsonValueKind.Object } prior)
			foreach (JsonProperty p in prior.EnumerateObject())
				if (!Managed.Contains(p.Name) && p.Name is not ("@type" or "version" or "kind" or "id" or "addressBookIds"))
					card[p.Name] = JsonSerializer.Deserialize<object>(p.Value.GetRawText());

		List<object> nameComponents = new();
		void Component(string kind, string? value)
		{
			if (!string.IsNullOrWhiteSpace(value))
				nameComponents.Add(new Dictionary<string, object?> { ["kind"] = kind, ["value"] = value });
		}

		Component("title", V("Title"));
		Component("given", V("FirstName"));
		Component("given2", V("MiddleName"));
		Component("surname", V("LastName"));
		Component("credential", V("Suffix"));
		string fileAs = V("FileAs") ?? string.Join(" ", new[] { V("FirstName"), V("MiddleName"), V("LastName") }
			.Where(s => !string.IsNullOrWhiteSpace(s)));
		Dictionary<string, object?> name = new();
		if (nameComponents.Count > 0)
			name["components"] = nameComponents;
		if (!string.IsNullOrWhiteSpace(fileAs))
			name["full"] = fileAs;
		if (name.Count > 0)
			card["name"] = name;

		Dictionary<string, object?> emails = new();
		for (int i = 1; i <= 3; i++)
			if (V($"Email{i}Address") is { Length: > 0 } address)
				emails[$"e{i}"] = new Dictionary<string, object?> { ["address"] = StripEmailDisplay(address) };
		if (emails.Count > 0)
			card["emails"] = emails;

		Dictionary<string, object?> phones = new();
		void Phone(string id, string? number, Dictionary<string, object?>? features, Dictionary<string, object?>? contexts)
		{
			if (string.IsNullOrWhiteSpace(number))
				return;
			Dictionary<string, object?> phone = new() { ["number"] = number };
			if (features is not null) phone["features"] = features;
			if (contexts is not null) phone["contexts"] = contexts;
			phones[id] = phone;
		}

		Phone("mobile", V("MobilePhoneNumber"), new() { ["mobile"] = true }, null);
		Phone("work", V("BusinessPhoneNumber"), null, new() { ["work"] = true });
		Phone("work2", V("Business2PhoneNumber"), null, new() { ["work"] = true });
		Phone("home", V("HomePhoneNumber"), null, new() { ["private"] = true });
		Phone("home2", V("Home2PhoneNumber"), null, new() { ["private"] = true });
		Phone("workfax", V("BusinessFaxNumber"), new() { ["fax"] = true }, new() { ["work"] = true });
		Phone("homefax", V("HomeFaxNumber"), new() { ["fax"] = true }, new() { ["private"] = true });
		Phone("pager", V("PagerNumber"), new() { ["pager"] = true }, null);
		if (phones.Count > 0)
			card["phones"] = phones;

		Dictionary<string, object?> addresses = new();
		AddAddress(addresses, "work", "Business", V);
		AddAddress(addresses, "home", "Home", V);
		if (addresses.Count > 0)
			card["addresses"] = addresses;

		if (V("CompanyName") is { } company || V("Department") is { } department1)
		{
			Dictionary<string, object?> org = new();
			if (V("CompanyName") is { } c) org["name"] = c;
			if (V("Department") is { } d) org["units"] = new object[] { new Dictionary<string, object?> { ["name"] = d } };
			card["organizations"] = new Dictionary<string, object?> { ["o"] = org };
		}

		if (V("JobTitle") is { } jobTitle)
			card["titles"] = new Dictionary<string, object?>
			{
				["t"] = new Dictionary<string, object?> { ["kind"] = "title", ["name"] = jobTitle }
			};

		if (applicationData.Element(Contacts2 + "NickName")?.Value is { Length: > 0 } nick)
			card["nicknames"] = new Dictionary<string, object?>
			{
				["n"] = new Dictionary<string, object?> { ["name"] = nick }
			};

		if (V("Birthday") is { } birthday && EasDateTime.TryParse(birthday, out DateTime bday))
			card["anniversaries"] = new Dictionary<string, object?>
			{
				["b"] = new Dictionary<string, object?>
				{
					["@type"] = "Anniversary",
					["kind"] = "birth",
					["date"] = new Dictionary<string, object?>
					{
						["@type"] = "Timestamp",
						["utc"] = JmapDate.ToUtc(bday)
					}
				}
			};

		string? body = applicationData.Element(AirSyncBase + "Body")?.Element(AirSyncBase + "Data")?.Value;
		if (!string.IsNullOrEmpty(body))
			card["notes"] = new Dictionary<string, object?>
			{
				["n"] = new Dictionary<string, object?> { ["note"] = body }
			};

		List<string>? categories = applicationData.Element(Contacts + "Categories")?
			.Elements(Contacts + "Category").Select(c => c.Value).Where(c => c.Length > 0).ToList();
		if (categories is { Count: > 0 })
			card["keywords"] = categories.ToDictionary(c => c, object? (_) => true);

		// Update (not create): every managed member the payload did not populate is explicitly
		// nulled, so clearing a field survives the PatchObject semantics of `ContactCard/set`.
		// A create sends only what it has — a null there is a member the card never had.
		if (existing is not null)
			foreach (string member in ClearedOnUpdate)
				if (!card.ContainsKey(member))
					card[member] = null;

		return card;
	}

	private static void AddAddress(
		Dictionary<string, object?> addresses, string id, string prefix, Func<string, string?> value)
	{
		string? street = value($"{prefix}Street");
		string? city = value($"{prefix}City");
		string? state = value($"{prefix}State");
		string? postal = value($"{prefix}PostalCode");
		string? country = value($"{prefix}Country");
		if (street is null && city is null && state is null && postal is null && country is null)
			return;
		List<object> components = new();
		void Comp(string kind, string? v)
		{
			if (!string.IsNullOrWhiteSpace(v))
				components.Add(new Dictionary<string, object?> { ["kind"] = kind, ["value"] = v });
		}

		Comp("name", street);
		Comp("locality", city);
		Comp("region", state);
		Comp("postcode", postal);
		Comp("country", country);
		addresses[id] = new Dictionary<string, object?>
		{
			["components"] = components,
			["contexts"] = new Dictionary<string, object?> { [id == "work" ? "work" : "private"] = true }
		};
	}

	/// <summary>
	///   Reads an RFC 9553 <c>Anniversary.date</c>, which is either a <c>Timestamp</c> (a
	///   <c>utc</c> date-time string) or a <c>PartialDate</c> (year/month/day numbers, no zone).
	///   The old reader looked for a <c>date</c> *string* member, which is neither shape and which
	///   nothing on the write side ever produced — so a birthday round-tripped to nothing at all.
	///   A bare <c>date</c> string is still accepted, since it costs one line and some servers
	///   have shipped it.
	/// </summary>
	private static DateTime? AnniversaryDate(JsonElement date)
	{
		if (date.ValueKind != JsonValueKind.Object)
			return null;

		foreach (string member in (string[])["utc", "date", "local"])
			if (Str(date, member) is { } text &&
			    DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
				    System.Globalization.DateTimeStyles.AssumeUniversal |
				    System.Globalization.DateTimeStyles.AdjustToUniversal, out DateTime parsed))
				return parsed;

		// PartialDate: a missing year or month makes the date unusable as an EAS Birthday (which
		// is a full timestamp), so all three are required rather than guessed at.
		if (Int(date, "year") is { } year && Int(date, "month") is { } month && Int(date, "day") is { } day &&
		    year is >= 1 and <= 9999 && month is >= 1 and <= 12 && day >= 1 &&
		    day <= DateTime.DaysInMonth(year, month))
			return new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);

		return null;
	}

	// ---------- JSON helpers ----------

	private static int? Int(JsonElement element, string property)
	{
		return element.TryGetProperty(property, out JsonElement v) && v.ValueKind == JsonValueKind.Number &&
		       v.TryGetInt32(out int i)
			? i
			: null;
	}

	private static IEnumerable<JsonElement> Values(JsonElement card, string property)
	{
		return card.TryGetProperty(property, out JsonElement map) && map.ValueKind == JsonValueKind.Object
			? map.EnumerateObject().Select(p => p.Value)
			: [];
	}

	private static IEnumerable<JsonElement> Array(JsonElement element, string property)
	{
		return element.TryGetProperty(property, out JsonElement arr) && arr.ValueKind == JsonValueKind.Array
			? arr.EnumerateArray()
			: [];
	}

	private static string? Component(JsonElement address, string kind)
	{
		return Array(address, "components")
			.Where(c => Str(c, "kind") == kind)
			.Select(c => Str(c, "value"))
			.FirstOrDefault(v => v is not null);
	}

	private static string? Str(JsonElement element, string property)
	{
		return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out JsonElement v) &&
		       v.ValueKind == JsonValueKind.String
			? v.GetString()
			: null;
	}

	private static bool Bool(JsonElement element, string mapProperty, string key)
	{
		return element.ValueKind == JsonValueKind.Object &&
		       element.TryGetProperty(mapProperty, out JsonElement map) && map.ValueKind == JsonValueKind.Object &&
		       map.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.True;
	}

	private static bool AddFirst(List<XElement> data, XName name, string value)
	{
		if (data.Any(e => e.Name == name))
			return false;
		data.Add(new XElement(name, value));
		return true;
	}

	private static string StripEmailDisplay(string value)
	{
		int lt = value.IndexOf('<');
		int gt = value.IndexOf('>');
		return lt >= 0 && gt > lt ? value[(lt + 1)..gt].Trim() : value.Trim();
	}
}

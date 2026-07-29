using System.Globalization;
using System.Text;
using System.Text.Json;
using ActiveSync.Contracts;
using ActiveSync.Protocol;
using FolkerKinzel.VCards;
using FolkerKinzel.VCards.Enums;
using FolkerKinzel.VCards.Extensions;
using FolkerKinzel.VCards.Models;
using FolkerKinzel.VCards.Models.Properties;
using MimeKit;

namespace ActiveSync.Backends.Jmap;

/// <summary>
///   JSContact (RFC 9553) ⇄ vCard — the provider-private format bridge for the JMAP contact
///   store. The contract's contacts currency is vCard, so this converter produces and consumes
///   vCard text; the EAS Contacts-class conversion is entirely host-side.
///   <para>
///     Covered: name parts, file-as/display name, emails, typed phones, home/work addresses,
///     organization/department, job title, nickname, birthday, note, categories. NOT covered:
///     photo — "media" is a Managed member but nothing reads it into a vCard PHOTO or writes one
///     back (see the <c>ClearedOnUpdate</c> comment). On write, unknown JSContact members of an
///     existing card are preserved so editing one field never drops data vCard cannot express.
///   </para>
/// </summary>
public static class JsContactConverter
{
	// vCard-managed JSContact top-level members: rewritten from the vCard on every change; any
	// other member of an existing card is preserved verbatim.
	private static readonly string[] Managed =
	[
		"name", "nicknames", "organizations", "titles", "emails", "phones", "addresses",
		"anniversaries", "notes", "keywords", "media"
	];

	// JMAP `*/set update` values are PatchObjects (RFC 8620 §5.3) — a member absent from the patch
	// is left untouched, it is not cleared. Verified against Stalwart 0.16: omitting "titles" from
	// an update leaves the old job title in place. The host hands over a COMPLETE vCard, so a
	// field the user cleared arrives as an *absent* property; it therefore has to be sent as an
	// explicit null or the clear never reaches the server. This is also correct under full-replace
	// semantics, where an explicit null and an absent member mean the same thing.
	//
	// "media" is deliberately not in this set: this bridge neither reads nor writes the photo, so
	// nulling it on every edit would destroy a picture the client never saw.
	private static readonly string[] ClearedOnUpdate =
	[
		"name", "nicknames", "organizations", "titles", "emails", "phones", "addresses",
		"anniversaries", "notes", "keywords"
	];

	/// <summary>
	///   Renders a JSContact Card as a vCard 3.0 document — the payload the contract carries.
	/// </summary>
	/// <param name="card">The JSContact Card object.</param>
	/// <returns>The vCard text.</returns>
	public static string ToVCard(JsonElement card)
	{
		StringBuilder sb = new();
		sb.Append("BEGIN:VCARD\r\nVERSION:3.0\r\n");

		// The card's own uid names the contact; the JMAP id is the fallback so the vCard always
		// carries one (the host reads it back to name a created resource and to keep a merge
		// stable across an edit).
		string uid = Str(card, "uid") ?? Str(card, "id") ?? Guid.NewGuid().ToString();
		AppendLine(sb, "UID", uid);

		string? given = null, given2 = null, surname = null, credential = null, title = null;
		if (card.TryGetProperty("name", out JsonElement name))
			foreach (JsonElement component in Array(name, "components"))
			{
				string? value = Str(component, "value");
				switch (Str(component, "kind"))
				{
					case "given": given ??= value; break;
					case "given2": given2 ??= value; break;
					case "surname": surname ??= value; break;
					case "credential": credential ??= value; break;
					case "title": title ??= value; break;
				}
			}

		AppendLine(sb, "N",
			$"{Escape(surname)};{Escape(given)};{Escape(given2)};{Escape(title)};{Escape(credential)}", true);

		string? full = card.TryGetProperty("name", out JsonElement nameForFull) ? Str(nameForFull, "full") : null;
		string display = !string.IsNullOrWhiteSpace(full)
			? full
			: string.Join(" ", new[] { given, given2, surname }.Where(s => !string.IsNullOrWhiteSpace(s)));
		AppendLine(sb, "FN", string.IsNullOrWhiteSpace(display) ? uid : display);

		foreach (JsonElement email in Values(card, "emails"))
			if (Str(email, "address") is { Length: > 0 } address)
				AppendLine(sb, "EMAIL;TYPE=INTERNET", address);

		foreach (JsonElement phone in Values(card, "phones"))
		{
			if (Str(phone, "number") is not { Length: > 0 } number)
				continue;
			bool work = Bool(phone, "contexts", "work");
			string property = Bool(phone, "features", "mobile")
				? "TEL;TYPE=CELL"
				: Bool(phone, "features", "fax")
					? work ? "TEL;TYPE=WORK,FAX" : "TEL;TYPE=HOME,FAX"
					: Bool(phone, "features", "pager")
						? "TEL;TYPE=PAGER"
						: work
							? "TEL;TYPE=WORK,VOICE"
							: "TEL;TYPE=HOME,VOICE";
			AppendLine(sb, property, number);
		}

		foreach (JsonElement address in Values(card, "addresses"))
			AppendAdr(sb, Bool(address, "contexts", "work") ? "WORK" : "HOME",
				Component(address, "name") ?? Component(address, "street"),
				Component(address, "locality"), Component(address, "region"),
				Component(address, "postcode"), Component(address, "country"));

		JsonElement org = Values(card, "organizations").FirstOrDefault();
		if (org.ValueKind == JsonValueKind.Object)
		{
			string? company = Str(org, "name");
			string? department = Array(org, "units").Select(u => Str(u, "name")).FirstOrDefault(u => u is not null);
			if (!string.IsNullOrWhiteSpace(company) || !string.IsNullOrWhiteSpace(department))
				AppendLine(sb, "ORG", $"{Escape(company)};{Escape(department)}", true);
		}

		AppendLine(sb, "TITLE",
			Values(card, "titles").Select(t => Str(t, "name")).FirstOrDefault(t => t is not null));
		AppendLine(sb, "NICKNAME",
			Values(card, "nicknames").Select(n => Str(n, "name")).FirstOrDefault(n => n is not null));

		foreach (JsonElement anniversary in Values(card, "anniversaries"))
			if (Str(anniversary, "kind") == "birth" &&
			    anniversary.TryGetProperty("date", out JsonElement date) &&
			    AnniversaryDate(date) is { } parsed)
			{
				AppendLine(sb, "BDAY", parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
				break;
			}

		AppendLine(sb, "NOTE",
			Values(card, "notes").Select(n => Str(n, "note")).FirstOrDefault(n => !string.IsNullOrEmpty(n)));

		if (card.TryGetProperty("keywords", out JsonElement keywords) && keywords.ValueKind == JsonValueKind.Object)
		{
			List<string> categories = keywords.EnumerateObject()
				.Where(k => k.Value.ValueKind == JsonValueKind.True)
				.Select(k => k.Name)
				.ToList();
			if (categories.Count > 0)
				AppendLine(sb, "CATEGORIES", string.Join(",", categories.Select(Escape)), true);
		}

		sb.Append("END:VCARD\r\n");
		return sb.ToString();
	}

	/// <summary>
	///   Builds a JSContact Card object from a COMPLETE vCard (the host already merged the
	///   client's partial data). Managed members are rewritten from the card; every other member
	///   of <paramref name="existing" /> is carried over so a member vCard cannot express survives
	///   the edit.
	/// </summary>
	/// <param name="vcf">The complete vCard text.</param>
	/// <param name="existing">The stored JSContact card on update; <c>null</c> on create.</param>
	/// <returns>The JSContact Card object to send to <c>ContactCard/set</c>.</returns>
	public static Dictionary<string, object?> FromVCard(string vcf, JsonElement? existing)
	{
		VCard? vcard = Vcf.Parse(vcf).FirstOrDefault();

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

		if (vcard is null)
			return Finish(card, existing);

		Name? name = vcard.NameViews?.FirstOrDefault(n => n is not null)?.Value;
		List<object> nameComponents = new();
		void Component(string kind, string? value)
		{
			if (!string.IsNullOrWhiteSpace(value))
				nameComponents.Add(new Dictionary<string, object?> { ["kind"] = kind, ["value"] = value });
		}

		Component("title", name?.Prefixes.FirstOrDefault());
		Component("given", name?.Given.FirstOrDefault());
		Component("given2", name?.Given2.FirstOrDefault());
		Component("surname", name?.Surnames.FirstOrDefault());
		Component("credential", name?.Suffixes.FirstOrDefault());

		string? display = vcard.DisplayNames?.FirstOrDefault(d => d is not null)?.Value;
		Dictionary<string, object?> jsName = new();
		if (nameComponents.Count > 0)
			jsName["components"] = nameComponents;
		if (!string.IsNullOrWhiteSpace(display))
			jsName["full"] = display;
		if (jsName.Count > 0)
			card["name"] = jsName;

		Dictionary<string, object?> emails = new();
		int emailIndex = 1;
		foreach (TextProperty email in vcard.EMails.OrderByPref())
		{
			if (email?.Value is not { Length: > 0 } address)
				continue;
			emails[$"e{emailIndex++}"] = new Dictionary<string, object?> { ["address"] = StripEmailDisplay(address) };
		}

		if (emails.Count > 0)
			card["emails"] = emails;

		Dictionary<string, object?> phones = new();
		int phoneIndex = 1;
		foreach (TextProperty phone in vcard.Phones.OrderByPref())
		{
			if (phone?.Value is not { Length: > 0 } number)
				continue;
			Tel? types = phone.Parameters.PhoneType;
			bool work = phone.Parameters.PropertyClass.IsSet(PCl.Work);
			Dictionary<string, object?> entry = new() { ["number"] = number };
			if (types.IsSet(Tel.Cell))
				entry["features"] = new Dictionary<string, object?> { ["mobile"] = true };
			else if (types.IsSet(Tel.Fax))
				entry["features"] = new Dictionary<string, object?> { ["fax"] = true };
			else if (types.IsSet(Tel.Pager))
				entry["features"] = new Dictionary<string, object?> { ["pager"] = true };
			// The context is what the reader uses to pick the work/home slot back out, so it is
			// written for every phone that carries one — including the fax pair, whose slot
			// (business vs home) is decided by the context alone.
			if (work)
				entry["contexts"] = new Dictionary<string, object?> { ["work"] = true };
			else if (phone.Parameters.PropertyClass.IsSet(PCl.Home))
				entry["contexts"] = new Dictionary<string, object?> { ["private"] = true };
			phones[$"p{phoneIndex++}"] = entry;
		}

		if (phones.Count > 0)
			card["phones"] = phones;

		Dictionary<string, object?> addresses = new();
		foreach (AddressProperty address in vcard.Addresses.OrderByPref())
		{
			if (address?.Value is not { } adr)
				continue;
			bool work = address.Parameters.PropertyClass.IsSet(PCl.Work);
			string id = work ? "work" : "home";
			if (addresses.ContainsKey(id))
				continue; // one address per context, matching the EAS Home*/Business* field set
			List<object> components = new();
			void Comp(string kind, string? value)
			{
				if (!string.IsNullOrWhiteSpace(value))
					components.Add(new Dictionary<string, object?> { ["kind"] = kind, ["value"] = value });
			}

			Comp("name", string.Join(", ", adr.Street));
			Comp("locality", adr.Locality.FirstOrDefault());
			Comp("region", adr.Region.FirstOrDefault());
			Comp("postcode", adr.PostalCode.FirstOrDefault());
			Comp("country", adr.Country.FirstOrDefault());
			if (components.Count == 0)
				continue;
			addresses[id] = new Dictionary<string, object?>
			{
				["components"] = components,
				["contexts"] = new Dictionary<string, object?> { [work ? "work" : "private"] = true }
			};
		}

		if (addresses.Count > 0)
			card["addresses"] = addresses;

		Organization? organization = vcard.Organizations?.FirstOrDefault(o => o is not null)?.Value;
		string? companyName = organization?.Name;
		string? departmentName = organization?.Units?.FirstOrDefault();
		if (!string.IsNullOrWhiteSpace(companyName) || !string.IsNullOrWhiteSpace(departmentName))
		{
			Dictionary<string, object?> org = new();
			if (!string.IsNullOrWhiteSpace(companyName))
				org["name"] = companyName;
			if (!string.IsNullOrWhiteSpace(departmentName))
				org["units"] = new object[] { new Dictionary<string, object?> { ["name"] = departmentName } };
			card["organizations"] = new Dictionary<string, object?> { ["o"] = org };
		}

		if (vcard.Titles?.FirstOrDefault(t => t is not null)?.Value is { Length: > 0 } jobTitle)
			card["titles"] = new Dictionary<string, object?>
			{
				["t"] = new Dictionary<string, object?> { ["kind"] = "title", ["name"] = jobTitle }
			};

		if (vcard.NickNames?.FirstOrDefault(n => n is not null)?.Value?.FirstOrDefault() is { Length: > 0 } nick)
			card["nicknames"] = new Dictionary<string, object?>
			{
				["n"] = new Dictionary<string, object?> { ["name"] = nick }
			};

		// Anniversaries: only "birth" is rewritten from the vCard BDAY below. Any other kind (e.g.
		// a wedding anniversary — vCard carries it as ANNIVERSARY, which this bridge does not
		// read/write) has no vCard-side representation to rebuild it from, so it is carried over
		// from the existing card rather than dropped — "anniversaries" is Managed, so without this
		// it is nulled/overwritten on every edit.
		Dictionary<string, object?> anniversaries = new();
		if (existing is { ValueKind: JsonValueKind.Object } priorCard &&
		    priorCard.TryGetProperty("anniversaries", out JsonElement priorAnniversaries) &&
		    priorAnniversaries.ValueKind == JsonValueKind.Object)
			foreach (JsonProperty entry in priorAnniversaries.EnumerateObject())
				if (Str(entry.Value, "kind") != "birth")
					anniversaries[entry.Name] = JsonSerializer.Deserialize<object>(entry.Value.GetRawText());

		DateAndOrTime? birthday = vcard.BirthDayViews?.FirstOrDefault(b => b is not null)?.Value;
		DateTime? birthUtc = birthday?.DateOnly is { } dateOnly
			? new DateTime(dateOnly, new TimeOnly(12, 0), DateTimeKind.Utc)
			: birthday?.DateTimeOffset?.UtcDateTime;
		if (birthUtc is { } birth)
			anniversaries["b"] = new Dictionary<string, object?>
			{
				["@type"] = "Anniversary",
				["kind"] = "birth",
				["date"] = new Dictionary<string, object?>
				{
					["@type"] = "Timestamp",
					["utc"] = JmapDate.ToUtc(birth)
				}
			};

		if (anniversaries.Count > 0)
			card["anniversaries"] = anniversaries;

		if (vcard.Notes?.FirstOrDefault(n => n is not null)?.Value is { Length: > 0 } note)
			card["notes"] = new Dictionary<string, object?>
			{
				["n"] = new Dictionary<string, object?> { ["note"] = note }
			};

		IReadOnlyList<string>? categories = vcard.Categories?.FirstOrDefault(c => c is not null)?.Value;
		if (categories is not null)
		{
			List<string> kept = categories.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
			if (kept.Count > 0)
				card["keywords"] = kept.ToDictionary(c => c, object? (_) => true);
		}

		return Finish(card, existing);
	}

	/// <summary>
	///   Update (not create): every managed member the vCard did not populate is explicitly
	///   nulled, so clearing a field survives the PatchObject semantics of <c>ContactCard/set</c>.
	///   A create sends only what it has — a null there is a member the card never had.
	/// </summary>
	private static Dictionary<string, object?> Finish(Dictionary<string, object?> card, JsonElement? existing)
	{
		if (existing is not null)
			foreach (string member in ClearedOnUpdate)
				if (!card.ContainsKey(member))
					card[member] = null;
		return card;
	}

	/// <summary>
	///   The typed GAL projection of a JSContact card. Photos are NOT read — "media" has no
	///   reader here — so the store reports <see cref="GalPictureStatus.None" /> when the client
	///   asked for pictures.
	/// </summary>
	/// <param name="card">The JSContact Card object.</param>
	/// <returns>The GAL entry (never null; matching is the caller's).</returns>
	public static GalEntry ToGalEntry(JsonElement card)
	{
		string? given = null, surname = null, full = null;
		if (card.TryGetProperty("name", out JsonElement name))
		{
			full = Str(name, "full");
			foreach (JsonElement component in Array(name, "components"))
				switch (Str(component, "kind"))
				{
					case "given": given ??= Str(component, "value"); break;
					case "surname": surname ??= Str(component, "value"); break;
				}
		}

		string display = !string.IsNullOrWhiteSpace(full)
			? full
			: string.Join(" ", new[] { given, surname }.Where(v => !string.IsNullOrEmpty(v)));
		string? email = Values(card, "emails").Select(e => Str(e, "address")).FirstOrDefault(a => a is not null);
		// Parity with the pre-contract projection: the GAL Phone slot carried the MOBILE number.
		string? phone = Values(card, "phones")
			.Where(p => Bool(p, "features", "mobile"))
			.Select(p => Str(p, "number"))
			.FirstOrDefault(n => n is not null);
		string? company = Values(card, "organizations").Select(o => Str(o, "name")).FirstOrDefault(n => n is not null);

		return new GalEntry
		{
			DisplayName = display,
			EmailAddress = email,
			FirstName = given,
			LastName = surname,
			Phone = phone,
			Company = company
		};
	}

	/// <summary>
	///   Reads an RFC 9553 <c>Anniversary.date</c>, which is either a <c>Timestamp</c> (a
	///   <c>utc</c> date-time string) or a <c>PartialDate</c> (year/month/day numbers, no zone).
	///   A bare <c>date</c> string is also accepted, since it costs one line and some servers
	///   have shipped it.
	/// </summary>
	private static DateTime? AnniversaryDate(JsonElement date)
	{
		if (date.ValueKind != JsonValueKind.Object)
			return null;

		foreach (string member in (string[])["utc", "date", "local"])
			if (Str(date, member) is { } text &&
			    DateTime.TryParse(text, CultureInfo.InvariantCulture,
				    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime parsed))
				return parsed;

		// PartialDate: a missing year or month makes the date unusable as a vCard BDAY (which this
		// bridge writes as a full calendar date), so all three are required rather than guessed at.
		if (Int(date, "year") is { } year && Int(date, "month") is { } month && Int(date, "day") is { } day &&
		    year is >= 1 and <= 9999 && month is >= 1 and <= 12 && day >= 1 &&
		    day <= DateTime.DaysInMonth(year, month))
			return new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);

		return null;
	}

	// ---------- vCard writing ----------

	private static void AppendAdr(
		StringBuilder sb, string type, string? street, string? city, string? state, string? postal, string? country)
	{
		if (street is null && city is null && state is null && postal is null && country is null)
			return;
		AppendFolded(sb,
			$"ADR;TYPE={type}:;;{Escape(street)};{Escape(city)};{Escape(state)};{Escape(postal)};{Escape(country)}");
	}

	private static void AppendLine(StringBuilder sb, string property, string? value, bool preEscaped = false)
	{
		if (string.IsNullOrWhiteSpace(value))
			return;
		AppendFolded(sb, $"{property}:{(preEscaped ? value : Escape(value))}");
	}

	/// <summary>
	///   Folds at 75 OCTETS per RFC 6350 §3.2 — not 75 chars, which over-runs the limit for any
	///   non-ASCII content — and only ever on a code-point boundary, so a surrogate pair (an emoji
	///   in a note) is never split into an unpaired half that corrupts the card's UTF-8.
	///   Continuation lines carry a leading space, which counts toward their 75.
	/// </summary>
	private static void AppendFolded(StringBuilder sb, string line)
	{
		const int width = 75;
		int start = 0;
		bool first = true;
		do
		{
			int take = TakeUtf8(line, start, first ? width : width - 1);
			if (!first)
				sb.Append(' ');
			sb.Append(line, start, take).Append("\r\n");
			start += take;
			first = false;
		} while (start < line.Length);
	}

	/// <summary>
	///   The number of UTF-16 units from <paramref name="start" /> whose UTF-8 encoding fits in
	///   <paramref name="maxBytes" />, counting whole code points only. Always advances by at
	///   least one code point so folding cannot loop.
	/// </summary>
	private static int TakeUtf8(string line, int start, int maxBytes)
	{
		int bytes = 0;
		int i = start;
		while (i < line.Length)
		{
			bool pair = char.IsHighSurrogate(line[i]) && i + 1 < line.Length && char.IsLowSurrogate(line[i + 1]);
			int cost = pair ? 4 : line[i] < 0x80 ? 1 : line[i] < 0x800 ? 2 : 3;
			if (bytes + cost > maxBytes && i > start)
				break;
			bytes += cost;
			i += pair ? 2 : 1;
		}

		return i - start;
	}

	/// <summary>
	///   Escapes a vCard property value: the structural characters, then every remaining control
	///   character (via the codebase's one classifier) so nothing can smuggle a line break into
	///   the card and write arbitrary properties into it.
	/// </summary>
	private static string Escape(string? value)
	{
		if (value is null)
			return "";
		string escaped = value.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,")
			.Replace("\r\n", "\\n").Replace("\n", "\\n").Replace("\r", "\\n");
		return escaped.Any(c => WireLog.IsUnsafe(c, allowLineStructure: false))
			? string.Concat(escaped.Where(c => !WireLog.IsUnsafe(c, allowLineStructure: false)))
			: escaped;
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

	private static string StripEmailDisplay(string value)
	{
		// A malformed "<" with no matching ">" (or the brackets swapped) used to fall through
		// to returning the WHOLE string, display-name text and stray bracket included — not a valid
		// email. MimeKit's mailbox parser is lenient enough to recover the intended address from a
		// truncated "Name <addr" shape; only genuinely unparseable input (no recognizable address at
		// all) keeps the old substring-or-whole-string heuristic as a last resort.
		if (MailboxAddress.TryParse(value, out MailboxAddress? mailbox) && !string.IsNullOrEmpty(mailbox.Address))
			return mailbox.Address;
		int lt = value.IndexOf('<');
		int gt = value.IndexOf('>');
		return lt >= 0 && gt > lt ? value[(lt + 1)..gt].Trim() : value.Trim();
	}
}

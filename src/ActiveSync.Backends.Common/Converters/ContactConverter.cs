using System.Globalization;
using System.Text;
using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;
using FolkerKinzel.VCards;
using FolkerKinzel.VCards.Enums;
using FolkerKinzel.VCards.Extensions;
using FolkerKinzel.VCards.Models;
using FolkerKinzel.VCards.Models.Properties;

namespace ActiveSync.Backends.Common.Converters;

/// <summary>vCard ↔ EAS Contacts-class ApplicationData (MS-ASCNTC).</summary>
public static class ContactConverter
{
	private static readonly XNamespace Contacts = EasNamespaces.Contacts;
	private static readonly XNamespace Contacts2 = EasNamespaces.Contacts2;
	private static readonly XNamespace AirSyncBase = EasNamespaces.AirSyncBase;
	private static readonly XNamespace Gal = EasNamespaces.Gal;

	public static string? ExtractUid(string vcf)
	{
		ContactIDProperty? id = Vcf.Parse(vcf).FirstOrDefault()?.ContactID;
		return id?.Value?.String ?? id?.Value?.Guid?.ToString() ?? id?.Value?.Uri?.ToString();
	}

	/// <summary>
	///   Returns null for an empty or unparsable card, matching every sibling converter —
	///   the store base classes read that as "skip this item", so one corrupt card costs one
	///   contact instead of the whole Sync response.
	/// </summary>
	public static List<XElement>? ToApplicationData(string vcf, BodyPreference bodyPreference)
	{
		return ToApplicationData(vcf, 96 * 1024, bodyPreference.TruncationSize);
	}

	/// <summary>
	///   <paramref name="maxPhotoBytes" /> caps PHOTO on the wire; the ghosting merge passes
	///   <see cref="int.MaxValue" /> so an oversized stored photo survives an update it was
	///   never sent in. <paramref name="noteTruncationSize" /> is null (no truncation) on the
	///   ghosting path — the merge needs the FULL stored note to re-embed in the vCard, never a
	///   client-budget-truncated one, or an unrelated edit would permanently cut the note down to
	///   whatever a past sync's TruncationSize allowed.
	/// </summary>
	private static List<XElement>? ToApplicationData(string vcf, int maxPhotoBytes, long? noteTruncationSize = null)
	{
		if (Vcf.Parse(vcf).FirstOrDefault() is not { } vcard)
			return null;

		List<XElement> data = new();

		void Add(XName name, string? value)
		{
			if (!string.IsNullOrWhiteSpace(value))
				data.Add(new XElement(name, value));
		}

		Name? name = vcard.NameViews?.FirstOrDefault(n => n is not null)?.Value;
		Add(Contacts + "FirstName", name?.Given.FirstOrDefault());
		Add(Contacts + "MiddleName", name?.Given2.FirstOrDefault());
		Add(Contacts + "LastName", name?.Surnames.FirstOrDefault());
		Add(Contacts + "Suffix", name?.Suffixes.FirstOrDefault());
		Add(Contacts + "Title", name?.Prefixes.FirstOrDefault());
		Add(Contacts + "FileAs", vcard.DisplayNames?.FirstOrDefault(d => d is not null)?.Value);

		int emailIndex = 1;
		foreach (var email in vcard.EMails.OrderByPref())
		{
			if (email?.Value is not { } address || emailIndex > 3)
				continue;
			Add(Contacts + $"Email{emailIndex}Address", address);
			emailIndex++;
		}

		foreach (var phone in vcard.Phones.OrderByPref())
		{
			if (phone?.Value is not { } number)
				continue;
			Tel? types = phone.Parameters.PhoneType;
			if (types.IsSet(Tel.Cell))
			{
				AddFirst(Contacts + "MobilePhoneNumber", number);
			}
			// D9: these two must precede the generic Fax/untyped branches below — the writer
			// emits TEL;TYPE=HOME,FAX for HomeFaxNumber and TEL;TYPE=CAR for CarPhoneNumber, so
			// the read has to recognise both slots or Ghost() migrates them to the wrong field
			// (BusinessFaxNumber / HomePhoneNumber) on the next unrelated edit.
			else if (types.IsSet(Tel.Fax) && phone.Parameters.PropertyClass.IsSet(PCl.Home))
			{
				AddFirst(Contacts + "HomeFaxNumber", number);
			}
			else if (types.IsSet(Tel.Fax))
			{
				AddFirst(Contacts + "BusinessFaxNumber", number);
			}
			else if (types.IsSet(Tel.Car))
			{
				AddFirst(Contacts + "CarPhoneNumber", number);
			}
			else if (types.IsSet(Tel.Pager))
			{
				AddFirst(Contacts + "PagerNumber", number);
			}
			else if (phone.Parameters.PropertyClass.IsSet(PCl.Work))
			{
				if (!AddFirst(Contacts + "BusinessPhoneNumber", number))
					AddFirst(Contacts + "Business2PhoneNumber", number);
			}
			else if (phone.Parameters.PropertyClass.IsSet(PCl.Home))
			{
				if (!AddFirst(Contacts + "HomePhoneNumber", number))
					AddFirst(Contacts + "Home2PhoneNumber", number);
			}
			else
			{
				if (!AddFirst(Contacts + "HomePhoneNumber", number))
					AddFirst(Contacts + "CarPhoneNumber", number);
			}
		}

		// D10: MS-ASCNTC's Home*/Business* address fields are single-instance, but there is no
		// Home2/Business2 fallback the way phones have — so once a slot is filled, a second
		// address of the same class must be skipped ENTIRELY (not field-by-field), or its
		// fields would blend with the first address's into one inconsistent record.
		HashSet<string> filledAddressSlots = new(StringComparer.Ordinal);
		foreach (var address in vcard.Addresses.OrderByPref())
		{
			if (address?.Value is not { } adr)
				continue;
			bool isWork = address.Parameters.PropertyClass.IsSet(PCl.Work);
			string prefix = isWork ? "Business" : "Home";
			if (!filledAddressSlots.Add(prefix))
				continue;
			Add(Contacts + $"{prefix}Street", string.Join(", ", adr.Street));
			Add(Contacts + $"{prefix}City", adr.Locality.FirstOrDefault());
			Add(Contacts + $"{prefix}State", adr.Region.FirstOrDefault());
			Add(Contacts + $"{prefix}PostalCode", adr.PostalCode.FirstOrDefault());
			Add(Contacts + $"{prefix}Country", adr.Country.FirstOrDefault());
		}

		Organization? org = vcard.Organizations?.FirstOrDefault(o => o is not null)?.Value;
		Add(Contacts + "CompanyName", org?.Name);
		Add(Contacts + "Department", org?.Units?.FirstOrDefault());
		Add(Contacts + "JobTitle", vcard.Titles?.FirstOrDefault(t => t is not null)?.Value);
		Add(Contacts + "WebPage", vcard.Urls?.FirstOrDefault(u => u is not null)?.Value);
		Add(Contacts2 + "NickName", vcard.NickNames?.FirstOrDefault(n => n is not null)?.Value?.FirstOrDefault());

		DateAndOrTime? birthday = vcard.BirthDayViews?.FirstOrDefault(b => b is not null)?.Value;
		if (birthday?.DateOnly is { } date)
			data.Add(new XElement(Contacts + "Birthday",
				EasDateTime.ToLong(new DateTime(date, new TimeOnly(12, 0), DateTimeKind.Utc))));
		else if (birthday?.DateTimeOffset is { } dto)
			data.Add(new XElement(Contacts + "Birthday", EasDateTime.ToLong(dto.UtcDateTime)));

		RawData? photo = vcard.Photos?.FirstOrDefault(p => p is not null)?.Value;
		if (photo?.Bytes is { Length: > 0 } bytes && bytes.Length < maxPhotoBytes)
			data.Add(new XElement(Contacts + "Picture", Convert.ToBase64String(bytes)));

		string? note = vcard.Notes?.FirstOrDefault(n => n is not null)?.Value;
		if (!string.IsNullOrEmpty(note))
		{
			(string sent, bool truncated, long estimated) = BodyText.ForBody(note, noteTruncationSize);
			data.Add(AirSyncBodyWriter.Build(estimated, truncated, sent));
		}

		IReadOnlyList<string>? categories = vcard.Categories?.FirstOrDefault(c => c is not null)?.Value;
		if (categories is not null && categories.Any())
			data.Add(new XElement(Contacts + "Categories",
				categories.Select(c => new XElement(Contacts + "Category", c))));

		return data;

		bool AddFirst(XName xname, string value)
		{
			if (data.Any(e => e.Name == xname))
				return false;
			data.Add(new XElement(xname, value));
			return true;
		}
	}

	/// <summary>
	///   Builds a vCard 3.0 from client ApplicationData. A managed property present in the
	///   payload is rewritten from it; one that is absent is ghosted through from
	///   <paramref name="existingVcard" />, and everything the Contacts class cannot express
	///   (X- properties, IMPP, GEO, 4th+ email addresses, …) is carried over verbatim — so
	///   editing one field never erases another.
	/// </summary>
	public static string FromApplicationData(XElement applicationData, string uid, string? existingVcard = null)
	{
		XElement merged = Ghost(applicationData, existingVcard);

		string? V(string localName)
		{
			return merged.Element(Contacts + localName)?.Value;
		}

		StringBuilder sb = new();
		sb.Append("BEGIN:VCARD\r\nVERSION:3.0\r\n");
		AppendLine(sb, "UID", uid);
		AppendLine(sb, "N",
			$"{Escape(V("LastName"))};{Escape(V("FirstName"))};{Escape(V("MiddleName"))};{Escape(V("Title"))};{Escape(V("Suffix"))}",
			true);

		string? fileAs = V("FileAs");
		string display = !string.IsNullOrWhiteSpace(fileAs)
			? fileAs
			: string.Join(" ", new[] { V("FirstName"), V("MiddleName"), V("LastName") }
				.Where(s => !string.IsNullOrWhiteSpace(s)));
		AppendLine(sb, "FN", string.IsNullOrWhiteSpace(display) ? uid : display);

		for (int i = 1; i <= 3; i++)
			AppendLine(sb, "EMAIL;TYPE=INTERNET", StripEmailDisplay(V($"Email{i}Address")));

		AppendLine(sb, "TEL;TYPE=CELL", V("MobilePhoneNumber"));
		AppendLine(sb, "TEL;TYPE=HOME,VOICE", V("HomePhoneNumber"));
		AppendLine(sb, "TEL;TYPE=HOME,VOICE", V("Home2PhoneNumber"));
		AppendLine(sb, "TEL;TYPE=WORK,VOICE", V("BusinessPhoneNumber"));
		AppendLine(sb, "TEL;TYPE=WORK,VOICE", V("Business2PhoneNumber"));
		AppendLine(sb, "TEL;TYPE=WORK,FAX", V("BusinessFaxNumber"));
		AppendLine(sb, "TEL;TYPE=HOME,FAX", V("HomeFaxNumber"));
		AppendLine(sb, "TEL;TYPE=PAGER", V("PagerNumber"));
		AppendLine(sb, "TEL;TYPE=CAR", V("CarPhoneNumber"));

		AppendAdr(sb, "HOME", V("HomeStreet"), V("HomeCity"), V("HomeState"), V("HomePostalCode"), V("HomeCountry"));
		AppendAdr(sb, "WORK", V("BusinessStreet"), V("BusinessCity"), V("BusinessState"), V("BusinessPostalCode"),
			V("BusinessCountry"));

		string? company = V("CompanyName");
		string? department = V("Department");
		if (company is not null || department is not null)
			AppendLine(sb, "ORG", $"{Escape(company)};{Escape(department)}", true);
		AppendLine(sb, "TITLE", V("JobTitle"));
		AppendLine(sb, "URL", V("WebPage"));
		AppendLine(sb, "NICKNAME", merged.Element(Contacts2 + "NickName")?.Value);

		string? birthday = V("Birthday");
		if (birthday is not null && EasDateTime.TryParse(birthday, out DateTime bday))
			AppendLine(sb, "BDAY", bday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

		string? body = merged.Element(AirSyncBase + "Body")?.Element(AirSyncBase + "Data")?.Value;
		AppendLine(sb, "NOTE", body);

		List<string>? categories = merged.Element(Contacts + "Categories")?
			.Elements(Contacts + "Category").Select(c => c.Value).ToList();
		if (categories is { Count: > 0 })
			AppendLine(sb, "CATEGORIES", string.Join(",", categories.Select(Escape)), true);

		bool photoWritten = AppendPhoto(sb, V("Picture"));

		if (existingVcard is not null)
			AppendPreserved(sb, existingVcard, photoWritten);

		sb.Append("END:VCARD\r\n");
		return sb.ToString();
	}

	/// <summary>
	///   Overlays the payload on the stored card's own EAS view, so a managed element the
	///   client omitted keeps its stored value (MS-ASCMD ghosting: absent means "leave as is",
	///   never "erase"). Presence, not value, is what decides — an element sent empty still
	///   clears the property. Same stance as <c>CalendarConverter</c> and <c>TasksConverter</c>.
	/// </summary>
	private static XElement Ghost(XElement applicationData, string? existingVcard)
	{
		if (existingVcard is null || ToApplicationData(existingVcard, int.MaxValue) is not { } stored)
			return applicationData;

		XElement merged = new(applicationData);
		foreach (XElement element in stored)
			if (merged.Element(element.Name) is null)
				merged.Add(element);
		return merged;
	}

	/// <summary>
	///   Property names the EAS Contacts class manages — rewritten from the merged payload on
	///   every change; all other existing lines are preserved verbatim.
	/// </summary>
	private static readonly HashSet<string> ManagedProperties = new(StringComparer.OrdinalIgnoreCase)
	{
		"BEGIN", "END", "VERSION", "PRODID", "REV", "UID",
		"N", "FN", "EMAIL", "TEL", "ADR", "ORG", "TITLE", "URL", "NICKNAME",
		"BDAY", "NOTE", "CATEGORIES", "PHOTO"
	};

	/// <summary>
	///   D3: "surplus" (not one of Email1-3) must be decided the same way Email1-3 itself was
	///   picked — <c>vcard.EMails.OrderByPref()</c>, via <see cref="Ghost" />/
	///   <see cref="ToApplicationData(string,int)" /> — not by raw FILE position, which is not
	///   guaranteed to match pref order (a PREF param on a later line promotes it to the front).
	///   Matching against what ended up WRITTEN (the merged/possibly client-overridden Email1-3
	///   values) is wrong too: a client that replaces Email1Address leaves the stored card's old
	///   top-pref address matching nothing, so it would wrongly reappear as "surplus". The
	///   correct exclusion set is the stored card's OWN top-3-by-pref addresses, independent of
	///   what the client did with those slots afterward.
	/// </summary>
	private static void AppendPreserved(StringBuilder sb, string existingVcard, bool photoWritten)
	{
		HashSet<string> top3 = TopEmailAddresses(existingVcard, 3);
		// D4: PHOTO is normally "managed" (rewritten from V("Picture") only), but the only value
		// ToApplicationData can ever produce for Picture comes from decodable BYTES — a
		// URI-valued PHOTO (verified against FolkerKinzel.VCards 8.2.0: Value.Bytes is null for
		// PHOTO;VALUE=URI) never round-trips through Picture at all, so treating it as "managed"
		// silently deletes it on the next unrelated edit. Preserve the raw stored PHOTO line
		// verbatim when nothing else wrote a photo AND the stored one carries no decodable bytes
		// — i.e. it is exactly the case ToApplicationData/AppendPhoto can never reproduce. When
		// the stored photo DOES carry bytes, an explicit clear (empty <Picture/>) must still work,
		// so this never preserves a byte-backed photo.
		bool preservePhoto = !photoWritten && !StoredPhotoHasDecodableBytes(existingVcard);
		foreach (string line in UnfoldLines(existingVcard))
		{
			string name = PropertyNameOf(line);
			if (name.Equals("EMAIL", StringComparison.OrdinalIgnoreCase))
			{
				string address = EmailAddressOf(line);
				// A blank/unparsable address can't be confirmed as one of the top 3 — preserve
				// it rather than risk silently dropping data.
				if (address.Length == 0 || !top3.Contains(address))
					AppendFolded(sb, line);
				continue;
			}

			if (name.Equals("PHOTO", StringComparison.OrdinalIgnoreCase))
			{
				if (preservePhoto)
					AppendFolded(sb, line);
				continue;
			}

			if (!ManagedProperties.Contains(name))
				AppendFolded(sb, line);
		}
	}

	private static bool StoredPhotoHasDecodableBytes(string vcf)
	{
		if (Vcf.Parse(vcf).FirstOrDefault() is not { } vcard)
			return false;
		return vcard.Photos?.FirstOrDefault(p => p is not null)?.Value?.Bytes is { Length: > 0 };
	}

	private static HashSet<string> TopEmailAddresses(string vcf, int count)
	{
		HashSet<string> addresses = new(StringComparer.OrdinalIgnoreCase);
		if (Vcf.Parse(vcf).FirstOrDefault() is not { } vcard)
			return addresses;
		foreach (var email in vcard.EMails.OrderByPref())
		{
			if (addresses.Count >= count)
				break;
			if (email?.Value is { } address)
				addresses.Add(address);
		}

		return addresses;
	}

	private static string EmailAddressOf(string line)
	{
		int colon = line.IndexOf(':');
		return colon >= 0 ? line[(colon + 1)..].Trim() : "";
	}

	private static IEnumerable<string> UnfoldLines(string vcf)
	{
		string? current = null;
		foreach (string raw in vcf.Split('\n'))
		{
			string line = raw.TrimEnd('\r');
			if (line.Length == 0)
				continue;
			if (line[0] is ' ' or '\t')
			{
				current += line[1..];
				continue;
			}

			if (current is not null)
				yield return current;
			current = line;
		}

		if (current is not null)
			yield return current;
	}

	private static string PropertyNameOf(string line)
	{
		int end = line.IndexOfAny([':', ';']);
		string name = end >= 0 ? line[..end] : line;
		int dot = name.LastIndexOf('.'); // strip Apple-style group prefixes ("item1.X-ABLABEL")
		return dot >= 0 ? name[(dot + 1)..] : name;
	}

	/// <summary>Matches a vCard against a GAL query; returns Gal-namespace properties if it matches.</summary>
	public static List<XElement>? ToGalEntry(string vcf, string query)
	{
		return Vcf.Parse(vcf).FirstOrDefault() is { } vcard ? ToGalEntry(vcard, query) : null;
	}

	/// <summary>
	///   D19: parses the vCard ONCE and produces the GAL entry plus (optionally) its photo, so a
	///   matching contact is not parsed three times (ToGalEntry + AppendGalPicture each used to
	///   re-parse). Returns null when the card is unparsable or does not match the query.
	/// </summary>
	public static List<XElement>? BuildGalEntry(
		string vcf, string query, bool wantPhoto, int? maxPhotoBytes, bool photoLimitReached, out bool photoGranted)
	{
		photoGranted = false;
		if (Vcf.Parse(vcf).FirstOrDefault() is not { } vcard)
			return null;
		List<XElement>? entry = ToGalEntry(vcard, query);
		if (entry is null)
			return null;
		if (wantPhoto)
			photoGranted = AppendGalPicture(entry, vcard, maxPhotoBytes, photoLimitReached);
		return entry;
	}

	private static List<XElement>? ToGalEntry(VCard vcard, string query)
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

		List<XElement> entry = new() { new XElement(Gal + "DisplayName", display) };
		if (!string.IsNullOrEmpty(email))
			entry.Add(new XElement(Gal + "EmailAddress", email));
		if (!string.IsNullOrEmpty(first))
			entry.Add(new XElement(Gal + "FirstName", first));
		if (!string.IsNullOrEmpty(last))
			entry.Add(new XElement(Gal + "LastName", last));
		string? phone = vcard.Phones.OrderByPref().FirstOrDefault(p => p?.Value is not null)?.Value;
		if (phone is not null)
			entry.Add(new XElement(Gal + "Phone", phone));
		string? company = vcard.Organizations?.FirstOrDefault(o => o is not null)?.Value?.Name;
		if (company is not null)
			entry.Add(new XElement(Gal + "Company", company));
		return entry;
	}

	/// <summary>
	///   Appends the gal:Picture element per the MS-ASCMD photo rules (status 1 + data,
	///   173 no photo, 174 over MaxSize, 175 count limit reached across the result set).
	///   Returns true when actual photo data was included, so the caller can count toward
	///   MaxPictures.
	/// </summary>
	public static bool AppendGalPicture(List<XElement> entry, string vcf, int? maxSizeBytes, bool limitReached)
	{
		return AppendGalPicture(entry, Vcf.Parse(vcf).FirstOrDefault(), maxSizeBytes, limitReached);
	}

	private static bool AppendGalPicture(List<XElement> entry, VCard? vcard, int? maxSizeBytes, bool limitReached)
	{
		if (limitReached)
		{
			entry.Add(new XElement(Gal + "Picture", new XElement(Gal + "Status", "175")));
			return false;
		}

		byte[]? photo = vcard?.Photos?.FirstOrDefault(p => p is not null)?.Value?.Bytes;
		if (photo is not { Length: > 0 })
		{
			entry.Add(new XElement(Gal + "Picture", new XElement(Gal + "Status", "173")));
			return false;
		}

		if (maxSizeBytes is { } maxSize && photo.Length > maxSize)
		{
			entry.Add(new XElement(Gal + "Picture", new XElement(Gal + "Status", "174")));
			return false;
		}

		entry.Add(new XElement(Gal + "Picture",
			new XElement(Gal + "Status", "1"),
			new XElement(Gal + "Data", Convert.ToBase64String(photo))));
		return true;
	}

	private static string? StripEmailDisplay(string? value)
	{
		if (value is null)
			return null;
		// Clients may send "Display Name <user@host>"
		int lt = value.IndexOf('<');
		int gt = value.IndexOf('>');
		return lt >= 0 && gt > lt ? value[(lt + 1)..gt].Trim() : value.Trim();
	}

	/// <summary>
	///   Emits PHOTO from the client's base64 text. The value is decoded and RE-ENCODED rather
	///   than interpolated: it is the one client-supplied string in this builder that never
	///   passes through <see cref="Escape" />, and an embedded CRLF would otherwise write
	///   arbitrary properties into the stored card and, via CardDAV, onto the DAV server.
	///   Re-encoding makes that structurally impossible. Undecodable input is skipped.
	/// </summary>
	/// <summary>Returns true when an actual PHOTO line was written (used by D4's preservation guard).</summary>
	private static bool AppendPhoto(StringBuilder sb, string? picture)
	{
		if (string.IsNullOrWhiteSpace(picture))
			return false;

		byte[] buffer = new byte[picture.Length]; // decoded bytes are always fewer than base64 chars
		if (!Convert.TryFromBase64String(picture.Trim(), buffer, out int written) || written == 0)
			return false;

		ReadOnlySpan<byte> bytes = buffer.AsSpan(0, written);
		string type = bytes switch
		{
			[0xFF, 0xD8, 0xFF, ..] => ";TYPE=JPEG",
			[0x89, (byte)'P', (byte)'N', (byte)'G', ..] => ";TYPE=PNG",
			[(byte)'G', (byte)'I', (byte)'F', (byte)'8', ..] => ";TYPE=GIF",
			_ => "" // unrecognised: no parameter beats a wrong one
		};
		AppendFolded(sb, $"PHOTO;ENCODING=b{type}:{Convert.ToBase64String(bytes)}");
		return true;
	}

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
	///   non-ASCII content — and only ever on a code-point boundary, so a surrogate pair (an
	///   emoji in a note) is never split into an unpaired half that corrupts the card's UTF-8.
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

	private static string Escape(string? value)
	{
		if (value is null)
			return "";

		// D24: the newline replaces only ever matched "\r\n" and "\n" — a bare "\r" (not part of a
		// CRLF pair) survived unescaped into the property value, and some parsers treat a lone CR
		// as a line terminator, truncating the property and turning the remainder into a spurious
		// continuation line. The trailing "\r" replace closes that; any other remaining control
		// character is stripped using the codebase's one control-character classifier
		// (WireLog.IsUnsafe) so nothing else can smuggle a structural break into the card.
		string escaped = value.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,")
			.Replace("\r\n", "\\n").Replace("\n", "\\n").Replace("\r", "\\n");
		return escaped.Any(c => WireLog.IsUnsafe(c, allowLineStructure: false))
			? string.Concat(escaped.Where(c => !WireLog.IsUnsafe(c, allowLineStructure: false)))
			: escaped;
	}
}

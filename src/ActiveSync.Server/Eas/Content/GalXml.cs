using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;

namespace ActiveSync.Server.Eas.Content;

/// <summary>
///   Typed <see cref="GalEntry" /> → EAS wire shapes. The store hands over the typed entry (it
///   enforced the photo limits); the HOST owns both wire projections — the Search command's
///   gal:-namespace properties here, and ResolveRecipients' RR-namespace shape in its handler —
///   including the MS-ASCMD photo status mapping (Available → 1+Data, None → 173,
///   OverSizeLimit → 174, OverCountLimit → 175).
/// </summary>
internal static class GalXml
{
	private static readonly XNamespace Gal = EasNamespaces.Gal;

	public static List<XElement> ToGalProperties(GalEntry entry)
	{
		List<XElement> properties = new() { new XElement(Gal + "DisplayName", entry.DisplayName) };
		Append(properties, "EmailAddress", entry.EmailAddress);
		Append(properties, "FirstName", entry.FirstName);
		Append(properties, "LastName", entry.LastName);
		Append(properties, "Phone", entry.Phone);
		Append(properties, "HomePhone", entry.HomePhone);
		Append(properties, "MobilePhone", entry.MobilePhone);
		Append(properties, "Company", entry.Company);
		Append(properties, "Title", entry.Title);
		Append(properties, "Office", entry.Office);
		Append(properties, "Alias", entry.Alias);
		if (entry.Picture is { } picture)
		{
			XElement element = new(Gal + "Picture", new XElement(Gal + "Status", WireStatus(picture.Status)));
			if (picture is { Status: GalPictureStatus.Available, Picture: { } photo })
				element.Add(new XElement(Gal + "Data", Convert.ToBase64String(photo.Data.Span)));
			properties.Add(element);
		}

		return properties;
	}

	/// <summary>The MS-ASCMD Picture Status wire value for a typed photo outcome.</summary>
	public static string WireStatus(GalPictureStatus status)
	{
		return status switch
		{
			GalPictureStatus.Available => "1",
			GalPictureStatus.OverSizeLimit => "174",
			GalPictureStatus.OverCountLimit => "175",
			_ => "173" // no photo
		};
	}

	private static void Append(List<XElement> properties, string name, string? value)
	{
		if (!string.IsNullOrEmpty(value))
			properties.Add(new XElement(Gal + name, value));
	}
}

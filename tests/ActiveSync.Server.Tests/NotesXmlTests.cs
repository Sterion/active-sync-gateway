using System.Xml.Linq;
using ActiveSync.Backends.Common.Converters;
using ActiveSync.Contracts;
using ActiveSync.Protocol.Wbxml;
using ActiveSync.Server.Eas.Content;

namespace ActiveSync.Server.Tests;

/// <summary>
///   The HOST half of the old notes converter: typed <see cref="NoteItem" /> ⇄ EAS Notes-class
///   ApplicationData, including the ghosting merge (an absent element keeps the stored value) and
///   the body truncation the client's BodyPreference asks for. The VJOURNAL storage half is the
///   local store's private convention and is asserted through that store instead.
/// </summary>
public class NotesXmlTests
{
	private static readonly XNamespace Notes = EasNamespaces.Notes;
	private static readonly XNamespace AirSyncBase = EasNamespaces.AirSyncBase;

	private static XElement AppData(string? subject, string? body, params string[] categories)
	{
		XElement data = new("ApplicationData");
		if (subject is not null)
			data.Add(new XElement(Notes + "Subject", subject));
		if (body is not null)
			data.Add(new XElement(AirSyncBase + "Body",
				new XElement(AirSyncBase + "Type", "1"),
				new XElement(AirSyncBase + "Data", body)));
		if (categories.Length > 0)
			data.Add(new XElement(Notes + "Categories",
				categories.Select(c => new XElement(Notes + "Category", c))));
		return data;
	}

	[Fact]
	public void RoundTrip_PreservesSubjectBodyAndCategories()
	{
		NoteItem note = NotesXml.FromApplicationData(
			AppData("Shopping list", "milk\nbread", "errands", "home"), null);

		List<XElement> data = NotesXml.ToApplicationData(note, new BodyPreference { Type = BodyType.PlainText });

		Assert.Equal("Shopping list", data.Single(e => e.Name == Notes + "Subject").Value);
		Assert.Equal("IPM.StickyNote", data.Single(e => e.Name == Notes + "MessageClass").Value);
		XElement body = data.Single(e => e.Name == AirSyncBase + "Body");
		Assert.Equal("milk\nbread", body.Element(AirSyncBase + "Data")?.Value);
		Assert.Equal("0", body.Element(AirSyncBase + "Truncated")?.Value);
		Assert.Equal(["errands", "home"], data.Single(e => e.Name == Notes + "Categories")
			.Elements(Notes + "Category").Select(c => c.Value).ToArray());
	}

	[Fact]
	public void PartialUpdate_KeepsTheStoredValuesOfAbsentElements()
	{
		// EAS ghosting: an absent element means "leave unchanged", never "clear".
		NoteItem stored = NotesXml.FromApplicationData(AppData("v1", "first", "errands"), null);

		NoteItem merged = NotesXml.FromApplicationData(AppData("v2", null), stored);

		Assert.Equal("v2", merged.Subject);
		Assert.Equal("first", merged.Body.Content);
		Assert.Equal(["errands"], merged.Categories);
	}

	[Fact]
	public void Body_IsTruncatedToTheClientsPreference()
	{
		NoteItem note = NotesXml.FromApplicationData(AppData("long", new string('x', 100)), null);

		List<XElement> data = NotesXml.ToApplicationData(
			note, new BodyPreference { Type = BodyType.PlainText, TruncationSize = 10 });

		XElement body = data.Single(e => e.Name == AirSyncBase + "Body");
		Assert.Equal(10, body.Element(AirSyncBase + "Data")?.Value.Length);
		Assert.Equal("1", body.Element(AirSyncBase + "Truncated")?.Value);
		Assert.Equal("100", body.Element(AirSyncBase + "EstimatedDataSize")?.Value);
	}
}

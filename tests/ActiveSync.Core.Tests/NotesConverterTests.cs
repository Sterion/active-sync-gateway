using System.Xml.Linq;
using ActiveSync.Backends.Converters;
using ActiveSync.Contracts;
using ActiveSync.Core.Backend;
using ActiveSync.Protocol.Wbxml;

namespace ActiveSync.Core.Tests;

public class NotesConverterTests
{
	private static readonly XNamespace Notes = EasNamespaces.Notes;
	private static readonly XNamespace AirSyncBase = EasNamespaces.AirSyncBase;

	private static XElement AppData(string subject, string body, params string[] categories)
	{
		XElement data = new("ApplicationData",
			new XElement(Notes + "Subject", subject),
			new XElement(AirSyncBase + "Body",
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
		string uid = Guid.NewGuid().ToString();
		string ics = NotesConverter.FromApplicationData(
			AppData("Shopping list", "milk\nbread", "errands", "home"), uid, null);

		Assert.Contains("VJOURNAL", ics);
		Assert.Equal(uid, NotesConverter.ExtractUid(ics));

		List<XElement>? data = NotesConverter.ToApplicationData(ics, new BodyPreference(1, null, false));
		Assert.NotNull(data);
		Assert.Equal("Shopping list", data.Single(e => e.Name == Notes + "Subject").Value);
		Assert.Equal("IPM.StickyNote", data.Single(e => e.Name == Notes + "MessageClass").Value);
		XElement body = data.Single(e => e.Name == AirSyncBase + "Body");
		Assert.Equal("milk\nbread", body.Element(AirSyncBase + "Data")?.Value);
		Assert.Equal("0", body.Element(AirSyncBase + "Truncated")?.Value);
		List<string> cats = data.Single(e => e.Name == Notes + "Categories")
			.Elements(Notes + "Category").Select(c => c.Value).ToList();
		Assert.Equal(["errands", "home"], cats);
	}

	[Fact]
	public void Update_PreservesUidAndReplacesContent()
	{
		string uid = Guid.NewGuid().ToString();
		string original = NotesConverter.FromApplicationData(AppData("v1", "first"), uid, null);
		string updated = NotesConverter.FromApplicationData(AppData("v2", "second"), uid, original);

		Assert.Equal(uid, NotesConverter.ExtractUid(updated));
		List<XElement>? data = NotesConverter.ToApplicationData(updated, new BodyPreference(1, null, false))!;
		Assert.Equal("v2", data.Single(e => e.Name == Notes + "Subject").Value);
		Assert.Equal("second",
			data.Single(e => e.Name == AirSyncBase + "Body").Element(AirSyncBase + "Data")?.Value);
	}

	[Fact]
	public void Body_IsTruncatedToPreference()
	{
		string body = new('x', 100);
		string ics = NotesConverter.FromApplicationData(AppData("long", body), "uid-1", null);

		List<XElement>? data = NotesConverter.ToApplicationData(ics, new BodyPreference(1, 10, false))!;
		XElement bodyElement = data.Single(e => e.Name == AirSyncBase + "Body");
		Assert.Equal(10, bodyElement.Element(AirSyncBase + "Data")?.Value?.Length);
		Assert.Equal("1", bodyElement.Element(AirSyncBase + "Truncated")?.Value);
		Assert.Equal("100", bodyElement.Element(AirSyncBase + "EstimatedDataSize")?.Value);
	}
}

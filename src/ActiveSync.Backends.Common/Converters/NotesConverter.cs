using System.Xml.Linq;
using ActiveSync.Core.Backend;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;

namespace ActiveSync.Backends.Converters;

/// <summary>iCalendar VJOURNAL ↔ EAS Notes-class ApplicationData (MS-ASNOTES).</summary>
public static class NotesConverter
{
	private static readonly XNamespace Notes = EasNamespaces.Notes;
	private static readonly XNamespace AirSyncBase = EasNamespaces.AirSyncBase;

	public static string? ExtractUid(string ics)
	{
		Calendar? calendar = Calendar.Load(ics);
		return calendar?.Journals.FirstOrDefault()?.Uid;
	}

	public static List<XElement>? ToApplicationData(string ics, BodyPreference bodyPreference)
	{
		Calendar? calendar = Calendar.Load(ics);
		Journal? journal = calendar?.Journals.FirstOrDefault();
		if (journal is null)
			return null;

		List<XElement> data = new()
		{
			new XElement(Notes + "Subject", journal.Summary ?? ""),
			new XElement(Notes + "MessageClass", "IPM.StickyNote"),
			new XElement(Notes + "LastModifiedDate",
				EasDateTime.ToLong(journal.LastModified?.AsUtc ?? DateTime.UtcNow))
		};

		(string sent, bool truncated, long estimated) =
			BodyText.ForBody(journal.Description ?? "", bodyPreference.TruncationSize);
		data.Add(AirSyncBodyWriter.Build(estimated, truncated, sent));

		if (journal.Categories is { Count: > 0 } categories)
			data.Add(new XElement(Notes + "Categories",
				categories.Select(c => new XElement(Notes + "Category", c))));

		return data;
	}

	public static string FromApplicationData(XElement applicationData, string uid, string? existingIcs)
	{
		Calendar calendar;
		Journal journal;
		if (existingIcs is not null)
		{
			calendar = IcalHelpers.Load(existingIcs);
			journal = calendar.Journals.FirstOrDefault() ?? AddNewJournal(calendar);
		}
		else
		{
			calendar = new Calendar { ProductId = "-//ActiveSync Gateway//EN" };
			journal = AddNewJournal(calendar);
		}

		journal.Uid = uid;
		if (applicationData.Element(Notes + "Subject")?.Value is { } subject)
			journal.Summary = subject;
		if (applicationData.Element(AirSyncBase + "Body")?.Element(AirSyncBase + "Data")?.Value is { } body)
			journal.Description = body;
		if (applicationData.Element(Notes + "Categories") is { } categories)
			journal.Categories = categories.Elements(Notes + "Category").Select(c => c.Value).ToList();

		journal.DtStart ??= new CalDateTime(DateTime.UtcNow, "UTC");
		journal.LastModified = new CalDateTime(DateTime.UtcNow, "UTC");
		return IcalHelpers.Serialize(calendar);
	}

	private static Journal AddNewJournal(Calendar calendar)
	{
		Journal journal = new();
		calendar.Journals.Add(journal);
		return journal;
	}
}

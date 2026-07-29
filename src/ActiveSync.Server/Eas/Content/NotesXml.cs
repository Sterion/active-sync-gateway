using System.Xml.Linq;
using ActiveSync.Contracts;
using ActiveSync.Eas.Conversion;
using ActiveSync.Protocol;
using ActiveSync.Protocol.Wbxml;

namespace ActiveSync.Server.Eas.Content;

/// <summary>
///   Typed <see cref="NoteItem" /> ⇄ EAS Notes-class ApplicationData (MS-ASNOTES) — the host's
///   half of what used to be the shared VJOURNAL notes converter (the storage half became the
///   local store's private mapper). Notes are the one class whose contract payload is typed, so
///   this is the one host conversion with no interchange format in the middle.
/// </summary>
internal static class NotesXml
{
	private static readonly XNamespace Notes = EasNamespaces.Notes;
	private static readonly XNamespace AirSyncBase = EasNamespaces.AirSyncBase;

	public static List<XElement> ToApplicationData(NoteItem note, BodyPreference bodyPreference)
	{
		List<XElement> data = new()
		{
			new XElement(Notes + "Subject", note.Subject),
			new XElement(Notes + "MessageClass", "IPM.StickyNote"),
			new XElement(Notes + "LastModifiedDate",
				EasDateTime.ToLong(note.LastModified?.UtcDateTime ?? DateTime.UtcNow))
		};

		(string sent, bool truncated, long estimated) =
			BodyText.ForBody(note.Body.Content, bodyPreference.TruncationSize);
		data.Add(AirSyncBodyWriter.Build(estimated, truncated, sent));

		if (note.Categories.Count > 0)
			data.Add(new XElement(Notes + "Categories",
				note.Categories.Select(c => new XElement(Notes + "Category", c))));

		return data;
	}

	/// <summary>
	///   The complete note for a client's (possibly partial) ApplicationData: absent elements
	///   keep the existing note's values — the ghosting merge, host-side, against the typed
	///   record the store handed over.
	/// </summary>
	public static NoteItem FromApplicationData(XElement applicationData, NoteItem? existing)
	{
		string subject = applicationData.Element(Notes + "Subject")?.Value
		                 ?? existing?.Subject ?? "";
		string? sentBody = applicationData.Element(AirSyncBase + "Body")?.Element(AirSyncBase + "Data")?.Value;
		TextBody body = sentBody is not null
			? new TextBody { Type = BodyType.PlainText, Content = sentBody }
			: existing?.Body ?? new TextBody { Type = BodyType.PlainText, Content = "" };
		IReadOnlyList<string> categories = applicationData.Element(Notes + "Categories") is { } sentCategories
			? sentCategories.Elements(Notes + "Category").Select(c => c.Value).ToList()
			: existing?.Categories ?? [];

		return new NoteItem
		{
			Subject = subject,
			Body = body,
			Categories = categories,
			LastModified = DateTimeOffset.UtcNow
		};
	}
}

using ActiveSync.Contracts;
using ActiveSync.Contracts.Interop;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;

namespace ActiveSync.Backends.Local;

/// <summary>
///   <see cref="NoteItem" /> ⇄ iCalendar VJOURNAL text — the local notes store's PRIVATE storage
///   convention, nothing more. There is no accepted notes interchange standard; VJOURNAL is kept
///   at rest purely so existing sealed <c>LocalItem</c> rows need no data migration, and no other
///   backend will ever see it (moved here from the shared converters for exactly that reason).
/// </summary>
internal static class NoteJournalMapper
{
	public static NoteItem? ToNote(string ics)
	{
		Calendar? calendar;
		try
		{
			calendar = Calendar.Load(ics);
		}
		catch (Exception)
		{
			return null; // unparsable stored row → skip the item, never fail the batch
		}

		Journal? journal = calendar?.Journals.FirstOrDefault();
		if (journal is null)
			return null;

		return new NoteItem
		{
			Subject = journal.Summary ?? "",
			// Stored notes are plain text (the EAS body the client sent, stored verbatim).
			Body = new TextBody { Type = BodyType.PlainText, Content = journal.Description ?? "" },
			Categories = journal.Categories is { Count: > 0 } categories ? categories.ToList() : [],
			LastModified = journal.LastModified?.AsUtc is { } modified
				? new DateTimeOffset(modified, TimeSpan.Zero)
				: null
		};
	}

	public static string ToJournal(NoteItem note, string? existingIcs, string uid)
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

		journal.Uid = existingIcs is not null && journal.Uid is { Length: > 0 } ? journal.Uid : uid;
		journal.Summary = note.Subject;
		journal.Description = note.Body.Content;
		journal.Categories = note.Categories.ToList();

		journal.DtStart ??= new CalDateTime(DateTime.UtcNow, "UTC");
		journal.LastModified = new CalDateTime(note.LastModified?.UtcDateTime ?? DateTime.UtcNow, "UTC");
		return IcalHelpers.Serialize(calendar);
	}

	public static string? ExtractUid(string ics)
	{
		try
		{
			return Calendar.Load(ics)?.Journals.FirstOrDefault()?.Uid;
		}
		catch (Exception)
		{
			return null;
		}
	}

	private static Journal AddNewJournal(Calendar calendar)
	{
		Journal journal = new();
		calendar.Journals.Add(journal);
		return journal;
	}
}

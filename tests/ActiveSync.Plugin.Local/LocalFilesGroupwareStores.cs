using ActiveSync.Contracts;

namespace ActiveSync.Plugin.Local;

/// <summary>Calendar events as <c>.ics</c> files under <c>&lt;root&gt;/calendar/&lt;folder&gt;/</c>.</summary>
internal sealed class LocalFilesCalendarStore(string root, LocalFilesOptions options, FileTreeWatcher watcher)
	: PayloadFileStore<CalendarItem>(root, options, watcher), ICalendarStore
{
	/// <summary>The folder-key prefix; the trailing colon is what keeps it disjoint from the other stores'.</summary>
	public const string Prefix = "lfs-calendar:";

	protected override string KeyPrefix => Prefix;
	protected override string Collection => "calendar";
	protected override string Extension => ".ics";
	protected override string DefaultFolderName => "Calendar";
	protected override FolderType DefaultFolderType => FolderType.Calendar;
	protected override FolderType AdditionalFolderType => FolderType.UserCalendar;

	// Identity in both directions: the iCalendar the host hands over IS the stored file, so
	// properties EAS cannot express survive an edit made on a phone.
	protected override CalendarItem? ParseContent(string content)
	{
		return string.IsNullOrWhiteSpace(content) ? null : new CalendarItem { ICalendar = content };
	}

	protected override string BuildContent(CalendarItem item, string? existingContent)
	{
		return item.ICalendar;
	}

	protected override string? ExtractUid(string content)
	{
		return IcalText.ExtractUid(content);
	}
}

/// <summary>Tasks as <c>.ics</c> files (VTODO) under <c>&lt;root&gt;/tasks/&lt;folder&gt;/</c>.</summary>
internal sealed class LocalFilesTaskStore(string root, LocalFilesOptions options, FileTreeWatcher watcher)
	: PayloadFileStore<TaskItem>(root, options, watcher), ITaskStore
{
	/// <summary>The folder-key prefix; disjoint from every other store's.</summary>
	public const string Prefix = "lfs-tasks:";

	protected override string KeyPrefix => Prefix;
	protected override string Collection => "tasks";
	protected override string Extension => ".ics";
	protected override string DefaultFolderName => "Tasks";
	protected override FolderType DefaultFolderType => FolderType.Tasks;
	protected override FolderType AdditionalFolderType => FolderType.UserTasks;

	protected override TaskItem? ParseContent(string content)
	{
		return string.IsNullOrWhiteSpace(content) ? null : new TaskItem { ICalendar = content };
	}

	protected override string BuildContent(TaskItem item, string? existingContent)
	{
		return item.ICalendar;
	}

	protected override string? ExtractUid(string content)
	{
		return IcalText.ExtractUid(content);
	}
}

/// <summary>Contacts as <c>.vcf</c> files under <c>&lt;root&gt;/contacts/&lt;folder&gt;/</c>.</summary>
internal sealed class LocalFilesContactStore(string root, LocalFilesOptions options, FileTreeWatcher watcher)
	: PayloadFileStore<ContactItem>(root, options, watcher), IContactStore
{
	/// <summary>The folder-key prefix; disjoint from every other store's.</summary>
	public const string Prefix = "lfs-contacts:";

	protected override string KeyPrefix => Prefix;
	protected override string Collection => "contacts";
	protected override string Extension => ".vcf";
	protected override string DefaultFolderName => "Contacts";
	protected override FolderType DefaultFolderType => FolderType.Contacts;
	protected override FolderType AdditionalFolderType => FolderType.UserContacts;

	protected override ContactItem? ParseContent(string content)
	{
		return string.IsNullOrWhiteSpace(content) ? null : new ContactItem { VCard = content };
	}

	protected override string BuildContent(ContactItem item, string? existingContent)
	{
		return item.VCard;
	}

	protected override string? ExtractUid(string content)
	{
		return IcalText.ExtractUid(content);
	}
}

/// <summary>Notes as <c>.json</c> files under <c>&lt;root&gt;/notes/&lt;folder&gt;/</c>.</summary>
internal sealed class LocalFilesNotesStore(string root, LocalFilesOptions options, FileTreeWatcher watcher)
	: PayloadFileStore<NoteItem>(root, options, watcher), INotesStore
{
	/// <summary>The folder-key prefix; disjoint from every other store's.</summary>
	public const string Prefix = "lfs-notes:";

	protected override string KeyPrefix => Prefix;
	protected override string Collection => "notes";
	protected override string Extension => ".json";
	protected override string DefaultFolderName => "Notes";
	protected override FolderType DefaultFolderType => FolderType.Notes;
	protected override FolderType AdditionalFolderType => FolderType.UserNotes;

	protected override NoteItem? ParseContent(string content)
	{
		return string.IsNullOrWhiteSpace(content) ? null : NoteJson.Read(content, "Note");
	}

	protected override string BuildContent(NoteItem item, string? existingContent)
	{
		return NoteJson.Write(item);
	}
}

/// <summary>
///   The two line-level readings this plugin needs from iCalendar/vCard text. It has no domain
///   library by design — the payload classes are stored verbatim — so this is deliberately a
///   line scan and nothing more.
/// </summary>
internal static class IcalText
{
	/// <summary>
	///   The document's UID, used only to give the file a recognisable name. Handles RFC 5545 line
	///   folding (a continuation line starts with a space or tab); returns <c>null</c> when there is
	///   none, in which case the caller mints a key instead.
	/// </summary>
	public static string? ExtractUid(string content)
	{
		string[] lines = content.Split('\n');
		for (int index = 0; index < lines.Length; index++)
		{
			string line = lines[index].TrimEnd('\r');
			if (!line.StartsWith("UID:", StringComparison.OrdinalIgnoreCase))
				continue;

			string value = line[4..];
			for (int next = index + 1; next < lines.Length; next++)
			{
				string continuation = lines[next].TrimEnd('\r');
				if (continuation.Length == 0 || continuation[0] is not (' ' or '\t'))
					break;
				value += continuation[1..];
			}

			return value.Trim();
		}

		return null;
	}
}

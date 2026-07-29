using System.Text.Json;
using System.Text.Json.Serialization;
using ActiveSync.Contracts;

namespace ActiveSync.Plugin.Local;

/// <summary>
///   The notes class's on-disk shape. Notes are the one class with no interchange format, so the
///   file layout is this store's PRIVATE convention — the same stance the in-repo local store takes
///   about its VJOURNAL mapping.
/// </summary>
/// <remarks>
///   <see cref="NoteItem" /> is deliberately NOT serialized directly: it is versioned contract, and
///   a future field added to it would silently change every file this store has ever written. The
///   DTO below is this plugin's own, and the mapping between the two is explicit.
/// </remarks>
internal static class NoteJson
{
	private static readonly JsonSerializerOptions Options = new()
	{
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase
	};

	/// <summary>Renders a note to the stored JSON.</summary>
	public static string Write(NoteItem note)
	{
		return JsonSerializer.Serialize(
			new NoteFile
			{
				Subject = note.Subject,
				Body = note.Body.Content,
				BodyType = note.Body.Type == BodyType.Html ? "html" : "text",
				Categories = note.Categories.Count == 0 ? null : [.. note.Categories],
				LastModified = note.LastModified
			},
			Options);
	}

	/// <summary>
	///   Reads a stored note. A file that is not this store's JSON is NOT discarded: it is surfaced
	///   as a note whose body is the raw text, because a store that skipped it would report it as a
	///   deletion to every device that already holds it — and because dropping a plain text file
	///   into the notes directory is a reasonable thing for someone to do.
	/// </summary>
	public static NoteItem Read(string content, string fallbackSubject)
	{
		NoteFile? file = null;
		try
		{
			file = JsonSerializer.Deserialize<NoteFile>(content, Options);
		}
		catch (JsonException)
		{
			// Not our shape — fall through to the raw-text reading below.
		}

		if (file is null || file.Subject is null && file.Body is null)
			return new NoteItem
			{
				Subject = fallbackSubject,
				Body = new TextBody { Type = BodyType.PlainText, Content = content }
			};

		return new NoteItem
		{
			Subject = file.Subject ?? fallbackSubject,
			Body = new TextBody
			{
				Type = string.Equals(file.BodyType, "html", StringComparison.OrdinalIgnoreCase)
					? BodyType.Html
					: BodyType.PlainText,
				Content = file.Body ?? ""
			},
			Categories = file.Categories ?? [],
			LastModified = file.LastModified
		};
	}

	/// <summary>This plugin's own note file shape — never the contract record.</summary>
	private sealed class NoteFile
	{
		public string? Subject { get; init; }
		public string? Body { get; init; }
		public string? BodyType { get; init; }
		public string[]? Categories { get; init; }
		public DateTimeOffset? LastModified { get; init; }
	}
}

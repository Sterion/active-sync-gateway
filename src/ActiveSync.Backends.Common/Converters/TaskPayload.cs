using Ical.Net;

namespace ActiveSync.Backends.Common.Converters;

/// <summary>
///   What a task STORE needs from a VTODO payload it already owns: the document's own UID, so
///   the stored resource can be named after it. The VTODO ⇄ ApplicationData conversion
///   (MS-ASTASK) is host-side, in ActiveSync.Eas.Conversion.
/// </summary>
public static class TaskPayload
{
	/// <summary>The first VTODO's UID, used to name the stored resource after its own document.</summary>
	public static string? ExtractUid(string ics)
	{
		return Calendar.Load(ics)?.Todos.FirstOrDefault()?.Uid;
	}
}

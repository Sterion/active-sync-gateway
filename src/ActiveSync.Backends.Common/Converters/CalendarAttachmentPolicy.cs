namespace ActiveSync.Backends.Converters;

/// <summary>
///   Dav:CalendarAttachments mode → per-attachment byte cap. Attachments are stored inline
///   in the event (base64 ATTACH), so the cap protects the DAV server from bloated items:
///   Auto = 1 MiB, On = 16 MiB, Off = feature disabled (null).
/// </summary>
public static class CalendarAttachmentPolicy
{
	public static long? CapBytes(string? mode)
	{
		return mode?.ToLowerInvariant() switch
		{
			"off" => null,
			"on" => 16L * 1024 * 1024,
			_ => 1024L * 1024 // Auto, and the local calendar store's default
		};
	}
}

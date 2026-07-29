namespace ActiveSync.Eas.Conversion;

/// <summary>
///   The <c>ActiveSync:Eas:CalendarAttachments</c> mode → per-attachment byte cap. Attachments
///   are stored inline in the event (base64 ATTACH), so the cap keeps a bloated item off the
///   calendar backend: Auto = 1 MiB, On = 16 MiB, Off = feature disabled (null). It sits with
///   the converters because it gates what the merge writes into the iCalendar.
/// </summary>
public static class CalendarAttachmentPolicy
{
	/// <summary>The per-attachment cap in bytes; null means attachments are refused.</summary>
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

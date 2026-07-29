using ActiveSync.Contracts;

namespace ActiveSync.Backends.Common.Converters;

/// <summary>
///   The client's body preference plus the negotiated-protocol flag the EAS converters need:
///   <see cref="Eas16" /> selects the 16.x shapes (airsyncbase:Location instead of
///   calendar:Location, draft/attachment metadata). HOST-side since the typed item currency:
///   stores hand over full payloads and know nothing of body preferences — truncation and
///   16.x shaping happen in the EAS conversion, which is where this record lives (it moves to
///   the conversion assembly with the converters in Phase 4).
/// </summary>
public sealed record BodyPreference
{
	/// <summary>The body shape the client asked for.</summary>
	public required BodyType Type { get; init; }

	/// <summary>Truncate the body at this many bytes; <c>null</c> means "no truncation".</summary>
	public long? TruncationSize { get; init; }

	/// <summary>Whether the client asked for the whole body or none of it (AirSyncBase AllOrNone).</summary>
	public bool AllOrNone { get; init; }

	/// <summary>Whether the negotiated protocol is 16.x, selecting the 16.x element shapes.</summary>
	public bool Eas16 { get; init; }

	/// <summary>Convenience default: plain text, truncated at 32 KB, AllOrNone false, pre-16.x shapes.</summary>
	public static readonly BodyPreference PlainText =
		new() { Type = BodyType.PlainText, TruncationSize = 32 * 1024 };
}

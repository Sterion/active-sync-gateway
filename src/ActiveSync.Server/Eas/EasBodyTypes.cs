using ActiveSync.Contracts;

namespace ActiveSync.Server.Eas;

/// <summary>
///   Maps the AirSyncBase BodyPreference Type WIRE value onto the contract's
///   <see cref="BodyType" />. The wire numbering is the host's business — a store is handed the
///   enum — and a client-supplied integer is never cast blindly onto it: an unrecognized value
///   degrades to plain text, which is what the converters' own default branch has always done
///   with one.
/// </summary>
internal static class EasBodyTypes
{
	/// <summary>Maps a client-supplied AirSyncBase Type value; anything unrecognized becomes plain text.</summary>
	public static BodyType FromWire(int type)
	{
		return type switch
		{
			(int)BodyType.PlainText => BodyType.PlainText,
			(int)BodyType.Html => BodyType.Html,
			(int)BodyType.Rtf => BodyType.Rtf,
			(int)BodyType.Mime => BodyType.Mime,
			_ => BodyType.PlainText
		};
	}
}

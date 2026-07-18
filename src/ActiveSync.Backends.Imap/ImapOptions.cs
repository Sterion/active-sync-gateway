using ActiveSync.Backends.Common;

namespace ActiveSync.Backends.Imap;

/// <summary>
///   Settings for the "imap" provider (MailStore role). Connection/TLS knobs come from
///   <see cref="MailConnectionOptions" />; only IMAP-specific settings live here.
/// </summary>
public sealed class ImapOptions : MailConnectionOptions
{
	public ImapOptions()
	{
		Port = 993;
	}

	/// <summary>IMAP folder path separator override; autodetected when null.</summary>
	public char? PathSeparator { get; set; }
}

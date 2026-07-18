using ActiveSync.Backends.Common;

namespace ActiveSync.Backends.Smtp;

/// <summary>
///   Settings for the "smtp" provider (MailSubmit role). Connection/TLS knobs come from
///   <see cref="MailConnectionOptions" />; only SMTP-specific settings live here.
/// </summary>
public sealed class SmtpOptions : MailConnectionOptions
{
	public SmtpOptions()
	{
		Port = 465;
	}

	/// <summary>
	///   Rewrite the From header of outgoing mail to the authenticated user before submission
	///   (the display name from the client is kept). Off by default: most SMTP servers already
	///   enforce sender alignment for authenticated submissions; enable this when yours does not.
	///   Only applies when the login name is a mail address.
	/// </summary>
	public bool ForceFrom { get; set; }
}

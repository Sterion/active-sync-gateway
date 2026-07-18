namespace ActiveSync.Core.Options;

/// <summary>
///   Per-user IMAP override. Every field is nullable on purpose: null means "inherit the
///   global section" — with non-nullable fields the config binder could not distinguish an
///   omitted value from a default (e.g. Port 993).
/// </summary>
public sealed class ImapAccountOptions
{
	/// <summary>IMAP login; defaults to the account's MailAddress, else the gateway login.</summary>
	public string? UserName { get; set; }

	/// <summary>Plaintext or "enc:v1:..." sealed value. Required in Accounts mode.</summary>
	public string? Password { get; set; }

	public string? Host { get; set; }
	public int? Port { get; set; }
	public bool? UseSsl { get; set; }
	public string? Security { get; set; }
	public bool? AllowInvalidCertificates { get; set; }
	public string? CaCertificatePath { get; set; }
	public char? PathSeparator { get; set; }
}

/// <summary>Per-user SMTP override; unset fields inherit the global section.</summary>
public sealed class SmtpAccountOptions
{
	/// <summary>SMTP login; defaults to the account's effective IMAP user name.</summary>
	public string? UserName { get; set; }

	/// <summary>Plaintext or "enc:v1:..."; defaults to the effective IMAP password.</summary>
	public string? Password { get; set; }

	public string? Host { get; set; }
	public int? Port { get; set; }
	public bool? UseSsl { get; set; }
	public string? Security { get; set; }
	public bool? AllowInvalidCertificates { get; set; }
	public string? CaCertificatePath { get; set; }
	public bool? ForceFrom { get; set; }
}

/// <summary>Per-user CalDAV/CardDAV override; unset fields inherit the global section.</summary>
public sealed class DavAccountOptions
{
	/// <summary>false = disable this DAV backend for this user even when configured globally.</summary>
	public bool? Enabled { get; set; }

	/// <summary>DAV login; defaults to the account's effective IMAP user name.</summary>
	public string? UserName { get; set; }

	/// <summary>Plaintext or "enc:v1:..."; defaults to the effective IMAP password.</summary>
	public string? Password { get; set; }

	public string? BaseUrl { get; set; }
	public string? HomeSetPath { get; set; }
	public string? TaskFolder { get; set; }
	public bool? AllowInvalidCertificates { get; set; }
	public string? CaCertificatePath { get; set; }

	/// <inheritdoc cref="DavServerOptions.CalendarAttachments" />
	public string? CalendarAttachments { get; set; }

	/// <summary>
	///   <inheritdoc cref="DavServerOptions.SharedCollections" /> null = inherit the global
	///   list; a non-null list REPLACES it (consistent with every other override).
	/// </summary>
	public List<string>? SharedCollections { get; set; }

	/// <inheritdoc cref="DavServerOptions.SendInvitations" />
	public string? SendInvitations { get; set; }
}

/// <summary>Per-user ManageSieve override; unset fields inherit the global section.</summary>
public sealed class SieveAccountOptions
{
	/// <summary>false = disable Sieve/Oof for this user even when enabled globally.</summary>
	public bool? Enabled { get; set; }

	/// <summary>Sieve login; defaults to the account's effective IMAP user name.</summary>
	public string? UserName { get; set; }

	/// <summary>Plaintext or "enc:v1:..."; defaults to the effective IMAP password.</summary>
	public string? Password { get; set; }

	public string? Host { get; set; }
	public int? Port { get; set; }
	public bool? UseTls { get; set; }
	public bool? AllowInvalidCertificates { get; set; }
	public string? CaCertificatePath { get; set; }
}

/// <summary>
///   Optional per-user overrides, keyed by the login the phone sends. Everything is
///   optional — an empty entry changes nothing (it only matters as an allowlist grant
///   when <see cref="ActiveSyncOptions.RequireDeclaredUsers" /> is set). Undeclared
///   logins are pure pass-through.
/// </summary>
public sealed class AccountOptions
{
	/// <summary>
	///   Optional gateway password override — decouples the phone's password from IMAP:
	///   a "pbkdf2$..." hash (hash-password verb; preferred) or plaintext (startup warning).
	///   When unset, the phone's password is validated against Imap:Password if configured,
	///   else by an IMAP login probe.
	/// </summary>
	public string? Password { get; set; }

	/// <summary>
	///   The user's mail address, used for From rewriting, Settings and meeting replies.
	///   When null, the gateway login is used if it contains '@'.
	/// </summary>
	public string? MailAddress { get; set; }

	public ImapAccountOptions? Imap { get; set; }
	public SmtpAccountOptions? Smtp { get; set; }
	public DavAccountOptions? CalDav { get; set; }
	public DavAccountOptions? CardDav { get; set; }
	public SieveAccountOptions? Sieve { get; set; }
}

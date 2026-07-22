namespace ActiveSync.Core.Options;

/// <summary>
///   Per-user override of one backend role. The host owns Enabled/Provider/UserName/Password;
///   everything else lives in <see cref="Settings" /> as flat configuration keys the role's
///   provider binds itself ("Host", "Port", "SharedCollections:0", ...) — provider-agnostic,
///   JSON-serializable for database account rows, and open to plugin providers.
/// </summary>
public sealed class BackendRoleOverride
{
	/// <summary>
	///   false = turn this role off for the user: content roles fall back to the "local"
	///   provider, Oof is disabled. Not valid on MailStore/MailSubmit.
	/// </summary>
	public bool? Enabled { get; set; }

	/// <summary>Serve this role with a different provider than the global assignment.</summary>
	public string? Provider { get; set; }

	/// <summary>Backend login; defaults to the effective MailStore user name (gateway login for MailStore).</summary>
	public string? UserName { get; set; }

	/// <summary>
	///   Backend password, plaintext or "enc:v1:..." sealed; defaults to the effective
	///   MailStore password (the presented EAS password for MailStore itself).
	/// </summary>
	public string? Password { get; set; }

	/// <summary>
	///   Flat configuration keys overlaid on the global role section — but ONLY when the
	///   effective provider matches the global assignment (a switched provider starts from
	///   these settings alone). Setting any list element ("X:0") REPLACES the whole global
	///   list "X". A null value CLEARS the inherited global key it addresses: the global
	///   subtree that key names is removed and nothing is written back, so the effective
	///   setting falls to the provider option-class default (matching the CLI/web field
	///   paths, where null = clear — see AccountFieldPaths). Other inherited keys are untouched.
	/// </summary>
	public Dictionary<string, string?>? Settings { get; set; }
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
	///   Optional gateway password override — decouples the phone's password from the mail
	///   backend: a "pbkdf2$..." hash (hash-password verb; preferred) or plaintext (startup
	///   warning). When unset, the phone's password is validated against the MailStore
	///   role's Password override if configured, else by the MailStore provider's probe.
	/// </summary>
	public string? Password { get; set; }

	/// <summary>
	///   The user's mail address, used for From rewriting, Settings and meeting replies.
	///   When null, the gateway login is used if it contains '@'.
	/// </summary>
	public string? MailAddress { get; set; }

	/// <summary>
	///   Grants access to the web admin interface (/admin). Irrelevant to EAS itself; under
	///   OIDC an admin claim can grant the same access without this flag.
	/// </summary>
	public bool? Admin { get; set; }

	/// <summary>
	///   Account master switch. <c>false</c> = DISABLED: every login for this account — all
	///   devices, EAS and web — is refused with 403 after valid credentials, exactly as a
	///   user-level block would, until it is re-enabled. <c>null</c>/<c>true</c> = enabled (the
	///   default). This is a persistent property of the account, distinct from an ad-hoc
	///   <c>eas block</c> (which stays for temporary or device-scoped refusals).
	/// </summary>
	public bool? Enabled { get; set; }

	/// <summary>
	///   True on a row the gateway created itself when a pass-through login first cleared its
	///   MailStore probe (<see cref="ActiveSyncOptions.AutoProvisionUsers" />). It is a pure
	///   provenance marker — the entry behaves exactly like a hand-added empty one (no gateway
	///   password, so auth still probes the backend) — surfaced by `eas users`/the admin UI
	///   so an operator can tell auto-created rows from ones they declared. Only ever set on
	///   database rows; config entries never carry it.
	/// </summary>
	public bool? AutoProvisioned { get; set; }

	/// <summary>
	///   The identity-provider subject (<c>sub</c>) this account is bound to. OIDC sign-in
	///   refuses a ticket whose subject differs, so an account cannot be taken over by anyone
	///   who manages to claim its login name — the login claim (<c>preferred_username</c> by
	///   default) is user-mutable at several common identity providers. Recorded automatically
	///   on the first OIDC sign-in of a DATABASE account (trust on first use); on a
	///   config-declared account it only binds when the operator writes it, because the gateway
	///   must not create a database row that would shadow the configuration entry.
	/// </summary>
	public string? OidcSubject { get; set; }

	/// <summary>Per-role overrides, keyed by role name (MailStore, Calendar, Oof, ...).</summary>
	public Dictionary<string, BackendRoleOverride>? Backends { get; set; }
}

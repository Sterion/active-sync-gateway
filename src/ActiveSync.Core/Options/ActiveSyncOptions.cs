using ActiveSync.Crypto;

namespace ActiveSync.Core.Options;

public sealed class DatabaseOptions
{
	/// <summary>
	///   Sqlite | Postgres. A <c>postgresql://</c> URI in <see cref="ConnectionString" />
	///   implies Postgres, so the provider can be left at its default in that case.
	/// </summary>
	public string Provider { get; set; } = "Sqlite";

	/// <summary>
	///   Provider connection string. For Postgres either the Npgsql keyword form
	///   ("Host=…;Database=…;Username=…;Password=…") or a libpq-style URI
	///   ("postgresql://user:password@host:5432/database") — the latter is what a
	///   CloudNativePG app secret's uri/fqdn-uri keys contain, and works verbatim.
	/// </summary>
	public string ConnectionString { get; set; } = "Data Source=activesync.db";
}

public sealed class EasOptions
{
	/// <summary>Maximum Ping/Sync heartbeat in seconds (EAS allows up to 3540).</summary>
	public int MaxHeartbeatSeconds { get; set; } = 1770;

	public int MinHeartbeatSeconds { get; set; } = 60;

	/// <summary>Interval for polling DAV collections during Ping.</summary>
	public int DavPollSeconds { get; set; } = 60;

	public int MaxWindowSize { get; set; } = 512;
	public int DefaultWindowSize { get; set; } = 100;

	/// <summary>Idle backend sessions are dropped after this many minutes.</summary>
	public int SessionIdleMinutes { get; set; } = 15;

	/// <summary>
	///   Days a folder that vanished from the backend is kept soft-deleted before a background
	///   sweep reclaims its row and dependent DAV/collection state. A folder that reappears within
	///   the window keeps its ServerId; past it, reappearance is a fresh folder. 0 disables the
	///   sweep (rows are kept forever).
	/// </summary>
	public int FolderRetentionDays { get; set; } = 30;

	/// <summary>
	///   Use a dedicated IMAP IDLE connection for the priority mail folder during Ping/Sync
	///   waits (sub-30 s push). Falls back to STATUS polling when disabled or unsupported.
	/// </summary>
	public bool UseImapIdle { get; set; } = true;

	/// <summary>
	///   Interval of the exact pending-change re-check that runs alongside the IDLE/STATUS
	///   watchers during Ping/Sync waits. IDLE notifications are best-effort in practice
	///   (some servers never broadcast flag changes), so the watchdog diffs the backend's
	///   revision map against the device's synced snapshot to catch anything the watchers
	///   miss. 0 disables the periodic ticks (the check at watch entry always runs).
	/// </summary>
	public int WatchdogSeconds { get; set; } = 60;
}

public sealed class AuthOptions
{
	/// <summary>
	///   Failed authentication attempts allowed per client address within
	///   <see cref="FailureWindowSeconds" /> before further requests are answered
	///   with 429 (without contacting the mail backend). 0 disables the throttle.
	/// </summary>
	public int MaxFailures { get; set; } = 20;

	/// <summary>Length of the failure-counting window, in seconds.</summary>
	public int FailureWindowSeconds { get; set; } = 300;

	/// <summary>
	///   Addresses (or CIDR ranges, e.g. <c>10.0.0.0/8</c>) the gateway accepts
	///   <c>X-Forwarded-For</c> from. Empty — the default — means the throttle keys on the
	///   address the socket actually came from, which is correct when phones reach the
	///   gateway directly and WRONG behind an ingress: every request then shares one key and
	///   one user's fumbled password 429s everybody. List the ingress here to fix that.
	///   Only a request that ARRIVED from a listed address may claim a different client
	///   address; from anyone else the header is ignored, so a direct client cannot mint a
	///   fresh throttle key per attempt.
	/// </summary>
	public List<string> TrustedProxies { get; set; } = [];

	/// <summary>
	///   How long a rejected (user, password) pair is remembered so repeats are refused
	///   without an IMAP login round-trip. 0 disables the negative cache.
	/// </summary>
	public int NegativeCacheSeconds { get; set; } = 15;

	/// <summary>
	///   How long a successful (user, password) pair is trusted without re-verifying against
	///   IMAP. A password revoked on the backend keeps working for at most this long.
	///   0 disables the cache (every request logs in to IMAP).
	/// </summary>
	public int SuccessCacheMinutes { get; set; } = 5;

	/// <summary>
	///   How often (at most) the gateway checks the database accounts change-stamp so
	///   `eas user ...` edits apply to running instances. One primary-key point-read per
	///   interval, performed lazily on the next request. 0 checks on every request
	///   (test-friendly); negative disables (database accounts load once at startup).
	/// </summary>
	public double UsersRefreshSeconds { get; set; } = 1;
}

// EncryptionOptions lives in the ActiveSync.Crypto assembly (same ActiveSync.Core.Options
// namespace) so the slim eas client can bind and derive the master key without referencing Core.

public sealed class LogOptions
{
	/// <summary>
	///   Console line shape: "Simple" (time + 3-letter level, the pre-1.0.7 look),
	///   "Standard" (date, full level name, logger category — pipe-delimited) or
	///   "Extended" (Standard plus every structured property, thread id and machine name).
	///   In Json format, Simple and Standard are identical (CLEF always carries them all).
	/// </summary>
	public string Mode { get; set; } = "Standard";

	/// <summary>
	///   "Text" (human-readable) or "Json" (CLEF — one JSON object per event, the right
	///   choice for Loki/Elastic; multi-line payload dumps become single events).
	/// </summary>
	public string Format { get; set; } = "Text";

	/// <summary>
	///   Also persist logs to the state database (a rolling buffer for `eas logs` and a future
	///   admin UI). Live-toggleable. Trace/Debug are never persisted regardless — only the
	///   rendered event at <see cref="DbMinimumLevel" /> or above.
	/// </summary>
	public bool Database { get; set; } = true;

	/// <summary>Minimum level persisted to the database: Information, Warning, Error or Fatal.</summary>
	public string DbMinimumLevel { get; set; } = "Information";

	/// <summary>Days of database log history to keep (a background sweep deletes older; 0 disables the sweep).</summary>
	public int RetentionDays { get; set; } = 7;
}

/// <summary>
///   The gateway's own HTTPS listener. When <see cref="Enabled" /> the gateway serves HTTPS on
///   <see cref="Port" /> using either an operator-supplied certificate
///   (<see cref="CertificatePath" /> — a mounted PEM or PFX, e.g. from cert-manager/ACME) or,
///   when none is set, a self-signed certificate generated on first serve and persisted in the
///   state database (sealed with the Encryption master key), so it stays identical across
///   restarts and replicas. Everything here is <b>restart-tier</b>: Kestrel binds the listener
///   and reads the certificate once at startup — a mounted certificate that rotates (or a path
///   change) takes effect on the next restart, which matches how Kubernetes mounts behave.
/// </summary>
public sealed class TlsOptions
{
	/// <summary>Serve the gateway's own HTTPS listener. Off = terminate TLS in front of the gateway.</summary>
	public bool Enabled { get; set; } = true;

	/// <summary>Listen port of the HTTPS endpoint.</summary>
	public int Port { get; set; } = 5443;

	/// <summary>
	///   Path to an operator-supplied certificate to serve instead of the built-in self-signed
	///   one: a PEM certificate (full chain; pair it with <see cref="CertificateKeyPath" />) or a
	///   PKCS#12/PFX bundle (leave the key path unset; use <see cref="CertificatePassword" /> if
	///   protected). Unset = the self-signed certificate from the database.
	/// </summary>
	public string? CertificatePath { get; set; }

	/// <summary>
	///   Path to the PEM private-key file that pairs with a PEM <see cref="CertificatePath" />.
	///   Leave unset when <see cref="CertificatePath" /> is a PFX bundle.
	/// </summary>
	public string? CertificateKeyPath { get; set; }

	/// <summary>
	///   Password for the PFX bundle, or for an encrypted PEM private key. Optional. Stored sealed
	///   (enc:v1:) with the Encryption master key and unsealed only when the certificate is loaded.
	/// </summary>
	public string? CertificatePassword { get; set; }
}

/// <summary>
///   Device security policy (MS-ASPROV) handed out via Provision and enforced on every
///   command once enabled. Property names match the EASProvisionDoc element names 1:1 so
///   the MS-ASPROV documentation applies directly. Nullable values are omitted from the
///   policy document (device default applies). There is deliberately NO remote-wipe knob.
/// </summary>
public sealed class PolicyOptions
{
	/// <summary>
	///   Master switch. Off (default): Provision hands out an empty policy and nothing is
	///   enforced — the pre-policy behavior. On: the document below is issued and every
	///   command (except Provision itself) requires a device holding the current policy
	///   key, re-provisioning automatically after any policy change (HTTP 449).
	/// </summary>
	public bool Enabled { get; set; }

	/// <summary>Require a device lock PIN/password.</summary>
	public bool DevicePasswordEnabled { get; set; }

	/// <summary>Require letters AND digits/symbols in the device password.</summary>
	public bool AlphanumericDevicePasswordRequired { get; set; }

	/// <summary>Allow PINs like "1111" or "1234". Only meaningful with a required password.</summary>
	public bool AllowSimpleDevicePassword { get; set; } = true;

	/// <summary>Minimum device password length (1-16).</summary>
	public int? MinDevicePasswordLength { get; set; }

	/// <summary>Minimum character classes (upper/lower/digit/symbol) in the password (1-4).</summary>
	public int? MinDevicePasswordComplexCharacters { get; set; }

	/// <summary>Seconds of inactivity before the device must lock (1-9999).</summary>
	public int? MaxInactivityTimeDeviceLock { get; set; }

	/// <summary>Wrong-password attempts before the device wipes itself locally (4-16).</summary>
	public int? MaxDevicePasswordFailedAttempts { get; set; }

	/// <summary>Days before the device password must be changed (0 = never expires).</summary>
	public int? DevicePasswordExpiration { get; set; }

	/// <summary>Number of previous passwords the device must refuse to reuse.</summary>
	public int? DevicePasswordHistory { get; set; }

	/// <summary>Require device storage encryption.</summary>
	public bool RequireDeviceEncryption { get; set; }

	/// <summary>Maximum attachment size in bytes the client may download.</summary>
	public int? MaxAttachmentSize { get; set; }

	/// <summary>
	///   Let the device escrow its recovery password with the gateway (readable via
	///   'eas device password'). Stored sealed with the Encryption master key.
	/// </summary>
	public bool PasswordRecoveryEnabled { get; set; }
}

/// <summary>
///   Prometheus metrics (OpenTelemetry exporter). Off by default; when <see cref="Port" />
///   is set, /metrics answers ONLY on that extra listener (scrape it inside the cluster),
///   otherwise it shares the main listeners — protect it via ingress/network policy then.
/// </summary>
public sealed class MetricsOptions
{
	public bool Enabled { get; set; }

	/// <summary>Dedicated plain-HTTP port for /metrics; unset = /metrics on the main listeners.</summary>
	public int? Port { get; set; }

	/// <summary>
	///   Per-account labels (user=...) on requests, item counts, mail and session gauges.
	///   Each active account adds label values — disable on large multi-tenant fleets where
	///   the cardinality would hurt, collapsing the label to "-".
	/// </summary>
	public bool PerUser { get; set; } = true;
}

/// <summary>Web admin interface (/admin). Off by default; the enable flag applies live.</summary>
public sealed class WebUiAdminOptions
{
	public bool Enabled { get; set; }
}

/// <summary>User self-service portal (/user). Off by default; the enable flag applies live.</summary>
public sealed class WebUiUserPortalOptions
{
	public bool Enabled { get; set; }
}

/// <summary>
///   OIDC login for the web interfaces. When <see cref="Authority" /> is set, ALL web logins
///   go through the identity provider and the local login form is disabled (the local
///   password is really the ActiveSync connect password). The mapped claim must match a
///   declared account login unless <see cref="AutoProvision" /> creates one. Authority,
///   client and scope changes need a restart (the OIDC handler registers at startup); the
///   admin-claim and AutoProvision knobs apply live (evaluated per login).
/// </summary>
public sealed class WebUiOidcOptions
{
	/// <summary>
	///   Master switch for OIDC. When false, the identity-provider settings are kept but ignored:
	///   web login falls back to local passwords, so OIDC can be turned off without deleting its
	///   configuration. Restart-tier (the handler is wired at startup).
	/// </summary>
	public bool Enabled { get; set; } = true;

	/// <summary>Issuer URL of the identity provider (e.g. https://id.example.com/realms/main).</summary>
	public string? Authority { get; set; }

	public string? ClientId { get; set; }

	/// <summary>Client secret, plaintext or "enc:v1:..." sealed with the encryption master key.</summary>
	public string? ClientSecret { get; set; }

	/// <summary>Space-separated scopes requested from the identity provider.</summary>
	public string Scopes { get; set; } = "openid profile email";

	/// <summary>Token claim mapped to the gateway login.</summary>
	public string LoginClaim { get; set; } = "preferred_username";

	/// <summary>
	///   Token claim granting web ADMIN access as an alternative to the account Admin flag
	///   (e.g. "roles"). Unset: only the account flag grants admin. Setting it REQUIRES
	///   <see cref="AdminClaimValue" /> — startup validation refuses the pair without it.
	/// </summary>
	public string? AdminClaim { get; set; }

	/// <summary>
	///   The value of <see cref="AdminClaim" /> that grants admin; required whenever the claim
	///   is set. "*" accepts any value — deliberately explicit, because reaching "any value" by
	///   omitting this field granted gateway admin to every user carrying the claim.
	/// </summary>
	public string? AdminClaimValue { get; set; }

	/// <summary>
	///   Create a database account entry for unknown OIDC logins on first sign-in, so they can
	///   use the portal. Admin access for such accounts comes only from the admin claim until
	///   an admin grants the flag.
	/// </summary>
	public bool AutoProvision { get; set; }

	/// <summary>Require HTTPS for the OIDC discovery endpoint. Disable only for local dev IdPs.</summary>
	public bool RequireHttpsMetadata { get; set; } = true;
}

/// <summary>The web interfaces (admin + user portal), served by the same listeners.</summary>
public sealed class WebUiOptions
{
	public WebUiAdminOptions Admin { get; set; } = new();
	public WebUiUserPortalOptions UserPortal { get; set; } = new();
	public WebUiOidcOptions? Oidc { get; set; }

	/// <summary>
	///   Emit the session cookie (and the OIDC correlation/nonce cookies) without the Secure
	///   attribute when the request itself resolves to http. OFF by default: the gateway cannot
	///   tell whether a plain-http request arrived over a TLS-terminating proxy — a proxy that
	///   forwards neither <see cref="ActiveSyncOptions.PublicUrl" /> nor X-Forwarded-Proto would
	///   otherwise leave the admin cookie transmittable in cleartext and strippable. Turn this on
	///   only to run the web interfaces over plain http locally; it is logged as a warning at
	///   startup. Restart tier (the cookie handler is wired at DI time).
	/// </summary>
	public bool AllowInsecureCookies { get; set; }
}

/// <summary>
///   The loopback CLI-forwarding endpoint (<c>POST /cli</c>). The slim <c>eas</c> client posts a
///   command line here so it runs against the already-warm gateway instead of paying a cold
///   process start. Reachable only from loopback connections, and (unless
///   <c>Encryption:AllowPlaintext</c> is explicitly set) only by a caller that can seal a request
///   with the master key; set <see cref="Enabled" /> false to remove the endpoint entirely.
/// </summary>
public sealed class CliOptions
{
	public bool Enabled { get; set; } = true;
}

/// <summary>
///   Host options. Backend endpoints are NOT bound here — the ActiveSync:Backends section
///   is role-keyed raw configuration each role's provider binds itself (see
///   <see cref="Accounts.BackendRolesConfig" />); the option classes above are what the
///   in-repo providers bind their sections onto.
/// </summary>
public sealed class ActiveSyncOptions
{
	public DatabaseOptions Database { get; set; } = new();
	public EasOptions Eas { get; set; } = new();
	public AuthOptions Auth { get; set; } = new();
	public EncryptionOptions Encryption { get; set; } = new();
	public TlsOptions Tls { get; set; } = new();
	public LogOptions Log { get; set; } = new();
	public PolicyOptions Policy { get; set; } = new();
	public MetricsOptions Metrics { get; set; } = new();
	public WebUiOptions WebUi { get; set; } = new();
	public CliOptions Cli { get; set; } = new();

	/// <summary>
	///   Public base URL of this gateway (e.g. https://eas.example.com) advertised by
	///   Autodiscover. When unset, the URL is derived from the request's Host header and
	///   X-Forwarded-Proto/-Host — set this when running behind a reverse proxy so the
	///   advertised URL never depends on client-supplied headers.
	/// </summary>
	public string? PublicUrl { get; set; }

	/// <summary>
	///   When true, the gateway is a pure mirror: every client write (deletes, flag changes,
	///   item edits/creates, moves, sends) is suppressed. Item-level writes are silently
	///   reverted via the sync snapshot; folder operations and sends return error statuses.
	/// </summary>
	public bool ReadOnly { get; set; }

	/// <summary>
	///   Optional per-user overrides keyed by gateway login; undeclared logins are pure
	///   pass-through (EAS credentials forwarded to all backends). Note that configuration
	///   keys are case-insensitive — two entries differing only by case merge silently.
	/// </summary>
	public Dictionary<string, AccountOptions>? Users { get; set; }

	/// <summary>
	///   Allowlist switch: when true, only logins declared in <see cref="Users" /> may
	///   authenticate — anyone else gets 401 without a backend probe. An empty entry is a
	///   valid grant (nothing overridden, access allowed).
	/// </summary>
	public bool RequireDeclaredUsers { get; set; }

	/// <summary>
	///   Create a database account row for a pass-through login the first time it clears its
	///   MailStore probe over EAS, so the user becomes visible and manageable — listed in
	///   `eas users`/the admin UI, blockable, and able to sign in to the self-service portal
	///   (validated against the same backend). The row carries no gateway password, so auth is
	///   unchanged (still a backend probe). ON by default so every user that actually syncs shows
	///   up as a first-class account instead of appearing only as device/session state that leads
	///   nowhere — set false to keep pass-through logins ephemeral (nothing is persisted).
	///   Naturally inert under <see cref="RequireDeclaredUsers" /> — that rejects undeclared
	///   logins before they authenticate, so nothing is ever provisioned. Deleting an auto-created
	///   row is not permanent while this stays on: the user's next sync re-creates it (block them,
	///   or turn this off, to make removal stick).
	/// </summary>
	public bool AutoProvisionUsers { get; set; } = true;

	/// <summary>
	///   Optional path to a JSON file (mounted secret/configmap) merged into configuration at
	///   startup. Full configuration shape: { "ActiveSync": { "Users": { ... } } }. Changes
	///   require a restart (the account snapshot is built once at startup).
	/// </summary>
	public string? UsersFile { get; set; }
}

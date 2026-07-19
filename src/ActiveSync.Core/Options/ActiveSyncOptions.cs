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

public sealed class EncryptionOptions
{
	/// <summary>
	///   Master key for local content encryption at rest — ANY string works. A base64 value
	///   decoding to exactly 32 bytes is used as the raw 256-bit key ('openssl rand -base64
	///   32'); anything else is a passphrase, stretched to 256 bits with PBKDF2-SHA256.
	/// </summary>
	public string? Key { get; set; }

	/// <summary>
	///   Path to a file containing the key (docker-secret friendly; same raw-or-passphrase
	///   interpretation as <see cref="Key" />). Mutually exclusive with <see cref="Key" />.
	/// </summary>
	public string? KeyFile { get; set; }

	/// <summary>
	///   Explicitly store local content unencrypted (dev/test only). Without a key, startup
	///   fails unless this is set. Ignored when a key is configured.
	/// </summary>
	public bool AllowPlaintext { get; set; }
}

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

public sealed class SelfSignedTlsOptions
{
	/// <summary>
	///   Serve HTTPS on <see cref="Port" /> with a self-signed certificate that is generated
	///   on first serve and persisted in the state database (private key sealed with the
	///   Encryption master key), so it stays identical across restarts and replicas. Skipped
	///   automatically when configuration declares a Kestrel HTTPS endpoint — mounted real
	///   certificates always win and never touch the database.
	/// </summary>
	public bool Enabled { get; set; } = true;

	/// <summary>Listen port of the self-signed HTTPS endpoint.</summary>
	public int Port { get; set; } = 5443;
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
	public SelfSignedTlsOptions SelfSignedTls { get; set; } = new();
	public LogOptions Log { get; set; } = new();
	public PolicyOptions Policy { get; set; } = new();
	public MetricsOptions Metrics { get; set; } = new();

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
	///   Optional path to a JSON file (mounted secret/configmap) merged into configuration at
	///   startup. Full configuration shape: { "ActiveSync": { "Users": { ... } } }. Changes
	///   require a restart (the account snapshot is built once at startup).
	/// </summary>
	public string? UsersFile { get; set; }
}

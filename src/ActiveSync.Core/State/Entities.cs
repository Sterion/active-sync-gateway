namespace ActiveSync.Core.State;

/// <summary>A device partnership (user + DeviceId).</summary>
public class Device
{
	public int Id { get; set; }
	public required string UserName { get; set; }
	public required string DeviceId { get; set; }
	public string DeviceType { get; set; } = "";
	public uint PolicyKey { get; set; }

	/// <summary>
	///   Hex SHA-256 of the policy document this device acknowledged (Provision phase 2).
	///   A config change produces a new hash, so stale devices are herded back through
	///   Provision with HTTP 449. Null = never completed a policy handshake.
	/// </summary>
	public string? PolicyDocHash { get; set; }

	/// <summary>
	///   Device recovery password escrowed via Settings→DevicePassword (only accepted when
	///   the policy enables PasswordRecoveryEnabled), sealed with the Encryption master key.
	/// </summary>
	public string? RecoveryPasswordProtected { get; set; }

	/// <summary>
	///   Set by 'eas device wipe': the device's next Provision carries the 16.1
	///   AccountOnlyRemoteWipe directive (every other command answers 449 until then).
	///   There is deliberately no full-device wipe.
	/// </summary>
	public bool PendingAccountWipe { get; set; }

	/// <summary>Protocol version last presented by the device (drives the CLI's &lt;16.1 wipe warning).</summary>
	public string? LastProtocolVersion { get; set; }

	/// <summary>Folder hierarchy sync key counter.</summary>
	public int FolderSyncKey { get; set; }

	/// <summary>DeviceInformation from Settings, stored as JSON.</summary>
	public string? DeviceInfoJson { get; set; }

	/// <summary>Cached Ping parameters (heartbeat + folder list) for empty Ping requests.</summary>
	public string? PingParamsJson { get; set; }

	/// <summary>Cached shape of the last full Sync request, replayed for empty Sync requests.</summary>
	public string? LastSyncRequestJson { get; set; }

	public DateTime CreatedUtc { get; set; }
	public DateTime LastSeenUtc { get; set; }

	public List<DeviceFolder> Folders { get; set; } = [];
	public List<CollectionState> Collections { get; set; } = [];
}

/// <summary>
///   Per-user folder registry: assigns stable EAS ServerIds to backend folders. Shared across the
///   user's devices so every device sees the same collection ids.
/// </summary>
public class UserFolder
{
	public int Id { get; set; }
	public required string UserName { get; set; }

	/// <summary>Backend identity, e.g. "imap:INBOX/Sub" or "caldav:/dav/user/calendar/".</summary>
	public required string BackendKey { get; set; }

	public required string DisplayName { get; set; }
	public string? ParentBackendKey { get; set; }
	public int Type { get; set; }
	public required string EasClass { get; set; }

	/// <summary>Soft-delete marker for folders that disappeared from the backend.</summary>
	public bool Deleted { get; set; }

	/// <summary>EAS ServerId (CollectionId) exposed to clients.</summary>
	public string ServerId => Id.ToString();

	public List<DavItem> DavItems { get; set; } = [];
}

/// <summary>Folder hierarchy as last acknowledged by a device (for FolderSync diffs).</summary>
public class DeviceFolder
{
	public int Id { get; set; }
	public int DeviceKey { get; set; }
	public Device Device { get; set; } = null!;
	public required string ServerId { get; set; }
	public required string DisplayName { get; set; }
	public string? ParentServerId { get; set; }
	public int Type { get; set; }
}

/// <summary>Per-device, per-collection sync state with one generation of replay history.</summary>
public class CollectionState
{
	public int Id { get; set; }
	public int DeviceKey { get; set; }
	public Device Device { get; set; } = null!;
	public required string CollectionId { get; set; }
	public int SyncKey { get; set; }

	/// <summary>JSON: item ServerId → revision, as of SyncKey.</summary>
	public string SnapshotJson { get; set; } = "{}";

	/// <summary>Snapshot as of SyncKey-1, kept so a replayed key can be honored.</summary>
	public string? PreviousSnapshotJson { get; set; }

	/// <summary>
	///   JSON: ClientId → applied-Add outcome for the request that produced SyncKey. A client
	///   that never saw that response re-sends the same Adds with the same ClientIds; this map
	///   lets the replay reuse the already-created items instead of duplicating them.
	/// </summary>
	public string? LastClientAddsJson { get; set; }

	/// <summary>
	///   JSON: replay key (item ServerId; ServerId + '\n' + InstanceId for occurrence
	///   cancels) → applied-Change outcome for the request that produced SyncKey. Lets a
	///   replayed Change acknowledge the edit already on the backend instead of re-applying
	///   it — and re-mailing iMIP updates to attendees.
	/// </summary>
	public string? LastClientChangesJson { get; set; }

	public int FilterType { get; set; }

	/// <summary>Cached client sync options (body preference, window, etc.) for empty Sync requests.</summary>
	public string? OptionsJson { get; set; }

	public DateTime UpdatedUtc { get; set; }

	/// <summary>Optimistic-concurrency token; re-stamped on every save (see SyncDbContext).</summary>
	public Guid ConcurrencyToken { get; set; }
}

/// <summary>Maps DAV item hrefs to short numeric ids used inside EAS item ServerIds.</summary>
public class DavItem
{
	public int Id { get; set; }
	public int UserFolderKey { get; set; }
	public UserFolder Folder { get; set; } = null!;
	public required string Href { get; set; }
}

/// <summary>
///   Locally stored PIM item, served from the gateway database when no external DAV backend
///   is configured (and always for Notes). Content is standards-based text — vCard for
///   contacts, iCalendar VEVENT for events, iCalendar VJOURNAL for notes — so the data stays
///   exportable even though only ActiveSync clients can see it.
/// </summary>
public class LocalItem
{
	public int Id { get; set; }
	public required string UserName { get; set; }

	/// <summary>Content class bucket: "contacts", "calendar" or "notes".</summary>
	public required string Collection { get; set; }

	public required string Uid { get; set; }
	public required string Content { get; set; }

	/// <summary>Monotonic per-item revision; exposed to the sync engine as the revision token.</summary>
	public int Version { get; set; }

	/// <summary>Item date used by EAS filter windows (event start); null = always in range.</summary>
	public DateTime? ItemDateUtc { get; set; }

	public DateTime LastModifiedUtc { get; set; }

	/// <summary>Optimistic-concurrency token; re-stamped on every save (see SyncDbContext).</summary>
	public Guid ConcurrencyToken { get; set; }
}

/// <summary>
///   An operator-imposed login block, enforced after successful authentication (403). A null
///   <see cref="DeviceId" /> blocks the whole user; otherwise only that device is refused.
/// </summary>
public class LoginBlock
{
	public int Id { get; set; }
	public required string UserName { get; set; }
	public string? DeviceId { get; set; }
	public DateTime CreatedUtc { get; set; }
}

/// <summary>
///   Per-login cut-off for web sessions: any session STARTED before <see cref="ValidAfterUtc" />
///   is refused at its next revalidation. The web cookie is a self-contained ticket, so signing
///   out or changing a password cannot invalidate copies of it — deleting the browser's cookie
///   leaves a stolen one cryptographically valid until it expires. This row is the server-side
///   half that closes that: one row per login, rewritten (never appended) on logout and on a
///   password change.
/// </summary>
public class WebSessionRevocation
{
	public int Id { get; set; }
	public required string UserName { get; set; }
	public DateTime ValidAfterUtc { get; set; }
}

/// <summary>
///   A CLI-managed grant exposing one extra CalDAV collection to one user as an additional
///   calendar folder (`eas share`). ReadOnly grants are enforced gateway-side (silent
///   revert, like ReadOnly mode) on top of whatever the DAV server itself allows.
/// </summary>
public class SharedCalendarGrant
{
	public int Id { get; set; }
	public required string UserName { get; set; }
	public required string CollectionHref { get; set; }
	public bool ReadOnly { get; set; }
	public DateTime CreatedUtc { get; set; }
}

/// <summary>
///   A database-declared user account entry: <see cref="Json" /> holds the serialized
///   ActiveSync.Core.Options.AccountOptions shape, secrets stored exactly as config would
///   hold them (pbkdf2$/plaintext/enc:v1:). A row REPLACES the whole config entry for the
///   same login; deleting it falls back to config.
/// </summary>
public class AccountEntry
{
	public int Id { get; set; }
	public required string UserName { get; set; }
	public required string Json { get; set; }
	public DateTime UpdatedUtc { get; set; }
}

/// <summary>
///   Single-row change signal (Id always 1): every account mutation bumps Version in the
///   same SaveChanges, and each gateway replica point-reads it to notice changes cheaply.
/// </summary>
public class AccountsStamp
{
	public int Id { get; set; }
	public Guid Version { get; set; }
}

/// <summary>
///   One database-stored global configuration value: <see cref="Key" /> is the full
///   configuration path (e.g. "ActiveSync:Eas:MaxHeartbeatSeconds"), <see cref="Value" /> the
///   string form a configuration provider supplies. A row OVERRIDES the same key from
///   appsettings/env (the database wins); deleting it falls back to file/env, then the code
///   default. The two bootstrap sections (Database, Encryption) are never stored here — they are
///   needed to open and decrypt this very database.
/// </summary>
public class GlobalSetting
{
	public int Id { get; set; }
	public required string Key { get; set; }
	public required string Value { get; set; }
	public DateTime UpdatedUtc { get; set; }
}

/// <summary>
///   Single-row change signal (Id always 1) for <see cref="GlobalSetting" />: every settings
///   mutation bumps Version in the same SaveChanges, and each gateway replica point-reads it to
///   notice CLI/admin changes cheaply — the same idiom as <see cref="AccountsStamp" />.
/// </summary>
public class SettingsStamp
{
	public int Id { get; set; }
	public Guid Version { get; set; }
}

/// <summary>
///   One persisted log line — a rolling buffer for `eas logs` and a future admin UI. Written by
///   the DatabaseLogSink at Information or above (never Trace/Debug wire dumps); old rows are swept
///   per ActiveSync:Log:RetentionDays. <see cref="Machine" /> disambiguates rows across replicas.
/// </summary>
public class LogEntry
{
	public long Id { get; set; }
	public DateTime TimestampUtc { get; set; }
	public required string Level { get; set; }
	public required string Message { get; set; }
	public string? Exception { get; set; }
	public string? SourceContext { get; set; }
	public string? User { get; set; }
	public string? Machine { get; set; }
}

/// <summary>
///   The gateway's self-signed TLS certificate (Id always 1): a PKCS#12 blob, base64-encoded
///   and sealed with the Encryption master key, generated on first serve and shared by every
///   replica so the fingerprint stays stable. Deleting the row generates a fresh certificate
///   at the next startup. Unused when configuration declares a real Kestrel HTTPS endpoint.
/// </summary>
public class ServerCertificate
{
	public int Id { get; set; }
	public required string PfxProtected { get; set; }
	public DateTime CreatedUtc { get; set; }
}

/// <summary>
///   One ASP.NET DataProtection key-ring entry — the signing/encryption keys behind the web
///   UI's auth cookies, stored in the state database so sessions survive restarts and
///   validate on every replica. <see cref="Xml" /> is the key XML, sealed with the Encryption
///   master key when one is configured (a database dump alone cannot forge web sessions).
///   Written by the web UI's key repository, never by hand.
/// </summary>
public class DataProtectionKeyEntry
{
	public int Id { get; set; }
	public string? FriendlyName { get; set; }
	public string? Xml { get; set; }
}

/// <summary>
///   Per-user out-of-office state — the source of truth for Settings→Oof Get (the sieve
///   script on the mail server is derived output, never parsed back). The message is stored
///   in plaintext deliberately: the same text sits as a plaintext sieve script on the mail
///   server and in every auto-reply anyway.
/// </summary>
public class OofSetting
{
	public int Id { get; set; }
	public required string UserName { get; set; }

	/// <summary>0 = disabled, 1 = enabled, 2 = scheduled (EAS OofState values).</summary>
	public int State { get; set; }

	public DateTime? StartUtc { get; set; }
	public DateTime? EndUtc { get; set; }
	public string Message { get; set; } = "";

	/// <summary>"Text" or "HTML" — echoed back to the client; the reply is sent as text.</summary>
	public string BodyType { get; set; } = "Text";

	/// <summary>Active sieve script name before the gateway took over; restored on disable.</summary>
	public string? PreviousActiveScript { get; set; }

	public DateTime UpdatedUtc { get; set; }
}

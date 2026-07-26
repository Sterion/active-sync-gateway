namespace ActiveSync.Core.State;

/// <summary>A device partnership (user + DeviceId).</summary>
public class Device
{
	public int Id { get; set; }

	/// <summary>The owning user — THE identity (never a login string, never a backend name).</summary>
	public int UserId { get; set; }

	public User User { get; set; } = null!;
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

	/// <summary>Optimistic-concurrency token; re-stamped on every save (see SyncDbContext).</summary>
	public Guid ConcurrencyToken { get; set; }

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

	/// <summary>The owning user (FK, cascade).</summary>
	public int UserId { get; set; }

	public User User { get; set; } = null!;

	/// <summary>Backend identity, e.g. "imap:INBOX/Sub" or "caldav:/dav/user/calendar/".</summary>
	public required string BackendKey { get; set; }

	public required string DisplayName { get; set; }
	public string? ParentBackendKey { get; set; }
	public int Type { get; set; }
	public required string EasClass { get; set; }

	/// <summary>Soft-delete marker for folders that disappeared from the backend.</summary>
	public bool Deleted { get; set; }

	/// <summary>
	///   When the folder was first soft-deleted (stamped once, on the Deleted false→true
	///   transition; cleared if it reappears). Drives the retention sweep that eventually
	///   reclaims the row and its dependent DAV/collection state so the tables stop growing (A35).
	/// </summary>
	public DateTime? DeletedUtc { get; set; }

	/// <summary>EAS ServerId (CollectionId) exposed to clients.</summary>
	public string ServerId => Id.ToString();

	public List<DavItem> DavItems { get; set; } = [];
}

/// <summary>Folder hierarchy as last acknowledged by a device (for FolderSync diffs).</summary>
public class DeviceFolder
{
	// PK is (DeviceKey, ServerId) — the unique index that already identified the row. The
	// surrogate Id was never looked up (one identity per row).
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
	// PK is (DeviceKey, CollectionId) — see DeviceFolder.
	public int DeviceKey { get; set; }
	public Device Device { get; set; } = null!;
	public required string CollectionId { get; set; }
	public int SyncKey { get; set; }

	/// <summary>
	///   Gzipped JSON of {item ServerId → revision}, as of SyncKey. Stored compressed because it
	///   holds every item ever sent (2–3 MB uncompressed on a large mailbox); read and written only
	///   through <see cref="SnapshotCodec" /> (A4).
	/// </summary>
	public byte[]? SnapshotCompressed { get; set; }

	/// <summary>Gzipped snapshot as of SyncKey-1, kept so a replayed key can be honored.</summary>
	public byte[]? PreviousSnapshotCompressed { get; set; }

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

/// <summary>
///   F2: a durable claim that one irreversible send (16.x draft submit, occurrence-CANCEL iTIP)
///   has already been carried out for one client command. Written with its OWN immediate
///   SaveChangesAsync BEFORE the send runs (<see cref="SendDedupStore" />), independently of the
///   round's SyncKey/ledger commit — so it survives a crash between the send and
///   <c>CommitCollectionStateAsync</c>, the one window the applied-command ledger cannot cover
///   (a crash there leaves the SyncKey unadvanced, so the client's resend validates as Current —
///   a fresh round with an empty ledger — not Replay). <see cref="SyncKeyAtClaim" /> scopes the
///   claim to the ATTEMPT, not the item: once a collection's commit lands, every claim carrying an
///   older SyncKey is pruned, so a later, genuinely new edit that happens to reuse the same
///   ServerId/ClientId is never mistaken for a crash-retry of a stale one.
/// </summary>
public class SentCommandToken
{
	/// <summary>
	///   Deliberately KEEPS its surrogate key despite having a natural one: that key is four
	///   columns wide including a variable-length string, and this is the hot write path (a claim
	///   is inserted before every irreversible send). The unique index still enforces the natural
	///   key; promoting it would be the highest churn and the smallest payoff of any table here.
	/// </summary>
	public int Id { get; set; }

	/// <summary>Owning device — a real FK with cascade, so a deleted device leaves no orphans.</summary>
	public int DeviceKey { get; set; }

	public Device Device { get; set; } = null!;
	public required string CollectionId { get; set; }
	public int SyncKeyAtClaim { get; set; }

	/// <summary>ClientId (Add); ServerId (Change); ServerId + '\n' + InstanceId (occurrence cancel).</summary>
	public required string Key { get; set; }

	public DateTime CreatedUtc { get; set; }

	/// <summary>
	///   <c>true</c> once the irreversible action this claim guards actually SUCCEEDED — set by
	///   <see cref="SendDedupStore" /> right after the send/cancel returns, never at claim time. A
	///   row with <c>Completed == false</c> means an attempt was claimed but never confirmed to have
	///   finished (still running, crashed, or failed) and must be retried, not skipped — only a
	///   <c>true</c> row is durable proof the action already happened.
	/// </summary>
	public bool Completed { get; set; }
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

	/// <summary>The owning user (FK, cascade). Also bound into the content's encryption AAD.</summary>
	public int UserId { get; set; }

	public User User { get; set; } = null!;

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
///   An operator cut-off for ONE DEVICE, enforced after successful authentication (403). Also
///   written automatically when an account-only wipe is acknowledged, so stolen credentials stay
///   dead after the account is removed from the device.
///   <para>
///     Per-device only, by design. A nullable device used to mean "block the whole user", which
///     duplicated <see cref="User.Enabled" /> — two mechanisms for one concept, which the
///     documentation then had to keep explaining apart. Now there is exactly one of each:
///     <c>Users.Enabled = false</c> turns the USER off everywhere; a <see cref="LoginBlock" />
///     cuts off THIS DEVICE and nothing else.
///   </para>
///   <para>
///     It carries no <c>UserId</c> either: a device belongs to exactly one user, so the column
///     would be derivable — and derivable columns can go stale with nothing to catch it. "All
///     blocks for this user" is a join either way.
///   </para>
/// </summary>
public class LoginBlock
{
	/// <summary>The blocked device — the primary key AND the FK (one block per device, enforced).</summary>
	public int DeviceKey { get; set; }

	public Device Device { get; set; } = null!;
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
	/// <summary>
	///   The owning user — the primary key AND the FK. One row per user is now enforced by
	///   identity rather than by a unique index sitting beside an unused surrogate, which is
	///   what keeps the revocation a REWRITE (never an append).
	/// </summary>
	public int UserId { get; set; }

	public DateTime ValidAfterUtc { get; set; }
}

/// <summary>
///   A CLI-managed grant exposing one extra CalDAV collection to one user as an additional
///   calendar folder (`eas share`). ReadOnly grants are enforced gateway-side (silent
///   revert, like ReadOnly mode) on top of whatever the DAV server itself allows.
/// </summary>
public class SharedCalendarGrant
{
	// PK is (UserId, CollectionHref) — the unique index that already identified the row.

	/// <summary>The grantee (FK, cascade).</summary>
	public int UserId { get; set; }

	public User User { get; set; } = null!;
	public required string CollectionHref { get; set; }
	public bool ReadOnly { get; set; }
	public DateTime CreatedUtc { get; set; }
}

/// <summary>
///   One gateway user — THE identity record. <see cref="UserId" /> is the immutable surrogate
///   key everything per-user hangs off (sync state, local items, blocks, grants, the encryption
///   AAD); it is NEVER reused. <see cref="Login" /> is the identity string the phone sends — a
///   mutable, unique, case-folded attribute.
///   <para>
///     Every scalar of the user DECLARATION is a real column (there is no serialized blob left
///     here): a malformed scalar is impossible, and "which users are admins / disabled / bound to
///     this OIDC subject" are real queries rather than a full-table deserialize. Secrets are
///     stored exactly as configuration would hold them — <c>pbkdf2$…</c> for the gateway password,
///     <c>enc:v1:…</c> (or plaintext) for backend passwords. <see cref="Declared" /> distinguishes
///     a database DECLARATION from an identity-only row (the user exists — it authenticated while
///     declared in configuration, or was named by a block/share — but the database declares
///     nothing, so the resolver ignores it and config keeps supplying the values).
///   </para>
/// </summary>
public class User
{
	/// <summary>THE identity — immutable, never reused (AUTOINCREMENT/identity enforced).</summary>
	public int UserId { get; set; }

	/// <summary>The gateway login, stored case-folded, unique. Mutable (rename = one-row update).</summary>
	public required string Login { get; set; }

	/// <summary>
	///   True when this row is a database DECLARATION (it REPLACES the config entry for the same
	///   login); false = identity only. A declaration with every column null is still a
	///   declaration — that is exactly the allowlist grant `eas user add` writes.
	/// </summary>
	public bool Declared { get; set; }

	/// <summary>
	///   DEVICE → GATEWAY password: a <c>pbkdf2$…</c> hash (preferred) or plaintext, verified
	///   LOCALLY and never sent to any backend. A different trust domain from the backend
	///   credentials below — keep the two chains apart.
	/// </summary>
	public string? Password { get; set; }

	/// <summary>
	///   GATEWAY → BACKENDS: the default backend user name for every role. Unset ⇒ the gateway
	///   login (today's behaviour).
	/// </summary>
	public string? DefaultBackendLogin { get; set; }

	/// <summary>
	///   GATEWAY → BACKENDS: the default backend secret for every role, <c>enc:v1:</c> sealed.
	///   Unset ⇒ the PRESENTED EAS password, i.e. pass-through — the zero-administration
	///   baseline, which must survive.
	/// </summary>
	public string? DefaultBackendPassword { get; set; }

	/// <summary>Mail address for From rewriting, Settings and meeting replies; null ⇒ the login if it contains '@'.</summary>
	public string? MailAddress { get; set; }

	/// <summary>Grants access to the web admin interface (/admin).</summary>
	public bool? Admin { get; set; }

	/// <summary><c>false</c> = disabled: every login refused with 403 after valid credentials.</summary>
	public bool? Enabled { get; set; }

	/// <summary>The identity-provider subject (<c>sub</c>) this user is bound to (OIDC TOFU).</summary>
	public string? OidcSubject { get; set; }

	/// <summary>Provenance marker for a declaration the gateway created itself on first sign-in.</summary>
	public bool? AutoProvisioned { get; set; }

	public DateTime UpdatedUtc { get; set; }

	/// <summary>Per-role overrides; only roles that actually deviate have a row.</summary>
	public List<UserBackendRole> BackendRoles { get; set; } = [];
}

/// <summary>
///   One (user, role) backend override. The host-owned parts — Enabled/Provider/UserName/Password —
///   have a compile-time-known shape and so are columns; <see cref="SettingsJson" /> is the ONE
///   surviving serialized blob because its keys are provider-defined and discoverable only at
///   runtime (<c>IBackendProvider.DescribeConfiguration</c>), which is the provider model working
///   as intended rather than a shortcut.
/// </summary>
public class UserBackendRole
{
	// PK is (UserId, Role) — one override row per role, enforced by identity.

	/// <summary>Owning user (FK, cascade).</summary>
	public int UserId { get; set; }

	public User User { get; set; } = null!;

	/// <summary>Role name: MailStore, MailSubmit, Calendar, Tasks, Contacts, Notes, Oof.</summary>
	public required string Role { get; set; }

	/// <summary><c>false</c> = turn the role off (content roles fall back to local, Oof off). Invalid on the mail roles.</summary>
	public bool? Enabled { get; set; }

	/// <summary>Serve this role with a different provider than the global assignment.</summary>
	public string? Provider { get; set; }

	/// <summary>Backend login for this role.</summary>
	public string? UserName { get; set; }

	/// <summary>Backend secret for this role, <c>enc:v1:</c> sealed.</summary>
	public string? Password { get; set; }

	/// <summary>Flat provider-defined settings keys overlaid on the global role section.</summary>
	public string? SettingsJson { get; set; }
}

/// <summary>
///   The change signal every replica point-reads (~1 s) to notice edits without re-reading
///   everything: one row per WATCHED AREA, keyed by a string (<c>"users"</c>, <c>"settings"</c>),
///   whose <see cref="Version" /> is bumped in the SAME SaveChanges as the mutation.
///   <para>
///     One row per area, NEVER one row total: a shared version would make a user write
///     invalidate the settings snapshot and vice versa, so every consumer would reload on every
///     unrelated change. On PostgreSQL the distinct rows are also distinct row locks.
///   </para>
///   <para>
///     A stamp belongs to a CONSUMER'S AGGREGATE, not to a table — <c>UserBackendRoles</c> writes
///     bump <c>"users"</c> because the resolver rebuilds the whole user snapshot anyway. Getting
///     that backwards yields one stamp per table and a resolver that reloads several times for one
///     logical change. Adding a watched area later is an inserted row, not a migration.
///   </para>
/// </summary>
public class DataChange
{
	/// <summary>The watched area — the primary key (see <see cref="DataChangeAreas" />).</summary>
	public required string Key { get; set; }

	public Guid Version { get; set; }
	public DateTime UpdatedUtc { get; set; }
}

/// <summary>The watched areas of <see cref="DataChange" /> — one constant per consumer aggregate.</summary>
public static class DataChangeAreas
{
	/// <summary>Users: declarations and their per-role overrides (the resolver's snapshot).</summary>
	public const string Users = "users";

	/// <summary>Global settings (the DB configuration layer).</summary>
	public const string Settings = "settings";
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
	/// <summary>The full configuration path — the primary key (one identity per row).</summary>
	public required string Key { get; set; }
	public required string Value { get; set; }
	public DateTime UpdatedUtc { get; set; }
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

	/// <summary>
	///   EF concurrency token (K6): two replicas racing to replace an unreadable row must not
	///   both silently succeed — the loser gets a <see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException" />
	///   (already handled in <see cref="Security.GatewayCertificateStore.GetOrCreateAsync" />
	///   the same way the first-boot insert race is) instead of flip-flopping the served
	///   fingerprint. Same stamping idiom as <c>Device</c>/<c>CollectionState</c>/<c>LocalItem</c>.
	/// </summary>
	public Guid ConcurrencyToken { get; set; }
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
	/// <summary>The owning user — the primary key AND the FK. One Oof row per user, enforced.</summary>
	public int UserId { get; set; }

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

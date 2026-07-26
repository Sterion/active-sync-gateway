# Current database schema — as it exists today

> **This describes the schema as it is NOW**, before the account-model restructure. It is a reference
> for planning that work, not a description of the target. For the target, see
> [`account-model.md`](account-model.md).

Transcribed from `src/ActiveSync.Core/State/Entities.cs` (the C# entities) and
`src/ActiveSync.Core/State/SyncDbContext.cs` (`DbSet`s, keys, indexes, relationships). Those two
files are the source of truth; if this document disagrees with them, they win.

**18 tables.** Every `Id` is an `int` identity primary key unless noted (`LogEntry.Id` is `long`;
three single-row tables use an explicit, non-generated `Id`).

---

## Reading the link graph

Only **three** relationships are real foreign keys with cascade delete. Everything else is a **soft
link** — matched by value at query time, with no database constraint and no cascade.

**Hard FKs (declared in `OnModelCreating`, `DeleteBehavior.Cascade`):**

```
Device ──< DeviceFolder        (DeviceFolder.DeviceKey → Device.Id)
Device ──< CollectionState     (CollectionState.DeviceKey → Device.Id)
UserFolder ──< DavItem         (DavItem.UserFolderKey → UserFolder.Id)
```

**Everything else is soft**, and three patterns account for almost all of it:

| Pattern | How it links | Tables |
|---|---|---|
| **The gateway login** (`UserName` string) | value equality; **this is the de-facto account key today** | `Device`, `UserFolder`, `LocalItem`, `LoginBlock`, `WebSessionRevocation`, `SharedCalendarGrant`, `AccountEntry`, `OofSetting` |
| **CollectionId ↔ folder** | `CollectionState.CollectionId` / `SentCommandToken.CollectionId` / `DeviceFolder.ServerId` hold `UserFolder.Id` **as a string** (`UserFolder.ServerId` is `Id.ToString()`) | `CollectionState`, `SentCommandToken`, `DeviceFolder` → `UserFolder` |
| **DeviceId string** | `LoginBlock.DeviceId` matches `Device.DeviceId` (not `Device.Id`) | `LoginBlock` → `Device` |

⚠️ **`SentCommandToken.DeviceKey` holds a `Device.Id` but is NOT a declared FK** — no constraint, no
cascade. Deleting a `Device` cascades `DeviceFolder` and `CollectionState` but leaves
`SentCommandToken` rows orphaned. Worth knowing before the restructure.

**Six tables are global**, with no per-user scoping at all: `AccountsStamp`, `SettingsStamp`,
`GlobalSetting`, `LogEntry`, `ServerCertificate`, `DataProtectionKeyEntry`.

**Concurrency tokens.** `Device`, `CollectionState`, `LocalItem` and `ServerCertificate` carry a
`Guid ConcurrencyToken`, re-stamped on every insert/update by `SyncDbContext.StampConcurrencyTokens`.
A lost update surfaces as `DbUpdateConcurrencyException` rather than silently overwriting.

---

# Sync state

## `Devices`

| Column | Type | Key / index | Purpose |
|---|---|---|---|
| `Id` | int | **PK** | Surrogate key; referenced by `DeviceFolder`/`CollectionState`/`SentCommandToken` as `DeviceKey` |
| `UserName` | string | **unique (UserName, DeviceId)** | Gateway login — the identity |
| `DeviceId` | string | ↑ | Client-supplied device id from the EAS query string |
| `DeviceType` | string | | Client-supplied device type ("iPhone", "WindowsOutlook15") |
| `PolicyKey` | uint | | MS-ASPROV policy key currently issued to the device |
| `PolicyDocHash` | string? | | SHA-256 of the acknowledged policy document; a config change makes it stale → HTTP 449 re-provision. Null = never handshaked |
| `RecoveryPasswordProtected` | string? | | Device recovery password escrowed via Settings→DevicePassword, sealed with the master key |
| `PendingAccountWipe` | bool | | Set by `eas device wipe`; next Provision carries the 16.1 account-only wipe directive |
| `LastProtocolVersion` | string? | | Last EAS version presented (drives the CLI's <16.1 wipe warning) |
| `FolderSyncKey` | int | | FolderSync hierarchy sync-key counter |
| `DeviceInfoJson` | string? | | DeviceInformation from Settings, as JSON |
| `PingParamsJson` | string? | | Cached heartbeat + folder list, replayed for empty Ping requests |
| `LastSyncRequestJson` | string? | | Cached shape of the last full Sync request, replayed for empty Sync requests |
| `CreatedUtc` / `LastSeenUtc` | DateTime | | Partnership lifetime; `LastSeenUtc` drives `eas devices` |
| `ConcurrencyToken` | Guid | concurrency token | Guards `FolderSyncKey` against pipelined FolderSyncs losing a bump |

**What it is:** one row per *device partnership* — a (user, DeviceId) pair. The anchor of all
per-device sync state, and the thing `eas devices` / the admin Devices page list.

**Where used:** `DeviceStore`; the policy gate in `EasEndpoint` (449 handling); Provision; FolderSync;
Ping/Sync request caching; `eas device wipe`/`password`.

**Links:** ← `DeviceFolder.DeviceKey`, `CollectionState.DeviceKey` (**hard FK, cascade**);
← `SentCommandToken.DeviceKey` (**soft**, no constraint); ← `LoginBlock.DeviceId` (soft, matches
`DeviceId` the string, not `Id`); → the account via `UserName` (soft).

## `UserFolders`

| Column | Type | Key / index | Purpose |
|---|---|---|---|
| `Id` | int | **PK** | Also the EAS CollectionId — `ServerId` is a computed `Id.ToString()` |
| `UserName` | string | **unique (UserName, BackendKey)** | Gateway login |
| `BackendKey` | string | ↑ | Backend identity, prefixed by store: `imap:INBOX/Sub`, `caldav:/dav/user/cal/`, `local:` … |
| `DisplayName` | string | | Folder name shown to the client |
| `ParentBackendKey` | string? | | Parent's `BackendKey` — **self-referencing soft link** |
| `Type` | int | | EAS folder type (2 = Inbox, 8 = Calendar, …) |
| `EasClass` | string | | Content class: Email / Calendar / Contacts / Tasks / Notes |
| `Deleted` | bool | | Soft-delete marker for folders that vanished from the backend |
| `DeletedUtc` | DateTime? | | Stamped on the false→true transition; drives the retention sweep that reclaims the row |

**What it is:** the **per-user** folder registry, assigning stable EAS ServerIds to backend folders.
Shared across all of a user's devices, so every device sees the same collection ids.

**Where used:** the folder registry / FolderSync; every ServerId that reaches a client; DAV item id
mapping; `FolderRetentionService`.

**Links:** ← `DavItem.UserFolderKey` (**hard FK, cascade**); ← `CollectionState.CollectionId`,
`SentCommandToken.CollectionId`, `DeviceFolder.ServerId`/`ParentServerId` — all soft, all holding
`Id` **as a string**; self-link `ParentBackendKey` → `BackendKey`; → account via `UserName` (soft).

## `DeviceFolders`

| Column | Type | Key / index | Purpose |
|---|---|---|---|
| `Id` | int | **PK** | |
| `DeviceKey` | int | **unique (DeviceKey, ServerId)**, **FK → Device.Id** | Owning device |
| `ServerId` | string | ↑ | The `UserFolder.Id` this device has acknowledged |
| `DisplayName` | string | | Name as the device last saw it |
| `ParentServerId` | string? | | Parent as the device last saw it |
| `Type` | int | | Folder type as the device last saw it |

**What it is:** the folder hierarchy **as last acknowledged by one device**. FolderSync diffs this
against `UserFolders` to compute adds/changes/deletes per device.

**Where used:** FolderSync only.

**Links:** → `Device` (**hard FK, cascade**); → `UserFolder` via `ServerId`/`ParentServerId` (soft,
string form of `UserFolder.Id`).

## `CollectionStates`

| Column | Type | Key / index | Purpose |
|---|---|---|---|
| `Id` | int | **PK** | |
| `DeviceKey` | int | **unique (DeviceKey, CollectionId)**, **FK → Device.Id** | Owning device |
| `CollectionId` | string | ↑ | `UserFolder.Id` as a string |
| `SyncKey` | int | | Current per-collection sync key |
| `SnapshotCompressed` | byte[]? | | Gzipped JSON `{itemServerId → revision}` as of `SyncKey`. 2–3 MB uncompressed on a large mailbox; only ever read/written through `SnapshotCodec` |
| `PreviousSnapshotCompressed` | byte[]? | | Same as of `SyncKey − 1`, so a replayed key can be honoured (the N−1 rule) |
| `LastClientAddsJson` | string? | | ClientId → applied-Add outcome for the request that produced `SyncKey`; lets a replay reuse created items instead of duplicating |
| `LastClientChangesJson` | string? | | Replay key → applied-Change outcome (ServerId, or ServerId + `\n` + InstanceId for occurrence cancels) |
| `FilterType` | int | | EAS filter window in force |
| `OptionsJson` | string? | | Cached client sync options (body preference, window size) for empty Sync requests |
| `UpdatedUtc` | DateTime | | |
| `ConcurrencyToken` | Guid | concurrency token | |

**What it is:** the heart of the sync engine — per (device, collection) sync key plus one generation
of replay history. The snapshot is diffed against the backend's current revision map each round.

**Where used:** `SyncStateService` / `CollectionStateStore`; every Sync and GetItemEstimate; the
SyncKey lifecycle and N−1 replay.

**Links:** → `Device` (**hard FK, cascade**); → `UserFolder` via `CollectionId` (soft, string).

## `SentCommandTokens`

| Column | Type | Key / index | Purpose |
|---|---|---|---|
| `Id` | int | **PK** | |
| `DeviceKey` | int | **unique (DeviceKey, CollectionId, SyncKeyAtClaim, Key)** | Owning device — **soft, NOT an FK** |
| `CollectionId` | string | ↑ | `UserFolder.Id` as a string |
| `SyncKeyAtClaim` | int | ↑ | Scopes the claim to the *attempt*, not the item; claims older than a committed SyncKey are pruned |
| `Key` | string | ↑ | ClientId (Add), ServerId (Change), or ServerId + `\n` + InstanceId (occurrence cancel) |
| `CreatedUtc` | DateTime | | |
| `Completed` | bool | | `true` only once the irreversible action actually succeeded. `false` = claimed but unconfirmed (running, crashed, failed) → must be retried, not skipped |

**What it is:** the `F2` fix — a durable claim that one irreversible send (16.x draft submit,
occurrence-CANCEL iTIP) already happened. Written with its **own immediate** `SaveChangesAsync`
*before* the send, independently of the round's commit, so it survives a crash between the send and
`CommitCollectionStateAsync` — the one window the applied-command ledger cannot cover.

**Where used:** `SendDedupStore`, from the Sync client-command path.

**Links:** → `Device` via `DeviceKey` (**soft — no FK, no cascade; orphans if the device is
deleted**); → `UserFolder` via `CollectionId` (soft, string).

## `DavItems`

| Column | Type | Key / index | Purpose |
|---|---|---|---|
| `Id` | int | **PK** | The short numeric id used inside EAS item ServerIds (`{collectionId}:{Id}`) |
| `UserFolderKey` | int | **unique (UserFolderKey, Href)**, **FK → UserFolder.Id** | Owning folder |
| `Href` | string | ↑ | The DAV item href |

**What it is:** the href ↔ short-id map. DAV hrefs are long and unsuitable for EAS ServerIds, so each
gets a stable integer per folder.

**Where used:** `DavItemMap`; every DAV item ServerId a client sees.

**Links:** → `UserFolder` (**hard FK, cascade**).

---

# Local user data

## `LocalItems`

| Column | Type | Key / index | Purpose |
|---|---|---|---|
| `Id` | int | **PK** | Also the item key / revision anchor |
| `UserName` | string | index (UserName, Collection) — **not unique** | Gateway login |
| `Collection` | string | ↑ | Content bucket: `contacts`, `calendar`, `notes` |
| `Uid` | string | | Item UID (iCal/vCard UID) |
| `Content` | string | | **AES-256-GCM ciphertext at rest** (`"v1:" + base64`), AAD = user + collection. vCard / VEVENT / VJOURNAL plaintext underneath |
| `Version` | int | | Monotonic per-item revision — the sync engine's revision token |
| `ItemDateUtc` | DateTime? | | Event start, for EAS filter windows. Null = always in range. **Deliberately plaintext** |
| `LastModifiedUtc` | DateTime | | |
| `ConcurrencyToken` | Guid | concurrency token | |

**What it is:** real user PIM data — contacts, calendar and tasks when no DAV backend is configured,
and **always** notes. This is the one table holding user content rather than sync metadata, which is
why the volume needs backing up.

**Where used:** the `local` provider's stores (`Backends.Local`), which open short-lived contexts via
`ISyncDbContextFactory`; `LocalContentProtector` at the encrypt/decrypt seam.

**Links:** → account via `UserName` (soft). No other links — items are addressed by
`(UserName, Collection, Id)`.

---

# Accounts & access

## `AccountEntries`

| Column | Type | Key / index | Purpose |
|---|---|---|---|
| `Id` | int | **PK** | |
| `UserName` | string | **unique** | Gateway login, stored **case-folded** (`AccountStore.NormalizeLogin`, finding `B1`) |
| `Json` | string | | Serialized `AccountOptions` — secrets exactly as config holds them (`pbkdf2$`, plaintext, `enc:v1:`) |
| `UpdatedUtc` | DateTime | | |

**What it is:** a database-declared account. **A row REPLACES the whole config entry** for the same
login; deleting it falls back to config. This whole-entry replacement is one of the things the
restructure removes.

**Where used:** `AccountStore` (writes, via `eas user`/admin API), `AccountResolver` (reads, merged
with config into the snapshot), `PassThroughProvisioner` (auto-provisioning).

**Links:** → everything per-user via `UserName` (soft). Note that `Json` carries the per-role
backend overrides as an opaque blob rather than as columns.

## `AccountsStamps`

| Column | Type | Key / index | Purpose |
|---|---|---|---|
| `Id` | int | **PK, explicitly set (always 1)** — no identity | Single well-known row |
| `Version` | Guid | | Bumped in the same `SaveChanges` as any account mutation |

**What it is:** a cheap change signal. Every replica point-reads this row (at most every
`Auth:UsersRefreshSeconds`, ~1 s) to notice account changes without re-reading all accounts.

**Where used:** `AccountResolver.EnsureFreshAsync` / `ChangeStampRefreshGate`.

**Links:** none (a bare signal).

## `LoginBlocks`

| Column | Type | Key / index | Purpose |
|---|---|---|---|
| `Id` | int | **PK** | |
| `UserName` | string | **unique (UserName, DeviceId)** | Blocked login |
| `DeviceId` | string? | ↑ | **Null blocks the whole user**; otherwise only that device |
| `CreatedUtc` | DateTime | | |

**What it is:** an operator-imposed block, enforced **after** successful authentication with a 403.
Also written automatically when an account-only wipe is acknowledged.

**Where used:** `EasEndpoint` and `AutodiscoverEndpoint` post-auth; `eas block`/`unblock`;
`DeviceStore.CompleteAccountWipeAsync`.

**Links:** → account via `UserName` (soft); → `Device` via `DeviceId` (soft — matches
`Device.DeviceId`, the client string, **not** `Device.Id`).

## `WebSessionRevocations`

| Column | Type | Key / index | Purpose |
|---|---|---|---|
| `Id` | int | **PK** | |
| `UserName` | string | **unique** | One row per login |
| `ValidAfterUtc` | DateTime | | Any web session *started* before this is refused at next revalidation |

**What it is:** the server-side half of web logout. The auth cookie is a self-contained ticket, so
deleting the browser copy leaves a stolen one cryptographically valid until expiry; this row is what
actually invalidates it. **Rewritten, never appended** — on logout and on password change.

**Where used:** WebUi `SessionValidation` (`OnValidatePrincipal`).

**Links:** → account via `UserName` (soft).

## `SharedCalendarGrants`

| Column | Type | Key / index | Purpose |
|---|---|---|---|
| `Id` | int | **PK** | |
| `UserName` | string | **unique (UserName, CollectionHref)** | Grantee |
| `CollectionHref` | string | ↑ | The extra CalDAV collection href |
| `ReadOnly` | bool | | Enforced gateway-side via silent revert, on top of whatever the DAV server allows |
| `CreatedUtc` | DateTime | | |

**What it is:** `eas share` — exposes one extra CalDAV collection to one user as an additional
calendar folder. Unions with the config `SharedCollections` list; DB wins per href.

**Where used:** `ShareAdminService`; `BackendSessionFactory` loads the grants once per session build,
so changes apply on session recycle rather than immediately.

**Links:** → account via `UserName` (soft). The href is matched leniently against DAV collections
(unescape + case-insensitive), not against any local table.

## `OofSettings`

| Column | Type | Key / index | Purpose |
|---|---|---|---|
| `Id` | int | **PK** | |
| `UserName` | string | **unique** | Owner |
| `State` | int | | 0 = disabled, 1 = enabled, 2 = scheduled (EAS OofState) |
| `StartUtc` / `EndUtc` | DateTime? | | Scheduled window |
| `Message` | string | | Reply body — **deliberately plaintext** (the same text sits as a sieve script on the mail server) |
| `BodyType` | string | | "Text" or "HTML"; echoed to the client, reply always sent as text |
| `PreviousActiveScript` | string? | | Sieve script active before the gateway took over; restored on disable |
| `UpdatedUtc` | DateTime | | |

**What it is:** the **source of truth** for Settings→Oof Get. The sieve script on the mail server is
derived output and is never parsed back.

**Where used:** `SyncStateService.SaveOofAsync`/`GetOofAsync`; the Settings handler; `SieveOofBackend`
/ JMAP VacationResponse.

**Links:** → account via `UserName` (soft). ⚠️ **Not** in the concurrency-token set — finding `A3`
(read-modify-write race, item 20).

---

# Settings & operations

## `GlobalSettings`

| Column | Type | Key / index | Purpose |
|---|---|---|---|
| `Id` | int | **PK** | |
| `Key` | string | **unique** | Full configuration path, e.g. `ActiveSync:Eas:MaxHeartbeatSeconds` |
| `Value` | string | | String form a configuration provider supplies |
| `UpdatedUtc` | DateTime | | |

**What it is:** the DB configuration layer. A row **overrides** the same key from appsettings/env;
deleting it falls back to file/env, then the code default. The two bootstrap sections (`Database`,
`Encryption`) are never stored here — they are needed to open and decrypt this database.

**Where used:** `DbSettingsConfigurationProvider` (layered **last** in `IConfiguration`, so DB wins);
`eas config set`; the admin Settings and Backends pages.

**Links:** none structurally — but the keys address the whole options tree, including
`ActiveSync:Backends:*`, which is how the Backends page stores role assignments.

## `SettingsStamps`

| Column | Type | Key / index | Purpose |
|---|---|---|---|
| `Id` | int | **PK, explicitly set (always 1)** — no identity | Single well-known row |
| `Version` | Guid | | Bumped in the same `SaveChanges` as any settings mutation |

**What it is:** the `GlobalSetting` counterpart to `AccountsStamp` — same idiom, same polling.

**Where used:** `SettingsRefresher` / `SettingsRefreshService`, which swaps the provider snapshot and
fires the config reload token so `IOptionsMonitor` recomputes (~1 s, no restart).

**Links:** none.

## `LogEntries`

| Column | Type | Key / index | Purpose |
|---|---|---|---|
| `Id` | **long** | **PK** | The only non-int key |
| `TimestampUtc` | DateTime | **index** | For `eas logs` window queries and the retention sweep |
| `Level` | string | | Serilog level name |
| `Message` | string | | Rendered message |
| `Exception` | string? | | Exception text if any |
| `SourceContext` | string? | | Logger category |
| `User` | string? | | Gateway login, when the event has one — **soft, free-text** |
| `Machine` | string? | | Disambiguates rows across replicas |

**What it is:** a rolling log buffer for `eas logs` and the admin Logs page. Written at Information
and above only — never the Trace/Debug wire dumps.

**Where used:** `DatabaseLogSink` (write), `LogQueryService` (read), `LogRetentionService` (sweep per
`ActiveSync:Log:RetentionDays`).

**Links:** → account via `User` (soft, nullable, purely informational — no constraint, and it holds
the login *as it was at the time*).

## `ServerCertificates`

| Column | Type | Key / index | Purpose |
|---|---|---|---|
| `Id` | int | **PK, explicitly set (always 1)** — no identity | Single well-known row; the PK conflict serializes first-boot races |
| `PfxProtected` | string | | PKCS#12 blob, base64, sealed with the master key (AAD `_gateway`/`tls`) |
| `CreatedUtc` | DateTime | | |
| `ConcurrencyToken` | Guid | concurrency token | Added by `K6` — serializes the *replace an unreadable/expiring row* race, which the PK conflict alone does not cover (that path is an UPDATE) |

**What it is:** the gateway's self-signed TLS certificate, shared by every replica so the fingerprint
stays stable across restarts. Deleting the row regenerates at next startup. Unused when an operator
certificate is configured.

**Where used:** `GatewayCertificateStore`, `TlsCertificateResolver`, the admin TLS page, `eas tls`.

**Links:** none.

## `DataProtectionKeys`

| Column | Type | Key / index | Purpose |
|---|---|---|---|
| `Id` | int | **PK** | |
| `FriendlyName` | string? | | ASP.NET key-ring friendly name |
| `Xml` | string? | | The key XML, sealed with the master key when one is configured |

**What it is:** the ASP.NET DataProtection key ring behind the web UI's auth cookies, stored in the
database so sessions survive restarts and validate on every replica.

**Where used:** the WebUi's `DbXmlRepository`. Never written by hand.

**Links:** none.

---

---

# Inside the serialized columns

Several columns hold serialized objects rather than scalars, so the schema above understates what is
actually stored. **This is where most of the account model lives today** — `AccountEntry.Json` is one
opaque blob containing everything the restructure wants to normalise.

## `AccountEntries.Json` → `AccountOptions`

`src/ActiveSync.Core/Options/AccountOptions.cs`. Serialized with `System.Text.Json`. Secrets are held
exactly as configuration would hold them — `pbkdf2$…`, `enc:v1:…`, or plaintext.

| Property | Type | Purpose |
|---|---|---|
| `Password` | string? | **Gateway password** — decouples the phone's password from the mail backend. `pbkdf2$…` (preferred) or plaintext. Verified locally |
| `MailAddress` | string? | The user's mail address, for From rewriting, Settings and meeting replies. Null ⇒ the gateway login if it contains `@`. **This is where `MailAddress` lives today — it is not a column** |
| `Admin` | bool? | Grants `/admin` access |
| `Enabled` | bool? | `false` = account DISABLED — every login refused with 403 after valid credentials. The persistent counterpart to an ad-hoc `LoginBlock` |
| `AutoProvisioned` | bool? | Set on rows the gateway created itself on a pass-through login's first sync. Provenance marker only; behaves like a hand-added empty entry |
| `OidcSubject` | string? | The IdP `sub` this account is bound to. Recorded on first OIDC sign-in of a DB account (TOFU); on a config account only when the operator writes it |
| `Backends` | Dictionary&lt;string, BackendRoleOverride&gt; | Per-role overrides, keyed by role name (`MailStore`, `Calendar`, `Oof`, …) |

### `BackendRoleOverride` (the values of `Backends`)

| Property | Type | Purpose |
|---|---|---|
| `Enabled` | bool? | `false` = turn this role off for the user (content roles fall back to `local`, Oof off). Invalid on MailStore/MailSubmit |
| `Provider` | string? | Serve this role with a different provider than the global assignment |
| `UserName` | string? | Backend login. Defaults to the effective MailStore user name (the gateway login for MailStore itself) |
| `Password` | string? | Backend password, plaintext or `enc:v1:` sealed. Defaults to the effective MailStore password |
| `Settings` | Dictionary&lt;string, string?&gt; | Flat config keys overlaid on the global role section — but only when the effective provider matches the global assignment. Setting any list element (`X:0`) REPLACES the whole global list `X`; a **null value CLEARS** the inherited key rather than falling through |

> **The two rules that are invisible in the schema and matter most to the restructure:** an unset role
> password inherits the **presented EAS password**, and every non-MailStore role's credentials default
> to the **effective MailStore pair**. There is no "default backend credential" field — it is this
> implicit chain, which is exactly what `DefaultBackendLogin`/`DefaultBackendPassword` would replace.

## `Devices.DeviceInfoJson` → `Dictionary<string, string>`

The EAS Settings→DeviceInformation `Set` element, flattened to element-name → value (`Model`, `IMEI`,
`FriendlyName`, `OS`, `OSLanguage`, `PhoneNumber`, `MobileOperator`, `UserAgent`, …). **Not a fixed
schema** — whatever the client sent. Written by `SettingsHandler` and `ProvisionHandler`.

## `Devices.PingParamsJson` → `PingParams`

| Property | Type | Purpose |
|---|---|---|
| `HeartbeatSeconds` | int | Heartbeat the client last requested |
| `FolderIds` | List&lt;string&gt; | Collection ids being monitored |

Replayed when a bare Ping arrives with no parameters.

## `Devices.LastSyncRequestJson` → `CachedSyncRequest`

| Property | Type | Purpose |
|---|---|---|
| `WaitSeconds` | int? | Wait/HeartbeatInterval from the last full request |
| `GlobalWindowSize` | int | Request-level window size |
| `Collections` | List&lt;CachedSyncCollection&gt; | Per-collection replay shape |

`CachedSyncCollection` = `{ CollectionId: string, GetChanges: bool, WindowSize: int? }`.

Replayed to rebuild synthetic `<Collection>` elements for an empty Sync request. Client `Commands`
are **never** cached — one-shot by design.

## `CollectionStates.SnapshotCompressed` / `PreviousSnapshotCompressed` → gzipped `Dictionary<string, string>`

`{ itemServerId → revision }`. Gzipped because it holds every item ever sent (2–3 MB uncompressed on
a large mailbox). Read and written **only** through `SnapshotCodec`. Revision format is
backend-specific — IMAP flags string (`"101"`, plus `|kw1,kw2` when keyworded), DAV ETag, JMAP
content hash.

## `CollectionStates.LastClientAddsJson` → `Dictionary<string, AppliedClientAdd>`

Key = the client's `ClientId`. Value = `{ ItemKey: string?, Revision: string? }`.

Lets a replayed Add reuse the already-created item instead of duplicating it.

## `CollectionStates.LastClientChangesJson` → `Dictionary<string, AppliedClientChange>`

Key = item `ServerId`, or `ServerId + '\n' + InstanceId` for an occurrence cancel. Value =
`{ ItemKey: string?, Revision: string? }`; a **null `Revision` marks a Change that removed the item**
(16.x draft submitted and deleted via `email2:Send`).

Lets a replayed Change acknowledge the edit instead of re-applying it — which would re-mail iMIP
updates to attendees.

## `CollectionStates.OptionsJson` → `SyncCollectionOptions`

| Property | Type | Default | Purpose |
|---|---|---|---|
| `FilterType` | int | 0 | EAS filter window |
| `BodyType` | int | 2 | Body preference type |
| `TruncationSize` | long? | 200 KB | Body truncation |
| `MimeSupport` | int | 0 | Parsed and persisted but **never re-consulted** — dead state (finding `F7`) |
| `Conflict` | int | 1 | 0 = client wins; anything else (incl. absent) = server wins |

## Non-JSON opaque columns

| Column | Contents |
|---|---|
| `LocalItems.Content` | `"v1:" + base64` of AES-256-GCM ciphertext. AAD = `userName + "\n" + collection`. Plaintext underneath is **vCard** (contacts), **iCalendar VEVENT** (calendar), **iCalendar VJOURNAL** (notes) |
| `ServerCertificates.PfxProtected` | Base64 PKCS#12, sealed with the master key (AAD `_gateway`/`tls`) |
| `DataProtectionKeys.Xml` | ASP.NET DataProtection key XML, sealed with the master key when one is configured |

---

## Notes for the restructure

Things this schema does that the redesign changes or must account for:

1. **`UserName` is the account key, eight times over.** There is no account id — `AccountEntry` is
   just another table keyed by the same string, not a parent. That is why a login cannot be renamed.
2. **`CollectionId` is a stringified `UserFolder.Id`.** Three tables carry it. Any change to folder
   identity ripples through `CollectionState`, `SentCommandToken` and `DeviceFolder` as *string*
   comparisons, which no constraint protects.
3. **`SentCommandToken.DeviceKey` has no FK**, so it does not cascade with its device.
4. **`AccountEntry.Json` is opaque, and it holds more than it looks.** Per-role overrides,
   credentials, `MailAddress`, `Admin`, `Enabled`, `OidcSubject` and the whole per-role `Settings`
   dictionary all live inside one serialized `AccountOptions` blob (see "Inside the serialized
   columns"). Nothing about a user is queryable or per-field resolvable at the database level today —
   any change means read-blob, deserialize, mutate, re-serialize, write-blob. **Normalising this is
   the single biggest piece of the restructure**, and it is what per-field resolution actually
   requires.
5. **Only four tables carry concurrency tokens** — `Device`, `CollectionState`, `LocalItem`,
   `ServerCertificate`. `OofSetting` notably does not (finding `A3`).
6. **Three single-row tables** (`AccountsStamp`, `SettingsStamp`, `ServerCertificate`) use an
   explicit non-generated `Id = 1`, relying on the PK conflict to serialize concurrent creation.

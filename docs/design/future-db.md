# Target database schema — after the restructure

> **This projection was IMPLEMENTED as written (2026-07-27).** It is now a description of the
> schema, not a proposal. The generated `Initial` migration pair matches it: 18 tables, 11
> cascading foreign keys, nine surrogate keys dropped.
>
> How the **ⓘ inferred** lines resolved:
>
> - **`UpdatedUtc` on `Users`** — kept, as inferred.
> - **`OidcSubject` index** — added, and made UNIQUE (filtered on null). The inference called it
>   "worth a unique index — two users bound to one subject is a takeover vector"; that reasoning
>   held, so the database now forbids it rather than trusting the writer.
> - **`Role` as a string** — kept, as inferred.
> - **`LogEntry.User` stays a login string** — kept, as inferred, for the reason given (a log line
>   records what was true at the time and must survive the user row being deleted).
> - **`DataChanges.UpdatedUtc`** — kept, as inferred.
> - **`CollectionId`-as-string survived** — yes, deliberately: `ServerId` is an EAS wire value.
> - **No table gained a convenience `UserId`** it should not have.
>
> One shape changed from the projection: `Users` has no `Json` column at all. Every scalar is a
> typed column (as projected) plus a `Declared` flag, so an identity-only row is distinguishable
> from a declaration without parsing anything.

**Still 18 tables.** Two stamps merge into one, one table is added, one is renamed and normalised:

| Change | Today | Target |
|---|---|---|
| Renamed + normalised | `AccountEntries` (login + one JSON blob) | **`Users`** (login + 9 typed columns) |
| **New** | — | **`UserBackendRoles`** (per-role credentials as columns) |
| Merged | `AccountsStamps` + `SettingsStamps` | **`DataChanges`** (one row per watched area) |
| Re-keyed | 8 tables carrying `UserName` string | FK to `Users.UserId`, cascade |
| FK added | `SentCommandToken.DeviceKey` (no FK) | real FK → `Device`, cascade |
| Narrowed | `LoginBlock` (nullable `DeviceId` string) | per-device only, non-nullable FK |
| **Surrogate `Id` dropped** | 9 tables carried an unused `Id` *and* a unique natural key | natural key promoted to PK — see below |
| Unchanged | `DavItem`, `LogEntry`, `ServerCertificate`, `DataProtectionKeys` | — |

### One identity per row

Nine tables today carry a surrogate `Id` **and** a unique index that already identifies the row —
two ways to name one thing, which is the same drift risk that keeps `UserId` off `LoginBlock`.
Verified against the code: **not one of them is ever looked up by `Id`.** So the natural key becomes
the primary key and the surrogate goes:

| Table | New PK |
|---|---|
| `WebSessionRevocation` | `UserId` |
| `OofSetting` | `UserId` |
| `LoginBlock` | `DeviceKey` |
| `GlobalSetting` | `Key` |
| `DataChanges` | `Key` |
| `UserBackendRoles` | (`UserId`, `Role`) |
| `DeviceFolder` | (`DeviceKey`, `ServerId`) |
| `CollectionState` | (`DeviceKey`, `CollectionId`) |
| `SharedCalendarGrant` | (`UserId`, `CollectionHref`) |

**The gain is not tidiness — it turns conventions into constraints.** `current-db.md` says of
`WebSessionRevocation`: *"one row per login; the unique index is what keeps the revocation a
rewrite."* That rule currently rides a unique index while the PK sits unused; promote it and the rule
*is* the identity. Same for `OofSetting` (one Oof per user) and `LoginBlock` (a device is blocked or
it is not).

**No index is added** — the unique constraint already exists, so the PK replaces it rather than
joining it. **No composite FK propagates** — none of these tables has children. On SQLite the net
effect is one fewer *column*, not one fewer index, since `Id INTEGER PRIMARY KEY` is the rowid and a
non-integer PK leaves a hidden rowid behind (EF Core does not emit `WITHOUT ROWID`).

**Surrogates that stay, and why:** `Users.UserId` (the immutable identity), `Device.Id` and
`UserFolder.Id` (FK targets — and `UserFolder.Id` is the EAS CollectionId on the wire), `DavItem.Id`
(the sub-part of every DAV item ServerId), `LocalItem.Id` (the item key), `LogEntry.Id` (no natural
key exists), `ServerCertificate.Id` (the `= 1` single-row idiom), `DataProtectionKeys.Id`, and
`SentCommandToken.Id` (see its entry).

---

## Reading the link graph

The inversion from today: **soft links become real foreign keys.** Today three relationships are
declared FKs and everything else is a string matched by value; in the target, every per-user and
per-device relationship is a constraint the database enforces.

```
Users ──< UserBackendRoles          (UserId, cascade)          ← new
      ──< Device ──< DeviceFolder     (DeviceKey, cascade)
      │         ──< CollectionState   (DeviceKey, cascade)
      │         ──< SentCommandToken  (DeviceKey, cascade)     ← FK added
      │         ──< LoginBlock        (DeviceKey, cascade)     ← was a string pair
      ──< UserFolder ──< DavItem      (UserFolderKey, cascade)
      ──< LocalItem                   (UserId, cascade)
      ──< SharedCalendarGrant         (UserId, cascade)
      ──< OofSetting                  (UserId, cascade)
      ──< WebSessionRevocation        (UserId, cascade)
```

**Deleting a `Users` row removes everything above it.** That is the point, and it is why the
application must count content before issuing the delete (`db-restructure.md` § *Deleting a user must
not silently destroy data*).

**Five tables remain global** — no user scoping at all: `DataChanges`, `GlobalSetting`, `LogEntry`,
`ServerCertificate`, `DataProtectionKeys`.

**The one surviving soft link is deliberate:** `CollectionState.CollectionId`,
`SentCommandToken.CollectionId` and `DeviceFolder.ServerId` still hold `UserFolder.Id` **as a string**,
because `ServerId` is the EAS wire identifier and is a string by protocol. `db-restructure.md` does
not propose changing this. ⓘ **inferred** — it is worth a second look during implementation, since it
is the last place where a rename or re-id could silently mis-scope rows, but converting it would
change every ServerId the clients hold.

**Concurrency tokens** stay on `Device`, `CollectionState`, `LocalItem`, `ServerCertificate`.
`OofSetting` still has none — finding `A3` (open, review item 20) is not part of this restructure.

---

# Users and access

## `Users`  *(was `AccountEntries`)*

| Column | Type | Key / index | Purpose |
|---|---|---|---|
| `UserId` | int identity | **PK** | **THE identity.** Immutable, never reused — every per-user table FKs to it. `AUTOINCREMENT` on SQLite, identity on Postgres, so ids are never recycled |
| `Login` | string | **unique**, case-folded | The identity string the phone sends. **Mutable** — except while the user is config-declared, when rename is refused |
| `Password` | string? | | **Device → gateway.** `pbkdf2$…` or plaintext. Verified locally; never sent to a backend |
| `DefaultBackendLogin` | string? | | **Gateway → backends.** Default user name for every role. Unset ⇒ the gateway login |
| `DefaultBackendPassword` | string? | | Default secret for every role, `enc:v1:` sealed. Unset ⇒ the presented EAS password (pass-through) |
| `MailAddress` | string? | | From rewriting, Settings, meeting replies. Null ⇒ the login if it contains `@`. *Was inside the JSON blob* |
| `Admin` | bool? | | Grants `/admin` access |
| `Enabled` | bool? | | `false` = disabled; every login refused 403 after valid credentials. **Owns whole-user blocking** now that `LoginBlock` is device-only |
| `OidcSubject` | string? | ⓘ index? | IdP `sub` this user is bound to. ⓘ **inferred**: worth a unique index — two users bound to one subject is a takeover vector — but not stated |
| `AutoProvisioned` | bool? | | Provenance marker for gateway-created rows |
| `UpdatedUtc` | DateTime | | ⓘ **inferred** — `AccountEntries` has it and nothing says to drop it |

**What it is:** the registry of every user, whatever their origin — config-declared, DB-declared or
auto-provisioned on first successful auth. Past the auth boundary a row always exists, which is what
makes `UserId` safe to assume everywhere.

**Where used:** the resolver (renamed from `AccountResolver`) merges these with config into the
snapshot; the store writes them; `eas user`/`/admin/api/users` edit them; auth reads `Password`,
`Enabled` and `OidcSubject`.

**Links:** parent of everything in the graph above. No outbound links.

**Gone from here:** the `Json` column. Its scalars are the columns above; its per-role `Backends`
dictionary is the table below.

## `UserBackendRoles`  *(new)*

| Column | Type | Key / index | Purpose |
|---|---|---|---|
| `UserId` | int | **PK (UserId, Role)**, **FK → `Users`, cascade** | Owning user |
| `Role` | string | ↑ | `MailStore`, `MailSubmit`, `Calendar`, `Tasks`, `Contacts`, `Notes`, `Oof` |
| `Enabled` | bool? | | `false` = turn this role off (content roles fall back to `local`, Oof off). Invalid on the two mail roles |
| `Provider` | string? | | Serve this role with a different provider than the global assignment |
| `UserName` | string? | | Backend login for this role. Unset ⇒ `Users.DefaultBackendLogin`, then the global section, then pass-through |
| `Password` | string? | | Backend secret, `enc:v1:` sealed. Same fallback chain |
| `SettingsJson` | string? | | **The one surviving blob** — provider-defined keys, see below |

**What it is:** one row per (user, role) override. Only rows that actually deviate need to exist —
a user with no overrides has none, and resolution falls straight through to the user defaults and
then the global role section.

**Where used:** credential and settings resolution when building a backend session.

**Links:** → `Users` (**FK, cascade**). ⓘ **inferred**: `Role` stored as a string rather than an int
enum, matching how role names already appear in config keys and the CLI; an int would be denser but
unreadable in the table.

## `LoginBlock`

| Column | Type | Key / index | Purpose |
|---|---|---|---|
| `DeviceKey` | int | **PK**, **FK → `Device`, cascade** | The blocked device. **Non-nullable** — the whole-user case moved to `Users.Enabled`. No surrogate `Id`: one block per device, enforced by identity |
| `CreatedUtc` | DateTime | | |

**What it is:** an operator cut-off for **one device**, enforced after successful authentication with
a 403. Also written automatically when an account-only wipe is acknowledged.

**Where used:** the EAS and Autodiscover endpoints post-auth; `eas block`/`unblock` (now
device-scoped); `CompleteAccountWipeAsync`.

**Links:** → `Device` (**FK, cascade**), and through it to `Users`.

**No `UserId` here — settled.** A device belongs to exactly one user, so the column would be
derivable, and this is exactly the case the rule forbids: *do not add a `UserId` to a table that
already reaches a user through a FK.* The only argument for it was query convenience, and that turns
out to be no argument at all — "all blocks for this user" is a join either way, `Users → LoginBlock`
versus `Users → Device → LoginBlock`. `db-restructure.md` was corrected to match.

## `SharedCalendarGrant`

| Column | Type | Key / index | Purpose |
|---|---|---|---|
| `UserId` | int | **PK (UserId, CollectionHref)**, **FK → `Users`, cascade** | Grantee |
| `CollectionHref` | string | ↑ | The extra CalDAV collection href |
| `ReadOnly` | bool | | Enforced gateway-side via silent revert |
| `CreatedUtc` | DateTime | | |

**What it is:** `eas share` — one extra CalDAV collection exposed to one user as an additional
calendar folder. Unions with the config `SharedCollections` list; DB wins per href.

**Where used:** the share admin service; the session factory loads grants once per session build, so
changes apply on session recycle.

**Links:** → `Users` (**FK, cascade**). The href is matched leniently against DAV collections, not
against any local table.

## `WebSessionRevocation`

| Column | Type | Key / index | Purpose |
|---|---|---|---|
| `UserId` | int | **PK**, **FK → `Users`, cascade** | One row per user — now enforced by identity, not by a separate unique index |
| `ValidAfterUtc` | DateTime | | Any web session *started* before this is refused at next revalidation |

**What it is:** the server-side half of web logout — the auth cookie is a self-contained ticket, so
this row is what actually invalidates copies of it. Rewritten, never appended.

**Where used:** the WebUi session-validation hook.

**Links:** → `Users` (**FK, cascade**).

## `OofSetting`

| Column | Type | Key / index | Purpose |
|---|---|---|---|
| `UserId` | int | **PK**, **FK → `Users`, cascade** | Owner — one Oof row per user, enforced by identity |
| `State` | int | | 0 = disabled, 1 = enabled, 2 = scheduled |
| `StartUtc` / `EndUtc` | DateTime? | | Scheduled window |
| `Message` | string | | Reply body — deliberately plaintext |
| `BodyType` | string | | "Text" or "HTML" |
| `PreviousActiveScript` | string? | | Sieve script active before the gateway took over |
| `UpdatedUtc` | DateTime | | |

**What it is:** the source of truth for Settings→Oof Get. The sieve script on the mail server is
derived output, never parsed back.

**Links:** → `Users` (**FK, cascade**). Still **not** concurrency-token stamped — finding `A3`.

---

# Sync state

## `Device`

Unchanged except for identity: `UserName` string → **`UserId` FK, cascade**; unique index becomes
**(`UserId`, `DeviceId`)**. Everything else — `DeviceType`, `PolicyKey`, `PolicyDocHash`,
`RecoveryPasswordProtected`, `PendingAccountWipe`, `LastProtocolVersion`, `FolderSyncKey`,
`DeviceInfoJson`, `PingParamsJson`, `LastSyncRequestJson`, `CreatedUtc`, `LastSeenUtc`,
`ConcurrencyToken` — is as [`current-db.md`](current-db.md) describes.

**Links:** → `Users` (**FK, cascade**); ← `DeviceFolder`, `CollectionState`, `SentCommandToken`,
`LoginBlock` (all **FK, cascade**).

## `UserFolder`

`UserName` string → **`UserId` FK, cascade**; unique index becomes **(`UserId`, `BackendKey`)**.
Columns otherwise unchanged (`BackendKey`, `DisplayName`, `ParentBackendKey`, `Type`, `EasClass`,
`Deleted`, `DeletedUtc`), including the computed `ServerId => Id.ToString()`.

**Links:** → `Users` (**FK, cascade**); ← `DavItem` (**FK, cascade**); self-link
`ParentBackendKey` → `BackendKey` (still soft); ← the three tables holding `Id` as a string.

## `DeviceFolder` and `CollectionState`

Columns unchanged, and both keep reaching a user transitively through `DeviceKey` — they **must not
gain a `UserId`**. The one change is that each **drops its surrogate `Id`** and promotes what was
already its unique index to the primary key:

- `DeviceFolder` → **PK (`DeviceKey`, `ServerId`)**
- `CollectionState` → **PK (`DeviceKey`, `CollectionId`)**

## `DavItem` — unchanged

Keeps its surrogate `Id`, and that is deliberate: `item.Id.ToString()` is the sub-part of every DAV
item ServerId on the wire (`DavItemMap.cs:55`). Unique index on (`UserFolderKey`, `Href`) stays.

## `SentCommandToken`

Columns unchanged (`Id`, `DeviceKey`, `CollectionId`, `SyncKeyAtClaim`, `Key`, `CreatedUtc`,
`Completed`) and the unique index stays **(`DeviceKey`, `CollectionId`, `SyncKeyAtClaim`, `Key`)**.
The change is that `DeviceKey` becomes a **real FK to `Device` with cascade**, closing today's
orphan-on-device-delete gap.

**This is the one table that deliberately keeps a surrogate `Id`** despite having a natural key. The
key is four columns wide including a variable-length string, and this is the hot write path — a claim
is inserted before every irreversible send. The payoff for promoting it is the smallest of any table
here and the churn is the highest. ⓘ **judgement, not a decision** — flip it if the implementer
disagrees.

---

# Local user data

## `LocalItem`

| Column | Type | Key / index | Purpose |
|---|---|---|---|
| `Id` | int identity | **PK** | Item key / revision anchor |
| `UserId` | int | index (UserId, Collection) — not unique, **FK → `Users`, cascade** | Owner |
| `Collection` | string | ↑ | `contacts`, `calendar`, `notes` |
| `Uid` | string | | Item UID |
| `Content` | string | | **AES-256-GCM ciphertext**, `"v1:" + base64`. **AAD now binds `UserId`, not the login** |
| `Version` | int | | Monotonic per-item revision |
| `ItemDateUtc` | DateTime? | | Event start for EAS filter windows; deliberately plaintext |
| `LastModifiedUtc` | DateTime | | |
| `ConcurrencyToken` | Guid | concurrency token | |

**The AAD change is the single most consequential line in this document.** Binding to `UserId`
instead of the login is what makes a rename survivable — and getting it wrong is only discovered when
data will not decrypt. `db-restructure.md` suggests a versioned, length-prefixed framing
(`"v2" ‖ LE64(userId) ‖ LE32(len) ‖ collection`) to keep `K2`'s injectivity without relying on
control-character rejection.

**This is the table that makes deletion dangerous** — it is real user content, existing nowhere else
in a local-stores deployment, and it cascades.

---

# Settings & operations

## `DataChanges`  *(replaces `AccountsStamps` + `SettingsStamps`)*

| Column | Type | Key / index | Purpose |
|---|---|---|---|
| `Key` | string | **PK** | Watched area: `"users"`, `"settings"` |
| `Version` | Guid | | Bumped in the same `SaveChanges` as the mutation |
| `UpdatedUtc` | DateTime | | ⓘ **inferred** — mirrors `GlobalSetting` |

**What it is:** the change signal each replica point-reads (~1 s) to notice edits without re-reading
everything. **One row per watched area, never one row total** — a shared version would make a user
write invalidate the settings snapshot and vice versa.

**A stamp belongs to a consumer's aggregate, not to a table:** `UserBackendRoles` writes bump
`"users"`, because the resolver rebuilds the whole snapshot anyway.

**Where used:** the resolver's refresh gate (`"users"`); the settings refresher (`"settings"`).

**Links:** none — a bare signal. Adding a watched area later is an inserted row, not a migration.

## `GlobalSetting`

Columns unchanged (`Key`, `Value`, `UpdatedUtc`), but **drops its surrogate `Id`**: `Key` was already
unique and becomes the primary key.

## `LogEntry`, `ServerCertificate`, `DataProtectionKeys` — unchanged

As [`current-db.md`](current-db.md) describes, surrogate keys included — and in these three that is
correct, not an oversight. A log line has **no** natural key (timestamp plus message is not unique),
`ServerCertificate` keeps its explicit single-row `Id = 1` idiom, and a DataProtection key-ring entry
is identified by content the gateway does not parse. Two notes:

- **`LogEntry.User` stays a login string, not a `UserId`.** ⓘ **inferred** — not stated in
  `db-restructure.md`. Rationale: a log line is an audit record of what was true at the time, so it
  should not silently change meaning when someone renames a login, and it must survive the user row
  being deleted (which an FK with cascade would prevent). Carrying `UserId` *as well*, for joining,
  would be reasonable.
- `ServerCertificate` keeps its explicit `Id = 1` and its concurrency token.

---

# Inside the serialized columns

Far less is serialized than today — the `AccountOptions` blob is gone entirely.

## `UserBackendRoles.SettingsJson` → `Dictionary<string, string?>`

The **only** remaining user-related blob. Flat provider-defined configuration keys overlaid on the
global role section. Kept serialized on purpose: the keys are declared at runtime by
`IBackendProvider.DescribeConfiguration` → `BackendConfigField`, and the host deliberately never knows
a plugin provider's option shape.

Two semantics must survive resolution across all five levels:

- **List replacement** — setting any element (`X:0`) REPLACES the whole inherited list `X`.
- **Null clears** — a null value removes the inherited key rather than falling through.

## Unchanged from today

| Column | Contents |
|---|---|
| `Device.DeviceInfoJson` | `Dictionary<string,string>` — the client's Settings→DeviceInformation `Set`, no fixed schema |
| `Device.PingParamsJson` | `{ HeartbeatSeconds, FolderIds[] }` |
| `Device.LastSyncRequestJson` | `{ WaitSeconds?, GlobalWindowSize, Collections[{ CollectionId, GetChanges, WindowSize? }] }` |
| `CollectionState.SnapshotCompressed` / `PreviousSnapshotCompressed` | gzipped `{ itemServerId → revision }` |
| `CollectionState.LastClientAddsJson` | `{ ClientId → { ItemKey?, Revision? } }` |
| `CollectionState.LastClientChangesJson` | `{ ServerId (or ServerId\nInstanceId) → { ItemKey?, Revision? } }`; null `Revision` = removed |
| `CollectionState.OptionsJson` | `{ FilterType, BodyType, TruncationSize?, MimeSupport, Conflict }` |
| `LocalItem.Content` | AES-GCM ciphertext — **AAD changes to bind `UserId`** |
| `ServerCertificate.PfxProtected` | sealed base64 PKCS#12 |
| `DataProtectionKeys.Xml` | sealed key XML |

---

## What to check when this is implemented

The lines most likely to have drifted from this projection:

1. **Everything marked ⓘ** — `UpdatedUtc` on `Users`, the `OidcSubject` index, `Role` as string,
   `LogEntry.User`, `DataChanges.UpdatedUtc`.
2. ~~`LoginBlock`'s shape~~ — settled: `DeviceKey` alone, no `UserId`. Both documents agree.
3. **Whether `CollectionId`-as-string survived** — kept here because `ServerId` is an EAS wire value,
   but it is the last soft link that could mis-scope rows.
4. **The AAD framing** — the exact byte layout is a suggestion in `db-restructure.md`, not a decision.
5. **Whether any table gained a convenience `UserId`** it should not have. The rule is: if it already
   reaches a user through a FK, it does not get one.

# Account model — surrogate identity and per-field resolution

> **Status: proposed, not implemented.** Nothing here describes how the gateway works today. It is a
> design brief to be executed on the **schema reinit** (see Standing context). Do not cite it as
> documentation of current behaviour; `AGENTS.md`, `README.md` and `docs/webui.md` are the authority
> on what exists now.

**Who this is for:** an agent or contributor implementing this in a fresh session, with no knowledge
of the conversation that produced it. Everything needed to execute is here or named here.

**Read first, in this order:** `AGENTS.md` (§ *Auth model*, § *State store*, § *Backend layer notes*,
§ *Web UI layer notes*), `docs/webui.md`, `docs/configuration.md`, then the code under
"Where this lives today". If any of those contradicts this document, **stop and report** — one of the
two is wrong and that is a human decision.

---

## Standing context — the enabling constraint

Gated on a clean-slate **schema reinit** the project owner has confirmed and planned:

- The repository is squashed / reinitialised.
- **Every existing deployment breaks.** In-place upgrade is not supported. This is accepted,
  deliberate, and the reason this design is affordable.

### Database: no upgrade path, but migrations still exist

Read this precisely, because the two halves are easy to conflate:

| ✅ Required | ❌ Never write |
|---|---|
| A **fresh `Initial` migration per provider**, generated from the final model | Any migration **from an existing schema version** |
| It must apply cleanly to a **blank database** — that is the only case that has to work | `AddColumn`/`RenameColumn`/`DropColumn` sequences that walk an old shape to the new one |
| Both providers in lockstep (`Migrations/Sqlite` **and** `Migrations/Npgsql`) | Data movement: backfills, `migrationBuilder.Sql(...)`, provenance inference, "treat unflagged rows as X" |
| | A `LegacyAccountJson`-style converter for old persisted shapes |

**Delete the entire existing migration chain** — both `src/ActiveSync.Core/Migrations/Sqlite/` and
`src/ActiveSync.Core/Migrations/Npgsql/` (~43 files each, accumulated across rounds 1–2) plus their
model snapshots — and replace it with one `Initial` per provider. There is no data anywhere that
needs carrying forward, so **no upgrade script needs to be written for any part of this refactor.**

**Migrations do not disappear, and must not.** Production applies them at startup
(`WebApplicationExtensions.cs:50`, `MigrateAsync`) and the integration suite boots the real host, so
a missing or non-applying migration means the live suite cannot create a schema and every
integration test fails. The contract for the whole refactor is simply: **blank DB → `Initial` →
working schema.**

**While the refactor is in progress, regenerate rather than chain.** `AGENTS.md` § *State store* says
to add a migration for both contexts whenever an entity changes. That rule is **suspended for this
work only**: as the model evolves across items 2–4, delete and re-generate the single `Initial` pair
rather than stacking incremental migrations on top of a shape nobody will ever deploy. Normal
per-change migration discipline resumes for any change made **after** the reinit ships.

| Licensed | NOT licensed |
|---|---|
| Changing the persisted shape of accounts freely | Skipping tests because "it's a rewrite" |
| Renaming config keys and CLI verbs | Leaving the CLI/admin/portal write paths inconsistent |
| Deleting `LegacyAccountJson` and its upgrade path | Changing the plugin contract without a `ContractVersion` bump |
| Dropping back-compat shims and data migrations | Re-introducing whole-entry replacement (below) |

**There is no data to migrate** — see "Database: no upgrade path" above for what that does and does
not license.

---

## Decisions (settled — do not re-open)

Recorded so a fresh session executes rather than re-litigates.

| # | Decision |
|---|---|
| 1 | **`AccountId` is an `int` identity column.** Not a GUID. Immutable, never reused. |
| 2 | **The login is a mutable attribute**, unique and case-folded — **except while the account is config-declared**, when it is immutable so config↔DB matching by login can never drift. Everything else is mutable. |
| 3 | **One stored value per field per account.** Admin and holder write the **same** slot; the difference between them is *permission*, not storage. |
| 4 | **Resolution is per field**, following the one rule below. Whole-entry replacement is deleted. |
| 5 | **`Settings` resolves per key**, by the same rule. |
| 6 | **`AutoProvisionUsers` → `AutoProvisionAccounts`**, default **true**; always creates an account on first successful auth; **false** refuses the undeclared login before any backend probe. |
| 7 | **`RequireDeclaredUsers` is deleted** — `AutoProvisionAccounts=false` now means exactly that. |
| 8 | **`eas user` → `eas account`, with no alias.** No legacy surface anywhere; this is a clean refresh. |
| 9 | **An admin may read and overwrite a holder's values, and a holder may overwrite an admin's** — same slot, last write wins. |
| 10 | **An account carries two passwords**: `Password` (device → gateway, verified locally) and `DefaultBackendPassword` (gateway → backends), plus `DefaultBackendLogin`. Different trust domains; keep the chains separate. |
| 11 | **Unset defaults mean today's behaviour** — `DefaultBackendPassword` unset ⇒ forward the presented EAS password; `DefaultBackendLogin` unset ⇒ the gateway login. Zero-administration pass-through must survive. |
| 12 | **Per-device credentials are deferred**, not rejected — see "What this deliberately does NOT do". |
| 13 | **The `AccountOptions` JSON blob is normalised away.** Scalars become columns on `Accounts`; per-role `Enabled`/`Provider`/`UserName`/`Password` become columns on a new **`AccountBackendRoles`** child table. |
| 14 | **Only `Settings` stays serialized** (`AccountBackendRoles.SettingsJson`) — its keys are provider-defined and discoverable only at runtime, which is the provider model working as intended. |
| 15 | **Every account-linked table gets a real FK to `AccountId` with cascade delete**, and `SentCommandToken` gets the FK to `Device` it is missing today. |
| 16 | **Deleting an account must not silently destroy content.** The DB cascades; the application counts what would be lost and demands confirmation naming it. |
| 17 | **Config-declared accounts get rows at startup** (identity from the row, values from config). |

> **The shape is still settling.** Decisions 1–9 have been through a full round of review; 10–17 are
> newer and the owner expects them to move. Treat the *direction* as agreed and the field-level detail
> as provisional — but do not silently re-open 1–9 while adjusting the rest.

### Open — needs a decision before Phase A completes

1. **`LoginBlock.DeviceId`: non-nullable FK (recommended) or nullable?** Non-nullable removes the
   whole-account case from `LoginBlock` and leaves `Enabled = false` owning it — one mechanism per
   concept instead of two overlapping ones. Nullable preserves today's "block the whole user" shape.
2. ~~Config identity~~ — **settled**: no config-side identifier; match by login, and make the login
   immutable per account while it is config-declared. See "Config-declared accounts need rows too".
3. **Delete guard: confirm-and-cascade (as written) or refuse-until-purged?** The latter makes
   accidental content loss structurally impossible rather than confirmation-dependent.
4. **Terminology: `Account` or `User`?** This document uses **Account** throughout. A rename to
   `User`/`Users` was raised — it reads more naturally to operators and keeps `eas user` working
   unchanged — then apparently reversed. Settle it before item 1, because it is the one decision that
   touches every file.

---

## The problems this fixes

### 1. The login is physically the primary key, so nothing about an account can change

Eight entities carry the login as a string — `Device`, `UserFolder`, `LocalItem`, `LoginBlock`,
`WebSessionRevocation`, `SharedCalendarGrant`, `AccountEntry`, `OofSetting`
(`src/ActiveSync.Core/State/Entities.cs`) — and so do the `LocalChangeNotifier` keys, the
session/watcher cache keys, and the **encryption AAD**: `LocalContentProtector.Aad` is
`userName + "\n" + collection` (`src/ActiveSync.Core/Security/LocalContentProtector.cs:126`).

Hence the constraint `README.md` states plainly: *keep logins stable or devices re-sync from scratch
and locally stored items become unreadable*. **A login cannot be renamed** — not "should not":
renaming orphans every sync-state row and permanently bricks every encrypted local item.

### 2. A DB account row REPLACES the whole config entry

`AccountResolver.cs:320-341`. Set one field in the database and every config-set field for that
account is silently discarded. Operators reasonably expect an override to be a *deviation*, not a
wholesale substitution.

### 3. The vocabulary is ambiguous

"Users" (the `ActiveSync:Users` config section, `UsersFile`), "accounts" (`AccountEntry`, `eas user`,
the admin *Users* page), "declared" vs "synced" vs "auto-provisioned", `MergedAccount`,
`ResolvedAccount`, `AccountTemplate`, `AccountOptions`, `BackendRoleOverride` — some are real
distinctions, some are the same thing named twice.

**Settled vocabulary:**

| Term | Means |
|---|---|
| **Account** | The persistent record for one gateway login. The single word for the thing |
| **Login** | The identity string the phone sends — now a mutable attribute |
| **`AccountId`** | The immutable surrogate key. THE identity |
| **Effective** | The result of resolving all levels for a field |
| **Resolved\*** | The compiled per-request view (`ResolvedAccount`/`ResolvedRole`) — keep these names |

Retire "user" for the persistent record; keep it for the human.

---

## The design

### Identity: `AccountId`

```
Account
  AccountId   int identity   // THE identity — immutable, NEVER reused
  Login       string         // unique (case-folded), MUTABLE
  ...everything else, all mutable
```

Those eight entities FK to `AccountId` instead of carrying the login. Notifier keys, cache keys and
the encryption AAD key on `AccountId`. **A rename then becomes a single-row update**: sync state
survives, encrypted local items stay readable, the device keeps its folder registry and sync keys,
and the holder just updates the username on the phone.

**Only those eight need the new column — the rest come along for free.** Of the 18 tables
(`SyncDbContext.cs:14-31`), `DeviceFolder`, `CollectionState` and `SentCommandToken` scope
transitively through `DeviceKey`, and `DavItem` through `UserFolderKey`; the remaining six
(`AccountsStamp`, `SettingsStamp`, `GlobalSetting`, `LogEntry`, `ServerCertificate`,
`DataProtectionKeyEntry`) are global and not per-user at all. Verified against the model — do not add
an `AccountId` to a table that already reaches an account through a FK, or the two paths can disagree
after a rename and there is no constraint that would catch it.

Five things to get right:

1. **Never reuse an id.** Security-critical, not hygiene: if account 42 is deleted and a new account
   becomes 42, any surviving encrypted `LocalItem` decrypts under a *different person's* AAD.
   **Verified safe on both providers, but only because of an annotation that must not be dropped:**
   EF emits `Sqlite:Autoincrement` for int identity keys, and SQLite guarantees monotonic non-reuse
   only with `AUTOINCREMENT` (plain `INTEGER PRIMARY KEY` rowids **are** recycled after deleting the
   highest row). Postgres identity sequences do not recycle. Assert this in a schema test rather than
   trusting it to survive a future model tweak.
2. **The AAD change is the point and the biggest risk.** `Aad(userName, collection)` becomes
   `Aad(accountId, collection)`. Keep `K2`'s injectivity — the parts must not be ambiguously
   concatenable — via explicit length-prefixing or an unambiguous separator that cannot occur in
   either part.
3. **The id does not exist until the row is persisted.** An identity column is assigned by the
   INSERT, so provisioning must be *insert account → obtain id → write anything that references it*.
   `AutoProvisionAccounts` (below) makes this ordering natural, because provisioning happens at auth,
   before any sync-state row exists.
4. **First-auth is a race.** Two devices authenticating a brand-new login concurrently will both try
   to insert. Use the pattern this codebase already applies in `DeviceStore`, `DavItemMap` and
   `GatewayCertificateStore`: catch the unique violation on `Login` and re-read the winner. Never
   assume the insert succeeds.
5. **The login still needs case-folded uniqueness.** `B1`'s `NormalizeLogin` (`ToLowerInvariant` on
   write) and the unique index stay — they move from "the primary key" to "a unique attribute".
6. **A rename must invalidate every cache keyed by login** — `BackendSessionFactory`'s session cache,
   the auth verdict caches, watchers — exactly as `SnapshotChanged` already does for account edits.

### Account shape — two passwords, and explicit backend defaults

**The `AccountOptions` JSON blob is gone.** Everything with a compile-time-known shape becomes a real
column; only the genuinely runtime-discovered part stays serialized. Two tables:

### `Accounts`

| Column | Type | Notes |
|---|---|---|
| `AccountId` | int identity | **PK.** THE identity — immutable, never reused |
| `Login` | string | **unique, case-folded.** MUTABLE |
| `Password` | string? | **Device → gateway.** `pbkdf2$…` (preferred) or plaintext. Verified LOCALLY, never sent anywhere |
| `DefaultBackendLogin` | string? | **Gateway → backends**: default user name for every role |
| `DefaultBackendPassword` | string? | Default secret for every role, `enc:v1:` sealed |
| `MailAddress` | string? | From rewriting, Settings, meeting replies. Null ⇒ the login if it contains `@`. *(Extracted from the blob — this is why it appears nowhere in today's schema.)* |
| `Admin` | bool? | Grants `/admin` access |
| `Enabled` | bool? | `false` = disabled; every login refused 403 after valid credentials |
| `OidcSubject` | string? | IdP `sub` this account is bound to |
| `AutoProvisioned` | bool? | Provenance marker for gateway-created rows |

### `AccountBackendRoles`

One row per (account, role). Child of `Accounts`, **FK with cascade delete**.

| Column | Type | Notes |
|---|---|---|
| `Id` | int identity | **PK** |
| `AccountId` | int | **FK → `Accounts.AccountId`, cascade.** Unique with `Role` |
| `Role` | string | `MailStore`, `MailSubmit`, `Calendar`, `Contacts`, `Tasks`, `Notes`, `Oof` |
| `Enabled` | bool? | `false` = turn this role off (content roles fall back to `local`, Oof off). Invalid on the two mail roles |
| `Provider` | string? | Serve this role with a different provider than the global assignment |
| `UserName` | string? | Backend login for this role |
| `Password` | string? | Backend secret for this role, `enc:v1:` sealed |
| `SettingsJson` | string? | **The one surviving blob** — see below |

**Why the line falls there.** `Enabled`/`Provider`/`UserName`/`Password` have a fixed,
compile-time-known shape, and the credentials among them are exactly what the resolution rule spends
its time resolving — so they earn columns, and "which accounts override MailStore?" becomes a query
rather than a full-table deserialize. `Settings` cannot: its keys are **provider-defined and only
discoverable at runtime** (`IBackendProvider.DescribeConfiguration` → `BackendConfigField`), and the
host deliberately never knows a plugin provider's option shape. That is the whole point of the
provider model, so `Settings` stays serialized — with its existing semantics intact: setting a list
element (`X:0`) REPLACES the inherited list, and a null value CLEARS the inherited key rather than
falling through.

**Effort note, since this looks bigger than it is.** `AccountOptions` does **not** disappear — it
remains the in-memory and config-bound shape (`ActiveSync:Accounts` binds to it through
`IConfiguration`). Only *persistence* changes, and today the blob is confined to `AccountStore`
(`TryDeserialize` and `UpsertAsync`); nothing else in the codebase reads into it, because it is
opaque. So `AccountResolver`, `AccountEditing`, `AccountFieldPaths`, `AccountSecretPolicy`, the CLI,
the admin API, the portal and the banner all keep operating on `AccountOptions` objects unchanged.
The work is: entity + child table, a mapping in `AccountStore` each way, regenerated migration, tests.

**What this buys beyond tidiness:** `WHERE Admin = true` / `WHERE Enabled = false` /
`WHERE OidcSubject = ?` become real queries (today `AccountResolver` loads every row and deserializes
to answer any of them); a malformed scalar becomes impossible, narrowing the corrupt-row guard
(`B15`) to `SettingsJson` alone; and secrets become individually selectable rather than arriving
inside a blob you must hold whole (relevant to `B5`).

**An account carries two distinct credentials, and conflating them is the current model's weak
point.** `Password` authenticates the *device to the gateway*; `DefaultBackendLogin`/
`DefaultBackendPassword` authenticate the *gateway to the backends*. They answer different questions
and belong to different trust domains.

**What this replaces.** Today there is no default-credential field — there is an implicit *rule*:
an unset role password inherits the presented EAS password, and non-MailStore roles inherit the
effective MailStore pair. So the MailStore role does double duty: "the mail backend" **and** "the
template every other role copies from". That works only while the EAS password *is* the mail
password. The moment the device credential is decoupled — a gateway password, an app password, or
anything an external IdP governs — pass-through has nothing left to forward, and the operator must
set credentials per role or lean on MailStore-as-implicit-default. Explicit defaults collapse that to
one pair, and let MailStore go back to being just another role.

**Unset must mean today's behaviour — this is not optional.** The zero-administration baseline is the
project's core value proposition (`README.md`: *"The baseline needs zero user administration"*), and
it must survive:

| Field | Unset ⇒ |
|---|---|
| `DefaultBackendLogin` | the gateway login (as today) |
| `DefaultBackendPassword` | the **presented EAS password** — i.e. pass-through, unchanged |

So an undeclared, unconfigured account behaves exactly as it does now, and a decoupled account is one
field instead of N.

### THE RESOLUTION RULE — one order, every setting, per field

The load-bearing rule of this design, and **not specific to accounts**: it is the order *every*
overridable setting follows. Implement it once; make every new setting conform rather than inventing
its own precedence.

**Most specific wins. Resolution is PER FIELD, never per entry.**

| # | Level | Written by | Stored in |
|---|---|---|---|
| 1 | **Account · DB** | the admin (`eas account set`, admin API) **or** the holder (portal) — same slot | `AccountEntry` |
| 2 | **Account · config** | operator, in `ActiveSync:Accounts:<login>:…` | appsettings / env / accounts file |
| 3 | **Global · DB** | admin (`eas config set`, admin UI) | `GlobalSetting` rows |
| 4 | **Global · config** | operator | appsettings / env |
| 5 | **Code default** | — | the options POCO |

```
account (DB)  →  account (config)  →  global (DB)  →  global (config)  →  code default
```

A setting that **cannot** be set per account simply has no levels 1–2; the rule is unchanged and
resolution starts at level 3:

```
global (DB)  →  global (config)  →  code default
```

Levels 3–5 already behave this way (`DbSettingsConfigurationProvider` is layered last in
`IConfiguration`, so DB beats appsettings/env, which beat POCO defaults — `AGENTS.md` § *Backend
layer notes*). **This design adds levels 1–2 above them and requires per-field composition.**

Worked example — the SMTP host:

1. `appsettings.json` sets `Backends:MailSubmit:Host = smtp.example.com` → every account uses it.
2. `eas config set ActiveSync:Backends:MailSubmit:Host smtp2.example.com` → the **global DB** value
   wins for every account, live, no restart.
3. An admin sets it for one account → that account uses their value; everyone else still gets
   `smtp2`.
4. The holder overrides it in the portal (if permitted for that field) → it lands in the **same
   slot**, so it simply replaces the admin's value; clearing it falls back to level 2, then 3.

**Null/absent means "not set at this level", not "cleared."** A gap falls through. Clearing an
account value reverts to config, then global — an override is a *deviation*, and removing the
deviation restores the inherited value. Keep this distinct from the existing explicit-clear
semantics on `Settings` (a null value CLEARS the inherited key rather than falling through); carry
that forward deliberately, not by accident.

### Credentials resolve by the same rule, with one extra scope tier

Backend credentials are not a special case — they add a level of **scope** (this role vs every role)
between the account and global tiers. The rule is unchanged; there is simply one more step:

```
account · role · DB      →  account · role · config
  →  account · default · DB  →  account · default · config
    →  global role section (DB → config)
      →  pass-through (presented EAS credential / gateway login)
```

Read it as "most specific wins" on two axes at once — **which role** and **which source** — with role
beating default, and DB beating config within each. The terminal step is pass-through, which is why
an account with nothing set still works.

The gateway `Password` is **not** in this chain. It never resolves against anything global, is never
sent to a backend, and is verified locally — keep the two chains visibly separate in the code, or
someone will eventually "simplify" a device credential into a backend one.

### `Settings` resolves per key

The per-role `Settings` dictionary follows the same rule, resolved **per key**, not as a whole
dictionary. This is the fiddliest part of the implementation, because two existing semantics must
survive being pushed through five levels instead of two:

- **List replacement:** setting any list element (`X:0`) REPLACES the whole inherited list `X`.
- **Null clears:** a null value removes the inherited subtree that key names.

### Who may write what — permission, not storage

There is **one slot per field**. Admin and holder write the same value; last write wins, in either
direction. The distinction between them is *which fields each may write*:

| Field class | Holder (portal) | Admin (CLI / admin API) |
|---|---|---|
| `Enabled`, `Provider` (serving topology) | ✗ | ✓ |
| Backend credentials (`UserName`, `Password`) | ✓ | ✓ |
| `Settings` keys the provider schema marks `SelfServiceEditable` | ✓ | ✓ |
| All other `Settings` keys | ✗ | ✓ |
| Account-level (`Admin`, `Enabled`, `OidcSubject`, `AutoProvisioned`) | ✗ | ✓ |
| Gateway `Password` | ✓ (with current-password check) | ✓ |

**How an admin pins a value the holder must not change: mark it not `SelfServiceEditable`.** That is
the only lock, and it is the mechanism that already exists. Setting a value in *config* does **not**
lock it — level 1 beats level 2, so a permitted holder still overrides it. Anyone who wants a
guaranteed value must remove it from the self-service surface, not hide it in a lower level.

### Real foreign keys and cascade delete

Today only three relationships are declared FKs (`DeviceFolder`/`CollectionState` → `Device`,
`DavItem` → `UserFolder`); everything else is a string matched by value. See
[`current-db.md`](current-db.md) for the full as-built graph. With `AccountId` in place, the soft
links become real:

| Table | Today | Target |
|---|---|---|
| `LocalItem` | `UserName` string | **`AccountId` FK, cascade** |
| `UserFolder` | `UserName` string | **`AccountId` FK, cascade** |
| `Device` | `UserName` string | **`AccountId` FK, cascade** |
| `LoginBlock` | `UserName` + nullable `DeviceId` **string** | **`AccountId` FK + `DeviceKey` FK → `Device.Id`, both cascade** |
| `SharedCalendarGrant`, `OofSetting`, `WebSessionRevocation` | `UserName` string | **`AccountId` FK, cascade** |
| `SentCommandToken` | `DeviceKey` int, **no FK** | **real FK → `Device.Id`, cascade** — closes the orphan-on-device-delete gap |
| `AccountBackendRole` | *(new)* | **`AccountId` FK, cascade** |

**`LoginBlock.DeviceId` should become a NON-nullable FK, and that is a simplification, not a
restriction.** Today a null `DeviceId` means "block the whole account", which duplicates
`Enabled = false` — two mechanisms for one concept, and `AGENTS.md` already has to explain the
difference ("the persistent-property counterpart to the ad-hoc `LoginBlock`"). Making the FK
mandatory forces the whole-account case out of `LoginBlock` entirely, leaving one mechanism each:

- **`Accounts.Enabled = false`** — the account is off. Every device, EAS and web.
- **`LoginBlock(AccountId, DeviceKey)`** — this one device is cut off. Nothing else changes.

*(This is my recommendation, not a settled decision — see Open questions.)*

### Deleting an account must not silently destroy data

Cascade delete is right, but it makes account deletion destructive in a way it currently is not:
**`LocalItem` is real user content** — contacts, calendar, tasks and always notes — not sync metadata.
Cascading it means deleting an account irreversibly deletes the user's PIM data, and for a
local-stores deployment that data exists **nowhere else**.

So the *database* cascades, and the *application* refuses to issue the delete blind:

1. Before deleting, count what will go with it — `LocalItem` by collection above all, plus devices,
   folders, shares and blocks.
2. If anything holds real content, **require an explicit confirmation naming what is lost**:
   "this permanently deletes 342 contacts, 89 events, 12 notes — they exist nowhere else."
3. Sync state alone (devices, folders, collection state) is *not* worth prompting over — it rebuilds
   on the next sync. Only content is.

**The CLI is not the hard part.** The precedent already exists on both surfaces: the web API demands
a typed confirmation echo for destructive device operations (`DevicesEndpoints.cs:85` — *"confirm
must echo the exact device id"*), and the CLI has its own confirmation flow for the same operations.
Reuse both. Add a `--yes`/`--force` escape for scripting, and make the *default* interactive path
refuse rather than proceed.

**A cheaper structural option worth weighing:** refuse to delete an account that still owns
`LocalItem` rows at all, and require an explicit purge first (`eas account purge-data`, then
`eas account remove`). That makes accidental content loss impossible rather than
confirmation-dependent — the prompt becomes an error you cannot click through. Costs one extra step
in the rare legitimate case.

### `AutoProvisionAccounts` — an account always exists past auth

- **`true` (default)** — a login that authenticates and has no account gets one created immediately,
  with a fresh `AccountId`. No exceptions, no deferral.
- **`false`** — an undeclared login is **refused**, before any backend probe.

**This makes `AccountId` total**, which is the real prize: past the auth boundary every request,
handler, store, notifier and cache can assume an account exists and its id is known. No nullable
account threaded through the call graph, no "not provisioned yet" branch, no ordering hazard where
sync state is written before the row that owns it. **Do not re-introduce a lazy or deferred
provisioning path** — one deferred case costs the totality everywhere.

`RequireDeclaredUsers` is deleted: `AutoProvisionAccounts=false` means exactly what it meant. Keep
the property worth keeping — the refusal happens **before** the backend probe, so undeclared logins
never reach the mail server. That is a brute-force shield, not just policy, and today's
`PassThroughProvisioner` runs *after* a successful probe, so the refusal must move earlier rather
than flip a branch where provisioning currently sits.

### Config-declared accounts need rows too

Once every table FKs to `AccountId`, a config-declared account cannot stay config-only — sync state
has nothing to point at. So **on startup, every account declared in `ActiveSync:Accounts` (or the
accounts file) gets a row**: `AccountId` + `Login`, everything else null. Config keeps supplying the
*values*; the row supplies the *identity*.

**Matching is always by login. There is no config-side identifier** — no `Id` to hand-manage, no
`ConfigKey` column, no new concept. That works because of one rule:

> **The login is immutable for as long as the account is declared in configuration, and freely
> renameable when it is not.**

Evaluated **per account**, not globally: an account currently declared in `ActiveSync:Accounts` (or
the accounts file) cannot be renamed through the CLI or admin UI; every other account can. A DB-only
account stays renameable even when other accounts come from config, and an account dropped from
config becomes renameable again.

This is what makes matching-by-login safe. The database side can never drift from configuration,
because the only mutable side is the one config doesn't own. `AccountResolver` already tracks whether
an entry is config-declared (`MergedAccount.FromDatabase` / `ShadowsConfig`), so the guard is a check
the code can already answer.

**Three guards complete it:**

1. **Refuse the rename** for a config-declared account, in both the CLI and the admin UI, naming
   where to change it instead: *"`anna@example.com` is declared in configuration — change it there."*
   The UI should not offer the action at all rather than fail after the fact.
2. **Reject a colliding rename.** The new login must not match any existing account, config-declared
   or not.
3. **Reconcile at startup and warn.** For every config-declared login with no matching account:
   *"configuration declares `anna@example.com` but no account has that login — was it renamed?"*
   Cheap, and it is the only thing that catches the residual below.

**What this deliberately does not cover, and cannot.** Renaming the *configuration key itself* still
creates a new account and strands the old row's data — the gateway cannot distinguish a rename from
a delete-plus-add in a file it does not own. Immutability closes the database side, which is where
the accident is far more likely (an admin clicking rename in a UI, unaware the account is
config-backed); the config side stays an operator responsibility, made visible by guard 3.

**A rename command is worth having regardless.** Making renames possible at all is the whole point of
the surrogate key, and it is the supported path for every account configuration does not declare.

---

## What this deliberately does NOT do

State this plainly so nobody "fixes" it later believing it an oversight.

- **It does not close `C5`** (portal echoes an admin-set backend username). `C5` is closed **`N/A`**
  in `docs/review/review-items.md` and stays closed. With one slot per field there is no provenance
  to gate a read on, and none is wanted: the holder can already change the value, and seeing it is
  what stops them changing it blind. Do **not** re-introduce a settings-surface gate — that was tried
  (review item 12, `7f0c73b`) and reverted (`fec1cfe`).
- **It accepts that a holder can overwrite an admin-set value** for any field they are permitted to
  write, destroying the previous value. That is the intended semantics of "it is their account". The
  admin's remedy is to set it again, or to remove the field from the self-service surface.
- **It does not add per-field provenance or an audit trail.** "Who set this?" is not answerable from
  the stored value. If that is ever wanted, it is a separate design — log-based, most likely, since
  logs already record the actor.
- **It does not add per-device credentials (considered, deferred).** The idea: N app-password-style
  credentials per account instead of one, each revocable independently — Google's model, and a
  natural fit because every EAS request carries a DeviceId. Deferred for one decisive reason and
  three supporting ones:
  - **The users who would benefit do not use the portal.** Self-service registration is the entire
    point of per-device credentials, and in this deployment most accounts are administered rather
    than self-managed. It solves a problem that is not being had.
  - Binding a credential to a DeviceId is **fragile**: iOS regenerates its DeviceId on
    restore-from-backup and after a reset, so a hard binding locks the user out precisely when they
    are least able to diagnose it. If it is ever built, *record* the DeviceId that used a credential,
    never *require* it.
  - **Autodiscover carries no DeviceId at all** (it authenticates with Basic and parses an email from
    the body), and the WebUi portal login is a human in a browser — so an account-level credential
    has to exist regardless. Per-device would be additive, never a replacement.
  - Naive verification is **O(N) PBKDF2 per failed attempt**, which is the verify-cost DoS `K3` was
    about. It would need the DeviceId as a lookup *hint* to stay O(1) in the common case.

  Per-device *revocation* already exists via `LoginBlock` (`eas block <user> <device>`); what is
  missing is only that it is admin-only rather than self-service.

---

## Where this lives today

| File | Role |
|---|---|
| `src/ActiveSync.Core/State/SyncDbContext.cs` | `DbSet`s (:14-31) — the definitive table list; indexes, keys and token stamping in `OnModelCreating` |
| `src/ActiveSync.Core/State/Entities.cs` | The eight entities carrying `UserName`; `AccountEntry` |
| `src/ActiveSync.Core/Options/AccountOptions.cs` | `AccountOptions` + `BackendRoleOverride` |
| `src/ActiveSync.Core/Accounts/AccountResolver.cs` | `MergedAccount` (:21), `BuildSnapshot` (:277), `BuildOne` (:355), `ResolveSecret` (:531), whole-entry replacement (:320-341) |
| `src/ActiveSync.Core/Accounts/ResolvedAccount.cs` | Compiled per-request view — keep this shape |
| `src/ActiveSync.Core/Accounts/AccountStore.cs` | DB persistence; `NormalizeLogin` case-folding (`B1`) |
| `src/ActiveSync.Core/Accounts/PassThroughProvisioner.cs` | Auto-provisioning — moves earlier in the pipeline |
| `src/ActiveSync.Core/Security/LocalContentProtector.cs` | The AAD (:126) |
| `src/ActiveSync.Core/Administration/AccountFieldPaths.cs` | `Backends:<Role>:<Field>` addressing |
| `src/ActiveSync.Core/Administration/AccountEditing.cs` | Shared edit pipeline for CLI + web |
| `src/ActiveSync.Server/Cli/UserCommands.cs` | CLI writer → `eas account` |
| `src/ActiveSync.WebUi/Api/UsersEndpoints.cs` | Admin writer |
| `src/ActiveSync.WebUi/Api/PortalEndpoints.cs` | Portal writer (`me` :36, PUT :233) |
| `src/ActiveSync.Core/Accounts/LegacyAccountJson.cs` | **Delete** — the reinit removes its reason to exist |

---

## Implementation plan

**Two phases, and the split is deliberate. Get the schema right first; the wiring is mechanical
afterwards.** The shape is what the reinit makes free and what cannot be cheaply revisited once
deployments exist — the resolver, CLI, portal and banner are just work. Phase A can land and be
verified on its own, before anything downstream is touched.

One item ≈ one session, in order. Follow `docs/review/fix-review.md`'s working protocol: red-first
tests, commit per unit of work, build at 0 warnings, and the live suite for anything touching the
schema, auth or the request pipeline.

### Phase A — the schema

*The shape decisions. Land these before touching how the application reads them.*

**1. Vocabulary, no behaviour change.** `ActiveSync:Users` → `ActiveSync:Accounts`,
`AutoProvisionUsers` → `AutoProvisionAccounts` (rename only; semantics land in item 2), `eas user` →
`eas account` (no alias), "user" → "account" for the persistent record in code and docs. Mechanical,
large diff, zero semantic change — do it first so later diffs read cleanly.

**2. `AccountId` + provisioning.** Add the identity column; FK the eight login-carrying entities to it
(and only those — see Identity); move
notifier keys, cache keys and the `LocalContentProtector` AAD onto it; keep the login as a unique
case-folded attribute; land `AutoProvisionAccounts`' semantics and delete `RequireDeclaredUsers`.
**Delete the existing migration chain for both providers and generate a fresh `Initial` pair from the
new model** — no upgrade path from the old schema, and none needed (Standing context). **Acceptance
tests, non-negotiable:** (a) rename a login and
assert sync state survives and an encrypted `LocalItem` written before the rename still decrypts
after it; (b) past the auth boundary an `AccountId` is always present — an undeclared login either
gets an account or is refused; (c) the id column is non-reusing on both providers. Update `AGENTS.md`
and `README.md` in the same work (Invariant 1).

**3. Normalise the account shape.** Extract every `AccountOptions` scalar into columns on `Accounts`
(`Password`, `DefaultBackendLogin`, `DefaultBackendPassword`, `MailAddress`, `Admin`, `Enabled`,
`OidcSubject`, `AutoProvisioned`); add the `AccountBackendRoles` child table with
`Enabled`/`Provider`/`UserName`/`Password` as columns and `SettingsJson` as the one surviving blob.
`AccountOptions` stays as the in-memory/config-bound type — only `AccountStore`'s two mapping
directions change. Regenerate the `Initial` pair again (Standing context — regenerate, do not chain).
No resolver changes yet: prove the columns persist, seal and round-trip.
**Assert the two credential chains stay separate** — the gateway `Password` is verified locally and
must never be reachable by anything that builds a backend connection.

**3a. Foreign keys and cascades.** Convert every account-linked soft link to a real FK with cascade
(see "Real foreign keys and cascade delete"), including the `SentCommandToken` → `Device` FK that is
missing today and `LoginBlock`'s `DeviceKey`. **Test that deleting an account removes exactly its
own rows and nothing else's**, and that deleting a device no longer orphans `SentCommandToken` rows.

### Phase B — the wiring

*Mechanical once Phase A is right. Each item is a behaviour change with its own tests.*

**4. Per-field resolution.** Replace whole-entry replacement with the resolution rule. Round-trip
tests for: account DB beats account config; clearing an account value reverts to config, then to
global DB, then global config, then default; a global DB change reaches every account live;
`Settings` resolves per key with list-replacement and null-clear intact.

**5. Credential resolution.** The extra scope tier: role beats account-default beats global beats
pass-through, DB beating config within each. **The tests that matter are the fallbacks**, because
they are what preserves zero-administration: unset `DefaultBackendPassword` still forwards the
presented EAS password; unset `DefaultBackendLogin` still uses the gateway login; an account with
nothing set behaves exactly as it does today. Prove those before proving the overrides.

**6. Write paths + permissions.** One slot, both writers; `AccountFieldPaths` gains the two new
account-level fields. Enforce the permission table: assert the holder cannot write
`Enabled`/`Provider`/account-level fields or non-`SelfServiceEditable` settings keys, for every role
and provider. That assertion is the security property of this design.

**6a. Account lifecycle.** The config-declared bootstrap (rows created at startup for every declared
account, matched by login), `eas account rename` **with the config-declared immutability guard and
the collision check**, the startup reconciliation warning for config logins with no account, and the
delete guard that counts content before destroying it. **Test the guard from both surfaces** — CLI
and admin UI must both refuse to rename a config-declared account.

**7. Docs + banner.** Startup banner shows the level each effective value came from. Rewrite the
account sections of `README.md`, `docs/configuration.md`, `docs/webui.md`, `docs/cli.md`, and
`AGENTS.md` § *Auth model*.

---

## Invariants that must survive

Violating any of these is a stop-and-report.

1. **`AccountId` is THE identity — this DELIBERATELY REPLACES the current invariant.** `AGENTS.md`
   and `README.md` currently state the *gateway login* is the identity and that changing it orphans
   sync state and bricks encrypted rows. True today; removed by this design. **Update both in item
   2** — until then they contradict this document. What survives unchanged: **a per-backend user name
   is still never an identity**. The new hard rule: `AccountId` is immutable and never reused.
2. **Auth precedence is unchanged:** gateway `Password` → configured MailStore `Password`
   (timing-safe pinned compare) → MailStore provider probe → undeclared global probe.
3. **Fail closed.** An invalid/malformed account is kept visible but refused; one bad row never
   breaks auth for everyone.
4. **Live pickup (~1 s)** via the `AccountsStamp` point-read and atomic snapshot swap survives, and
   `SnapshotChanged` still clears the auth caches.
5. **An `AccountId` always exists past the auth boundary.** No caller handles a missing account; no
   code path defers provisioning.
6. **Secrets never leave the server** — the existing leak-guard test (no `pbkdf2$` / `enc:v1:` in any
   response) must still pass.
7. **MailStore + MailSubmit stay mandatory**; content roles still fall back to `local`.

---

## Verification

- Unit: `dotnet test ActiveSync.slnx --filter "Category!=Integration"` — 0 warnings, no skips.
- Live: `./scripts/stalwart-up.ps1`, then
  `dotnet test tests/ActiveSync.Integration.Tests --filter Category=Integration`. **Read the
  passed/skipped counts, never the exit code** — a skipped suite exits 0 and looks identical to a
  passing one. **Every Phase A item changes the schema, and items 4–6 change auth and the request
  pipeline — the live suite is mandatory for all of them.** Item 2 especially: re-keying every entity
  is exactly the class of change whose blast radius no unit suite can see.
- **The migration contract is a test, not an assumption:** a **blank** database plus the regenerated
  `Initial` must yield a working schema on **both** providers. SQLite is covered by the live suite
  (which boots the real host, and so runs `MigrateAsync` for real); **Postgres only runs in CI** with
  `AS_TEST_PG` set, so a Npgsql-only mistake is invisible locally — do not treat a green local run as
  proof both providers are sound.
- Unit suites build the schema model-driven rather than through migrations, so **a model change can
  pass every unit test while the migration is broken or absent**. Only the live suite catches that.

---

## Confidence

The **problem statements** are verified against the code at the files and line numbers named above,
as is the `Sqlite:Autoincrement` behaviour underpinning the non-reuse guarantee.

The **design** is a proposal and has not been prototyped. Items 3 and 4 are where unknowns will
surface — particularly `Settings` per-key resolution with list-replacement and null-clear semantics
intact across five levels, which is the single most intricate piece of work here.

**Item 2 carries the most risk in the least visible place: the encryption AAD.** Everything else is
recoverable by editing code; an AAD mistake is only discovered when data will not decrypt. Treat its
rename-and-still-decrypt test as non-negotiable, and do not let it be the last thing written.

# Database restructure — user identity, per-field resolution, schema hygiene

> **Status: IMPLEMENTED (2026-07-27).** Every item of both phases has landed; this document is now
> the RATIONALE for what exists rather than a plan for what should. `AGENTS.md`, `README.md`,
> `docs/configuration.md`, `docs/cli.md` and `docs/webui.md` are the authority on current
> behaviour — read them first and treat this as the "why".
>
> Deviations from the brief as written, all deliberate and each argued at its commit:
>
> 1. **The pinned-password rule was deleted, not generalised** (item 5). The brief left auth
>    precedence "unchanged", naming the *configured MailStore* password as a pinned compare: the
>    presented device password had to equal it. That pin existed because whenever the gateway holds
>    a backend password, the probe authenticates with *that* password and so returns success for
>    anything the device sent — with `DefaultBackendPassword` added, leaving the pin on the role
>    password alone would have made setting a default an open door.
>
>    Pinning closes the hole but pays for it by promoting a GATEWAY → BACKENDS credential into a
>    DEVICE → GATEWAY one, and it only stays correct while `VerifyLocally`'s chain mirrors
>    `Resolve`'s MailStore chain term for term — a correspondence nothing enforces, and exactly what
>    the new field broke. So the pin is **gone**, and the state that needed it is refused instead: a
>    stored MailStore secret (role override or `DefaultBackendPassword`) **requires** a gateway
>    `Password`, which also means the gateway password cannot be removed while one is present.
>    `VerifyLocally` is now *gateway password → probe*, and a backend credential is never compared
>    against a device password anywhere. Enforced on the MERGED user in `UserResolver.BuildOne`, so a
>    row hand-written around the write paths fails closed rather than authenticating everything;
>    `BackendSessionFactory` additionally probes with the PRESENTED password explicitly, so the door
>    stays shut even if a future credential tier escapes the rule. **Breaking:** a user with a
>    MailStore password and no gateway password was legal before (the pin made the device type the
>    mail password) and now fails closed until a gateway password is set.
> 2. **`UserEditing.LoadStartingEntryAsync` stopped cloning config** (item 6). Cloning was right
>    under whole-entry replacement; under per-field resolution it would freeze config values as
>    database overrides, so later config changes would stop reaching the user.
> 3. **`DefaultBackendLogin`/`DefaultBackendPassword` are admin-only.** The permission table does
>    not list them; they apply to every role at once and the password additionally pins the user's
>    own authentication, so they are administered rather than self-service.
> 4. **`User.Json` became typed columns plus a `Declared` flag** (item 3) rather than a nullable
>    blob, so an identity-only row is distinguishable from a declaration without parsing anything.
> 5. **The live suite has not been run** against these changes. Every Phase A item changes the
>    schema and items 4–6 change auth and the request pipeline, so per this document's own
>    Verification section that is still outstanding.

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
| Changing the persisted shape of users freely | Skipping tests because "it's a rewrite" |
| Renaming config keys and CLI verbs | Leaving the CLI/admin/portal write paths inconsistent |
| Deleting `LegacyAccountJson` and its upgrade path | Changing the plugin contract without a `ContractVersion` bump |
| Dropping back-compat shims and data migrations | Re-introducing whole-entry replacement (below) |

**There is no data to migrate** — see "Database: no upgrade path" above for what that does and does
not license.

---

## The problems this fixes

### 1. The login is physically the primary key, so nothing about a user can change

Eight entities carry the login as a string — `Device`, `UserFolder`, `LocalItem`, `LoginBlock`,
`WebSessionRevocation`, `SharedCalendarGrant`, `AccountEntry`, `OofSetting`
(`src/ActiveSync.Core/State/Entities.cs`) — and so do the `LocalChangeNotifier` keys, the
session/watcher cache keys, and the **encryption AAD**: `LocalContentProtector.Aad` is
`userName + "\n" + collection` (`src/ActiveSync.Core/Security/LocalContentProtector.cs:95`).

Hence the constraint `README.md` states plainly: *keep logins stable or devices re-sync from scratch
and locally stored items become unreadable*. **A login cannot be renamed** — not "should not":
renaming orphans every sync-state row and permanently bricks every encrypted local item.

### 2. A DB user row REPLACES the whole config entry

`AccountResolver.cs:362-383`. Set one field in the database and every config-set field for that
user is silently discarded. Operators reasonably expect an override to be a *deviation*, not a
wholesale substitution.

### 3. The vocabulary is ambiguous

**The codebase says "user" on the outside and "account" on the inside**, and nothing reconciles them:

| Surface | Term today |
|---|---|
| Config — `ActiveSync:Users`, `UsersFile`, `Auth:UsersRefreshSeconds`, `RequireDeclaredUsers`, `AutoProvisionUsers` | **user** |
| CLI — `eas users`, `eas user <verb>` | **user** |
| Web — `/admin/api/users`, `UsersEndpoints`, `UserDto`, the admin *Users* page | **user** |
| Internal types — `AccountEntry`, `AccountOptions`, `AccountResolver`, `AccountStore`, `AccountTemplate`, `AccountEditing`, `AccountFieldPaths`, `AccountSecretPolicy`, `AccountsStamp`, `MergedAccount`, `ResolvedAccount` | **account** |

**Verified: no `User` type exists, and none ever did.** The "account" naming was not forced by a
collision — there was nothing to collide with. It is simply an inconsistency, and every `User*` type
that *does* exist (`UserFolder`, the `eas user` command classes, the web DTOs) already means the
person. **There is no misuse to untangle** — nothing needs renaming to `UserState` or similar.

**Settled vocabulary — "user" wins, because the operator surface already says it:**

| Term | Means |
|---|---|
| **User** | The persistent record for one gateway login. The single word for the thing |
| **Login** | The identity string the phone sends — a mutable attribute (immutable while config-declared) |
| **`UserId`** | The immutable surrogate key. THE identity |
| **Effective** | The result of resolving all levels for a field |
| **Resolved\*** | The compiled per-request view — keep the shape, rename `ResolvedAccount` → `ResolvedUser` |

**This rename removes a term rather than adding one, and breaks nothing operators touch.** The
internal `Account*` types become `User*`; config keys, CLI verbs and API routes are already right and
stay exactly as they are.

---

## Decisions (settled — do not re-open)

Recorded so a fresh session executes rather than re-litigates.

| # | Decision |
|---|---|
| 1 | **`UserId` is an `int` identity column.** Not a GUID. Immutable, never reused. |
| 2 | **The login is a mutable attribute**, unique and case-folded — **except while the user is config-declared**, when it is immutable so config↔DB matching by login can never drift. Everything else is mutable. |
| 3 | **One stored value per field per user.** Admin and holder write the **same** slot; the difference between them is *permission*, not storage. |
| 4 | **Resolution is per field**, following the one rule below. Whole-entry replacement is deleted. |
| 5 | **`Settings` resolves per key**, by the same rule. |
| 6 | **`AutoProvisionUsers` keeps its name but changes meaning**, default **true**; always creates a user on first successful auth; **false** refuses the undeclared login before any backend probe. Same key, new behaviour — say so loudly in the docs, since a rename would otherwise have signalled it. |
| 7 | **`RequireDeclaredUsers` is deleted** — `AutoProvisionUsers=false` now means exactly that. |
| 8 | **The operator-facing surface does not change at all.** `ActiveSync:Users`, `UsersFile`, `AutoProvisionUsers`, `eas user`/`eas users`, `/admin/api/users` are already correct — only the *internal* `Account*` types are renamed. No config break, no CLI break. |
| 9 | **An admin may read and overwrite a holder's values, and a holder may overwrite an admin's** — same slot, last write wins. |
| 10 | **A user carries two passwords**: `Password` (device → gateway, verified locally) and `DefaultBackendPassword` (gateway → backends), plus `DefaultBackendLogin`. Different trust domains; keep the chains separate. |
| 11 | **Unset defaults mean today's behaviour** — `DefaultBackendPassword` unset ⇒ forward the presented EAS password; `DefaultBackendLogin` unset ⇒ the gateway login. Zero-administration pass-through must survive. |
| 12 | **Per-device credentials are deferred**, not rejected — see "What this deliberately does NOT do". |
| 13 | **The `AccountOptions` JSON blob is normalised away.** Scalars become columns on `Users`; per-role `Enabled`/`Provider`/`UserName`/`Password` become columns on a new **`UserBackendRoles`** child table. |
| 14 | **Only `Settings` stays serialized** (`UserBackendRoles.SettingsJson`) — its keys are provider-defined and discoverable only at runtime, which is the provider model working as intended. |
| 15 | **Every user-linked table gets a real FK to `UserId` with cascade delete**, and `SentCommandToken` gets the FK to `Device` it is missing today. |
| 16 | **Deleting a user must not silently destroy content — confirm-and-cascade.** The DB cascades; the application counts what would be lost first and demands confirmation naming it. The CLI gets a `ConfirmRequest` round-trip so the forwarded path can ask (it cannot today); the web reuses its existing typed-echo. One dry-run counting service behind both. |
| 17 | **Config-declared users get rows at startup** (identity from the row, values from config). |
| 18 | **`AccountsStamp` + `SettingsStamp` merge into one `DataChanges` table**, keyed by area string, **one row per watched area**. A stamp belongs to a consumer's aggregate, not to a table — so `UserBackendRoles` bumps `"users"` rather than getting its own. |
| 19 | **`LoginBlock` is per-device only** — a single non-nullable `DeviceKey` FK, **and no `UserId`** (it reaches the user through the device). Whole-user blocking is `Users.Enabled = false`, and `eas block` becomes device-scoped — on both the CLI and the admin API — so the duplication the schema removes is not recreated a level up. |
| 20 | **One identity per row.** Where a table carries an unused surrogate `Id` *and* a unique natural key, the natural key becomes the PK and the surrogate goes — nine tables, none of which is ever looked up by `Id`. Keep the surrogate only where it is a FK target, a wire value, or where no natural key exists. See [`future-db.md`](future-db.md) § *One identity per row* for the list and the exceptions. |

> **The shape is still settling.** Decisions 1–9 have been through a full round of review; 10–17 are
> newer and the owner expects them to move. Treat the *direction* as agreed and the field-level detail
> as provisional — but do not silently re-open 1–9 while adjusting the rest.

### Questions raised during design — all now settled

Kept with their reasoning, because "why not the other way" is the part a fresh reader cannot
reconstruct, and each of these was argued at least once.

1. ~~`LoginBlock.DeviceId` nullable?~~ — **settled: non-nullable.** Whole-user blocking is
   `Users.Enabled = false`; `LoginBlock` is per-device only. See "Real foreign keys and cascade
   delete" for the CLI consequence.
2. ~~Config identity~~ — **settled**: no config-side identifier; match by login, and make the login
   immutable per user while it is config-declared. See "Config-declared users need rows too".
3. ~~Delete guard shape?~~ — **settled: confirm-and-cascade.** Refuse-until-purged was a workaround
   for the CLI being unable to prompt over `/cli`; the `ConfirmRequest` round-trip removes that
   limitation. See "Deleting a user must not silently destroy data".
4. ~~Terminology: `Account` or `User`?~~ — **settled: `User`.** The internal `Account*` family is
   renamed to `User*`; the operator-facing surface already said "user" and does not change. See
   "The vocabulary is ambiguous".

---

## The design

### Identity: `UserId`

```
User
  UserId   int identity   // THE identity — immutable, NEVER reused
  Login    string         // unique (case-folded), MUTABLE
  ...everything else, all mutable
```

Those eight entities FK to `UserId` instead of carrying the login. Notifier keys, cache keys and
the encryption AAD key on `UserId`. **A rename then becomes a single-row update**: sync state
survives, encrypted local items stay readable, the device keeps its folder registry and sync keys,
and the holder just updates the username on the phone.

**Only those eight need the new column — the rest come along for free.** Of the 18 tables
(`SyncDbContext.cs:14-31`), `DeviceFolder`, `CollectionState` and `SentCommandToken` scope
transitively through `DeviceKey`, and `DavItem` through `UserFolderKey`; the remaining six
(`AccountsStamp`, `SettingsStamp`, `GlobalSetting`, `LogEntry`, `ServerCertificate`,
`DataProtectionKeyEntry`) are global and not per-user at all. Verified against the model — do not add
a `UserId` to a table that already reaches a user through a FK, or the two paths can disagree
after a rename and there is no constraint that would catch it.

Six things to get right:

1. **Never reuse an id.** Security-critical, not hygiene: if user 42 is deleted and a new user
   becomes 42, any surviving encrypted `LocalItem` decrypts under a *different person's* AAD.
   **Verified safe on both providers, but only because of an annotation that must not be dropped:**
   EF emits `Sqlite:Autoincrement` for int identity keys, and SQLite guarantees monotonic non-reuse
   only with `AUTOINCREMENT` (plain `INTEGER PRIMARY KEY` rowids **are** recycled after deleting the
   highest row). Postgres identity sequences do not recycle. Assert this in a schema test rather than
   trusting it to survive a future model tweak.
2. **The AAD change is the point and the biggest risk.** `Aad(userName, collection)` becomes
   `Aad(userId, collection)`. Keep `K2`'s injectivity — the parts must not be ambiguously
   concatenable — via explicit length-prefixing or an unambiguous separator that cannot occur in
   either part.
3. **The id does not exist until the row is persisted.** An identity column is assigned by the
   INSERT, so provisioning must be *insert user → obtain id → write anything that references it*.
   `AutoProvisionUsers` (below) makes this ordering natural, because provisioning happens at auth,
   before any sync-state row exists.
4. **First-auth is a race.** Two devices authenticating a brand-new login concurrently will both try
   to insert. Use the pattern this codebase already applies in `DeviceStore`, `DavItemMap` and
   `GatewayCertificateStore`: catch the unique violation on `Login` and re-read the winner. Never
   assume the insert succeeds.
5. **The login still needs case-folded uniqueness.** `B1`'s `NormalizeLogin` (`ToLowerInvariant` on
   write) and the unique index stay — they move from "the primary key" to "a unique attribute".
6. **A rename must invalidate every cache keyed by login** — `BackendSessionFactory`'s session cache,
   the auth verdict caches, watchers — exactly as `SnapshotChanged` already does for user edits.

### User shape — two passwords, and explicit backend defaults

**The `AccountOptions` JSON blob is gone.** Everything with a compile-time-known shape becomes a real
column; only the genuinely runtime-discovered part stays serialized. Two tables:

#### `Users`

| Column | Type | Notes |
|---|---|---|
| `UserId` | int identity | **PK.** THE identity — immutable, never reused |
| `Login` | string | **unique, case-folded.** MUTABLE |
| `Password` | string? | **Device → gateway.** `pbkdf2$…` (preferred) or plaintext. Verified LOCALLY, never sent anywhere |
| `DefaultBackendLogin` | string? | **Gateway → backends**: default user name for every role |
| `DefaultBackendPassword` | string? | Default secret for every role, `enc:v1:` sealed |
| `MailAddress` | string? | From rewriting, Settings, meeting replies. Null ⇒ the login if it contains `@`. *(Extracted from the blob — this is why it appears nowhere in today's schema.)* |
| `Admin` | bool? | Grants `/admin` access |
| `Enabled` | bool? | `false` = disabled; every login refused 403 after valid credentials |
| `OidcSubject` | string? | IdP `sub` this user is bound to |
| `AutoProvisioned` | bool? | Provenance marker for gateway-created rows |

#### `UserBackendRoles`

One row per (user, role). Child of `Users`, **FK with cascade delete**.

| Column | Type | Notes |
|---|---|---|
| `Id` | int identity | **PK** |
| `UserId` | int | **FK → `Users.UserId`, cascade.** Unique with `Role` |
| `Role` | string | `MailStore`, `MailSubmit`, `Calendar`, `Contacts`, `Tasks`, `Notes`, `Oof` |
| `Enabled` | bool? | `false` = turn this role off (content roles fall back to `local`, Oof off). Invalid on the two mail roles |
| `Provider` | string? | Serve this role with a different provider than the global assignment |
| `UserName` | string? | Backend login for this role |
| `Password` | string? | Backend secret for this role, `enc:v1:` sealed |
| `SettingsJson` | string? | **The one surviving blob** — see below |

**Why the line falls there.** `Enabled`/`Provider`/`UserName`/`Password` have a fixed,
compile-time-known shape, and the credentials among them are exactly what the resolution rule spends
its time resolving — so they earn columns, and "which users override MailStore?" becomes a query
rather than a full-table deserialize. `Settings` cannot: its keys are **provider-defined and only
discoverable at runtime** (`IBackendProvider.DescribeConfiguration` → `BackendConfigField`), and the
host deliberately never knows a plugin provider's option shape. That is the whole point of the
provider model, so `Settings` stays serialized — with its existing semantics intact: setting a list
element (`X:0`) REPLACES the inherited list, and a null value CLEARS the inherited key rather than
falling through.

**Effort note, since this looks bigger than it is.** `AccountOptions` does **not** disappear — it
remains the in-memory and config-bound shape (`ActiveSync:Users` binds to it through
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

**A user carries two distinct credentials, and conflating them is the current model's weak
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

So an undeclared, unconfigured user behaves exactly as it does now, and a decoupled user is one
field instead of N.

**The probe invariant — the one rule that binds the two credentials together.** Pass-through
authenticates by *probing*: the gateway signs in to the mail server with what the device presented,
and the mail server's verdict is the answer. That is only an answer while the password being sent
**is** the presented one. A stored MailStore secret breaks it — the probe would sign in with the
gateway's own copy and succeed for any password at all, including an empty one.

So the two credentials are not fully independent after all: **a stored MailStore secret requires a
gateway `Password`**, and equally the gateway password cannot be removed while such a secret remains
(one rule, two directions). `Backends:MailStore:Password` and `DefaultBackendPassword` are the two
ways to store one, since every role falls back to the default; a *content*-role secret is exempt,
because MailStore is what the probe targets and it still receives the presented credential.

The alternative was to compare the presented password against the stored backend secret. That closes
the same hole, but it promotes a GATEWAY → BACKENDS credential into a DEVICE → GATEWAY one — the
exact conflation this section calls the current model's weak point — and it stays correct only while
the local-verdict chain mirrors the resolution chain term for term, which is precisely what adding
`DefaultBackendPassword` broke. Refusing the combination cannot rot the same way: the dangerous state
is simply not representable.

### THE RESOLUTION RULE — one order, every setting, per field

The load-bearing rule of this design, and **not specific to users**: it is the order *every*
overridable setting follows. Implement it once; make every new setting conform rather than inventing
its own precedence.

**Most specific wins. Resolution is PER FIELD, never per entry.**

| # | Level | Written by | Stored in |
|---|---|---|---|
| 1 | **User · DB** | the admin (`eas user set`, admin API) **or** the holder (portal) — same slot | `Users` / `UserBackendRoles` |
| 2 | **User · config** | operator, in `ActiveSync:Users:<login>:…` | appsettings / env / users file |
| 3 | **Global · DB** | admin (`eas config set`, admin UI) | `GlobalSetting` rows |
| 4 | **Global · config** | operator | appsettings / env |
| 5 | **Code default** | — | the options POCO |

```
user (DB)  →  user (config)  →  global (DB)  →  global (config)  →  code default
```

A setting that **cannot** be set per user simply has no levels 1–2; the rule is unchanged and
resolution starts at level 3:

```
global (DB)  →  global (config)  →  code default
```

Levels 3–5 already behave this way (`DbSettingsConfigurationProvider` is layered last in
`IConfiguration`, so DB beats appsettings/env, which beat POCO defaults — `AGENTS.md` § *Backend
layer notes*). **This design adds levels 1–2 above them and requires per-field composition.**

Worked example — the SMTP host:

1. `appsettings.json` sets `Backends:MailSubmit:Host = smtp.example.com` → every user uses it.
2. `eas config set ActiveSync:Backends:MailSubmit:Host smtp2.example.com` → the **global DB** value
   wins for every user, live, no restart.
3. An admin sets it for one user → that user uses their value; everyone else still gets
   `smtp2`.
4. The holder overrides it in the portal (if permitted for that field) → it lands in the **same
   slot**, so it simply replaces the admin's value; clearing it falls back to level 2, then 3.

**Null/absent means "not set at this level", not "cleared."** A gap falls through. Clearing a
user value reverts to config, then global — an override is a *deviation*, and removing the
deviation restores the inherited value. Keep this distinct from the existing explicit-clear
semantics on `Settings` (a null value CLEARS the inherited key rather than falling through); carry
that forward deliberately, not by accident.

### Credentials resolve by the same rule, with one extra scope tier

Backend credentials are not a special case — they add a level of **scope** (this role vs every role)
between the user and global tiers. The rule is unchanged; there is simply one more step:

```
user · role · DB      →  user · role · config
  →  user · default · DB  →  user · default · config
    →  global role section (DB → config)
      →  pass-through (presented EAS credential / gateway login)
```

Read it as "most specific wins" on two axes at once — **which role** and **which source** — with role
beating default, and DB beating config within each. The terminal step is pass-through, which is why
a user with nothing set still works.

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
| User-level (`Admin`, `Enabled`, `OidcSubject`, `AutoProvisioned`) | ✗ | ✓ |
| Gateway `Password` | ✓ (with current-password check) | ✓ |

**How an admin pins a value the holder must not change: mark it not `SelfServiceEditable`.** That is
the only lock, and it is the mechanism that already exists. Setting a value in *config* does **not**
lock it — level 1 beats level 2, so a permitted holder still overrides it. Anyone who wants a
guaranteed value must remove it from the self-service surface, not hide it in a lower level.

### Real foreign keys and cascade delete

Today only three relationships are declared FKs (`DeviceFolder`/`CollectionState` → `Device`,
`DavItem` → `UserFolder`); everything else is a string matched by value. See
[`current-db.md`](current-db.md) for the full as-built graph. With `UserId` in place, the soft
links become real:

| Table | Today | Target |
|---|---|---|
| `LocalItem` | `UserName` string | **`UserId` FK, cascade** |
| `UserFolder` | `UserName` string | **`UserId` FK, cascade** |
| `Device` | `UserName` string | **`UserId` FK, cascade** |
| `LoginBlock` | `UserName` + nullable `DeviceId` **string** | **`DeviceKey` FK → `Device.Id`, cascade — and NO `UserId`** (see below) |
| `SharedCalendarGrant`, `OofSetting`, `WebSessionRevocation` | `UserName` string | **`UserId` FK, cascade** |
| `SentCommandToken` | `DeviceKey` int, **no FK** | **real FK → `Device.Id`, cascade** — closes the orphan-on-device-delete gap |
| `UserBackendRole` | *(new)* | **`UserId` FK, cascade** |

**`LoginBlock.DeviceKey` is NON-nullable — decided.** Today a null `DeviceId` means "block the whole
user", which duplicates `Enabled = false`; `AGENTS.md` already has to explain the difference ("the
persistent-property counterpart to the ad-hoc `LoginBlock`"). A mandatory FK forces the whole-user
case out of `LoginBlock` entirely, leaving exactly one mechanism for each concept:

- **`Users.Enabled = false`** — the user is off. Every device, EAS and web.
- **`LoginBlock(DeviceKey)`** — this one device is cut off. Nothing else changes.

**And `LoginBlock` carries NO `UserId`** — a device already belongs to exactly one user, so the column
would be derivable, and that is precisely the case this document's own rule forbids: *do not add a
`UserId` to a table that already reaches a user through a FK, or the two paths can disagree and there
is no constraint that would catch it.* The only argument for keeping it was that "all blocks for this
user" needs a join — but it needs a join either way, `Users → LoginBlock` versus
`Users → Device → LoginBlock`. One extra hop is not a reason to carry a column that can go stale.

**Consequence for the CLI, which must not recreate the duplication a level up.** `eas block <user>`
today means "block everything" via a null-device row, and that shape no longer exists. Make
`eas block` **device-scoped only**, and have the bare form fail with a pointer rather than silently
doing something subtly different:

```
eas block <user> <device>     → per-device LoginBlock
eas block <user>              → error: "use `eas user disable <user>` to disable the whole user"
```

Having both `eas block <user>` and `eas user disable <user>` write the same state would put back, in
the CLI, precisely the two-mechanisms-one-concept problem this removes from the schema.

**Both surfaces change, not just the CLI.** `DevicesEndpoints`' own summary says *"blocks mirror the
CLI exactly (user-level when deviceId is omitted)"*, so the admin API accepts a user-level block the
same way and needs the same treatment. `DeviceAdminService` is shared by both, so the natural place
to enforce device-scoping is there — fix it once and both callers inherit it, rather than guarding
the CLI and leaving the API able to write a shape the schema no longer has.

**No impact on the wipe path:** `CompleteAccountWipeAsync` already inserts a *per-partnership* block
on the `(user, device)` unique index, so it is unaffected by the column becoming mandatory.

### Deleting a user must not silently destroy data

Cascade delete is right, but it makes user deletion destructive in a way it currently is not:
**`LocalItem` is real user content** — contacts, calendar, tasks and always notes — not sync metadata.
Cascading it means deleting a user irreversibly deletes the user's PIM data, and for a
local-stores deployment that data exists **nowhere else**.

So the *database* cascades, and the *application* refuses to issue the delete blind:

1. Before deleting, count what will go with it — `LocalItem` by collection above all, plus devices,
   folders, shares and blocks.
2. If anything holds real content, **require an explicit confirmation naming what is lost**:
   "this permanently deletes 342 contacts, 89 events, 12 notes — they exist nowhere else."
3. Sync state alone (devices, folders, collection state) is *not* worth prompting over — it rebuilds
   on the next sync. Only content is.

**Settled: confirm-and-cascade**, on both surfaces. (The alternative — refuse to delete while the
user owns `LocalItem` rows, forcing an explicit purge first — was mostly a workaround for the CLI
being unable to ask a question. The round-trip below removes that limitation, so the workaround is no
longer worth its extra step.)

#### One counting service, two confirmation idioms

The only genuinely new piece is a **dry-run count**, and both surfaces need the same one.
`DeviceAdminService.PurgeAsync` already returns `PurgeCount(Table, Count)` — but only *after*
deleting. The guard needs "what *would* go" before deciding, so add a count-only variant beside it.
Write it once; it feeds the CLI's question text and the web dialog's warning alike.

| | Server returns | Client does |
|---|---|---|
| **CLI** | `ConfirmRequest { Question, ResendArgs }` | prompt → resend `ResendArgs` verbatim |
| **Web** | 400 + the expected echo + counts | render dialog → repost with `Confirm` |

**Graduate the friction** (this is step 3 above, made concrete): no content ⇒ a plain confirm;
content ⇒ typed echo *plus* the counts. Sync state alone rebuilds on the next sync and does not
deserve a typed echo.

#### Web: already built

`WipeRequest`/`PurgeRequest` carry a `Confirm` field and the endpoint rejects anything that does not
echo the target exactly (`DevicesEndpoints.cs` — *"confirm must echo '{expected}'"*, the device id or
the **user** for a full purge). User deletion is that same pattern with the login as the expected
echo, and the SPA already renders this dialog for wipe and purge. Nothing new is required beyond
surfacing the counts.

#### CLI: needs a round-trip, because the forwarded console cannot prompt

**This is a real gap today, not a hypothetical.** `LocalCliEndpoint` builds its captured console with
`Interactive = InteractionSupport.No`, so a forwarded command can never prompt — which means
`eas purge` over `/cli` **always** fails with *"confirm with --yes when running non-interactively"*
(`PurgeCommands.cs`). The interactive branch only ever runs in the local-fallback path. Fixing this
for user deletion fixes `purge` too.

The mechanism: let the result carry an optional confirmation request, and let the **slim client** —
which is a real terminal process — do the asking.

```
LocalCliResult(int ExitCode, string Stdout, string Stderr, ConfirmRequest? Confirm)
    ConfirmRequest { string Question, string[] ResendArgs }
```

```
eas user delete anna@example.com
  → server: needs confirmation, returns Question + ResendArgs
  → client prompts locally: "…deletes 342 contacts, 89 events, 12 notes. Continue? [y/N]"
  → on yes, client sends ResendArgs verbatim (not shown to the operator)
```

Five details that make this safe rather than clever:

1. **The server supplies `ResendArgs`; the client never constructs them.** The slim client is a dumb
   forwarder by design — it must not learn which flag means "confirmed", nor re-assemble a command
   line (quoting bugs). A future command then gets this for free without touching the client.
2. **Use `--yes`, not `--force`.** `PurgeSettings` already defines `-y|--yes`; a second spelling for
   the same idea is exactly the duplication decisions 19 and 18 removed elsewhere.
3. **Re-check on the second call.** Call 1 counted; call 2 re-executes and deletes what is there
   *then*. If the counts have moved materially, refuse and re-prompt — the operator confirmed a
   specific loss, not an open-ended one.
4. **A non-interactive client must not auto-confirm.** If the client itself is piped or scripted,
   print the question to stderr and exit non-zero telling the operator to pass `--yes` — today's
   behaviour, preserved. The server never needs to distinguish "forwarded" from "piped": it always
   returns the request and the client decides.
5. **`LocalCliResult` is internal, so this costs nothing externally.** It lives in
   `ActiveSync.Crypto`, whose csproj states *"Host-only, NOT published"* — only `ActiveSync.Contracts`
   and `ActiveSync.Protocol` are packable. No contract version bump. The slim client already
   references Crypto, so it sees the new field for free, and a plain `Console.ReadLine()` suffices
   (the client is BCL-only — it has no Spectre dependency and needs none).

### `AutoProvisionUsers` — a user always exists past auth

- **`true` (default)** — a login that authenticates and has no user gets one created immediately,
  with a fresh `UserId`. No exceptions, no deferral.
- **`false`** — an undeclared login is **refused**, before any backend probe.

**This makes `UserId` total**, which is the real prize: past the auth boundary every request,
handler, store, notifier and cache can assume a user exists and its id is known. No nullable
user threaded through the call graph, no "not provisioned yet" branch, no ordering hazard where
sync state is written before the row that owns it. **Do not re-introduce a lazy or deferred
provisioning path** — one deferred case costs the totality everywhere.

`RequireDeclaredUsers` is deleted: `AutoProvisionUsers=false` means exactly what it meant. Keep
the property worth keeping — the refusal happens **before** the backend probe, so undeclared logins
never reach the mail server. That is a brute-force shield, not just policy, and today's
`PassThroughProvisioner` runs *after* a successful probe, so the refusal must move earlier rather
than flip a branch where provisioning currently sits.

### Config-declared users need rows too

Once every table FKs to `UserId`, a config-declared user cannot stay config-only — sync state
has nothing to point at. So **on startup, every user declared in `ActiveSync:Users` (or the
users file) gets a row**: `UserId` + `Login`, everything else null. Config keeps supplying the
*values*; the row supplies the *identity*.

**Matching is always by login. There is no config-side identifier** — no `Id` to hand-manage, no
`ConfigKey` column, no new concept. That works because of one rule:

> **The login is immutable for as long as the user is declared in configuration, and freely
> renameable when it is not.**

Evaluated **per user**, not globally: a user currently declared in `ActiveSync:Users` (or
the users file) cannot be renamed through the CLI or admin UI; every other user can. A DB-only
user stays renameable even when other users come from config, and a user dropped from
config becomes renameable again.

This is what makes matching-by-login safe. The database side can never drift from configuration,
because the only mutable side is the one config doesn't own. `AccountResolver` already tracks whether
an entry is config-declared (`MergedAccount.FromDatabase` / `ShadowsConfig`), so the guard is a check
the code can already answer.

**Three guards complete it:**

1. **Refuse the rename** for a config-declared user, in both the CLI and the admin UI, naming
   where to change it instead: *"`anna@example.com` is declared in configuration — change it there."*
   The UI should not offer the action at all rather than fail after the fact.
2. **Reject a colliding rename.** The new login must not match any existing user, config-declared
   or not.
3. **Reconcile at startup and warn.** For every config-declared login with no matching user:
   *"configuration declares `anna@example.com` but no user has that login — was it renamed?"*
   Cheap, and it is the only thing that catches the residual below.

**What this deliberately does not cover, and cannot.** Renaming the *configuration key itself* still
creates a new user and strands the old row's data — the gateway cannot distinguish a rename from
a delete-plus-add in a file it does not own. Immutability closes the database side, which is where
the accident is far more likely (an admin clicking rename in a UI, unaware the user is
config-backed); the config side stays an operator responsibility, made visible by guard 3.

**A rename command is worth having regardless.** Making renames possible at all is the whole point of
the surrogate key, and it is the supported path for every user configuration does not declare.

---

## Cross-cutting schema change: one `DataChanges` table

*Not user-specific — recorded here because the reinit is a single event. See the note at the end
of this section about where it really belongs.*

`AccountsStamp` and `SettingsStamp` are the same idiom twice: a single well-known row (`Id = 1`) whose
`Guid Version` is bumped in the same `SaveChanges` as the mutation, point-read by each replica to
notice changes cheaply. Replace both with one table:

```
DataChanges
  Id          int identity   // PK
  Key         string         // UNIQUE — "users", "settings", …
  Version     Guid           // bumped in the same SaveChanges as the mutation
  UpdatedUtc  DateTime
```

**One row per watched area — never one row total.** This is the way to get it wrong: a single shared
version would make a user write invalidate the settings snapshot and vice versa, so every
consumer reloads on every unrelated change. Distinct rows keep the invalidation scoped exactly as it
is today, and on PostgreSQL they are distinct row locks (SQLite serialises writes regardless, so
nothing changes there).

**Key it by string, not by a magic id.** `Key = "users"` reads better than "the code knows Id 2 is
users", it matches the `GlobalSetting.Key` idiom already in the codebase, and — the real payoff —
**adding a watched area becomes an inserted row rather than a new table and a migration.**

**`UserBackendRoles` does not get its own stamp.** It is part of the users aggregate, and
`AccountResolver` rebuilds the entire snapshot on any bump, so a write there bumps `"users"` like
any other user mutation. The rule to write down: **a stamp belongs to a *consumer's aggregate*,
not to a table.** Getting this backwards produces one stamp per table and a resolver that reloads
several times for one logical change.

**Keep the existing first-use race handling.** Both stores today do "read the row; if absent, add it",
which two replicas can execute concurrently. With `Key` unique, the loser catches the unique violation
and re-reads — the same pattern `DeviceStore`, `DavItemMap` and `GatewayCertificateStore` already use.

Areas worth having from the start: `"users"`, `"settings"`. A candidate the shared table makes
cheap later: shared-calendar grants, which today are picked up only on session recycle because giving
them a stamp would have meant another table.

> **Scope note.** This document was originally `account-model.md` and outgrew that name — it now
> specifies foreign keys and cascade rules on non-user tables, a delete guard, and this stamp
> consolidation. Renamed to `db-restructure.md` so nobody reads a narrower title and assumes the
> non-user parts are out of scope. The user model remains its largest chapter.

---

## What this deliberately does NOT do

State this plainly so nobody "fixes" it later believing it an oversight.

- **It does not close `C5`** (portal echoes an admin-set backend username). `C5` is closed **`N/A`**
  in `docs/review/round2/review-items.md` and stays closed. With one slot per field there is no provenance
  to gate a read on, and none is wanted: the holder can already change the value, and seeing it is
  what stops them changing it blind. Do **not** re-introduce a settings-surface gate — that was tried
  (review item 12, `7f0c73b`) and reverted (`fec1cfe`).
- **It accepts that a holder can overwrite an admin-set value** for any field they are permitted to
  write, destroying the previous value. That is the intended semantics of "it is their own account to manage". The
  admin's remedy is to set it again, or to remove the field from the self-service surface.
- **It does not add per-field provenance or an audit trail.** "Who set this?" is not answerable from
  the stored value. If that is ever wanted, it is a separate design — log-based, most likely, since
  logs already record the actor.
- **It does not add per-device credentials (considered, deferred).** The idea: N app-password-style
  credentials per user instead of one, each revocable independently — Google's model, and a
  natural fit because every EAS request carries a DeviceId. Deferred for one decisive reason and
  three supporting ones:
  - **The users who would benefit do not use the portal.** Self-service registration is the entire
    point of per-device credentials, and in this deployment most users are administered rather
    than self-managed. It solves a problem that is not being had.
  - Binding a credential to a DeviceId is **fragile**: iOS regenerates its DeviceId on
    restore-from-backup and after a reset, so a hard binding locks the user out precisely when they
    are least able to diagnose it. If it is ever built, *record* the DeviceId that used a credential,
    never *require* it.
  - **Autodiscover carries no DeviceId at all** (it authenticates with Basic and parses an email from
    the body), and the WebUi portal login is a human in a browser — so a user-level credential
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
| `src/ActiveSync.Core/Accounts/AccountResolver.cs` | `MergedAccount` (:21), `BuildSnapshot` (:319), `BuildOne` (:397), `ResolveSecret` (:588), whole-entry replacement (:362-383) |
| `src/ActiveSync.Core/Accounts/ResolvedAccount.cs` | Compiled per-request view — keep this shape |
| `src/ActiveSync.Core/Accounts/AccountStore.cs` | DB persistence; `NormalizeLogin` case-folding (`B1`) |
| `src/ActiveSync.Core/Accounts/PassThroughProvisioner.cs` | Auto-provisioning — moves earlier in the pipeline |
| `src/ActiveSync.Core/Security/LocalContentProtector.cs` | The AAD (`Aad`, :95) |
| `src/ActiveSync.Core/Administration/AccountFieldPaths.cs` | `Backends:<Role>:<Field>` addressing |
| `src/ActiveSync.Core/Administration/AccountEditing.cs` | Shared edit pipeline for CLI + web |
| `src/ActiveSync.Server/Cli/UserCommands.cs` | CLI writer → `eas user` |
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

**1. Vocabulary — internal types only, no behaviour change.** Rename the `Account*` family to `User*`:
`AccountEntry` → `User`, `AccountOptions` → `UserOptions`, `AccountResolver` → `UserResolver`,
`AccountStore` → `UserStore`, `AccountTemplate` → `UserTemplate`, `AccountEditing` → `UserEditing`,
`AccountFieldPaths` → `UserFieldPaths`, `AccountSecretPolicy` → `UserSecretPolicy`, `MergedAccount` →
`MergedUser`, `ResolvedAccount` → `ResolvedUser`. **Nothing operator-facing changes** — config keys,
`UsersFile`, `AutoProvisionUsers`, `eas user`/`eas users` and `/admin/api/users` are already correct
and must be left alone. Mechanical, large diff, zero semantic change; do it first so later diffs read
cleanly. Watch two things: `UserFolder` already exists and keeps its name (a folder belonging to a
user), and `LogEntry.User`/`HttpContext.User` are unrelated ambient names — do not sweep them in.

**2. `UserId` + provisioning.** Add the identity column; FK the eight login-carrying entities to it
(and only those — see Identity); move
notifier keys, cache keys and the `LocalContentProtector` AAD onto it; keep the login as a unique
case-folded attribute; land `AutoProvisionUsers`' semantics and delete `RequireDeclaredUsers`.
**Delete the existing migration chain for both providers and generate a fresh `Initial` pair from the
new model** — no upgrade path from the old schema, and none needed (Standing context). **Acceptance
tests, non-negotiable:** (a) rename a login and
assert sync state survives and an encrypted `LocalItem` written before the rename still decrypts
after it; (b) past the auth boundary a `UserId` is always present — an undeclared login either
gets a user or is refused; (c) the id column is non-reusing on both providers. Update `AGENTS.md`
and `README.md` in the same work (Invariant 1).

**3. Normalise the user shape.** Extract every `AccountOptions` scalar into columns on `Users`
(`Password`, `DefaultBackendLogin`, `DefaultBackendPassword`, `MailAddress`, `Admin`, `Enabled`,
`OidcSubject`, `AutoProvisioned`); add the `UserBackendRoles` child table with
`Enabled`/`Provider`/`UserName`/`Password` as columns and `SettingsJson` as the one surviving blob.
`AccountOptions` stays as the in-memory/config-bound type — only `AccountStore`'s two mapping
directions change. Regenerate the `Initial` pair again (Standing context — regenerate, do not chain).
No resolver changes yet: prove the columns persist, seal and round-trip.
**Assert the two credential chains stay separate** — the gateway `Password` is verified locally and
must never be reachable by anything that builds a backend connection.

**3a. `DataChanges`.** Replace `AccountsStamp` and `SettingsStamp` with the shared table; route
`GlobalSettingStore` and `AccountStore` (including `UserBackendRoles` writes) through it. **Test
that the areas stay independent** — a user write must not move the `"settings"` version, or every
consumer reloads on every unrelated change.

**3b. Foreign keys, cascades, and primary keys.** Promote the nine natural keys to PKs and drop their
unused surrogate `Id`s (decision 20) — do it in the same pass as the FK work, since both are pure
model configuration and one regenerated `Initial` covers them. Then convert every user-linked soft
link to a real FK with cascade
(see "Real foreign keys and cascade delete"), including the `SentCommandToken` → `Device` FK that is
missing today and `LoginBlock`'s `DeviceKey`. **Test that deleting a user removes exactly its
own rows and nothing else's**, and that deleting a device no longer orphans `SentCommandToken` rows.

### Phase B — the wiring

*Mechanical once Phase A is right. Each item is a behaviour change with its own tests.*

**4. Per-field resolution.** Replace whole-entry replacement with the resolution rule. Round-trip
tests for: user DB beats user config; clearing a user value reverts to config, then to
global DB, then global config, then default; a global DB change reaches every user live;
`Settings` resolves per key with list-replacement and null-clear intact.

**5. Credential resolution.** The extra scope tier: role beats user-default beats global beats
pass-through, DB beating config within each. **The tests that matter are the fallbacks**, because
they are what preserves zero-administration: unset `DefaultBackendPassword` still forwards the
presented EAS password; unset `DefaultBackendLogin` still uses the gateway login; a user with
nothing set behaves exactly as it does today. Prove those before proving the overrides.

**6. Write paths + permissions.** One slot, both writers; `AccountFieldPaths` gains the two new
user-level fields. Enforce the permission table: assert the holder cannot write
`Enabled`/`Provider`/user-level fields or non-`SelfServiceEditable` settings keys, for every role
and provider. That assertion is the security property of this design.

**6a. `ConfirmRequest` round-trip.** Add the optional `Confirm` field to `LocalCliResult`, teach the
slim client to prompt and resend `ResendArgs`, and add the dry-run counting method both surfaces
consume. **This is a prerequisite for 6b's delete guard**, and it stands on its own: retrofit
`eas purge` / `eas device wipe` onto it in the same change, since they demand `--yes` over `/cli`
today purely because the forwarded console is non-interactive. It fixes an existing wart rather than
only serving the new command. Test the non-interactive client path explicitly: piped stdin must fail
with the question on stderr, never auto-confirm.

**6b. User lifecycle.** The config-declared bootstrap (rows created at startup for every declared
user, matched by login), `eas user rename` **with the config-declared immutability guard and
the collision check**, the startup reconciliation warning for config logins with no user, and the
delete guard on top of 6a. **Test the guards from both surfaces** — CLI and admin UI must both refuse
to rename a config-declared user, and both must refuse to delete a content-owning user without
confirmation.

**7. Docs + banner.** Startup banner shows the level each effective value came from. Rewrite the
user sections of `README.md`, `docs/configuration.md`, `docs/webui.md`, `docs/cli.md`, and
`AGENTS.md` § *Auth model*.

---

## Implementation hints — take them or leave them

**Non-binding.** These are one reading of how the pieces fit, written to save rediscovery, not to
constrain the implementer. Where a hint conflicts with what the code actually wants, the code wins —
but say so, because a hint being wrong is worth knowing.

**Mapping `AccountOptions` ⇄ columns (item 3).** `ActiveSyncOptions.Users` is
`Dictionary<string, AccountOptions>` (`ActiveSyncOptions.cs:404`), bound straight from config, so the
type must survive as the DTO. The smallest change is two private mappers inside the store —
`ToEntity(UserOptions) → User + UserBackendRole[]` and `FromEntity(...) → UserOptions` — leaving every
caller untouched. Model `UserBackendRoles` as an EF navigation collection on `User` so a load brings
the roles with it; on write, diff the incoming roles against the loaded ones by `Role` and
add/update/remove, rather than delete-all-and-reinsert (which would churn ids and defeat the FK
cascade's usefulness for auditing).

**Do the AAD first inside item 2, not last.** It is the one change whose mistakes surface only when
data will not decrypt. A concrete framing that keeps `K2`'s injectivity without relying on
control-character rejection: version tag, then fixed-width id, then length-prefixed collection —
e.g. `"v2" ‖ LE64(userId) ‖ LE32(len) ‖ collection`. On a blank database the version tag costs
nothing and buys a future re-key path.

**Give resolution a provenance-carrying return type (item 4), and item 7 comes free.** If `BuildOne`
resolves each field to `(value, level)` rather than a bare value, the startup banner's "which level
did this come from" requirement is already satisfied, and `eas user show` / the admin UI can show it
too. Retrofitting provenance later means touching every resolution site again.

**`Settings` per-key resolution (item 4).** Walk the levels **lowest-priority first**, applying each
level's keys over the accumulating dictionary: a value sets, a null removes. That yields
null-clears-inherited naturally. For list replacement, treat a key's list root as one unit —
`BackendConfigValidation.ListRoot` (`BackendConfigValidation.cs:17`) already computes it — and clear
sibling `root:N` keys before applying a level that sets any of them.

**One `BumpAsync(db, area)` helper for `DataChanges` (item 3a).** Both stores currently re-implement
read-row-or-insert. Put it in one place with the unique-violation catch, so a third watched area
later is a call, not a copy.

**Enforce shared rules in the shared service, not in each caller.** The device-scoped block (decision
19) and the config-declared rename guard both need to hold for the CLI *and* the admin API.
`DeviceAdminService` (`Administration/DeviceAdminService.cs:14`) is already the shared seam for device
work and the natural home; the user-side equivalent is the store/editing pipeline both surfaces
already share. Guarding only the CLI leaves the API able to write a shape the schema no longer has.

**Prove non-reuse with a schema test, not a comment (item 2).** Assert the generated SQLite DDL for
the `Users` id contains `AUTOINCREMENT`, and that the Npgsql column is an identity column. It is one
assertion, and it is the only thing standing between a future model tweak and cross-user data
disclosure through a recycled id.

**Sequence within item 2 to fail fast:** AAD change + its decrypt-across-rename test → `UserId`
column and FKs → provisioning move → delete `RequireDeclaredUsers`. Each step leaves the suite
runnable, and the riskiest part is proven before the mechanical bulk lands on top of it.

---

## Invariants that must survive

Violating any of these is a stop-and-report.

1. **`UserId` is THE identity — this DELIBERATELY REPLACES the current invariant.** `AGENTS.md`
   and `README.md` currently state the *gateway login* is the identity and that changing it orphans
   sync state and bricks encrypted rows. True today; removed by this design. **Update both in item
   2** — until then they contradict this document. What survives unchanged: **a per-backend user name
   is still never an identity**. The new hard rule: `UserId` is immutable and never reused.
2. **Auth precedence — REVISED as implemented (deviation 1):** gateway `Password` → MailStore
   provider probe → undeclared global probe. The brief's middle term, a timing-safe pinned compare
   against the configured MailStore `Password`, is gone: a backend credential never decides a device
   login. What preserves the property that pin was protecting is the **probe invariant** — a stored
   MailStore secret requires a gateway `Password`, so the probe is only ever reachable when it sends
   the presented credential.
3. **Fail closed.** An invalid/malformed user is kept visible but refused; one bad row never
   breaks auth for everyone.
4. **Live pickup (~1 s) survives** — the stamp point-read plus atomic snapshot swap, and
   `SnapshotChanged` still clearing the auth caches. The *mechanism* moves (`AccountsStamp` becomes
   the `"users"` row of `DataChanges`, decision 18); the *behaviour* must not: a CLI or admin edit
   still reaches every replica within `Auth:UsersRefreshSeconds`, with no restart.
5. **A `UserId` always exists past the auth boundary.** No caller handles a missing user; no
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

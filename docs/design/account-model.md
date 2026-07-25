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
- **All EF migrations are deleted**; the schema is regenerated from scratch.
- **Every existing deployment breaks.** In-place upgrade is not supported. This is accepted,
  deliberate, and the reason this design is affordable.

| Licensed | NOT licensed |
|---|---|
| Changing the persisted shape of accounts freely | Skipping tests because "it's a rewrite" |
| Renaming config keys and CLI verbs | Leaving the CLI/admin/portal write paths inconsistent |
| Deleting `LegacyAccountJson` and its upgrade path | Changing the plugin contract without a `ContractVersion` bump |
| Dropping back-compat shims and data migrations | Re-introducing whole-entry replacement (below) |

**There is no data to migrate.** Do not write migration code, provenance heuristics, or
"treat unflagged rows as X" fallbacks.

---

## Decisions (settled — do not re-open)

Recorded so a fresh session executes rather than re-litigates.

| # | Decision |
|---|---|
| 1 | **`AccountId` is an `int` identity column.** Not a GUID. Immutable, never reused. |
| 2 | **The login is a mutable attribute**, unique and case-folded. Everything else is mutable too. |
| 3 | **One stored value per field per account.** Admin and holder write the **same** slot; the difference between them is *permission*, not storage. |
| 4 | **Resolution is per field**, following the one rule below. Whole-entry replacement is deleted. |
| 5 | **`Settings` resolves per key**, by the same rule. |
| 6 | **`AutoProvisionUsers` → `AutoProvisionAccounts`**, default **true**; always creates an account on first successful auth; **false** refuses the undeclared login before any backend probe. |
| 7 | **`RequireDeclaredUsers` is deleted** — `AutoProvisionAccounts=false` now means exactly that. |
| 8 | **`eas user` → `eas account`, with no alias.** No legacy surface anywhere; this is a clean refresh. |
| 9 | **An admin may read and overwrite a holder's values, and a holder may overwrite an admin's** — same slot, last write wins. |

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

All eight entities FK to `AccountId` instead of carrying the login. Notifier keys, cache keys and the
encryption AAD key on `AccountId`. **A rename then becomes a single-row update**: sync state survives,
encrypted local items stay readable, the device keeps its folder registry and sync keys, and the
holder just updates the username on the phone.

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

---

## Where this lives today

| File | Role |
|---|---|
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

One item ≈ one session, in order. Follow `docs/review/fix-review.md`'s working protocol: red-first
tests, commit per unit of work, build at 0 warnings, and the live suite for anything touching the
schema, auth or the request pipeline.

**1. Vocabulary, no behaviour change.** `ActiveSync:Users` → `ActiveSync:Accounts`,
`AutoProvisionUsers` → `AutoProvisionAccounts` (rename only; semantics land in item 2), `eas user` →
`eas account` (no alias), "user" → "account" for the persistent record in code and docs. Mechanical,
large diff, zero semantic change — do it first so later diffs read cleanly.

**2. `AccountId` + provisioning.** Add the identity column; FK all eight entities to it; move
notifier keys, cache keys and the `LocalContentProtector` AAD onto it; keep the login as a unique
case-folded attribute; land `AutoProvisionAccounts`' semantics and delete `RequireDeclaredUsers`.
Regenerate the schema (no migration). **Acceptance tests, non-negotiable:** (a) rename a login and
assert sync state survives and an encrypted `LocalItem` written before the rename still decrypts
after it; (b) past the auth boundary an `AccountId` is always present — an undeclared login either
gets an account or is refused; (c) the id column is non-reusing on both providers. Update `AGENTS.md`
and `README.md` in the same work (Invariant 1).

**3. Per-field resolution.** Replace whole-entry replacement with the resolution rule across levels
1–5. Round-trip tests for: account DB beats account config; clearing an account value reverts to
config, then to global DB, then global config, then default; a global DB change reaches every account
live; `Settings` resolves per key with list-replacement and null-clear intact.

**4. Write paths + permissions.** One slot, both writers; `AccountFieldPaths` unchanged in shape but
now addressing a single layer. Enforce the permission table: assert the holder cannot write
`Enabled`/`Provider`/account-level fields or non-`SelfServiceEditable` settings keys, for every role
and provider. That assertion is the security property of this design.

**5. Docs + banner.** Startup banner shows the level each effective value came from. Rewrite the
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
  passing one. Items 2, 3 and 4 change the schema, auth and the request pipeline, so the live suite is
  mandatory for them; item 2 especially, since re-keying every entity is exactly the class of change
  whose blast radius no unit suite can see.

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

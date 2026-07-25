# Account model — layered redesign (forward design, NOT current behaviour)

> **Status: proposed, not implemented.** Nothing in this document describes how the gateway works
> today. It is a design brief to be executed on the **schema reinit** (see Standing context). Do not
> cite it as documentation of current behaviour; `AGENTS.md`, `README.md` and `docs/webui.md` are the
> authority on what exists now.

**Who this is for:** an agent or contributor implementing the redesign in a fresh session, with no
knowledge of the conversation that produced it. Everything needed to execute is here or named here.

**Read first, in this order:** `AGENTS.md` (§ *Auth model*, § *State store*, § *Web UI layer notes*),
`docs/webui.md`, `docs/configuration.md`, then the code named in "Where this lives today". If any of
those contradicts this document, **stop and report** — one of the two is wrong and that is a human
decision.

---

## Standing context — the enabling constraint

This design is **gated on a clean-slate schema reinit** that the project owner has confirmed is
acceptable and planned:

- The GitHub repository is squashed / reinitialised.
- **All EF migrations are deleted** and the schema is regenerated from scratch.
- **Every existing deployment breaks.** Upgrading in place is not supported; a new deployment is
  required. This is accepted, deliberate, and the reason this design is affordable.

What that licenses, and what it does not:

| Licensed | NOT licensed |
|---|---|
| Changing the persisted shape of accounts freely | Breaking the *gateway login* as the identity (see Invariants) |
| Renaming config sections (`ActiveSync:Users` → `ActiveSync:Accounts`) | Skipping tests because "it's a rewrite" |
| Deleting `LegacyAccountJson` and its upgrade path entirely | Leaving the CLI/admin/portal write paths inconsistent |
| Dropping back-compat shims and data migrations | Changing the plugin contract without a `ContractVersion` bump |

**There is no data to migrate.** Do not write migration code, provenance-inference heuristics, or
"treat unflagged rows as X" fallbacks. Their absence is the whole reason this design beats the
alternatives that were rejected (below).

---

## The problem

### 1. The vocabulary is genuinely ambiguous

The codebase currently uses, for overlapping concepts: **Users** (the `ActiveSync:Users` config
section, and `UsersFile`), **accounts** (`AccountEntry` DB rows, `eas user ...`, the admin "Users"
page), **declared** vs **synced** vs **auto-provisioned**, `MergedAccount`, `ResolvedAccount`,
`AccountTemplate`, `AccountOptions`, `BackendRoleOverride`. Some of these are real distinctions and
some are the same thing named twice.

**Decision — settle it as follows, and rename in code:**

| Term | Means | Notes |
|---|---|---|
| **Account** | The persistent record for one gateway login | The single word for the thing. Replaces "user"/"account" used interchangeably |
| **Login** | The identity string the phone sends | Unchanged; still THE identity (Invariants) |
| **Layer** | One provenance-scoped set of overrides on an account | New concept; the heart of this design |
| **Effective** | The result of collapsing all layers for a role/field | What backends actually connect with |
| **Resolved*** | The compiled, per-request view (`ResolvedAccount`/`ResolvedRole`) | Keep these names; they already mean "effective, compiled" |

Retire the word "user" for the persistent record. Keep it only for the human. `ActiveSync:Users`
becomes `ActiveSync:Accounts`; `eas user` becomes `eas account` (with `user` as a hidden alias only
if the owner wants it — see Open questions).

### 2. One flat shape, three writers, no field provenance

`AccountOptions.Backends[role]` is a `BackendRoleOverride { Enabled, Provider, UserName, Password,
Settings }` — **one slot per field** — and it is written by three different actors with three
different authorities:

| Writer | File | Authority |
|---|---|---|
| CLI `eas user set` | `src/ActiveSync.Server/Cli/UserCommands.cs` | Administrator |
| Admin API | `src/ActiveSync.WebUi/Api/UsersEndpoints.cs` | Administrator |
| Self-service portal | `src/ActiveSync.WebUi/Api/PortalEndpoints.cs` | The account holder |

Provenance is tracked **per entry** — `MergedAccount(Options, FromDatabase, ShadowsConfig, Invalid)`
at `src/ActiveSync.Core/Accounts/AccountResolver.cs:21` — but **never per field**. Once a value is
written, nothing records who wrote it.

Three concrete consequences, all real today:

- **`C5` (closed `N/A` only because the fix was disproportionate).** `GET /user/api/me` echoes an
  admin-set backend `userName` — often a shared service account — to the non-admin account holder.
  It cannot be withheld selectively, because nothing knows whether the admin or the holder set it.
- **The destruction hazard (not a filed finding; arguably worse than `C5`).**
  `PortalEndpoints.cs:233` sets `UserName` for **any** role with no `SelfServiceEditable` check. The
  holder can overwrite an administrator's credential, **destroying it** — the previous value is gone
  and their own sync breaks until an admin restores it by hand. Today the *only* thing mitigating
  this is that the portal shows the value, i.e. `C5`'s "defect" is currently load-bearing as a
  safety feature. That is a design smell, and this redesign removes the trade entirely.
- **Admin/holder edit collision.** Two authorities writing one slot means last-write-wins with no
  record, no attribution, and no way for an admin to answer "did I set this, or did they?".

### 3. Two rejected alternatives, and why (do not re-attempt)

Both were tried or costed during the round-2 review. Recorded so this is not rediscovered:

- **Gate the read on the *settings* self-service surface** — implemented as `HasSelfServiceSurface`
  in review item 12 (commit `7f0c73b`), **reverted** (`fec1cfe`). Wrong surface: backend
  *credentials* are unconditionally self-editable while *settings* are gated, so the gate withheld a
  value the same caller could freely rewrite, broke the write/read round trip, and failed two live
  integration tests. **Never re-apply a settings-surface gate to a credential field.**
- **A per-field provenance flag** (`UserNameSetBySelf` on `BackendRoleOverride`) — costed and
  rejected. `BackendRoleOverride` is *both* config-bound (`ActiveSync:Users`, where a provenance
  flag is meaningless yet settable) *and* JSON-serialised into `AccountEntry`; the flag needs a
  clear-it-on-every-admin-write invariant that drifts silently the first time a write path forgets,
  and a flag that fails open is worse than no flag because it reads as closed.

**The layered design below beats both because provenance becomes structural — a value's authority is
*where it is stored*, not a flag someone must remember to maintain.**

---

## The design

### Shape

An account holds **two override layers** plus its identity fields:

```
Account
  Login            string           // THE identity — never changes meaning (Invariants)
  MailAddress      string?
  Password         string?          // gateway password (pbkdf2$ / plaintext)
  Admin            bool?
  Enabled          bool?
  OidcSubject      string?
  AutoProvisioned  bool?

  Administered     AccountLayer     // written by administrators ONLY
  SelfService      AccountLayer     // written by the account holder ONLY
```

where a layer is the existing per-role override shape, unchanged in content:

```
AccountLayer
  Backends  Dictionary<role, RoleOverride { Enabled, Provider, UserName, Password, Settings }>
```

The identity fields stay single-slot: they are administration (`Admin`, `Enabled`, `OidcSubject`,
`AutoProvisioned`) or already have their own self-service path with a current-password check
(`Password`). Only the per-role overrides are layered. **Do not layer `Enabled`/`Provider`** — those
change serving topology and stay admin-only, exactly as `PortalEndpoints`' header comment already
says.

### Precedence

For each role and each field, most specific wins:

```
SelfService  →  Administered  →  global role section  →  pass-through default
```

A **null/absent** value in a layer means "not set at this layer", falling through. Clearing a
self-service value therefore **reverts to the administered value** — it does not clear the field.
This is the single most important behavioural property of the design.

Config-declared accounts (`ActiveSync:Accounts`) populate the **Administered** layer: config *is*
administration. A config account has an empty SelfService layer until its holder writes one.

### Read rules

| Caller | Sees |
|---|---|
| Account holder (`GET /user/api/me`) | Its **own SelfService layer verbatim**; for Administered, per-role `enabled`/`provider` and a `userNameSet`/`passwordSet` **boolean only** — never an administered credential value |
| Administrator (CLI, admin API) | **Both layers, labelled**, plus the effective result |
| Backends / sync | Only the **effective** result (`ResolvedAccount`), as today |

The portal UI renders an unset self-service field with the administered state as a dimmed
placeholder — "configured by your administrator" — matching the existing
default-as-placeholder convention in `AGENTS.md` § *Web UI layer notes*. The holder can always see
*that* a value is administered and *that* they may override it, without seeing the value.

### Write rules

| Writer | May write | May NOT |
|---|---|---|
| Portal | `SelfService` only | Touch `Administered` in any way |
| CLI / admin API | `Administered`; may **clear** a holder's `SelfService` layer as an explicit "reset self-service" action | Silently edit `SelfService` as though it were their own |

An administrator resetting a holder's self-service layer is a distinct, named operation — not a
side effect of an ordinary admin edit.

### What this fixes

- **`C5` disappears as a class**, not as a patch: `me` structurally cannot echo an administered
  credential, because it reads a different layer. No flag, no gate, no drift.
- **The destruction hazard disappears.** A portal write lands in `SelfService`; the administered
  value is untouched and recoverable by clearing the override. Breaking your own sync becomes
  **recoverable instead of destructive** — a strict improvement over today independent of `C5`.
- **Attribution becomes free.** "Who set this?" is answerable by construction, for support, for the
  admin UI, and for the startup banner.
- **`B5` gets easier** (`Users` config secrets unsealed into the long-lived snapshot,
  `AccountResolver.cs:446`): with layers separated, a lazy per-layer unseal is a smaller change.
  Not required by this design — note it, do not scope-creep into it.

---

## Where this lives today

Read these before designing anything; they are the surface that changes.

| File | Role |
|---|---|
| `src/ActiveSync.Core/Options/AccountOptions.cs` | `AccountOptions` + `BackendRoleOverride` — the shape to split |
| `src/ActiveSync.Core/Accounts/AccountResolver.cs` | `MergedAccount` (:21), `BuildSnapshot` (:277), `BuildOne` (:355), `ResolveSecret` (:531) — the merge core |
| `src/ActiveSync.Core/Accounts/ResolvedAccount.cs` | Compiled per-request view; **keep this shape** |
| `src/ActiveSync.Core/Accounts/AccountStore.cs` | DB persistence; note `NormalizeLogin` case-folding (`B1`) |
| `src/ActiveSync.Core/Accounts/PassThroughProvisioner.cs` | Auto-provisioning |
| `src/ActiveSync.Core/Administration/AccountFieldPaths.cs` | The `Backends:<Role>:<Field>` addressing scheme — **needs a layer segment** |
| `src/ActiveSync.Core/Administration/AccountEditing.cs` | Shared edit pipeline for CLI + web |
| `src/ActiveSync.Core/Administration/AccountSecretPolicy.cs` | Seal/hash on write |
| `src/ActiveSync.Server/Cli/UserCommands.cs` | CLI writer |
| `src/ActiveSync.WebUi/Api/UsersEndpoints.cs` | Admin writer |
| `src/ActiveSync.WebUi/Api/PortalEndpoints.cs` | Portal writer (`me` at :36, PUT at :233) |
| `src/ActiveSync.Core/Accounts/LegacyAccountJson.cs` | **Delete** — the reinit removes its reason to exist |

---

## Implementation plan

One item ≈ one session. Work in order; each lands on a clean tree and is independently committable.
Follow `docs/review/fix-review.md`'s working protocol (red-first tests, commit per unit of work,
build 0 warnings, live suite for anything touching auth or the request pipeline).

**1. Terminology + rename, no behaviour change.** `ActiveSync:Users` → `ActiveSync:Accounts`,
`eas user` → `eas account`, "user" → "account" for the persistent record throughout code and docs.
Mechanical, large diff, zero semantic change. Do it first so every later diff reads cleanly. Update
`docs/configuration.md`, `docs/cli.md`, `docs/webui.md`, `README.md`, `AGENTS.md`.

**2. Split the shape.** Introduce `AccountLayer`; move `Backends` into `Administered`. Leave
`SelfService` present but always empty and unwritten. Regenerate the schema (no migration — reinit).
Everything still behaves as today because only one layer is populated. This is the load-bearing
structural commit; keep it behaviour-free and prove it with the existing suites unchanged.

**3. Teach the resolver the precedence.** `BuildOne` collapses `SelfService → Administered → global
→ pass-through`. Add round-trip tests for: self overrides admin; clearing self reverts to admin;
absent both falls to global; `Enabled`/`Provider` ignore the self layer entirely.

**4. Route the writers.** Portal writes `SelfService`; CLI/admin write `Administered`; add the
explicit admin "reset self-service" operation. `AccountFieldPaths` gains a layer segment. Prove each
writer cannot reach the other layer — that assertion is the security property of the whole design.

**5. Read rules + portal UI.** `me` returns the self layer verbatim and administered state as
booleans only. Portal renders administered values as placeholders. Admin UI/CLI show both layers
labelled. **This is where `C5` is actually closed** — re-open `C5` in
`docs/review/review-items.md`, or file a successor finding, and strike it against this work rather
than leaving the `N/A` standing.

**6. Docs + banner.** Startup banner shows origin per layer. Rewrite the account sections of
`README.md`, `docs/configuration.md`, `docs/webui.md`, `docs/cli.md`, and `AGENTS.md` § *Auth model*.

---

## Invariants that must survive

Violating any of these is a stop-and-report, not a judgement call. All are from `AGENTS.md`.

1. **The gateway login is THE identity.** DB row scoping (`Device`/`UserFolder`/`LocalItem.UserName`),
   `LocalChangeNotifier` keys, the `LocalContentProtector` AAD and session/watcher cache keys are all
   the gateway login — never a per-backend user name. Changing this orphans sync state and makes
   encrypted local rows undecryptable.
2. **Auth precedence is unchanged:** gateway `Password` → configured MailStore `Password` (timing-safe
   pinned compare) → MailStore provider probe → undeclared global probe.
3. **Fail closed.** An invalid/malformed account is kept visible but refused; one bad row never
   breaks auth for everyone.
4. **Live pickup (~1 s)** via the `AccountsStamp` point-read and atomic snapshot swap survives, and
   `SnapshotChanged` still clears the auth caches.
5. **`RequireDeclaredUsers`** (allowlist) and **`AutoProvisionUsers`** semantics are preserved; an
   auto-provisioned account is an empty **Administered** layer plus the marker.
6. **Secrets never leave the server** — the existing leak-guard test (no `pbkdf2$` / `enc:v1:` in any
   response) must pass against **both** layers.
7. **MailStore + MailSubmit stay mandatory**; content roles still fall back to `local`.

---

## Verification

- Unit: `dotnet test ActiveSync.slnx --filter "Category!=Integration"` — 0 warnings, no skips.
- Live: `./scripts/stalwart-up.ps1` then
  `dotnet test tests/ActiveSync.Integration.Tests --filter Category=Integration`. **Read the
  passed/skipped counts, never the exit code** — a skipped suite exits 0 and looks identical to a
  passing one. Items 4 and 5 change auth and the request pipeline, so the live suite is mandatory
  for them.
- The security property of the design is a *test*, not a review comment: assert that a portal request
  cannot mutate `Administered`, and that `me`'s response never contains an administered credential
  value, for every role and provider.

---

## Open questions for the human

Decide before item 1; each changes real work:

1. **Keep `eas user` as a hidden alias for `eas account`?** Muscle memory and any existing scripts
   versus one clean vocabulary. (Recommendation: alias it, undocumented.)
2. **May an administrator *see* a holder's self-service values?** Support argues yes; the layering
   makes either enforceable. (Recommendation: yes, labelled — an admin can already reset them.)
3. **Should `Settings` be layered too, or stay admin-only-with-`SelfServiceEditable`?** This design
   layers only credentials and leaves the existing `SelfServiceEditable` gate on settings. Layering
   settings as well would be more uniform but is a bigger change.
4. **Does the config section stay a full account source, or become administered-defaults only?** The
   current "a DB row REPLACES the whole config entry" rule (`AccountResolver.cs:320-341`) is a wart
   the reinit could remove — e.g. config populates Administered, DB overrides per field, no
   whole-entry replacement.

---

## Confidence

The **problem statement** is verified against the code at the files and line numbers named above.
The **design** is a proposal: it has not been prototyped, and items 3–5 are where unknowns will
surface (particularly `AccountFieldPaths`' addressing and the portal UI's placeholder rendering).
Treat the layering and precedence as settled, the item breakdown as a starting plan, and expect to
re-scope items 4–6 once item 2 is real.

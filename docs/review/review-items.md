# ActiveSync Gateway — source review findings (Round 3)

> **Execution protocol: [`../fix-review.md`](fix-review.md) — read it first.**
> This file holds only project data: the findings index, the work queue, the commands, the invariants.
> Nothing here describes *how* to work; that lives in `fix-review.md` and does not change.
> Full technical detail for every finding is in [`review-items-detail.md`](review-items-detail.md) —
> **read a finding's detail entry before implementing it.** Results of completed items are recorded by
> the fix orchestrator in [`review-results.md`](review-results.md).

**Scope:** all production code under `src/` — ~37.6k lines of C# across 14 projects, plus the 2.6k-line
no-build admin/portal SPA (`ActiveSync.WebUi/wwwroot`, reviewed as code, not as assets). Tests, docs and
CI read for context only. The ~3.8k lines of EF migration scaffolding were reviewed for hygiene and
cross-provider lockstep rather than line by line.

**Method:** ten parallel subsystem passes (A Core state/sync/backend · B Core accounts/administration/
settings/options · C WebUi incl. the SPA · D Backends.Common converters + shared HTTP/TLS helpers ·
E Server pipeline/hosting/startup/CLI · F EAS handlers · G Backends Imap/Smtp/Sieve/Local · H Backends
DAV/JMAP · K Security/Crypto/Plugins/Contracts · W WBXML/protocol) plus a cross-cutting structural pass
(S) run by the coordinator. Each agent read its files in full and was **withheld from rounds 1 and 2** so
this pass is independent; cross-round reconciliation is recorded in
[`claimed-fixed-but-not.md`](claimed-fixed-but-not.md).

**This is round 3.** Round 1 (365 findings, all 56 items landed) is archived under [`round1/`](round1/).
Round 2 (132 findings; items 1–25 landed, 26–32 never worked) is archived under [`round2/`](round2/).
Since round 2 the **db-restructure** landed — a rewrite of the user model (per-field resolution,
`UserId` as durable identity, normalised `User`/`UserBackendRole` tables, and a schema reinit down to one
baseline migration per provider). Area B and the user-facing halves of C and E are therefore reviewing
largely new code, and several findings are drift between that design and its documentation.

**Baseline commit:** `406b83f` — every `file:line` is exact as of this commit. Line numbers drift as items
land; locate by symbol. See "Locating a finding after code has moved" in `fix-review.md`.

**Baseline health at `406b83f`:** build **0 warnings** · **1270 unit tests green, 0 skipped**
(Cli 16 · Protocol 91 · Core 771 · WebUi 101 · Server 291) · integration suite skips locally when no
backend is reachable (8 skipped).

> **Note on the build baseline:** an in-tree `dotnet build` while a gateway process is running reports
> ~110 "warnings" that are MSB3026 file-copy retries against locked DLLs, not compiler warnings. Verify
> the real count with the gateway stopped, or build to a separate `-p:BaseOutputPath`.

**Totals:** 245 findings — 0 Critical, 25 High, 84 Medium, 105 Low, 31 Nit · 37 work items.

---

## Invariants

These never change as work progresses — striking a finding through does not remove it. Any drift means an
edit went wrong. See "Integrity check" in `fix-review.md`.

| Invariant | Value |
|---|---|
| Work items | **37** |
| Items marked [LIVE] | **14** |
| Findings assigned | **245** |
| Findings unique | **245** |
| Duplicate assignments | **0** |
| Encoding-damage matches | **0** |

```sh
sed -n '/^# WORK QUEUE/,/^# FINDINGS/p' docs/review/review-items.md > /tmp/q
echo "items=$(grep -cE '^\*\*[0-9]+\. ' /tmp/q) live=$(grep -cE '^\*\*[0-9]+\..*\[LIVE\]' /tmp/q)"
grep -E '^\*\*[0-9]+\. ' /tmp/q | grep -o '`[ABCDEFGHKSW][0-9]\+`' | tr -d '`' | sort > /tmp/f
echo "assigned=$(wc -l </tmp/f) unique=$(sort -u /tmp/f|wc -l) dupes=$(uniq -d /tmp/f|wc -l)"
grep -c $'\xc3\xa2\xc2\x80\|\xc3\xb0\xc2\x9f' docs/review/review-items.md
```

---

## Orientation documents

Read before touching the areas they cover — see "Orient before you start" in `fix-review.md`. These carry
constraints the code does not state and a reasonable change will violate silently.

| Document | Read before touching | Contains |
|---|---|---|
| [`AGENTS.md`](../../AGENTS.md) | **any** item | Solution layout and the dependency rule; per-layer invariants; coding conventions; decisions already taken and why |
| [`README.md`](../../README.md) | first item in an unfamiliar area | what the project is, how the pieces fit |
| [`docs/design/db-restructure.md`](../design/db-restructure.md) | items touching users, settings, admin UI or the CLI | the user-model design that just landed — several findings are code/design drift against it |
| [`docs/testing.md`](../testing.md) | any [LIVE] item | backend stacks, how the suites skip, which runner to use |
| [`docs/plugins.md`](../plugins.md) | items 10, 36 | the published plugin contract |
| [`round1/`](round1/) · [`round2/`](round2/) | when a finding touches an area a prior round worked | what was already fixed and why — do not re-litigate a settled decision without cause |

**Hard gates:**

- `AGENTS.md` § *Protocol layer invariants* — **read before touching `src/ActiveSync.Protocol/`**
  (items 16, 31, 37). Code-page tables are transcribed from MS-ASWBXML: never guess, and **every table
  change needs a round-trip test**. The OPAQUE marker attribute is a convention every producer and
  consumer relies on.
- `AGENTS.md` § *Solution layout and dependency rule* — the authority on which assembly may reference
  which. **Note `S1`: that section is currently wrong about `Backends.Common`.**
- `AGENTS.md` § *Sync model* — the SyncKey lifecycle, windowing and full-enumeration posture. Items 1, 5,
  23, 24 must not break "the revision map is the whole truth" or the N−1 replay invariant.
- > ### ⛔ CHANGING THE CONTRACT SURFACE? RAISE `ContractVersionMinor` FIRST.
  > Items 1, 36 and 37 touch `ActiveSync.Contracts` / `ActiveSync.Protocol`. Any public-member change
  > requires raising `<ContractVersionMinor>` in `Directory.Build.props`, updating the literal in
  > `ContractSurfaceTests`, and regenerating the approved snapshot with
  > `EAS_APPROVE_CONTRACT_SURFACE=1`. **Raising `ContractVersionMajor` is a HUMAN decision — never do it
  > unless explicitly asked.**

If an orientation document contradicts a finding, **stop and report it**. One of the two is wrong, and
that is a human decision.

---

## Project commands

**Build** — baseline is **0 warnings**; treat any new one as a failure:

```powershell
dotnet build ActiveSync.slnx
```

**Single test** — run per finding for red-green (seconds):

```powershell
dotnet test tests/ActiveSync.Core.Tests --filter "FullyQualifiedName~YourNewTestName"
dotnet test tests/ActiveSync.Server.Tests --filter "FullyQualifiedName~YourNewTestName"
dotnet test tests/ActiveSync.WebUi.Tests --filter "FullyQualifiedName~YourNewTestName"
dotnet test tests/ActiveSync.Protocol.Tests --filter "FullyQualifiedName~YourNewTestName"
```

**Unit suite** — run once per item, before the last commit (~40 s):

```powershell
dotnet test ActiveSync.slnx --filter "Category!=Integration"
```

Baseline at `406b83f` was **1270 passed, 0 skipped**; it grows as items add tests. The last verified figure
is in the most recent [`review-results.md`](review-results.md) entry.

**Live suite** — required for [LIVE] items, **and** for any item landing an EF migration, changing auth or
cookie policy, or altering the HTTP pipeline:

```powershell
./scripts/stalwart-up.ps1      # canonical ports; reuses a warm container, ~15 s
dotnet test tests/ActiveSync.Integration.Tests --filter Category=Integration
```

**Fresh restart** (clean volume) — for the parallel-restart rule in `fix-review.md`:

```powershell
./scripts/stalwart-up.ps1 -Down   # tear down, discarding the accumulated volume
./scripts/stalwart-up.ps1         # bring up fresh; probes all four ports before returning
```

**Environment gotchas:**

- **A skipped suite exits 0 and looks exactly like a passing one.** Read the passed/skipped counts, never
  the exit code. The local baseline showed the integration suite **skipping** (8 skipped, no backend up) —
  a [LIVE] item MUST bring a backend up and confirm passed > 0.
- **Stop any locally running gateway before building.** A running `ActiveSync.Server` holds the output DLLs
  and turns a clean build into ~110 copy-retry warnings plus MSB3021 errors that look like real failures.
- `stalwart-up` puts Stalwart on the **canonical** ports (143/587/4190/5232), `TestBackend`'s built-in
  defaults — no `AS_TEST_*` setup needed.
- `scripts/test-fast` also covers Axigen but rebuilds/recreates both stacks each run and needs `AS_TEST_*`
  overrides. **Do not alternate between the two runners** — same compose project, different port sets.
- Some findings are backend-specific. Verify JMAP findings against the `stalwart` stack, CalDAV/CardDAV
  against a DAV-capable stack. `H2` and `H5` specifically describe **Axigen** behaviour (async indexing) —
  `scripts/test-fast` runs Axigen.

---

## Standing context

- **Breaking changes are acceptable and preferred where they yield a better design.** Not deployed outside
  testing; the published NuGet packages have no external consumers. Several items are intentionally
  breaking (`K9`/`K12` change published `TransientRetry` signatures; `K5` fails closed on weaker stored
  hashes; `H8` changes a JMAP capability set; `D3` changes stored all-day dates). Take them, and write the
  breaking consequence into the finding's own text and the results entry.
- **Do not push.** Commit freely; pushing is a human decision.
- **The build baseline is 0 warnings.** Treat a new warning as a failure.
- **Do not touch `review-results.md`** — the fix orchestrator owns it.
- **`ContractVersion` is 1.1 and the last surface change bumped it correctly.** Any item that changes the
  Contracts/Protocol public surface must bump the minor in the same change (see the hard gate above).
- **Several findings are documentation-only fixes by design** (`B9`, `B10`, `K10`, `K15`, `K16`, `S1`,
  `A11`, `A12`, `A13`, `C11`, `C17`, `H25`, `W20`). Those close by correcting the doc/comment — say so
  explicitly in the results entry so the strike is not read as a behaviour change.

---

# WORK QUEUE

**37 items, each sized for one session, in the order to run them.** Say *"Implement item 12"* — no
sub-choices, no sizing decisions. Phase headings are context only; the numbering is a straight line.

Findings are grouped by *what breaks* and by *which files they touch*, so an item is one coherent piece of
work. Every finding ID appears in exactly one item.

---

## Phase 1 — Data loss, silent corruption, and security
*The Highs and the Mediums that ride with them. Start here.*

**1. Lost server-to-client changes** [LIVE] — ~~`F3`~~ ~~`F2`~~ ~~`K2`~~ **COMPLETE**
> The single worst family in this round. `F3`: a `Change` whose render fails is recorded in the snapshot as
> delivered, so the edit **never reaches the phone and no later diff re-offers it** — the sibling `Add` loop
> three lines above has the rollback this one lacks. `K2` is the same defect stated at the contract
> (`IContentStore.GetItemsAsync` documents a promise only the Add path keeps). `F2`: when every item in a
> round fails to render the round never commits, so Ping/Sync hot-loop forever. Touches Contracts — mind the
> version gate.

**2. Send-once integrity** [LIVE] — ~~`F1`~~ ~~`G4`~~ ~~`F7`~~ ~~`F8`~~ ~~`F9`~~ ~~`F12`~~ **COMPLETE**
> Every irreversible-send path that lacks a guard. `F1` SendMail/SmartReply/SmartForward ignore
> `composemail:ClientId`, so a lost 200 duplicates the mail. `G4` the SMTP DATA phase observes
> `RequestAborted`, so a dropped connection reports failure on an accepted message. `F7` MeetingResponse has
> no dedup claim → duplicate iTIP REPLY. `F8`/`F9` respond for the whole series and default a malformed
> UserResponse to Accept. `F12` SendMail-by-reference hard-deletes whatever it was pointed at.

**3. DAV credential boundary** [LIVE] — ~~`H1`~~ ~~`D26`~~ ~~`H24`~~ **COMPLETE**
> `H1` `WebDavClient.Resolve` returns an absolute server-supplied href verbatim, and the Basic header rides
> every request — a malicious CalDAV server harvests the user's mail password with a legal RFC 4918
> construct. `D26` is the same hole at the `RedirectingHttpSender` seam; one guard closes both. `H24` is the
> one JMAP call site missing the same assertion.

**4. DAV create: cost and href resolution** [LIVE] — ~~`H2`~~ ~~`H10`~~ ~~`H13`~~ ~~`H20`~~ **COMPLETE**
> `H2` on a server whose listings lag a PUT (Axigen — a CI backend) a single Sync Add fetches **every**
> existing item in the collection. `H10` a create-PUT 409 is misread as success → phantom snapshot entry the
> next diff deletes. `H13` a replayed update-PUT surfaces a spurious ETag conflict. `H20` a missing ETag
> falls back to a random GUID, guaranteeing a re-send.

**5. JMAP listing & submission integrity** [LIVE] — ~~`H3`~~ ~~`H18`~~ ~~`H8`~~ ~~`H9`~~ **COMPLETE**
> `H3` position-based paging over a descending sort means a concurrent delete drops a live message from the
> revision map, and the diff engine **deletes it from the phone**. `H18` the same loop can spin forever
> against a server that reports `position: 0`. `H8` `Email/import` demands an RFC 9404 capability it does
> not need, breaking drafts on servers without it. `H9` submission never gates on the submission capability
> and uses the mail account id.

**6. ManageSieve protocol safety** [LIVE] — ~~`G1`~~ ~~`G2`~~ ~~`G5`~~ ~~`G10`~~ ~~`G17`~~ ~~`G23`~~ ~~`G24`~~ **COMPLETE**
> `G1` literal-encoded script names lose their `ACTIVE` flag, so turning Oof off **deactivates the user's own
> spam/forwarding rules**. `G2` a server-controlled literal length is used as an allocation size (OOM).
> `G5` no I/O timeout anywhere, and an uncancellable LOGOUT. `G10`/`G17`/`G23`/`G24` SASL negotiation,
> control-character injection, orphaned scripts, a dead guard.

**7. Calendar & draft data corruption** [LIVE] — ~~`D1`~~ ~~`D2`~~ ~~`D3`~~ ~~`D5`~~ **COMPLETE**
> `D1` meeting-request times are read from VTIMEZONE, so every zoned invitation shows a 1970 start. `D2` a
> draft with two or more recipients loses **all** of them. `D3` all-day events land a day early for half the
> year (DST ignored in the MS-ASTZ read-back). `D5` `AllDayEvent` is the one calendar field with no
> ghosting guard, so a partial 16.x Change converts an all-day event to timed.

**8. Backend session lifetime & metric cardinality** — ~~`A1`~~ ~~`A2`~~ ~~`A3`~~ ~~`A10`~~ **COMPLETE**
> `A1` a partially-built composite session leaks every connection it already opened — a phone Pinging against
> a half-broken configuration leaks ~120 sockets/hour until the healthy backend starts refusing logins.
> `A2` `DisposeAsync` can throw into the request's `await using` after the response is written. `A3` an
> unauthenticated caller controls an unbounded Prometheus label. `A10` a static observer is never detached.

**9. TLS certificate lifecycle** — ~~`K1`~~ ~~`K11`~~ ~~`K13`~~ ~~`K18`~~ **COMPLETE**
> `K1` the self-signed certificate is **never renewed inside a running process** — the 397-day cap landed,
> the renewal did not, so a long-lived gateway serves an expired leaf and every phone stops connecting.
> `K11` no `NotBefore` check. `K13` an unzeroed PKCS#12 holding the private key. `K18` a misleading error
> when the race fallback finds no row.

**10. Plugin trust boundary** — ~~`K3`~~ ~~`K4`~~ ~~`K19`~~ **COMPLETE**
> `K3` the pin hashes `*.dll` only, so on the Linux image a plugin's native `.so` payload and its
> `.deps.json` can be swapped with the pin still matching — `docs/plugins.md`'s "byte-for-byte what you
> reviewed" is false as implemented. `K4` `RequirePinned` fails **open** on `1`/`yes`/`on`. `K19` the
> contract gate defaults to the permissive version when unreadable.

**11. Password & throttle hardening** — ~~`K5`~~ ~~`K6`~~ ~~`K7`~~ ~~`K21`~~ **COMPLETE**
> `K5` the stored hash **length** is unbounded, so a lower-privilege writer can plant a credential that costs
> minutes of PBKDF2 per login attempt. `K6` filling the throttle table from one address disables protection
> for every other address. `K7` the WebUi login clears the per-address ceiling, voiding the class's own
> guarantee. `K21` `Hash` can mint a value `Verify` rejects.

**12. WebUi session, authorization & OIDC** — ~~`C1`~~ ~~`C3`~~ ~~`C9`~~ ~~`C16`~~ **COMPLETE**
> `C1` a portal password change signs the user out on their very next request (second-truncated session stamp
> vs. sub-second revocation cut-off) — the feature is documented as doing the opposite. `C3` the user-delete
> route can destroy the last enabled administrator, bypassing the 409 guard every weaker verb enforces.
> `C9` an unbound config-declared account under OIDC still gets a full portal session, from which the
> impersonator can set that account's gateway password. `C16` dead `blocked` parameter.

**13. User-resolution resilience** — ~~`B1`~~ ~~`B11`~~ ~~`B17`~~ **COMPLETE**
> `B1` one invalid *config*-declared user makes `EnsureFreshAsync` throw before advancing the stamp, freezing
> **all** database user pickup until restart — `eas user disable bob` reports success and never reaches any
> replica. `B11` a throwing subscriber is mislogged as a refresh failure and aborts the remaining handlers.
> `B17` an `ActiveSync:Users` edit in a reloadable file lands at an arbitrary unrelated moment.

**14. Metrics listener exposure & tier** — ~~`E1`~~ ~~`E2`~~ ~~`B3`~~ **COMPLETE**
> `E1` the dedicated metrics listener serves the **entire application** — opening `Metrics:Port` to a
> monitoring network also exposes `/admin`, `/user` and the EAS endpoint over cleartext HTTP. `E2`/`B3` are
> one defect found from both sides: `Metrics:PerUser` is catalogued and documented as live-tier but is read
> once at startup, so `eas config set` reports success and changes nothing.

**15. Find & ItemOperations conformance** [LIVE] — ~~`F4`~~ ~~`F5`~~ ~~`F6`~~ ~~`F10`~~ ~~`F11`~~ **COMPLETE**
> `F4` a mailbox-wide Find returns results with **no ServerId and no CollectionId**, so nothing the user taps
> can be opened — a feature the README and the 16.1 checklist both mark done. `F6` ItemOperations and Search
> never set `BodyPreference.Eas16`, so a 16.x client silently loses event locations and attachments.
> `F5`/`F10`/`F11` status classification.

**16. WBXML untrusted-input hardening** — ~~`W1`~~ ~~`W2`~~ ~~`W4`~~ ~~`W5`~~ **COMPLETE**
> **Protocol layer — read the AGENTS.md hard gate; any table change needs a round-trip test.** `W1` the
> decoder caps elements and depth but not text nodes: a well-formed 64 MB body allocates ~1.3 GB. `W2`
> illegal-XML code points survive into the `XDocument` and throw from `ToString()` on any Trace-enabled
> gateway. `W4` a duplicate tag name is a permanent process-wide outage with no test to catch it. `W5` an
> unknown token aborts the whole document.

## Phase 2 — The merged user model and the admin surface
*Mostly drift between the landed db-restructure and what the UI/CLI/validators do with it.*

**17. Merged-view write-back** — ~~`C2`~~ ~~`C4`~~ ~~`C8`~~ ~~`C11`~~ ~~`C12`~~ ~~`C13`~~ **COMPLETE**
> `C2` the admin Users editor round-trips the **merged** (config ⊕ db) view back into the database row,
> freezing every config-supplied field as a permanent override — precisely the trap `db-restructure.md`
> deviation 2 says was designed out. `C12` the portal does the same. `C8` the API advertises per-field
> provenance it does not carry, which is what makes the freeze invisible. `C4`/`C13` the portal's permission
> gate and its form are computed from two different views of the user.

**18. Settings validation & catalogue** — ~~`B2`~~ ~~`B4`~~ ~~`B5`~~ ~~`B6`~~ ~~`B7`~~ ~~`B12`~~ ~~`B14`~~ ~~`E8`~~ **COMPLETE**
> `B2` the OIDC admin-claim pair can never be configured through either write surface — both orders are
> rejected by a substring test. `B4` setting *removal* is validated by nothing, so `unset` can persist a
> configuration the next start refuses to boot on; `E8` a port collision does the same. `B5` a backend write
> is never re-validated against declared users. `B6` `TrustedProxies` is documented as DB-settable and is not
> in the catalogue. `B7` global settings are non-sargable and case-collidable.

**19. Admin UI gaps & coherence** — ~~`C5`~~ ~~`C6`~~ ~~`C7`~~ ~~`C10`~~ ~~`C14`~~ ~~`C17`~~ ~~`C18`~~ ~~`C19`~~ **COMPLETE**
> `C10` rename, delete-user and deletion-impact have **no path in the SPA** at all, though the API and the
> design doc both treat them as UI features. `C5` switching a role's provider leaves the old provider's rows
> behind for the new one to bind. `C6` a stored backend secret can never be cleared. `C14` a portal PUT that
> omits `UserName` silently clears it. `C7`/`C17`/`C18`/`C19` casing, stale docs, wrong badges, unhandled
> errors.

**20. CLI configuration & warm-host reuse** — ~~`E4`~~ ~~`E5`~~ ~~`E6`~~ ~~`E7`~~ ~~`E10`~~ ~~`E14`~~ ~~`E17`~~ **COMPLETE**
> `E4` the standalone CLI builds configuration **without** the database layer, so a locally-run `eas user set`
> validates against a backend view that is empty in the documented deployment. `E5` forwarded
> `config`/`logs`/`tls` rebuild the container per call and leak a non-collectible `AssemblyLoadContext` each
> time. `E6` bare `eas` inside a running container reports the gateway is not running. `E7` four startup
> awaits ignore `ApplicationStopping`. `E10` `eas help` does not work when forwarded.

**21. `/cli` endpoint hardening** — ~~`E3`~~ ~~`E11`~~ ~~`E18`~~ ~~`E19`~~ ~~`E20`~~ **COMPLETE**
> `E3` the body — up to 64 MB — is deserialized **before** the loopback pre-filter, and the 415/400 it
> produces is an existence oracle that defeats the documented "404 so the endpoint is invisible". `E11` a
> replay-cache entry can expire before the envelope it protects. `E18` an unvalidated caller-supplied render
> width reaches Spectre's layout inside the long-lived gateway. `E19`/`E20` null args, unre-checked purge.

**22. Identity normalization** — ~~`A4`~~ ~~`B13`~~ ~~`B15`~~ ~~`B20`~~ ~~`C15`~~ **COMPLETE**
> `B13` logins are never trimmed, so `" bob"` mints a **second permanent identity** with its own `UserId`,
> folder registry and AAD-bound rows that the real user can never see. `C15` the admin API's create and
> delete verbs disagree about trimming, so a grant can be unremovable. `A4` the session cache keys on the
> login rather than the `UserId`, so a reissued login can be served the previous holder's session.

## Phase 3 — Correctness

**23. Sync handler status & lifecycle** [LIVE] — ~~`F13`~~ ~~`F14`~~ ~~`F15`~~ ~~`F16`~~ ~~`F17`~~ ~~`F18`~~ ~~`F23`~~ ~~`F27`~~ **COMPLETE**
> `F14` the 16.1 account-only wipe is delivered to pre-16.1 devices that cannot decode it — a permanent 449
> loop, so the operator's wipe silently never completes on exactly the old devices most likely to need it.
> `F13` FolderCreate can report success with no ServerId. `F15` the long poll abandons its losing waits
> instead of draining them. `F16`/`F17` unmapped backend failures reach the client as HTTP 500.

**24. IMAP correctness** [LIVE] — ~~`G3`~~ ~~`G6`~~ ~~`G7`~~ ~~`G9`~~ ~~`G12`~~ ~~`G13`~~ ~~`G16`~~ ~~`G22`~~ **COMPLETE**
> **`G22` was struck, ROLLED BACK, and then REDONE** — history kept because the rollback is the lesson.
> The first fix gave `SnapshotStatusAsync` a brand-new IMAP connection per poll
> (`ConnectStandaloneAsync` → LOGIN → STATUS → LOGOUT, every 30 s, for the whole heartbeat).
> `PollForChangesAsync` races IDLE on EVERY long-poll rather than only when IDLE is down
> (`ImapMailBackend.Watch.cs`, `Task.WhenAny(idleTask, pollTask)`), so that was ~118 logins per device per
> hour in the NORMAL path — aggravating the very connection-cap pressure `G6` in this same item exists to
> survive. Reverted in `3a269d4`.
> **The redo does what the finding actually says — "its own lightweight connection (as `ImapIdleWatcher`
> already has)", i.e. PERSISTENT.** The defect is sharing the session GATE, not sharing a connection, so
> the fix is a second gate, not a new connection: `ImapStatusPoller`, owned by `ImapBackendProvider` and
> keyed on the gateway login (one per user, shared by all their devices and folders, like `_watchers`),
> with its own `SemaphoreSlim`, lazy start, capped-backoff reconnect, and `IPerUserResourceOwner` eviction
> when the user's last session goes. Steady state is 3 connections per user (session + IDLE + poll),
> constant.
> **The test pins connection REUSE, not just non-blocking.** The first attempt passed a live suite and a
> full unit suite while opening a connection every 30 s, because nothing counted connections — that is
> exactly how the regression shipped.
> `WaitForChangesAsync_PollsOverOneOwnConnection_NotTheSessionGate` (Integration.Tests) holds the session
> gate for 8 s, drives four poll rounds (eight STATUS snapshots) and asserts BOTH that they are not
> blocked AND that exactly **two** connections were opened in total (the session's + one poll connection),
> counted through MailKit's per-connection protocol-logger line. Red-first on unmodified code: 11.0 s vs
> the 6 s bound. A per-call reconnect scores 9 there, so the regression cannot come back silently.
> `G6` one transient `AuthenticationException` (Dovecot's per-user connection cap, which this design provokes)
> **permanently** disables IDLE push for that folder. `G7` a per-user backend change leaves a live
> authenticated IDLE connection against the old server. `G9` a draft rewrite is append-then-delete with no
> claim → duplicates on retry. `G3`/`G12`/`G13`/`G16`/`G22` UIDVALIDITY, special-folder classification,
> search floor, silently-discarded edits, gate contention.

**25. Local stores** — ~~`G18`~~ ~~`G19`~~ ~~`G20`~~ ~~`G21`~~ ~~`G26`~~ ~~`G30`~~ **COMPLETE**
> `G18` one undecryptable row fails the **entire** GAL search and free/busy lookup, contradicting "a free/busy
> failure must never fail the whole ResolveRecipients". `G21` the local change notifier has no latch, so a
> write landing between a Ping's check and its wait is invisible for a full watchdog interval. `G20` the
> meeting-response path lacks the concurrency retry every sibling has.

**26. Calendar & contact converter correctness** [LIVE] — ~~`D4`~~ ~~`D6`~~ ~~`D7`~~ ~~`D8`~~ ~~`D9`~~ ~~`D10`~~ ~~`D17`~~ ~~`D18`~~ ~~`D19`~~ ~~`D21`~~ **COMPLETE**
> `D9` the vCard phone-type read is not the inverse of the write, so HOME-FAX and CAR numbers **migrate to
> other fields on the first unrelated edit** — permanent, silent, on the user's CardDAV server. `D4` a
> URI-valued PHOTO is deleted on any edit. `D7` every meeting is advertised as organized by the syncing user.
> `D19` MeetingResponse can stamp PARTSTAT on an override instead of the master. `D21` recurrence day
> derived from the UTC instant.

**27. Mail & draft converter correctness** [LIVE] — ~~`D11`~~ ~~`D12`~~ ~~`D13`~~ ~~`D14`~~ ~~`D15`~~ ~~`D16`~~ `D20` `D22` `D25`
> `D20` a draft edit discards `In-Reply-To`/`References`, so a reply started elsewhere is sent as a new
> thread. `D15` a Type-4 body is decoded as UTF-8 and NUL-stripped, corrupting non-UTF-8 and binary parts.
> `D14` `DateReceived` comes from the sender-supplied header and defaults to year 0001. `D16` every
> attachment is fully decoded per sync round just to report its size.

**28. JMAP mapping & watcher** [LIVE] — `H4` `H7` `H11` `H12` `H14` `H19` `H21` `H25` `H26`
> `H4` a category containing `/` corrupts the JSON-Pointer patch and fails the whole Sync Change. `H11` the
> update denylist is hard-coded, so a server returning `utcStart`/`utcEnd` makes **every** event edit fail.
> `H7` contacts/calendar re-download the entire account per folder per round. `H12` the SSE stream has no
> size cap.

**29. DAV polling & folder shape** [LIVE] — `H5` `H6` `H15` `H16` `H17` `H22` `H23`
> `H5` ctag polling does one PROPFIND **per folder**, contradicting the documented H12 mitigation (one
> `Depth:1` per home set) — 7–14 round trips per poll interval per device. `H6` GAL silently returns nothing
> against a server that omits `address-data`. `H16` transport failures escape the "never break folder sync
> over a share" guards. `H22`/`H23` shared calendars folded into own availability; no default calendar.

**30. State layer & retention** — `A5` `A6` `A7` `A8` `A9` `B8` `B16` `B18`
> `A9` a duplicate `BackendKey` from any store makes **every** FolderSync for that user 500 until the backend
> stops emitting it. `A8` the race-safe stamp helper is dead code while both call sites use the racing one.
> `B8` every authenticated request materializes backend-role rows it never uses, on the Ping/Sync hot path.
> `A5`/`A6`/`A7` tracker hygiene after a conflict.

**31. Protocol support types** — `W3` `W6` `W12` `W13` `W17` `W18` `W19`
> **Protocol layer — read the hard gate.** `W3` `CompareIds` is intransitive (`9 < 10 < 1a < 9`), so `Sort`
> can throw or reshuffle a windowed device's items between rounds. `W6` `ToBase64` silently truncates any
> length-prefixed field over 255 bytes, emitting a blob its own parser cannot read — and `LongId`/
> `AttachmentName` are not bounded. `W18` the tolerant no-`Z` date format asserts UTC for the one input that
> did not say UTC.

## Phase 4 — Structure, docs and cleanup
*By area. Safe to reorder or skip; nothing else depends on these.*

**32. Structural & schema documentation** — `S1` `S2` `A11` `A12` `A13` `B9` `B10` `B19`
> `S1` AGENTS.md's dependency table says `Backends.Common` depends on Core; it does not, and an enforced test
> asserts the opposite — a contributor following the document gets a red build. `A11` the per-user-scoping
> entity list contradicts the schema it documents (names `LoginBlock`, omits `UserBackendRole`, still cites
> deleted tables). `S2` two file-wide unrestored `CS0618` pragmas.

**33. Handler & WebUi polish** — `C20` `C21` `C22` `C23` `F19` `F20` `F21` `F22` `F24` `F25` `F26` `F28` `F29`
> `F21` an empty DeviceId is accepted, so every client that omits one shares a single sync-state row.
> `F20` `Picture` is emitted before `Availability`, out of MS-ASCMD sequence. `F22` the FileReference path
> skips the registry check its sibling performs. Plus CSS/CSP/nav polish and status-code accuracy.

**34. Converter & TLS-helper nits** — `D23` `D24` `D27` `D28` `D29` `D30` `D31` `D32` `D33` `D34` `D35`
> `D27` a rotated CA bundle needs a process restart (cached forever by path). `D31` the derived default for
> the standard mail ports is opportunistic STARTTLS, which downgrades silently. `D24` a bare CR survives
> vCard escaping. Plus `ConversationIndex`, surrogate cuts, `var` usage, allocation.

**35. Hosting, CLI & backend nits** — `E9` `E12` `E13` `E15` `E16` `E21` `E22` `E23` `G8` `G11` `G14` `G15` `G25` `G27` `G28` `G29`
> `E9` `/readyz` component detail is gated on a loopback peer, so the k8s node probe the code names never sees
> it. `E16` `X-Forwarded-Proto` is taken leftmost with no chain walk. `G8` `PathSeparator` is a documented,
> schema-exposed IMAP option nothing reads. `G14` folder listing is one LIST per folder, unbounded depth,
> under the session gate.

**36. Contracts, crypto & plugin nits** — `K8` `K9` `K10` `K12` `K14` `K15` `K16` `K17` `K20` `K22` `K23`
> **Touches the published contract — mind the version gate.** `K12` `TransientRetry` defaults to *replaying*,
> so a plugin author who omits one argument gets at-least-once on a non-idempotent send. `K9` `DelaysMs` is a
> publicly mutable static array any plugin can retune process-wide. `K8` the `enc:v1:` scheme has no domain
> separation between its three message types. `K10` the documented plugin declaration names the wrong version.

**37. Protocol & WireLog nits** — `W7` `W8` `W9` `W10` `W11` `W14` `W15` `W16` `W20` `W21`
> **Protocol layer — read the hard gate.** `W7` **needs a human with MS-ASWBXML**: Find page 25 orders
> `MaxPictures`/`MaxSize`/`Picture` as the reverse of every sibling page, which — if wrong — means GAL photos
> over Find silently never work. `W9` the bidi-defence classifier embeds raw bidi overrides in its own source
> (Trojan Source). `W15` pooled buffers holding message plaintext are returned unscrubbed. `W16` the code-page
> tables are mutable through a cast.

---

## If you only do part of this

Items **1–16** are the ones that matter for a system anyone else runs: silent data loss (1, 7, 26, 27),
duplicate or lost mail (2, 5), credential exposure (3, 10, 11, 12), and the two that take the whole service
down (9's certificate expiry, 14's listener exposure). Items **17–22** are the db-restructure's unfinished
edges and are what an operator will actually trip over. Everything from 23 on is quality-of-implementation —
real, but survivable if left.

---
---

# FINDINGS

*Every finding is recorded in full in [`review-items-detail.md`](review-items-detail.md), indexed by ID
(`A1.`, `B1.`, …). Area **S**, the cross-cutting structural pass, is self-contained below because it has no
detail entry by design.*

**Per-area counts:** A 13 · B 20 · C 23 · D 35 · E 23 · F 29 · G 30 · H 26 · K 23 · W 21 · S 2 = **245**.

## Area S — cross-cutting structural (2)

`S1` **Low** `AGENTS.md`'s dependency table states that `ActiveSync.Backends.Common` "**Depends on Core**
(+ MailKit, Ical.Net, FolkerKinzel.VCards) so those deps stay OUT of Core" — `AGENTS.md:138-142`. It does
not: `src/ActiveSync.Backends.Common/ActiveSync.Backends.Common.csproj` has exactly one ProjectReference,
to `ActiveSync.Contracts`. The dependency was deliberately removed (Common used Core for a single
`WireLog.Payload` call; WireLog then moved down to Protocol) and the absence is now PINNED by an enforced
test, `DependencyRuleTests.BackendsCommon_DoesNotReferenceCore()`, which asserts
`Assert.DoesNotContain("ActiveSync.Core", referenced)`. So the document contradicts a test that fails the
build if the document is followed. This matters because AGENTS.md designates this exact section as "the
authority on which assembly may reference which" — a contributor adding a Core reference to Common on the
document's authority gets a red build with no explanation of why the rule they read is wrong. FIX: correct
the table entry to "Depends on Contracts only (+ MailKit, Ical.Net, FolkerKinzel.VCards)", and state that
the no-Core property is test-enforced.

`S2` **Nit** Two file-wide `#pragma warning disable CS0618` with no matching `restore` —
`src/ActiveSync.Backends.Common/Converters/CalendarConverter.cs:14` and
`Converters/TasksConverter.cs:12`. Both sit above the `namespace` declaration, so they suppress
obsolete-member warnings for the ENTIRE file (761 and 261 lines) rather than the 16 and 5 obsolete Ical.Net
recurrence uses they were written for. The justification comments are accurate and specific (EAS carries at
most one recurrence rule, so Ical.Net's obsolete single-value `RecurrenceRules`/`RecurrenceId` surface is
the right one) — the defect is scope, not rationale. Any genuinely-wrong future use of any obsolete API
anywhere in the largest converter file is silently accepted. Note this makes AGENTS.md's "every `#pragma`
narrowly scoped + justified" only half-true. FIX: move each `disable` to immediately before its using site
(or the smallest enclosing member) and add the matching `restore`; 28 disables vs 26 restores repo-wide is
these two.

**Verified correct / structurally sound:** namespace↔assembly alignment holds for **every** file in every
project (checked mechanically; zero mismatches — round 2's converter-namespace rename survived). Dependency
DIRECTION holds everywhere declared: Protocol→nothing; Crypto→nothing; Contracts→Protocol;
Core→Contracts+Protocol+Crypto; Backends.*→Core+Common; WebUi→Core only (never Server, never a backend);
Server→Core+all backends+WebUi with the explicit Core reference present; Cli→Crypto only. No provider
references another provider. Previously-consolidated security primitives are each still SINGLE-sourced —
one `SecretRedaction`, one `TransientRetry`, one `RedirectingHttpSender` (`JmapClient:461` CALLS
`RedirectingHttpSender.IsSafeRedirect`, it does not redefine it), one `ServerCertificateValidator`, one
`SealedBlob`, one `BackendHttpClientFactory`, one `WireLog`. **No raw `AesGcm` construction anywhere outside
`ActiveSync.Crypto`** — the framing consolidation holds. Packaging boundary coherent: only Contracts and
Protocol carry `IsPackable=true`, and package identity is gated on it in `Directory.Build.targets` (not
.props, which would evaluate too early). Central Package Management intact — zero per-project `Version=`
attributes outside the one `GlobalPackageReference`. Analyzer posture: no `[SuppressMessage]` anywhere in
`src`; one `.editorconfig` downgrade (VSTHRD200 = no mandatory Async suffix), justified inline; the
VSTHRD002/003/011 suppression in `BackendSessionFactory.cs:409` is the single justified home for the
async-lazy idiom and carries a nine-line explanation of why each of the three cannot bite. Migration sets
are in lockstep — one baseline migration per provider, identical names after the timestamp prefix, both
machine-generated. Test topology matches the documented design (no per-provider test project; Crypto and
Contracts exercised from Core.Tests), with `ActiveSync.Cli.Tests` still thin at 1 file / 16 tests and the
HTTP round trip in `EasForwardingClient.RunAsync` still unexercised.

## Found while working the queue

*(New findings discovered mid-implementation go here — see `fix-review.md` step 8. Self-contained: these
have no entry in `review-items-detail.md` by design, so the definition-adequacy check is expected to list
them under "missing detail".)*

`N1` **Low** The F1/F7 send-dedup claims (`SentCommandTokens` rows keyed on the fixed collection
namespaces `"compose"`/`"meetingresponse"`, generation `0`) are never pruned. `SendDedupStore.PruneAsync`
only runs from `CollectionStateStore.CommitCollectionStateAsync`, keyed to a REAL Sync collection's own
new SyncKey (`SyncStateService.cs`, right after a Sync round commits) — it never touches rows whose
`CollectionId` is one of these two fixed strings, because no Sync round ever commits under that
collection id. Every ComposeMail send (`ComposeMailHandlers.cs`, keyed on `ClientId`) and every
MeetingResponse (`MeetingResponseHandler.cs`, keyed on `CollectionId:RequestId:UserResponse`) therefore
adds a permanent row that lives until the owning `Device` row is deleted (cascade) — unlike the Sync
draft-submit claims this design was copied from, whose rows the collection's own commit reclaims within
one generation. Over a device's lifetime this is one row per mail ever sent by reference and per meeting
ever responded to, not just per retry. FIX: either give `SendDedupStore` a age-based sweep (e.g. delete
completed claims older than N days, independent of a collection commit) or a small periodic purge command
analogous to `FolderRetentionService`/`LogRetentionService`.

`N2` **Low** `JmapMailSubmit.ResolvePrerequisitesAsync` (`src/ActiveSync.Backends.Jmap/JmapMailSubmit.cs`)
issues `Identity/get` under the MAIL primary account, not the submission one, even after H9 gave
`EmailSubmission/set` its own submission-capability account. RFC 8621 §7.1 defines `Identity` under
`urn:ietf:params:jmap:submission`, same as `EmailSubmission`, so on a server where the mail and submission
primary accounts genuinely differ, `Identity/get` may return the wrong (or an empty) identity list for the
account actually used to submit. `Mailbox/get` in the same batch is correctly a Mail-capability call and
is unaffected. H9's own remedy text named only the `Email/import`/`EmailSubmission/set` split, so this was
left as `AccountAsync` (mail) for both calls in the batch rather than widened without a finding backing it.
FIX: route `Identity/get` through `SubmissionAccountAsync` alongside `EmailSubmission/set`, keeping
`Mailbox/get` on the mail account.

~~`N3`~~ **Low — FIXED** (see the resolution note at the end of this entry; the mechanism recorded below is
**wrong**, and the FIX it proposes would not have worked).
`TlsCertificateRenewalServiceTests.NearExpiryCertificate_IsRenewedOnATick_AndTheHolderIsSwapped`
(`tests/ActiveSync.Server.Tests/TlsCertificateRenewalServiceTests.cs:71`, landed under item 9's K1) is
flaky under a full parallel unit-suite run — observed failing ~1 run in 8 of `dotnet test ActiveSync.slnx
--filter "Category!=Integration"`, but green every time (5+ runs) when run in isolation
(`--filter "FullyQualifiedName~TlsCertificateRenewalServiceTests"`). The test drives a real
`TlsCertificateRenewalService` with a 20 ms tick interval and polls `WaitUntilAsync` for up to 5 seconds
for the holder to swap to a renewed (freshly RSA-2048-generated) certificate; under the CPU contention of
the full parallel suite, cert generation plus scheduler jitter can apparently exceed the 5 s budget.
Discovered incidentally while verifying item 11's unit-suite baseline — not caused by, or related to, any
change in item 11 (K5/K6/K7/K21 touch only `GatewayPasswordHasher`, `AuthThrottle`, and the WebUi login
endpoint). FIX: widen the timeout and/or shorten the tick interval further, or seed a pre-generated
certificate/key pair so the test does not pay RSA key generation under load.
**RESOLUTION (fixed — the diagnosis above is superseded).** The failure was never the 5-second budget: it
is a **use-after-dispose**. `TlsCertificateRenewalService.DisposeAfterGraceAsync` frees the PREVIOUS
certificate once the grace period elapses (20 ms in the test, 30 s in production), and the test kept
reading `stale.Thumbprint` afterwards — in the poll predicate AND in the assertion — so both had to run
inside a 20 ms window to win the race, and under parallel load they often did not. It surfaced as
`CryptographicException: m_safeCertContext is an invalid handle` from `GetCertHashString()`, not as a
timeout. Item 13's results entry had already named this mechanism and noted it superseded this finding;
this entry's own text was never corrected, so the wrong FIX (widen the timeout) stayed on the record.
The test now captures the thumbprint as a `string` BEFORE the service starts, so nothing touches a handle
the service is entitled to close — the race is eliminated structurally, not made less likely. The timeout
was widened to 30 s as well, which costs nothing (the wait returns as soon as the swap is observed) and
covers `N3`'s original RSA-under-load theory in case it was ever a second contributor. **Production is
untouched** — the grace-period disposal is correct, and it is what the 30 s window exists to bound.

`N4` **Low** A backend-section REMOVAL is still never re-validated against declared users, even after
B4/B5 (item 18). B4 gave `SettingKeys.ValidateRemovalImpact` a check against
`BackendRolesConfig.Load` + each assigned provider's `ValidateConfiguration`, and B5 gave
`BackendKeyValidator.Validate` (the WRITE path) a check against `UserResolver.ValidateUsers` — but
`ValidateRemovalImpact`'s own `BackendSectionFailures` helper (`SettingKeys.cs`) does not call
`UserResolver.ValidateUsers` at all. So `eas config unset ActiveSync:Backends:Oof:Provider` (or the
web DELETE) while a config user declares `ActiveSync:Users:bob:Backends:Oof:{UserName:…}` is
accepted — `BackendRolesConfig.Load` treats an absent Oof role as simply "the feature is off" (no
failure recorded), so the section-only check sees nothing wrong — yet the very scenario B5's own
prose names ("`eas config set`/`unset` that drops the global Oof role assignment") produces
`UserResolver`'s `"ActiveSync:Users:bob:Backends:Oof: no global Oof role is configured"` at the next
boot. FIX: have `ValidateRemovalImpact`'s before/after diff also run `UserResolver.ValidateUsers`
over the candidate `BackendRolesConfig` (mirroring what B5 added to the write path), the same way it
already runs `BackendSectionFailures`.

`N5` **Nit** The IMAP live-connection observability surface still knows only about IDLE watchers, so
G22's new per-user `ImapStatusPoller` is invisible. `ImapBackendProvider`'s
`GatewayMetrics.SetIdleWatchersObserver` gauge (`activesync_idle_watchers`) and `SnapshotWatchers()`
— which feeds the admin dashboard's watcher list — both enumerate `_watchers` only, while the
provider now also holds one persistent authenticated IMAP connection per gateway user in `_pollers`.
Steady state per user is three connections (session + IDLE + poll) and the operator-visible count is
one, which matters exactly where it is most likely to be looked at: diagnosing a server-side per-user
connection cap (the `G6` scenario). Not a correctness defect — the poller is trimmed by the same
`TrimUserResources` sweep, so nothing leaks. FIX: either add a sibling gauge/observer for the poll
connections, or generalise `WatcherInfo`/`SnapshotWatchers` to report a kind ("idle" | "poll") and
have the gauge group by it.

`N6` **Low** `TasksConverter` derives its recurrence day/month fallbacks from the UTC instant, exactly as
`CalendarConverter` did before `D21` — `src/ActiveSync.Backends.Common/Converters/TasksConverter.cs:101`.
`D21` fixed the calendar side by anchoring `RecurrenceMapper.Build` on `master.Start.Value` (the event's
own zone) rather than the UTC value, because a wall-clock-local Monday can be a Sunday in UTC and the
emitted `DayOfWeek`/`DayOfMonth`/`MonthOfYear` shifted across the boundary. `TasksConverter` calls the same
shared `RecurrenceMapper` with the same UTC-instant shape, so a VTODO whose start sits near midnight in a
non-UTC zone mis-reports its recurrence day the same way. Noticed by the item 26 worker while fixing `D21`
and correctly left unfixed under "stay inside the item", but not filed — recorded here by the orchestrator
so it is not lost. FIX: anchor the `RecurrenceMapper.Build` call on the task's local wall-clock start,
mirroring `D21`.

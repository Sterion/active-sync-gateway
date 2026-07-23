# ActiveSync Gateway — source review findings (Round 2)

> **Execution protocol: [`fix-review.md`](fix-review.md) — read it first.**
> This file holds only project data: the findings, the work queue, the commands, the invariants.
> Nothing here describes *how* to work; that lives in `fix-review.md` and does not change.
> Full technical detail for every A–W finding is in [`review-items-detail.md`](review-items-detail.md) —
> **read a finding's detail entry before implementing it.** Results of completed items are recorded by
> the fix orchestrator in [`review-results.md`](review-results.md).

**Scope:** all production code under `src/` — ~54k lines, 14 projects. Tests, docs and CI read for
context only. (~20k of Core is EF migration scaffolding, reviewed for hygiene and lockstep rather
than line by line.)

**Method:** nine parallel subsystem passes (A Core state/sync · B Core accounts/settings · C WebUi ·
D Backends Common/Imap/Smtp/Local/Sieve · E Server pipeline/hosting/CLI · F EAS handlers · H Backends
JMAP/DAV · K security/crypto/plugins/contracts · W WBXML/protocol) plus a cross-cutting structural
pass (S).

**This is round 2.** Round 1 (365 findings, all 56 items landed) is archived under
[`round1/`](round1/); its fixes were re-verified here and appear in each area's *Verified correct*
notes. Round 2 finds what exists **now** on the hardened tree: fewer Criticals, a handful of residual
Highs, a large body of Medium correctness/robustness issues, and the structural work a mature codebase
makes worthwhile.

**Baseline commit:** `946a9c0` — every `file:line` below is exact as of this commit. Line numbers
drift as items land; locate by symbol. See "Locating a finding after code has moved" in
`fix-review.md`.

**Baseline health at `946a9c0`:** build **0 warnings** · **1020 unit tests green, 0 skipped**
(Protocol 78 · Core 628 · WebUi 71 · Server 243) · integration suite skips locally when no backend is
reachable (`[BackendFact]`).

**Totals:** 132 findings — 0 Critical, 9 High, 44 Medium, 53 Low, 26 Nit · 32 work items.

---

## Invariants

These never change as work progresses — striking a finding through does not remove it. Any drift
means an edit went wrong. See "Integrity check" in `fix-review.md`.

| Invariant | Value |
|---|---|
| Work items | **32** |
| Items marked [LIVE] | **10** |
| Findings assigned | **132** |
| Findings unique | **132** |
| Duplicate assignments | **0** |
| Encoding-damage matches | **0** |

```sh
sed -n '/^# WORK QUEUE/,/^# FINDINGS/p' docs/review/review-items.md > /tmp/q
echo "items=$(grep -cE '^\*\*[0-9]+\. ' /tmp/q) live=$(grep -cE '^\*\*[0-9]+\..*\[LIVE\]' /tmp/q)"
grep -E '^\*\*[0-9]+\. ' /tmp/q | grep -o '`[ABCDEFHKSW][0-9]\+`' | tr -d '`' | sort > /tmp/f
echo "assigned=$(wc -l </tmp/f) unique=$(sort -u /tmp/f|wc -l) dupes=$(uniq -d /tmp/f|wc -l)"
grep -c $'\xc3\xa2\xc2\x80\|\xc3\xb0\xc2\x9f' docs/review/review-items.md
```

---

## Orientation documents

Read before touching the areas they cover — see "Orient before you start" in `fix-review.md`. These
carry constraints the code does not state and a reasonable change will violate silently.

| Document | Read before touching | Contains |
|---|---|---|
| [`AGENTS.md`](../../AGENTS.md) | **any structural work** (items 14–18) and any backend/sync change | Solution layout and the dependency rule; per-layer invariants; coding conventions; decisions already taken and why |
| [`README.md`](../../README.md) | first item in an unfamiliar area | what the project is, how the pieces fit |
| [`docs/testing.md`](../testing.md) | any [LIVE] item | backend stacks, how the suites skip, which runner to use |
| [`docs/plugins.md`](../plugins.md) | items 14–15, 17 | the published plugin contract |
| [`round1/`](round1/) | when a finding touches a round-1 area | what was already fixed and why — do not re-litigate a settled decision without cause |

**Hard gates:**

- `AGENTS.md` § *Protocol layer invariants* — **read before touching `src/ActiveSync.Protocol/`** (Area
  W, item 8 and item 32). Code-page tables are transcribed from MS-ASWBXML: never guess, and **every
  table change needs a round-trip test**. The OPAQUE marker attribute is a convention every producer
  and consumer relies on.
- `AGENTS.md` § *Solution layout and dependency rule* — the authority on which assembly may reference
  which. Items 14–18 are this section executed.
- `AGENTS.md` § *Sync model* — the SyncKey lifecycle, windowing and full-enumeration posture (H16).
  Items touching A1/A11/F2/D2/D15/H2/H5 must not break the "revision map is the whole truth" or the
  N−1 replay invariants.

If an orientation document contradicts a finding, **stop and report it**. One of the two is wrong,
and that is a human decision.

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
dotnet test tests/ActiveSync.Integration.Tests --filter "FullyQualifiedName~YourNewTestName"
```

**Unit suite** — run once per item, before the last commit (~30 s):

```powershell
dotnet test ActiveSync.slnx --filter "Category!=Integration"
```

Baseline at `946a9c0` was **1020 passed, 0 skipped**; it grows as items add tests. The last verified
figure is in the most recent [`review-results.md`](review-results.md) entry.

**Live suite** — required for [LIVE] items, **and** for any item landing an EF migration, changing
auth or cookie policy, or altering the HTTP pipeline:

```powershell
./scripts/stalwart-up.ps1      # canonical ports; reuses a warm container, ~15 s
dotnet test tests/ActiveSync.Integration.Tests --filter Category=Integration
```

**Fresh restart** (clean volume) — for the parallel-restart rule in `fix-review.md`. Run this the
moment the worker is spawned, so it reprovisions inside the worker's work window at no wall-clock cost:

```powershell
./scripts/stalwart-up.ps1 -Down   # tear down, discarding the accumulated volume
./scripts/stalwart-up.ps1         # bring up fresh; probes all four ports before returning
```

**Environment gotchas:**

- **A skipped suite exits 0 and looks exactly like a passing one.** Every integration test is a
  `[BackendFact]` that xunit turns into a *skip* when the IMAP probe fails. Read the passed/skipped
  counts, never the exit code. The local baseline showed the integration suite **skipping** (no
  backend up) — a [LIVE] item MUST bring a backend up and confirm passed > 0.
- `stalwart-up` puts Stalwart on the **canonical** ports (143/587/4190/5232), `TestBackend`'s built-in
  defaults — no `AS_TEST_*` setup needed. It fails loudly if a port is unpublished.
- `scripts/test-fast` also covers Axigen but rebuilds/recreates both stacks each run and needs
  `AS_TEST_*` overrides. **Do not alternate between the two runners** — same compose project, different
  port sets, so each switch recreates the container.
- Some findings are backend-specific (JMAP: H1/H3/H4/H5/H6/H7/H8/H9; CalDAV: H2). Verify JMAP
  findings against the `stalwart` stack (its JMAP surface), CalDAV/CardDAV against a DAV-capable stack.

---

## Standing context

- **Breaking changes are acceptable and preferred where they yield a better design.** Not deployed
  outside testing; the published NuGet packages have no external consumers. Several items are
  intentionally breaking (K1 re-seals passphrase-derived values; K11/S2 change the crypto helper
  surface; item 18 renames the converter namespace; item 17 may move a Contracts type). Take them, and
  write the breaking consequence into the finding's own text and the results entry.
- **Do not push.** Commit freely; pushing is a human decision.
- **The build baseline is 0 warnings.** Treat a new warning as a failure.
- **Do not touch `review-results.md`** — the fix orchestrator owns it.
- **A finding whose fix is inherently breaking says so in its own detail text.** K1 in particular
  changes the derived key for passphrase-stretched keys, so any persisted `enc:v1:` value re-seals and
  local rows re-key on upgrade — acceptable here (not deployed), but state it in the results entry.

---

# WORK QUEUE

**32 items, each sized for one session, in the order to run them.** Say *"Implement item 12"* — no sub-choices, no sizing decisions. Phase headings are context only; the numbering is a straight line.

Findings are grouped by *what breaks* and by *which files they touch*, so an item is one coherent piece of work. Every finding ID appears in exactly one item.

---

## Phase 1 — Security & data-integrity Highs
*The residual Highs. Start here.*

**1. Encryption key derivation & content AAD** — ~~`K1`~~ ~~`K2`~~ ~~`K14`~~ **COMPLETE**
> `K1` every default deployment stretches its passphrase against one publicly-known salt → one rainbow table recovers the master key (which decrypts everything at rest). `K2` the content-protector AAD is not injective, so a newline in a login collides cross-collection. `K14` a base64-shaped passphrase skips PBKDF2 and the length floor. All in `Crypto`/`Security`. **Breaking** (K1 re-keys passphrase deployments) — see Standing context. **Before starting `K1`, read [`claimed-fixed-but-not.md`](claimed-fixed-but-not.md) §2** — round 1 already "fixed" this (`K45`, commit `41a476d`) with an *opt-in* `KeyDerivationSalt` that left the fixed salt as the default; do not repeat that — the default path must be safe with no operator action.

**2. Sync-state flush integrity** [LIVE] — ~~`A1`~~ ~~`A4`~~ ~~`A11`~~ **COMPLETE**
> `A1` a deferred Replay rollback on one collection is flushed with no replay generation when a sibling collection commits in the same request → the client re-receives already-delivered items (the exact F12 failure). `A4`/`A11` are the shared-context flush coupling that makes it reachable. Decide the per-round transaction/isolation policy here. Needs a real backend to drive a multi-collection Sync.
> **Resolution:** the per-round policy is *no volatile mutation pending on the shared tracker between decision and its own commit.* `ValidateSyncKeyAsync` no longer mutates the tracked entity for a Replay (`A1`) or a key-0 reset (`A11`), nor SaveChanges either — it returns only the verdict. `CommitCollectionStateAsync` gained a `SyncKeyValidation validation` parameter (**breaking** to that internal method signature; host-only, no plugin surface) and applies the Initial/Replay/Current transition atomically with the new generation, so a round that never commits leaves the prior generation intact and a sibling flush cannot persist a half-applied rollback (`A4`). Behaviour is otherwise byte-identical (net SyncKey/snapshot outcomes unchanged); the Replay diff base is now read via `ReadPreviousSnapshot` in the handler. Verified live: full integration suite 141 passed / 0 skipped on Stalwart.

**3. IMAP send & category integrity** [LIVE] — ~~`D1`~~ ~~`D6`~~ ~~`F1`~~ **COMPLETE**
> `F1` a post-reply failure in MeetingResponse reports retryable → duplicate iTIP REPLY + double PARTSTAT on retry. `D1` no SMTP MaxSize preflight. `D6` category sanitization collapses distinct EAS categories → perpetual revision churn. `MeetingResponseHandler` + `SmtpSubmitBackend` + `ImapMailBackend`.

**4. JMAP submission & revision integrity** [LIVE] — ~~`H1`~~ ~~`H5`~~ **COMPLETE**
> `H1` a failed JMAP send leaves an orphan `$draft` in Drafts that then syncs to the device. `H5` contact/calendar revision is a hash of raw JSON whose member order is server-defined → the diff can re-send the entire collection on a re-serialization. Verify against the `stalwart` JMAP stack.

**5. Account-row case collation** — ~~`B1`~~ ~~`B8`~~ **COMPLETE**
> `B1` two BINARY-distinct rows (`Phone1`/`phone1`) coexist under the unique index but collapse last-write-wins into the OrdinalIgnoreCase load map, silently dropping a user's overrides. `B8` the `ToLower()` predicates can't see the pair. Fix case-folded uniqueness at the DB layer (needs a migration on both providers). **Before starting, read [`claimed-fixed-but-not.md`](claimed-fixed-but-not.md) §1** — round 1 already "fixed" this (`B2`, commit `ca03f49`) with `.ToLower()` lookup predicates only, which is exactly why the collapse survives; the fix must change the index/storage casing + migrate existing duplicates, not add another lookup-level compare.

**6. WebUi throttle & OIDC admin binding** — ~~`C1`~~ ~~`C4`~~ **COMPLETE**
> `C1` login success clears only the per-user throttle key, never the IP-wide one, so a busy NAT IP eventually 429s everyone. `C4` OIDC signs in an unbound config-declared admin on a bare login-claim match (mutable `preferred_username` takeover).

**7. Forwarded-header trust** — ~~`E1`~~ ~~`E10`~~ **COMPLETE**
> `E1` `X-Forwarded-Proto` is trusted from any peer to rewrite the scheme (drives OIDC redirect_uri + Autodiscover URLs) unless `PublicUrl` is set. `E10` Autodiscover reflects `X-Forwarded-Host` into the advertised server URL. Gate both on `Auth:TrustedProxies` like `EndpointAuth` already does.

**8. Protocol version gating & query parsing** — `W3` `W2` `W4`
> `W3` `EasVersion.Parse` (the header path) accepts "99.9", satisfying every `>=V160` gate — the query-byte hole one field over. `W2` host-endianness reads on the little-endian wire format. `W4` unknown query tags skipped rather than rejected. **Protocol layer — read the AGENTS.md hard gate; any table/parse change needs a round-trip test.**

## Phase 2 — Security hardening (Medium)

**9. Certificate store & TLS resolver** — `K4` `K5` `K6` `K17` `K18` `K19`
> `K4` 20-year self-signed cert is refused by iOS (Apple's ≤398/825-day rule) — the flagship client. `K5` no IP SAN → name-mismatch for IP-addressed clients. `K6` unguarded replace-race across replicas looks like a MITM. `K17` operator cert not validated for private-key/expiry. `K18` native handle leak per describe. `K19` odd `PublicUrl` throws with no fallback.

**10. Password & throttle robustness** — `K3` `K8` `K9` `K15`
> `K3` unbounded PBKDF2 iteration count from a stored hash → per-login DoS. `K8` torn `DateTime` prune stamp. `K9` no `TimeProvider` (untestable rate limiter). `K15` parse accepts weaker-than-generated hashes. `Security/GatewayPasswordHasher` + `AuthThrottle`.

**11. CLI auth & envelope hardening** — `K7` `K16` `E3` `E8`
> `K7` credential-bearing `/cli` responses go cleartext in AllowPlaintext mode. `K16` single-use is a caller obligation, not enforced by the sealed type. `E3` captured stdout can corrupt under a fan-out command. `E8` master key not zeroed on an early return.

**12. SSRF, oracle & info disclosure** — `C2` `C5` `E6` `E11`
> `C2` the backend `/test` probe is an SSRF reachability oracle for arbitrary operator-supplied hosts. `C5` portal leaks the admin-set backend username to a non-admin. `E6` `/readyz` treats a null peer as local and discloses topology. `E11` uncapped Autodiscover body buffer.

**13. Shared-collection, redirect & backend TLS** [LIVE] — `K10` `K13` `H9`
> `K10` `SharedCollection.Parse` truncates an href containing `|` → wrong collection shared. `K13` custom-CA path disables revocation unconditionally. `H9` JMAP blob/eventsource requests don't re-assert same-origin before attaching credentials. DAV/JMAP touch → verify against a live backend.

## Phase 3 — Boundaries (structural)
*Do before the deep correctness refactors — each lands on a clean tree. See Area S detail below.*

**14. Explicit Core reference & CLI testability** — `S1` `S8`
> `S1` Server depends on Core pervasively but references it only transitively (an accident of the backend/WebUi references) — add the explicit ProjectReference. `S8` the slim `eas` client (holds the master key, gates admin commands) has no test project and no namespace — extract the forwarding logic from top-level `Program.cs` into a testable class and add `ActiveSync.Cli.Tests`.

**15. Unify AES-GCM framing** — `S2` `K11`
> The `nonce‖ct‖tag` framing is hand-rolled twice (`SecretValue` in Crypto, `LocalContentProtector` in Core/Security). Extract one `SealedBlob` primitive into `ActiveSync.Crypto` taking `(prefix, aad, key)`; both callers use it, distinct prefixes/AAD preserved as arguments. **Do this before item 1 lands its K1/K2 crypto edits if scheduling allows — but K1/K2 can also go first; if so, re-home them here.** Core already references Crypto.

**16. Consolidate the redirect follower** [LIVE] — `S3`
> `SendFollowingRedirectsAsync` + `IsSafeRedirect` are verbatim twins in `WebDavClient` and `JmapClient` — the security-sensitive same-origin credential-forwarding logic in two places (the JMAP copy's comment even says "Mirrors WebDavClient"). Move into `Backends.Common` (a `RedirectingHttpSender`); relocate the `IsSafeRedirect` unit test with it. Verify both DAV and JMAP live.

**17. Log-scrubbers, free/busy & WireLog placement** — `S6` `K21` `S5` `S9`
> `S6`/`K21` the two log-scrubbers (`WireLog.Payload`, `LogText.Clean`) duplicate the character-safety core and the bidi-override defense exists on only one path — route both through one classifier, extend bidi-stripping to payloads. `S5` `MergedFreeBusy` is host-only output — move to Core (and correct AGENTS.md) so Contracts carries only what a plugin builds. `S9` `WireLog` is incidental host utility in the published contract — move down to Protocol (BCL-only). **Breaking Contracts surface — acceptable.**

**18. Namespace coherence & JmapMailStore split** — `S7` `S4`
> `S7` the converter namespace `ActiveSync.Backends.Converters` is a second root in the `Backends.Common` assembly — rename to `ActiveSync.Backends.Common.Converters` (mechanical, touches backend `using` sites). `S4` `JmapMailStore.cs` (847 lines) is the one un-split backend store — split into partials by concern (CRUD/listing, Search, Watch, Attachments) following the IMAP precedent. No API change.

## Phase 4 — Correctness

**19. Backend session lifetime & auth cache** — `A5` `A6` `A8` `A9` `A10`
> `A10` a faulted Lazy session slot is never swept and wedges that (user,device) into repeated failures until restart; `A9` it also pins the user's IDLE watchers. `A5`/`A8` positive-auth-cache staleness after a backend-side password change and a snapshot-clear TOCTOU. `A6` a wipe-ack concurrency conflict 500s. `Backend/BackendSessionFactory`. **For `A6`, read [`claimed-fixed-but-not.md`](claimed-fixed-but-not.md) §Probable** — round 1's wipe-ack fix (`A22`, commit `d59a730`) caught only the `LoginBlock` unique violation; `A6` is the `Device` concurrency-token conflict the same method still doesn't handle, so also reload+re-apply on `DbUpdateConcurrencyException` rather than adding another unique-violation catch.

**20. State layer performance & Oof concurrency** — `A2` `A3` `A7`
> `A2` a batched DAV-id insert loses ids for this request's new hrefs when a concurrent unique-violation rolls back the whole batch → items drop from the render window. `A3` Oof read-modify-write has no concurrency token/upsert guard → lost update or 500. `A7` snapshot decompress uses a mismatched comparer. `State/*`.

**21. Retention services & DB-log lifecycle** — `E2` `E4` `E13`
> `E2` the two retention services permanently stop on any `OperationCanceledException` (an EF timeout freezes retention for the process lifetime) — mirror `SettingsRefreshService`'s shutdown-only guard. `E4` the DB-log shutdown flush is uncancellable. `E13` the drain spins tight when logging is toggled off live. `Setup/*`.

**22. Config & account resolution** — `B2` `B3` `B4` `B5` `B7` `B12`
> `B4` a stale account refresh can overwrite a newer role-aware snapshot (unlocked swap). `B2` negative `UsersRefreshSeconds` polls every request though the doc says it disables pickup. `B3` startup-impact validation can't catch a backend/user-validator brick. `B5`/`B7`/`B12` secret residency, legacy-Oof upgrade, level-alias drift. `Accounts`/`Settings`/`Administration`.

**23. DAV & JMAP request correctness** [LIVE] — `H2` `H3` `H4` `H6` `H7` `H8` `H10` `H11`
> `H4` non-birth (wedding) anniversaries are destroyed on every contact edit. `H2` CalDAV create always does a wasted full enumeration (contradicts H13). `H3` all-day/timed duration is off across DST. `H6`/`H7`/`H8`/`H10`/`H11` SSE record buffering, wholesale mailbox-set replacement, recurrence `until` zone, an extra round-trip, malformed-address parsing. Verify JMAP on stalwart, CalDAV on a DAV stack.

**24. Converter correctness** [LIVE] — `D2` `D3` `D4` `D5` `D13` `D15`
> `D5` all-day calendar events shift a day in a non-UTC zone. `D3` duplicate EMAIL lines on contact merge. `D4` Type-4 MIME corrupted by the plaintext truncator. `D2`/`D15` mail listing filter narrows the "whole truth" map and holds the gate across a full flags fetch. `D13` `Occurrences`+`Until` mutual exclusion drops the bound. `Backends.Common/Converters` + `Imap`.

**25. Compose, move & folder handlers** [LIVE] — `F2` `F4` `F5` `F6`
> `F2` draft-submit/occurrence-CANCEL record the replay ledger after the irreversible send → resend-on-crash duplicates. `F5` MoveItems poisons the destination snapshot with `"moved"` → needless Change re-send. `F4` double source resolution per reply/forward. `F6` FolderUpdate silently ignores a parent move.

**26. Sync command conformance** [LIVE] — `F3` `F7` `F8` `F9`
> `F3` GetItemEstimate returns Status 2 (drop-the-folder) for a transient fault where Sync returns retryable 5. `F8` a big folder mid-drain reports "pending" every heartbeat (tight re-poll). `F7` dead `MimeSupport` state. `F9` Search top-level status hard-coded 1 on store error.

**27. Hosting, CLI & logging correctness** — `E5` `E12` `E14` `E15`
> `E5` `eas logs`/`config`/`tls` rebuild the DI container per `/cli` invocation instead of reusing the warm host (mind the config source-attribution caveat). `E12` CLI users-file error is raw where the server path is friendly. `E14` bootstrap logger never flushed. `E15` `RunServerAsync` still inlines all maps + pipeline.

## Phase 5 — Cleanup
*By area. Safe to reorder or skip; nothing else depends on these.*

**28. Metrics, observability & snapshot nits** — `A12` `A13` `A14`
> `A13` per-user metric label cardinality is unbounded from pre-auth usernames (credential-stuffing inflates Prometheus series). `A12` a disposed factory's gauge observer points at cleared state. `A14` `SnapshotCodec.Compress` allocates JSON+gzip+ToArray per commit on a multi-MB snapshot (serialize straight into the GZip stream).

**29. Sieve, SMTP & local backend nits** [LIVE] — `D7` `D8` `D9` `D10` `D11` `D14`
> `D9` Sieve sends AUTHENTICATE PLAIN without checking advertised mechanisms; `D10` treats BYE as success. `D8` `ForceFrom` silently no-ops with no `MailAddress`. `D7`/`D11`/`D14` unknown-destination status, ignored `permanent` flag, literal-length slice order.

**30. Backend behaviour nits** — `D12` `D16` `D17` `D18` `D19` `H12`
> ConversationIndex comment/bytes mismatch (`D12`), unused `bodyPreference`/photo-cap param (`D16`), no reminder-clear (`D17`), `<script>`/`<style>` leak into plain text (`D18`), dead `PathSeparator` knob (`D19`), JMAP envelope `mailFrom` trusts the header From (`H12`).

**31. Contracts, crypto & retry nits** — `K12` `K20` `K22` `K23`
> `K12` `TransientRetry` defaults `idempotent:true` (unsafe replay default). `K20` non-atomic `_disposed`. `K22`/`K23` dead free/busy rank + count-only `DelimitedKey.Decode`.

**32. WebUi, handler & protocol nits** — `C3` `C6` `C7` `C8` `C9` `F10` `F11` `B6` `B9` `B10` `B11` `E7` `E9` `W1` `W5` `W6` `W7` `W8`
> A grab-bag of low-risk polish: DELETE-shares login normalization (`C3`), revocation-latency doc (`C6`), provider-name casing (`C7`), index caching (`C8`), settings error ordering (`C9`), Find/Search default-size + duplication (`F10` `F11`), SQLite-fresh-install message match (`B6`), redaction markers (`B9`), lazy-cache/dup-diagnostics (`B10` `B11`), empty-username after domain-strip (`E7`), `eas tls` markup escape (`E9`), OPAQUE LOH re-encode (`W1`), device-id length truncation (`W5`), WBXML version laxity (`W6`), encoder dead-var (`W7`), silent NUL strip (`W8`).

---

## If you only do part of this

Items **1–13** are the ones that matter for a system anyone else runs: at-rest crypto, sync/send data
integrity, privilege and information disclosure. Items **14–18** pay for themselves in every later item
(the crypto framing, the redirect follower and the boundary cleanups). Everything from 19 on is
quality-of-implementation — real, but survivable if left.

---
---

# FINDINGS

*(Areas A–W are recorded in full in [`review-items-detail.md`](review-items-detail.md); indexed here.
Area S, the cross-cutting structural pass, is given in full below.)*

## Area A — Core: State / Sync / Backend (14)
`A1` **High** Cross-collection premature flush of a deferred Replay rollback → re-delivery of sent items — `State/CollectionStateStore.cs:42,66`.
`A2` **Med** Batched DAV-id insert loses this request's new ids when a concurrent unique-violation rolls back the batch — `State/DavItemMap.cs:97`.
`A3` **Med** `SaveOofAsync`/`GetOofAsync` have no concurrency token / insert-race guard — `State/SyncStateService.cs:185`.
`A4` **Med** `PersistAsync` flushes the entire unified change tracker, coupling independent mutations — `State/SyncStateService.cs:179`.
`A5` **Med** Auth success cache admits a stale password after a backend-side change — `Backend/BackendSessionFactory.cs:96`.
`A6` **Low** `CompleteAccountWipeAsync` can 500 on a concurrency conflict during a wipe ack — `State/DeviceStore.cs:165`.
`A7` **Low** `SnapshotCodec.Decompress` uses a default-comparer dict while the diff engine uses Ordinal — `State/SnapshotCodec.cs:36`.
`A8` **Low** Cache-clear on snapshot change races an in-flight verdict write (TOCTOU) — `Backend/BackendSessionFactory.cs:62`.
`A9` **Low** `activeUsers` counts faulted/unrealized Lazy slots, so watchers aren't trimmed — `Backend/BackendSessionFactory.cs:315`.
`A10` **Low** Faulted Lazy session slots never swept; wedge a (user,device) into repeated failures — `Backend/BackendSessionFactory.cs:296`.
`A11` **Low** Key-0 reset destroys+commits both snapshots/ledgers with no undo — `State/CollectionStateStore.cs:36`.
`A12` **Nit** Session/gauge observers never reset on `DisposeAsync` (stale under test/multi-host) — `Observability/GatewayMetrics.cs:173`.
`A13` **Nit** Per-user metric label cardinality unbounded from pre-auth usernames — `Observability/GatewayMetrics.cs:105`.
`A14` **Nit** `SnapshotCodec.Compress` allocates JSON+gzip+ToArray per commit on a multi-MB snapshot — `State/SnapshotCodec.cs:19`.
**Verified correct:** SaveChanges override placement; concurrency-token stamping set + reload-on-conflict; F12 deferred-Replay rationale (within one collection); unique-violation narrowing; FolderRegistry retry detaches only `UserFolder`; DavItemMap short-lived-context isolation; lease refcounting survives idle eviction; `LastUsedUtc` Interlocked; timer body fully try/caught; value-comparing `TryRemove`; DisposeAsync detaches handlers; `PeekSyncKeyAsync` read-only.

## Area B — Core: Accounts / Administration / Settings / Options (12)
`B1` **High** Case-only-duplicate account rows collapse last-write-wins, dropping a user's overrides — `Accounts/AccountStore.cs:43`. FIXED (item 5): `AccountStore` now STORES `UserName` case-folded (`NormalizeLogin` = `ToLowerInvariant`), so the raw unique index enforces case-folded uniqueness (the store can no longer create a case-variant pair); `LoadAllAsync` additionally WARNS instead of silently dropping if it ever meets an out-of-band pair. Data migration `NormalizeAccountUserNameCasing` (both providers) collapses any existing case-variant pair to the most-recently-updated survivor, then case-folds. BEHAVIOUR/BREAKING: stored/displayed `UserName` is now always lowercase; on upgrade existing mixed-case rows are folded and duplicates collapsed (one-time, deterministic).
`B2` **Med** Negative `UsersRefreshSeconds` polls every request though docs say it disables pickup — `Settings/ChangeStampRefreshGate.cs:33`.
`B3` **Med** Startup-impact validation can't catch a backend/user-validator brick — `Administration/SettingKeys.cs:271`.
`B4` **Med** `EnsureFreshAsync`/`OnRolesChanged` race on `_snapshot`; a stale refresh overwrites a newer one — `Accounts/AccountResolver.cs:117,170`.
`B5` **Low** `Users` config secrets unsealed and retained in the long-lived snapshot — `Accounts/AccountResolver.cs:446`.
`B6` **Low** SQLite "missing table" detected by a localized message substring — `Settings/DbSettingsLoader.cs:72`.
`B7` **Low** Legacy `sieve.enabled` upgrade yields an un-authenticable Oof row with no Host — `Administration/LegacyAccountJson.cs:108`.
`B8` **Low** Upsert/delete `ToLower()` predicates defeat the index and can't see the B1 pair — `Accounts/AccountStore.cs:57`. FIXED (item 5): Get/Upsert/Delete now match `a.UserName == NormalizeLogin(login)` — an exact index seek against the case-folded stored value (sargable), replacing the non-sargable `a.UserName.ToLower() == login.ToLower()` full scan.
`B9` **Low** `SecretRedaction` markers omit PrivateKey/ClientAuth/Signature → cleartext render — `Administration/SecretRedaction.cs:24`.
`B10` **Nit** `ResolvedAccount.OrderedRoles` lazy cache not thread-safe on a shared record — `Accounts/ResolvedAccount.cs:23`.
`B11` **Nit** `ValidateEntry` emits redundant "sealed but no key" per secret when the key won't load — `Accounts/AccountResolver.cs:260`.
`B12` **Nit** Log-level alias set ("critical") disagrees with the config enum validation — `Administration/LogQueryService.cs:43`.
**Verified correct:** fail-closed on invalid rows (+ `Enabled==false` honored); host-controlled/bootstrap key enforcement (write + read paths); empty gateway password can't hash; NaN/Infinity rejected + cadence clamp; timing-safe pinned/gateway compares; log-injection safety (`Contains` not `Like`, login validation); DB-outage resilience keeps last-good; connection-string secret redaction; provider-validation memoization; live role rebuild validated like startup; OIDC AdminClaim/Value pairing; legacy row deserialized-first.

## Area C — WebUi (9)
`C1` **High** Login success clears only the per-user throttle key → a busy NAT IP 429s everyone — `Auth/AuthEndpoints.cs:146`. FIXED (item 6): `LoginAsync` now calls `throttle.RecordSuccess(addressKey)` alongside `RecordSuccess(userKey)` on a successful login, so a valid sign-in clears the accrued IP-wide counter and a shared egress IP no longer locks out everyone behind it. Proven red-first through the real login endpoint (`WebLoginThrottleTests`).
`C2` **Med** Backend `/test` probe is an SSRF reachability oracle for arbitrary operator hosts — `Api/BackendsEndpoints.cs:178`.
`C3` **Med** `DELETE /admin/api/shares` doesn't normalize/validate the login (can't delete what POST created) — `Api/SharesEndpoints.cs:60`.
`C4` **Med** OIDC signs in an unbound config-declared admin on a bare login-claim match — `Auth/OidcLogin.cs:61`. FIXED (item 6): `EvaluateAsync` now tracks whether a subject was bound (matched or just TOFU-recorded) and honors an account's own `Admin` flag only when `account.FromDatabase || subjectBound` — so a config-declared account (which can never TOFU-bind, staying keyed on the mutable login claim) is NOT granted admin until the operator sets `OidcSubject`. Chosen the surgical "refuse the admin bit" over "refuse sign-in": the account still signs in as a plain user (matching the existing documented behaviour that unbound config accounts may sign in), only the admin capability is withheld. Database accounts are unchanged (TOFU-bind is their established model); the per-ticket IdP admin claim stays independent. Proven red-first (`OidcLoginTests.UnboundConfigAdmin_IsNotGrantedAdmin_OnLoginClaimAlone`).
`C5` **Low** Portal `me`/`backends/meta` echo the admin-set backend username to a non-admin — `Api/PortalEndpoints.cs:48`.
`C6` **Low** Session-revocation latency is `max(60s, UsersRefreshSeconds)`, not the documented 60s — `Auth/SessionValidation.cs:131`.
`C7` **Low** Provider name stored verbatim (casing) though resolved case-insensitively — `Api/BackendsEndpoints.cs:261`.
`C8` **Nit** SPA index streamed with no cache headers, re-reading the resource per request — `Setup/WebUiApplicationExtensions.cs:132`.
`C9` **Nit** Settings PUT rejects empty value before checking host-controlled → wrong error — `Api/SettingsEndpoints.cs:64`.
**Verified correct:** no accidental anonymous endpoints (minimal set); admin/user policy separation; CSRF pair on every non-GET + no CORS; cookie Secure unconditional (with logged opt-out); OnValidatePrincipal revocation (fail-open on DB fault); no secret leakage in DTOs; stored-secret preservation across edits; portal privilege-escalation blocked (SelfServiceEditable); TLS private key excluded; login timing-safe/non-enumerable; typed-confirmation destructive ops; OIDC principal re-minted; path-traversal/injection rejected in identifiers; last-admin protection; paged tables; CSP/headers; DataProtection sealed at rest; CLI-pipeline reuse.

## Area D — Backends: Common / Imap / Smtp / Local / Sieve (19)
`D1` **High** No SMTP MaxSize preflight; oversized mail fails only after full DATA, indistinguishable from transient — `Smtp/SmtpSubmitBackend.cs:63`. FIXED: `EnsureWithinMaxSize` preflights RFC 1870 SIZE before `SendAsync`, throwing a non-retryable `BackendException` (→ ComposeMail Status 120). Test is COVERAGE (the streaming symptom needs a live MSA with a small MaxSize the unit env can't exhibit).
`D2` **Med** Mail listing filter narrows the "whole truth" revision map + INTERNALDATE-vs-UTC skew — `Imap/ImapMailBackend.cs:104`.
`D3` **Med** `AppendPreserved` can duplicate EMAIL lines already written by the managed rewrite — `Common/Converters/ContactConverter.cs:262`.
`D4` **Med** Type-4 (full MIME) body corrupted by the plaintext byte-cut truncator — `Common/Converters/MailConverter.cs:147`.
`D5` **Med** All-day events emit `AsUtc` start/end, shifting a non-UTC-anchored all-day event a day — `Common/Converters/CalendarConverter.cs:44`.
`D6` **Med** `SanitizeKeyword` collapses distinct categories → perpetual revision churn — `Imap/ImapMailBackend.cs:582`. FIXED: `SanitizeKeyword` now DROPS a non-atom category (returns empty, caller filters) instead of '_'-mangling it, so two distinct categories can't collide. BEHAVIOUR CHANGE: a category with a space/special (e.g. "Follow Up") is no longer written to IMAP as a keyword at all — inherent since IMAP atoms can't carry them; the prior behaviour stored a lossy, churning "Follow_Up".
`D7` **Low** `MoveItemAsync` surfaces an unknown destination as an unhandled exception — `Imap/ImapMailBackend.cs:341`.
`D8` **Low** `ForceFrom` silently no-ops (submits client From) when `MailAddress` is unset — `Smtp/SmtpSubmitBackend.cs:27`.
`D9` **Low** Sieve sends AUTHENTICATE PLAIN without checking advertised mechanisms — `Sieve/ManageSieveClient.cs:76`.
`D10` **Low** `DeleteScriptAsync` treats server `BYE` as success — `Sieve/ManageSieveClient.cs:132`.
`D11` **Low** `LocalStoreBase.DeleteItemAsync` silently ignores the `permanent` flag, no comment — `Local/LocalStoreBase.cs:143`.
`D12` **Low** ConversationIndex writes the LOW 4 bytes while the comment claims the HIGH 4 — `Common/Converters/MailConverter.cs:99`.
`D13` **Low** `RecurrenceMapper.Parse` drops `Until` when both it and `Occurrences` are sent — `Common/Converters/RecurrenceMapper.cs:128`.
`D14` **Low** `ReadResponseAsync` slices the literal length before the `open >= 0` guard — `Sieve/ManageSieveClient.cs:206`.
`D15` **Med** Revision listing fetches ALL uids' flags in one gated FETCH — no windowing (cf. H8) — `Imap/ImapMailBackend.cs:102`.
`D16` **Nit** `ToApplicationData(string, BodyPreference)` ignores `bodyPreference`, hardcodes 96 KB — `Common/Converters/ContactConverter.cs:33`.
`D17` **Nit** No way to clear a calendar reminder (presence-guard) — `Common/Converters/CalendarConverter.cs:342`.
`D18` **Nit** `HtmlToText` leaks `<script>`/`<style>` bodies into plain text — `Common/Converters/MailConverter.cs:382`.
`D19` **Nit** `PathSeparator` parsed/schema'd/documented but never read — `Imap/ImapOptions.cs:17`.
**Verified correct:** UID EXPUNGE scoping; UIDVALIDITY discipline; session gate thread-safety + idempotent replay; SMTP submit idempotency (send outside retry, QUIT swallowed); DraftMessageBuilder index-matched deletes + multipart body preservation; ghosting presence-guards + EXDATE merge; vCard/iCal/Sieve injection defenses; ServerCertificateValidator single-callback; BackendHttpClientFactory no-redirect + pooled probes; MS-ASTZ blob semantics + bias-only readback; ImapIdleWatcher lifetime/latch/fallback; LocalStore encryption-at-rest AAD + short-lived context; UTF-8-safe truncation.

## Area E — Server: pipeline, hosting, startup, CLI (15)
`E1` **High** `X-Forwarded-Proto` trusted from any peer to rewrite the scheme (OIDC/Autodiscover URLs) — `Setup/WebApplicationExtensions.cs:211`.
`E2` **Med** Retention services permanently stop on any `OperationCanceledException` (EF timeout) — `Setup/LogRetentionService.cs:37`, `Setup/FolderRetentionService.cs:40`.
`E3` **Med** `/cli` captured stdout can corrupt under a fan-out command (two writers, one buffer) — `Cli/LocalCliEndpoint.cs:316`.
`E4` **Med** DB-log shutdown flush uncancellable, blocks up to 2 s with no save token — `Setup/DatabaseLogSink.cs:73,107`.
`E5` **Med** `eas logs`/`config`/`tls` rebuild the DI container per `/cli` call instead of reusing the warm host — `Cli/LogsCommand.cs:58`, `Cli/ConfigCommands.cs:28`, `Cli/TlsCommand.cs:24`.
`E6` **Med** `/readyz` treats a null peer as local, disclosing backend topology — `Setup/ReadinessResponse.cs:26`.
`E7` **Low** `HttpBasicAuth.Parse` accepts an empty username after `DOMAIN\` stripping — `Eas/HttpBasicAuth.cs:32`.
`E8` **Low** `ProtectAsync` doesn't zero the master key on the empty-secret early return — `Cli/CliVerbs.cs:176`.
`E9` **Low** `eas tls` renders cert Subject/SANs through Spectre markup (throws on `[`) — `Cli/TlsCommand.cs:46`.
`E10` **Low** Autodiscover reflects `X-Forwarded-Host` verbatim into the advertised URL — `Eas/AutodiscoverEndpoint.cs:124`.
`E11` **Low** Autodiscover body buffered to a string with no size cap — `Eas/AutodiscoverEndpoint.cs:95`.
`E12` **Low** CLI users-file error is a raw framework exception, unlike the server path — `Cli/CliVerbs.cs:39`.
`E13` **Low** DB-log drain spins tight when `Log:Database` is toggled off live — `Setup/DatabaseLogSink.cs:92`.
`E14` **Nit** Bootstrap Serilog logger never `CloseAndFlush`ed — `ProgramServer.cs:43`.
`E15` **Nit** `RunServerAsync` still inlines all endpoint maps + the pipeline (~100 lines) — `ProgramServer.cs:41`.
**Verified correct:** DI ValidateScopes+ValidateOnBuild in all environments (no captive deps); HTTP/2 body handling; KeepAliveTimeout semantics; dev-exception-page suppression shield; single request-body consumption; /metrics port gating; metrics label "-" until auth; auth throttle keys on trusted-proxy hops; /cli sealed-envelope auth + loopback pre-filter + replay window + AllowPlaintext-only-when-key-absent + response sealing + serve refused + ScopedConsoleWriter routing; SettingsRefreshService shutdown-only OCE; SqlitePragmaInterceptor WAL-once; LongPollWatchdog bounded drain; LogText bidi-strip; ReadinessProbe warm-cache; startup-banner secret redaction.

## Area F — EAS command handlers (11)
`F1` **High** MeetingResponse: a post-reply failure reports retryable → duplicate iTIP REPLY + double PARTSTAT — `Handlers/MeetingResponseHandler.cs:163`. FIXED: the post-send invite-mail delete is now a best-effort try/catch that logs and still returns Status 1 (mirrors ComposeMailHandlerBase), so a cleanup hiccup can't trigger a whole-request retry.
`F2` **Med** Draft-submit/occurrence-CANCEL record the replay ledger after the irreversible send → resend duplicates — `Handlers/SyncHandler.ClientCommands.cs:94`.
`F3` **Med** GetItemEstimate returns Status 2 (drop-folder) for a transient fault where Sync returns 5 — `Handlers/GetItemEstimateHandler.cs:79`.
`F4` **Med** SmartReply/SmartForward resolve the source item twice per send — `Handlers/ComposeMailHandlers.cs:316,367`.
`F5` **Med** MoveItems poisons the destination snapshot with `"moved"` → needless Change re-send — `Handlers/MoveItemsHandler.cs:119`.
`F6` **Low** FolderUpdate silently ignores a requested ParentId change (folder move) — `Handlers/FolderHandlers.cs:338`.
`F7` **Low** `SyncCollectionOptions.MimeSupport` parsed/persisted but never re-consulted (dead state) — `Handlers/SyncCollectionOptions.cs:79`.
`F8` **Low** `PendingChangeDetector` reports "pending" every heartbeat for a collection mid-drain — `Eas/PendingChangeDetector.cs:47`.
`F9` **Low** Search top-level status hard-coded 1 even on store error — `Handlers/SearchHandler.cs:160`.
`F10` **Nit** Find/Search default page sizes differ (25 vs 100), no shared constant — `Handlers/FindHandler.cs:180`, `Handlers/SearchHandler.cs:124`.
`F11` **Nit** Duplicated GAL/body-batch/range logic across Find and Search handlers — `Handlers/FindHandler.cs:79`, `Handlers/SearchHandler.cs:47`.
**Verified correct:** SyncKey Status-3 resets to 0 (no resync loop), transient keeps the key; N−1 replay (item + folder); windowing (deletes first, SoftDelete vs Delete); echo suppression on all client commands + both MoveItems ends; conflict detection (Status 7); send/submit idempotency ordering (submit the single failure point); degraded-send guards (Status 150); Provision handshake (key + ack Status, PolicyType); MoveItems statuses (3=success); Find/Search paging (MaxFetch short-circuit, Total, Range); iMIP triggers + three duplicate guards; MIMESupport/BodyPreference ladder; Class echo ≤12.1; N+1 mitigations (batched GetItems, DAV pre-resolve, concurrent GAL/free-busy); long-poll deadline correctness; ct-last.

## Area H — Backends: JMAP / DAV (12)
`H1` **High** Failed JMAP send leaves an orphan `$draft` in Drafts (syncs to device) — `Jmap/JmapMailSubmit.cs:75,91`. FIXED: on the `notCreated` submission path the store now issues an `Email/set destroy` for the staged import's server id before throwing (best-effort try/catch — a cleanup failure logs a warning and does not mask the non-retryable submission error, so it can't turn a rejection into a retry).
`H2` **Med** CalDAV create always does a full pre-PUT enumeration (contradicts H13) — `Dav/DavStoreBase.cs:98`.
`H3` **Med** JSCalendar duration off by an hour across DST (floating wall-clock subtraction) — `Jmap/JsCalendarConverter.cs:175`.
`H4` **Med** Non-birth (wedding) anniversaries destroyed on every contact edit — `Jmap/JsContactConverter.cs:42,285`.
`H5` **Med** Contact/calendar revision hashes raw JSON whose member order is server-defined → full re-sync — `Jmap/JmapContactStore.cs:289`, `Jmap/JmapCalendarStore.cs:363`. FIXED: both `Revision` methods now hash a CANONICAL serialization (new `JmapRevision.Compute` — object members sorted by name, array order preserved, scalars verbatim, no insignificant whitespace) so a server re-ordering the same JSON no longer flips the revision. BEHAVIOUR NOTE: the revision string for a given item now differs from the pre-fix hash, so on upgrade every JMAP contact/calendar item's stored snapshot revision mismatches once → a one-time full re-sync of those collections (harmless, self-healing; acceptable per Standing context).
`H6` **Low** SSE watcher signals per `data:` line without buffering a full record (ping mis-latch) — `Jmap/JmapEventSourceWatcher.cs:95`.
`H7` **Low** Delete-to-trash/MoveItem replace `mailboxIds` wholesale, losing multi-mailbox membership — `Jmap/JmapMailStore.cs:322,344`.
`H8` **Low** JSCalendar recurrence `until` written as naive local though parsed as UTC — `Jmap/JsCalendarConverter.cs:274`.
`H9` **Low** JMAP blob/eventsource requests don't re-assert same-origin before attaching credentials — `Jmap/JmapClient.cs:246`.
`H10` **Nit** `CategoriesOfAsync` issues an extra serial `Email/get` before the category patch — `Jmap/JmapMailStore.cs:264`.
`H11` **Nit** `StripEmailDisplay` keeps display-name text as the address on a malformed input — `Jmap/JsContactConverter.cs:407`.
`H12` **Nit** `JmapMailSubmit` envelope `mailFrom` trusts the message From verbatim — `Jmap/JmapMailSubmit.cs:32`.
**Verified correct:** TLS honored in all probes/clients; manual redirect safety (5 hops, same-origin, relative-Location); href percent-encoding preserved + lenient share compare; ETag/If-Match quoting + 412 surfacing; create-PUT replay handling; XXE hardening explicit; response size cap; JMAP paging (min(500,maxObjectsInGet), stop on empty/total); */set disposal + SetError buckets + notFound→NotFound; capability gating; SSE not killed by HttpClient timeout; watcher lifetime/reconnect/latch; GAL not N+1; Axigen RRULE time-range quirk; VEVENT/VTODO filtering; deterministic default-collection; contacts PROPFIND listing + hand-parsed free/busy; ctag/sync-token sentinels (transient≠changed); DavPollSeconds live; client-role/credential coherence; floating-time stays floating; read-only members stripped; writes never replayed.

## Area W — WBXML codec & protocol support types (8)
`W1` **Med** OPAQUE data re-encoded to a full base64 string on the LOH per attachment (decode side) — `Wbxml/WbxmlDecoder.cs:125`.
`W2` **Med** `EasRequestParameters` reads/writes multibyte fields with host endianness — `Http/EasRequestParameters.cs:147,214`.
`W3` **Med** `EasVersion.Parse` (header path) accepts "99.9", satisfying every `>=V160` gate — `EasVersion.cs:16`.
`W4` **Low** `FromBase64` silently skips unknown query tags rather than rejecting → hidden desync — `Http/EasRequestParameters.cs:156`.
`W5` **Low** `ToBase64` writes device-id/type length as one byte (truncates >255) — `Http/EasRequestParameters.cs:217`.
`W6` **Low** Decoder accepts WBXML 0x01/0x02 though the spec/tables assume 1.3 — `Wbxml/WbxmlDecoder.cs:80`.
`W7` **Nit** `WriteElement` computes `text` used only in the opaque branch, triple-enumerating nodes — `Wbxml/WbxmlEncoder.cs:68`.
`W8` **Nit** `WriteInlineString` strips embedded NUL silently with no diagnostic — `Wbxml/WbxmlEncoder.cs:121`.
**Verified correct:** MaxDepth/MaxElements caps (decoder + encoder, no StackOverflow); multibyte-uint overflow rejected + negative-count guards; unbounded-stream guard before write; OPAQUE marker convention both sides; element-with-text-and-children in document order; malformed-base64 → WbxmlException; ENTITY range guard; attributes/LITERAL/PI/StrT rejected; EasDateTime Unspecified-as-UTC + invariant culture, no loose fallback; version-byte allowlist + command-code range + policy-key length in the query path; CollectionDiff delete-prioritized budget + numeric-aware id compare; code-page tables spot-verified token-by-token (pages 0,2,4,14,17,21,23,25) incl. deliberate gaps, no duplicate tag names; WriteMultiByteUInt value-0 case.

## Area S — cross-cutting structural (9)
`S1` **Med** Server references Core only transitively though it depends on Core pervasively — `src/ActiveSync.Server/ActiveSync.Server.csproj` + all of `src/ActiveSync.Server/**`. The graph's largest dependency is an accident of the backend/WebUi references; a backend dropping its Core reference would break Server compilation for a non-obvious reason. FIX: add an explicit `<ProjectReference Include="..\ActiveSync.Core\ActiveSync.Core.csproj" />` to `ActiveSync.Server.csproj` (zero behavioral change; makes the graph honest).
`S2` **Med** AES-256-GCM framing implemented twice independently — `src/ActiveSync.Crypto/SecretValue.cs:28-87` and `src/ActiveSync.Core/Security/LocalContentProtector.cs:50-111`. Both hand-roll `nonce‖ct‖tag` (12/16 bytes, `RandomNumberGenerator.Fill`, `using AesGcm`, base64+prefix) + the mirror unseal; only the prefix (`enc:v1:`/`v1:`) and AAD differ. A framing fix must land in two assemblies or diverge. FIX: extract a `SealedBlob` primitive into `ActiveSync.Crypto` taking `(prefix, aad, key)`; both callers use it (Core already refs Crypto). Distinct prefixes/AAD preserved as arguments. (Pairs with `K11`; worked together in item 15.)
`S3` **Med** `SendFollowingRedirectsAsync` + `IsSafeRedirect` duplicated near-verbatim — `src/ActiveSync.Backends.Dav/WebDavClient.cs:231-297` and `src/ActiveSync.Backends.Jmap/JmapClient.cs:448-512` (the JMAP copy's own comment: "Mirrors WebDavClient"). The security-sensitive same-origin credential-forwarding logic in two places; a fix to one can miss the other. FIX: move the redirect-following send + `IsSafeRedirect` into `Backends.Common` (a `RedirectingHttpSender`), both clients call it wrapped in `TransientRetry.SendHttpAsync`; relocate the `IsSafeRedirect` unit test with it. [LIVE — DAV+JMAP.]
`S4` **Low** `JmapMailStore.cs` (847 lines, 26 async methods) is the one un-split backend store — `src/ActiveSync.Backends.Jmap/JmapMailStore.cs`. The IMAP equivalent is already split (`.Watch.cs`); JMAP splits calendar/contacts/oof/submit but not mail. FIX: split into partials by concern — `JmapMailStore.cs` (CRUD+listing), `.Search.cs`, `.Watch.cs`, `.Attachments.cs`. Same type, no API change.
`S5` **Low** `MergedFreeBusy` lives in `ActiveSync.Contracts` but AGENTS.md documents it as a Core type, and it is host-only output a plugin never calls — `src/ActiveSync.Contracts/MergedFreeBusy.cs:8`; consumed at `Server/Eas/Handlers/ResolveRecipientsHandler.cs:156`. A plugin implements `IFreeBusySource` (returns busy periods) but never calls `MergedFreeBusy.Build`. FIX: move `MergedFreeBusy` to `ActiveSync.Core` next to the host logic and correct AGENTS.md — Contracts should carry only what a plugin builds against (same rationale that moved `IBackendSession` out). **Breaking Contracts surface — acceptable.**
`S6` **Low** Two overlapping log-scrubbers with different rules in different assemblies — `src/ActiveSync.Contracts/WireLog.cs:33` (`Payload`, keeps CR/LF/TAB) and `src/ActiveSync.Server/Eas/LogText.cs:10` (`Clean`, additionally strips bidi overrides). Called side by side (`EasContext.cs:38`); the bidi-override defense exists on only one path, so a payload could still smuggle bidi. FIX: route both through one character classifier; keep the two entry points but extend the bidi rule to `Payload`. (Pairs with `K21`; worked together in item 17.)
`S7` **Info→Low** Converter namespace is a second root in the `Backends.Common` assembly — `src/ActiveSync.Backends.Common/Converters/*.cs` declare `namespace ActiveSync.Backends.Converters;` while siblings declare `ActiveSync.Backends.Common;`. One assembly exposes two namespace roots, neither `ActiveSync.Backends`. FIX: rename the converter namespace to `ActiveSync.Backends.Common.Converters` (folder-aligned); touches backend `using` sites only. **Breaking — acceptable.**
`S8` **Info→Low** No `ActiveSync.Cli` test path; the client holds the master key and gates admin commands — `tests/**` (no `ActiveSync.Cli.Tests`), `src/ActiveSync.Cli/Program.cs` (top-level statements, no namespace). The `/cli` sealing counterpart is tested in Server.Tests, but the client half (±60 s window construction, 127.0.0.1-only target, plaintext fallback, `serve`/`protect`/`EAS_NO_FORWARD` local-only) is untested. FIX: extract the forwarding logic from top-level `Program.cs` into a testable class (gives Cli a namespace too) and add `tests/ActiveSync.Cli.Tests` referencing Cli + Crypto, asserting the envelope round-trips against `LocalCliEnvelope`/`SecretValue` and that `serve`/`protect`/`EAS_NO_FORWARD` force local.
`S9` **Info→Nit** `WireLog` sits in the published plugin contract as an incidental host utility — `src/ActiveSync.Contracts/WireLog.cs`. Used by Backends.Common + Server but a plugin has no reason to call it; a logging tweak is technically a Contracts ABI change (version-gated). It is BCL-only, so it could live in `ActiveSync.Protocol` (below Contracts) instead, shrinking the contract surface. FIX: move `WireLog` to `ActiveSync.Protocol`. (`TransientRetry` earns its Contracts place — a plugin HTTP backend uses it; leave it.)
**Verified correct / structurally sound:** dependency direction holds everywhere declared (Backends.Common→Contracts only; Crypto→nothing; Cli→Crypto only; Contracts→Protocol; WebUi→Core only; backends→Core+Common; no provider references another); converters live in Backends (MimeKit/Ical.Net/FolkerKinzel out of Core); round-1 Crypto namespaces correct; host-only composite types out of Contracts; secret redaction / HTTP retry / HTTP handler creation consolidated; Central Package Management intact (`Directory.Packages.props`, no per-project Version, the one `GlobalPackageReference` exception); `nuget.config` pins nuget.org; analyzer posture keeps 0 warnings (every `#pragma` narrowly scoped + justified, EF-migration `612/618` in generated code, no `[SuppressMessage]`, no `.editorconfig` downgrades); packaging boundary coherent (Protocol/Crypto/Contracts/Backends.Common/Core packable); `ContractVersion` present + lockstepped; test topology matches design apart from the Cli gap (S8).

## Found while working the queue
*(New findings discovered mid-implementation go here — see `fix-review.md` step 8. None yet.)*

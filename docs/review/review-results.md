# Round 2 — fix results

Maintained by the fix orchestrator (see `fix-review.md` § Recording results). One entry per completed
item; each pairs the worker's claim with the orchestrator's independent verification.

Baseline at `946a9c0`: build 0 warnings · 1020 unit tests green, 0 skipped (Protocol 78 · Core 628 ·
WebUi 71 · Server 243).

---

## Item 1 — Encryption key derivation & content AAD
**Findings:** `K1` `K2` `K14`
**Commits:** `e53a225` (K1) · `60b1d64` (K2) · `c7324b2` (K14)
**Verification:** integrity items=32 live=10 assigned=unique=132 dupes=0 encoding=0 ✓ · cursor → item 2 ✓ ·
one commit per finding, ID in subject ✓ · build clean (0 warnings) ✓ · unit suite **1025 passed, 0 skipped**
(Protocol 78 · Core 633 · WebUi 71 · Server 243; +5 over baseline) ✓ · integration 8 skipped (no backend —
item 1 is not LIVE and lands no migration / no auth-pipeline / no HTTP-pipeline change, so no live suite
required) ✓ · diffs independently inspected — each fix matches the finding's described defect (not merely
compiles+passes) ✓.
**Notes:**
- **K1 is the fix `claimed-fixed-but-not.md §2` demanded.** Round 1's K45 left the fixed global salt as the
  silent default; the worker *removed* the fixed application salt entirely and now **refuses** a passphrase
  key when `ActiveSync:Encryption:KeyDerivationSalt` is unset (fail-closed). The default path can no longer
  silently share a salt. Verified in the K1 diff: `DerivationSalt` constant deleted; `TryLoadKey` returns
  null + actionable error for an unsalted passphrase.
- **Breaking (accepted, per Standing context):** passphrase deployments must now set `KeyDerivationSalt` (or
  switch to a base64 key). A newly-set salt derives a new master key → re-keys stored local content and
  re-seals `enc:v1:` values on upgrade. Deployments that already set `KeyDerivationSalt` are unaffected
  (that path was already SHA-256-bound and is unchanged); raw base64-key deployments unaffected.
- **Judgment call (K1) — refuse rather than first-boot DB-persisted salt.** The worker's rationale is sound
  and I concur: `EncryptionKeyLoader` lives in the BCL-only `Crypto` assembly and the slim `eas` CLI derives
  the *same* master key from config alone (no DB) to seal `/cli` envelopes — a DB-only salt would desync the
  CLI, and would need an EF migration (item 1 is not `[LIVE]`). "Refuse" is the self-contained fail-closed
  option the finding explicitly lists. Could reasonably have gone the other way (first-boot salt) had the
  CLI not shared the derivation.
- **Judgment call (K2) — reject C0 control chars rather than length-prefix the AAD.** Chosen because it
  closes the `\n`-collision with **zero re-key** for legitimate rows (real logins/collections never contain
  control chars), whereas re-framing the AAD would invalidate every existing local row. Defensible either
  way; the low-breakage variant was taken.
- **Judgment call (K14) — canonical-base64 requirement + documented residual.** A *canonical* low-entropy
  base64-32 value is still taken verbatim (the raw path is unstretched by definition); the worker documented
  this residual in the loader, meeting the finding's "at minimum note the floor" bar. A full close
  (opt-in flag / entropy check) would expand config/validator/docs beyond item 1's scope.
- **Collateral (verified in scope):** existing passphrase unit tests + `EncryptionAtRestTests` updated to
  supply a salt; `docs/configuration.md` + README quick-start reworked to recommend the base64 key and mark
  `KeyDerivationSalt` required for passphrases. All consistent with the finding; no source touched outside
  the crypto/security seam.
- No coverage-only tests (all three proven red-first). No new findings filed.

---

## Item 2 — Sync-state flush integrity [LIVE]
**Findings:** `A1` `A4` `A11`
**Commits:** `8573644` (A1, A4, A11 — one atomic commit; see note)
**Verification:** integrity items=32 live=10 assigned=unique=132 dupes=0 ✓ · cursor → item 3 ✓ ·
build clean (0 warnings) ✓ · unit suite **1027 passed, 0 skipped** (Protocol 78 · Core 635 · WebUi 71 ·
Server 243; +2 over item 1) ✓ · **live suite (independent, fresh clean-volume Stalwart): 141 passed, 0
skipped** ✓ (LIVE requirement met — real backend, passed > 0) · diffs independently inspected — fix
matches the A1 recommended remedy exactly ✓.
**Notes:**
- **Single commit for the cluster — accepted.** A1/A4/A11 are one intertwined change: A4 is the
  shared-tracker flush coupling, A1 is the cross-collection re-delivery it enables (the F12 bug reachable
  across collections), A11 is the same key-0 destroy-live-state hazard. The fix — **defer the Replay
  rollback and the key-0 reset out of `ValidateSyncKeyAsync` into the collection's own
  `CommitCollectionStateAsync` via a new `validation` mode** — resolves all three in one edit that cannot be
  meaningfully split. Protocol step 2 permits a "tight cluster" commit; all three IDs are in the subject.
  Confirmed in the diff: `ValidateSyncKeyAsync` no longer mutates the tracked entity or calls
  `SaveChangesAsync`; the Initial/Replay/Current transition is applied atomically with the new generation
  in `CommitCollectionStateAsync`. Net SyncKey/snapshot outcomes are byte-identical to before.
- **A1 & A11 proven red-first** (`ReplayRollback_NotCorruptedBySiblingCollectionFlush`,
  `KeyZeroReset_Deferred_DoesNotDestroyLiveStateOnAbandonedRound` — both failed on unmodified code with the
  described symptom, green after fix). **A4 is coverage, not an independent red:** it has no harmful symptom
  beyond A1's cross-collection flush, so it is proven by A1's sibling-flush mechanism plus the
  transaction-policy corollary documented in `SyncStateService`. Correctly labelled coverage by the worker.
- **Breaking (internal only):** `CommitCollectionStateAsync` gained a required `validation` parameter. It is
  a host-only method — no plugin/contract surface — and all internal call sites were updated in the same
  commit. Not a published-contract break.
- **Judgment call — defer-to-commit vs per-collection short-lived contexts.** The worker took A1's second
  suggested remedy (defer into the commit) over the first (short-lived context per collection). I concur:
  it preserves the atomic per-collection commit semantics and centralizes the state transition; the only
  cost is the wider `CommitCollectionStateAsync` signature. Could reasonably have gone the other way.
- **Behaviour-test rewrite (not weakened):** one `SyncKeyLifecycle` assertion now reads the post-Replay diff
  base via `ReadPreviousSnapshot` (the entity is no longer rolled back at validation time); the round-1
  `F12_ReplayRollback_IsNotPersistedBeforeCommit` guard still passes, comment updated to reflect deferral.

---

## Item 3 — IMAP send & category integrity [LIVE]
**Findings:** `D1` `D6` `F1`
**Commits:** `25344fe` (D1) · `b4f580c` + `c3c1cfc` (D6 fix + live-test alignment) · `1c64d4a` (F1)
**Verification:** integrity items=32 live=10 assigned=unique=132 dupes=0 encoding=0 ✓ · cursor → item 4 ✓ ·
one commit per finding, ID in subject ✓ · build clean (0 warnings) ✓ · unit suite **1037 passed, 0 skipped**
(Protocol 78 · Core 644 · WebUi 71 · Server 244; +10 over item 2) ✓ · **live suite (independent, fresh
clean-volume Stalwart): 141 passed, 0 skipped** ✓ · diffs independently inspected — each fix matches the
finding's remedy ✓.
**Notes:**
- **D6 & F1 proven red-first; D1 is coverage (justified).** D6 red: `"a b"` mangled to `"a_b"` colliding
  with a real `"a_b"` on unmodified code. F1 red: post-send invite-delete failure returned Status 4
  (retryable → duplicate REPLY + double PARTSTAT) on unmodified code, now Status 1. **D1 is coverage-not-
  proof:** the true symptom (streaming full DATA before a 552) requires a live MSA advertising a small
  `MaxSize`, which the unit env can't exhibit — the worker labelled it coverage and unit-tested the boundary
  helper (`EnsureWithinMaxSize`). Follows the repo's existing SMTP-finding precedent (D9). Diff confirms the
  preflight throws a non-retryable `BackendException` before `SendAsync`.
- **D6 is a deliberate behaviour change (disclosed):** a non-atom category (e.g. "Follow Up") is no longer
  written to IMAP at all, rather than stored as a lossy churning `Follow_Up`. Inherent — IMAP atoms cannot
  carry spaces/specials, no lossless option exists. This forced a rewrite of the live
  `MailFlowTests.Categories_RoundTripAsImapKeywords_AndGhostedChangeLeavesThem`, which encoded the old
  mangle behaviour; the pre-rewrite live run showed exactly 1 failure (that test), confirming the blast
  radius is contained. Rewrite (`c3c1cfc`) is not a weakening — it now asserts the atom category applies and
  the non-atom one is absent.
- **F1 tail swallows all exceptions including OCE** (unlike the outer catch, which filters OCE) — matches
  `ComposeMailHandlerBase`'s post-submit tail: a cancellation after the reply is sent must still report
  success or the client resends. Consistent with the established pattern.
- **Test scaffolding (non-production, disclosed):** added a `DeleteFailWith` hook to the handler harness's
  `RecordingStore` to drive F1's failure path (no existing test behaviour changed), and
  `InternalsVisibleTo("ActiveSync.Core.Tests")` on the Imap + Smtp csprojs to unit-test the two internal
  helpers — matching the existing Dav/Jmap pattern. No production surface widened.
- No new findings filed.

---

## Item 4 — JMAP submission & revision integrity [LIVE]
**Findings:** `H1` `H5`
**Commits:** `32dbe80` (H1) · `145a6c4` (H5)
**Verification:** integrity items=32 live=10 assigned=unique=132 dupes=0 encoding=0 ✓ · cursor → item 5 ✓ ·
one commit per finding, ID in subject ✓ · build clean (0 warnings) ✓ · unit suite **1040 passed, 0 skipped**
(Protocol 78 · Core 647 · WebUi 71 · Server 244; +3 over item 3) ✓ · **live suite (independent, fresh
clean-volume Stalwart): 141 passed, 0 skipped** ✓ · diffs independently inspected — both fixes match the
finding remedies ✓.
**Notes:**
- **Both proven red-first.** H1: `Send_SubmissionRejected_DestroysStagedDraft_AndThrows` failed on
  unmodified code (staged `$draft` survived a `notCreated` rejection); now `SendAsync` issues a best-effort
  `Email/set destroy` on the staged import before throwing. H5: `GetItemRevisions_IsIndependentOfMemberOrder`
  (added to both contact & calendar store tests) failed on unmodified code (member-order re-serialization
  flipped the SHA-256); now both `Revision` methods delegate to a shared `JmapRevision.Compute` that hashes a
  canonical serialization (object members sorted by name, array order preserved). Diffs confirm both.
- **Breaking (self-healing, disclosed):** H5 changes the revision string for every JMAP contact/calendar
  item, so on upgrade each item's stored snapshot revision mismatches once → a one-time full re-sync of those
  collections. Harmless, self-heals on the next round, acceptable per Standing context.
- **H1 cleanup can't mask the error:** the destroy is try/catch, re-propagates `OperationCanceledException`,
  and a destroy failure only logs a warning — so a rejected submission always still surfaces its
  non-retryable error (never flips to a retry). No API change.
- **Judgment call (H5):** full recursive canonicalization over the detail's alternative of hashing a stable
  server field (`updated`/`sequence`). I concur — the server field isn't guaranteed present across JMAP
  servers, whereas canonicalization is provider-agnostic and robust. Reasonable either way.
- No new findings filed.

---

## Item 5 — Account-row case collation
**Findings:** `B1` `B8`
**Commits:** `8c1b99a` (B1 + B8 — one commit; inseparable, same storage-normalization change)
**Verification:** integrity items=32 live=10 assigned=unique=132 dupes=0 encoding=0 ✓ · cursor → item 6 ✓ ·
both IDs in subject ✓ · **both-provider migrations present** (Sqlite `20260723013353_` + Npgsql
`20260723013410_NormalizeAccountUserNameCasing`) ✓ · build clean (0 warnings) ✓ · unit suite **1043 passed,
0 skipped** (Protocol 78 · Core 650 · WebUi 71 · Server 244; +3 over item 4) ✓ · **live suite (independent,
fresh clean-volume Stalwart): 141 passed, 0 skipped** — the new migration applies cleanly in-chain on fresh
SQLite temp DBs ✓ · diffs independently inspected ✓.
**Notes:**
- **This is the fix `claimed-fixed-but-not.md §1` demanded.** Round 1's B2 added only `.ToLower()` lookup
  predicates, leaving the raw BINARY unique index and `LoadAllAsync` collapse intact. The worker instead
  **changes stored casing**: `AccountStore.NormalizeLogin` (`ToLowerInvariant`) folds `UserName` on every
  write, so the existing raw unique index now enforces case-folded uniqueness (the store can no longer create
  a case-variant pair — B1's primary remedy); Get/Upsert/Delete predicates become exact `UserName ==
  normalized` index seeks (closes B8's non-sargable scan); `LoadAllAsync` warns instead of silently dropping
  an out-of-band pair (defense in depth). Confirmed in the diff.
- **Data migration verified on both providers.** `NormalizeAccountUserNameCasing` (data-only, model snapshot
  unchanged) collapses each existing case-variant pair to the most-recently-updated survivor (tie-break
  highest Id) **before** case-folding — correct ordering, since folding first would collide on the unique
  index. Applies cleanly on real SQLite via the live suite (141/0). Postgres migration exercised only in CI
  (`AS_TEST_PG`, unset locally) — the GitHub `integration` legs will apply it.
- **B1 & B8 proven red-first**, plus a dedicated migration-SQL-on-populated-data test
  (`Migration_DedupSql_CollapsesCaseVariantPair_KeepingNewestAndFolding`) — proof, not mere coverage, since
  the fixture `Migrate()` only runs the migration on empty DBs. Correctly reasoned by the worker.
- **Breaking (disclosed):** stored/displayed `UserName` is now always lowercase (`eas users` / admin list
  render lowercase); existing mixed-case rows fold and any duplicate collapses once, deterministically, on
  upgrade. Acceptable per Standing context.
- **Judgment call — normalize-on-write vs case-insensitive index (NOCASE/citext/functional).** Worker chose
  normalize-on-write because tests build schema model-driven (`EnsureCreated`) while production uses
  migrations, and a case-insensitive index would need provider-specific model config diverging between the
  two paths; normalize-on-write is uniform across both providers and is one of B1's two endorsed options. I
  concur. Residual: a raw out-of-band DB write could still insert a variant pair — covered by the
  `LoadAllAsync` warning. SQLite `lower()` folds ASCII-only (Postgres is Unicode-aware); non-ASCII SQLite
  logins fold on their next write — accepted edge, logins are overwhelmingly ASCII/email.
- **Scope discipline:** round-1 B2 also touched `GlobalSetting.Key`; the worker correctly stayed inside the
  item (B1/B8 are `AccountStore`/`AccountEntry` only) and did not touch `GlobalSettingStore`.
- No new findings filed.

---

## Item 6 — WebUi throttle & OIDC admin binding
**Findings:** `C1` `C4`
**Commits:** `12837d3` (C1) · `b3da969` (C4)
**Verification:** integrity items=32 live=10 assigned=unique=132 dupes=0 encoding=0 ✓ · cursor → item 7 ✓ ·
one commit per finding, ID in subject ✓ · build clean (0 warnings) ✓ · unit suite **1045 passed, 0 skipped**
(Protocol 78 · Core 650 · WebUi 73 · Server 244; WebUi +2) ✓ · **live suite (independent, fresh
clean-volume Stalwart): 141 passed, 0 skipped** — ran despite worker's WebUi-only scoping, per the
auth-change blast-radius rule; the EAS Basic-auth pipeline is confirmed untouched ✓ · diffs inspected ✓.
**Notes:**
- **Both proven red-first.** C1: a legitimate user sharing a NAT IP with a stream of failed logins got 429
  on unmodified code (the IP-wide `addressKey` counter never cleared on success); fix adds
  `throttle.RecordSuccess(addressKey)` — driven through the real `WebUiHost` TestServer login endpoint. C4:
  an unbound config-declared admin returned `IsAdmin: true` on a bare login-claim match before the fix,
  `false` after; a bound config admin still gets admin. Both diffs confirm the remedy.
- **Breaking (disclosed):** C4 withholds the admin capability from an unbound config-declared account under
  OIDC until the operator sets `OidcSubject` (per webui.md:82-85). The account still signs in as a plain
  user — only the admin bit is withheld.
- **Judgment call (C4) — refuse the admin bit, not the whole sign-in.** The detail offered both. The worker
  chose the surgical option because an existing test asserts an unbound config account (carol) IS allowed to
  sign in, and the finding's closing clause scopes the requirement to the *admin* bit; refusing sign-in
  would contradict that tested behaviour and could lock out operators who deliberately use an immutable
  `LoginClaim` (webui.md's supported alternative to binding). I concur — the per-ticket IdP `claimAdmin`
  and database accounts are correctly left unchanged. Confirmed in the diff: `accountAdmin = Admin == true
  && (FromDatabase || subjectBound)`.
- Both proven red-first; no coverage-only tests. No new findings filed.

---

## Item 7 — Forwarded-header trust
**Findings:** `E1` `E10`
**Commits:** `e962c7b` (E1) · `01022de` (E10)
**Verification:** integrity items=32 live=10 assigned=unique=132 dupes=0 encoding=0 ✓ · cursor → item 8 ✓ ·
one commit per finding, ID in subject ✓ · build clean (0 warnings) ✓ · unit suite **1052 passed, 0 skipped**
(Protocol 78 · Core 650 · WebUi 73 · Server 251; Server +7) ✓ · **live suite (independent, fresh
clean-volume Stalwart): 141 passed, 0 skipped** — ran per the request-pipeline-change rule (this is exactly
the "cookie-policy broke 23 tests" class); the EAS pipeline is unbroken ✓ · diffs inspected ✓.
**Notes:**
- **Both proven red-first** at the real decision seams. E1: `X-Forwarded-Proto` from an untrusted peer
  rewrote `Request.Scheme` to `https` on unmodified code (`ResolveRequestScheme`); now gated. E10:
  `X-Forwarded-Host: evil.example.com` leaked into the advertised Autodiscover EAS URL on unmodified code
  (`BuildEasUrl`); now gated. Both verified against the pre-fix behaviour extracted into the seam.
- **Shared mechanism confirmed:** a new `EndpointAuth.IsFromTrustedProxy(HttpContext, AuthOptions)`
  (`src/ActiveSync.Server/Eas/EndpointAuth.cs:72`) reuses the private `Normalize`/`IsTrusted` peer-trust
  logic that `ClientKey` already applies to `X-Forwarded-For` — so scheme/host trust and throttle-key trust
  share one gate. `PublicUrl` still wins from any peer (never depends on client headers); with no trusted
  proxies configured (the default) forwarded headers are ignored entirely. Exactly what E1/E10 asked for.
- **Behaviour change (hardening, disclosed):** a deployment that relied on `X-Forwarded-Proto/Host` being
  honoured with `PublicUrl` unset AND its proxy not listed in `Auth:TrustedProxies` will now see those
  headers ignored — the documented remedy (set `PublicUrl` or list the proxy) already matches how
  `EndpointAuth.ClientKey` behaves. `BuildEasUrl` visibility widened private→internal for testing only; no
  breaking API change.
- Both proven red-first; no coverage-only tests. No new findings filed.

---

## Item 8 — Protocol version gating & query parsing
**Findings:** `W3` `W2` `W4`
**Commits:** `ba7a5ca` (W3) · `c2b12fd` (W2 + W4 — tight cluster, both in `EasRequestParameters.cs`)
**Verification:** integrity items=32 live=10 assigned=unique=132 dupes=0 encoding=0 ✓ · cursor → item 9 ✓ ·
IDs in subjects ✓ · build clean (0 warnings) ✓ · unit suite **1059 passed, 0 skipped** (Protocol 85 · Core
650 · WebUi 73 · Server 251; Protocol +7) ✓ · no live suite required (not LIVE; no migration/auth/pipeline
change) ✓ · diffs inspected ✓.
**Notes:**
- **Protocol hard gate honoured.** These are parse-path changes, NOT WBXML code-page table changes, so the
  "every table change needs a round-trip test" rule does not apply — but each parse change still got a
  dedicated test. Confirmed no `WbxmlCodePages` edits in the diff.
- **W3 & W4 proven red-first; W2 is coverage (justified).** W3: `EasVersion.Parse` returned `99.9`/`25.5`
  verbatim (satisfying every `>=V160` gate) on unmodified code; now an exact `(major,minor)` allowlist
  mirroring `ProtocolVersionBytes` ({2.5,12.0,12.1,14.0,14.1,16.0,16.1}) degrades anything unknown to
  `V141`. W4: an unknown base64 query field tag parsed silently before; now `default: throw FormatException`
  → clean 400. **W2 is coverage-not-proof:** `BitConverter`→`BinaryPrimitives.*LittleEndian` for the locale
  + policy-key fields is latent on little-endian hosts (this amd64 box), where the two are byte-identical, so
  the bug cannot be exhibited; the test pins the on-the-wire bytes to explicit LE to guard a big-endian
  regression. Correctly labelled coverage by the worker in both the test and the note.
- **Behaviour changes (intended, disclosed):** (1) an `MS-ASProtocolVersion` header outside the known set now
  degrades to 14.1 rather than being honoured verbatim; (2) a base64 query with an unknown field tag now
  returns 400 instead of parsing with wrong values. No breaking API change.
- **Judgment call (W3) — fall back to `V141` rather than throw at the header call site.** The detail offered
  either; falling back keeps `Parse` total and matches the existing unparsable-input behaviour (non-
  disruptive). Exact allowlist over the looser "reject majors outside {2,12,14,16}", so 16.2/14.5 also
  degrade. I concur.
- No new findings filed.

---

## Run summary — items 1–8 (Phase 1 complete)
All eight Phase-1 items landed and independently verified. Final unit suite **1059 passed, 0 skipped**
(baseline 1020; +39 tests). Build 0 warnings throughout. LIVE / migration / auth / pipeline items (2, 3, 4,
5, 6, 7) each verified against a **fresh clean-volume Stalwart** with the **full** integration suite at
**141 passed, 0 skipped** — never a skipped-suite pass. Every finding proven red-first except four
coverage-labelled tests (D1 live-MSA-only, A4 coupling-of-A1, W2 LE-host-latent) — each justified and marked.
Breaking changes (K1 re-key, B1/B8 case-fold + migration, H5 revision re-sync, C4 admin-bit, D6 category
drop, E1/E10 header gating) are recorded per finding. Cursor rests at item 9 (Phase 2).

---

## Item 9 — Certificate store & TLS resolver
**Findings:** `K4` `K5` `K6` `K17` `K18` `K19`
**Commits:** `d4b9b54` (K4) · `d3d7c0b` (K5) · `13d2eeb` (K6) · `f3a9e14` (K17) · `f34598b` (K18) ·
`eeab847` (K19) · `a85aeef` (queue-line strike — see deviation note)
**Verification:** integrity items=32 live=10 assigned=unique=132 dupes=0 encoding=0 ✓ · cursor → item 10 ✓ ·
one commit per finding, ID in subject ✓ · **both-provider migrations present** (Sqlite `20260725123448_` +
Npgsql `20260725123503_AddServerCertificateConcurrencyToken`) ✓ · build clean (0 warnings) ✓ · unit suite
**1067 passed, 0 skipped** (Protocol 85 · Core 658 · WebUi 73 · Server 251; +8 over item 8) ✓ · **live suite
(independent, full, fresh clean-volume Stalwart): 141 passed, 0 skipped** ✓ (not a marked [LIVE] item, but
K6 lands an EF migration — the unmarked-item rule applies) · **all six diffs read against their detail
entries** — each touches the named site and implements the stated remedy ✓.
**Notes:**
- **First item run under the Sonnet-pinned worker model** and the new every-finding verification. The fixes
  themselves are clean; the one problem was process, not code (below).
- **PROCESS DEVIATION (worker self-disclosed, accepted this once):** the six fix commits did **not** each
  carry their own `review-items.md` strike. All six code commits landed first, then one separate
  bookkeeping commit (`a85aeef`) struck the whole queue line. Protocol step 3 requires the mark in the
  *same* commit, and the reason is exactly the exposure this created: an interruption between `eeab847`
  and `a85aeef` would have left six findings fixed with an unmoved cursor, and item 9 would have been
  redone from scratch. End state is correct and the cursor did advance, so no check failed — but this is
  the failure mode the rule exists to prevent, and it is the thing to watch on items 10–13.
- **K4/K5/K6/K17/K19 proven red-first; K18 is coverage (justified).** A leaked native key handle has no
  assertable symptom in a unit test, so the test guards `KeySizeOf` correctness across repeated calls
  instead. Correctly labelled in both the test comment and the worker's report.
- **K6 is NOT the `A22`/`A6` trap.** The worker reused the existing `catch (DbUpdateException)` rather than
  adding a new catch, claiming `DbUpdateConcurrencyException` is covered "for free". I checked the filter
  specifically because round 1's wipe-ack fix failed in exactly this way: that catch is **unfiltered**
  (no `when (IsUniqueViolation)`), so the subtype genuinely is caught and the loser reloads and serves the
  winner. Claim verified, not taken on trust.
- **K6 scope superset (deliberate, coherent):** the detail says replace "only when the current blob still
  fails to load under the same key"; as implemented the replace path also fires when the row is due for
  renewal — which is K4's renewal landing in the same method. The two interlock correctly. "Log loudly on
  replace" is satisfied by `TryLoad`'s existing warning, which names the fingerprint change.
- **Breaking / behaviour changes (disclosed):** (1) **K4** — self-signed validity drops 20 years → 397 days
  with auto-renewal 30 days out, so a deployment re-presents a **new fingerprint roughly annually** instead
  of never; devices must re-trust on each renewal. This makes the self-signed path work on iOS at the cost
  of periodic re-trust, and it contradicts README:526's "20-year validity" — **README/docs still need
  updating** (not in item 9's file cluster; noted here rather than fixed inline, per protocol step 8).
  (2) **K17** — a keyless or expired operator cert now throws at startup instead of failing opaquely at
  handshake, which is what README:559 already promised. (3) **K6** — new `ConcurrencyToken` column on
  `ServerCertificate`, no application-visible change beyond conflict detection.
- **Judgment call (K19) — fall back rather than throw.** The detail offered either fallback or routing an
  IP literal to `AddIpAddress`; the worker did both (K5 supplies the routing, K19 the fallback) and folded
  them into one `TryAddHostName` helper. The `host` reassignment precedes `CN={host}`, so subject and SAN
  stay consistent. `TryAddHostName` catches only `ArgumentException` — the exception the red-first test
  actually produced; a different SAN-builder failure mode would still escape. Narrow, accepted.
- No new findings filed.

---

## Item 10 — Password & throttle robustness
**Findings:** `K3` `K8` `K9` `K15`
**Commits:** `e53b00f` (K3 + K15 — tight cluster, same `TryParse` validation block) · `501c069` (K9 + K8 —
tight cluster, same `Prune`/`RecordFailure` lines) · `2d865cc` (queue-line strike — see deviation note)
**Verification:** integrity items=32 live=10 assigned=unique=132 dupes=0 encoding=0 ✓ · cursor → item 11 ✓ ·
all four IDs in commit subjects ✓ · build clean (0 warnings) ✓ · unit suite **1071 passed, 0 skipped**
(Protocol 85 · Core 660 · WebUi 73 · Server 253; +4 over item 9 — exactly the tests added) ✓ · **live suite
(independent, full, fresh clean-volume Stalwart): 141 passed, 0 skipped** ✓ (not marked [LIVE], but this
changes authentication — the unmarked-item rule applies) · **all four diffs read against their detail
entries** ✓.
**Notes:**
- **Tight-cluster commits accepted.** K3+K15 are two guards in the same `TryParse` validation block;
  K9+K8 rewrite the same `Prune` lines (K9 injects the clock, K8 makes the stamp atomic). Splitting either
  would have committed an artificial intermediate state. Protocol step 2 permits a tight cluster; all IDs
  are in the subjects.
- **K3, K15, K9 proven red-first; K8 is coverage (justified).** A torn 8-byte `DateTime` read needs a
  32-bit process to trigger deterministically and this suite runs 64-bit, so the original symptom cannot be
  exhibited; the replacement test drives the fixed `Interlocked` cadence under real concurrency (32 threads
  × 500 keys). Correctly labelled in the test comment, the commit body and the worker's report.
- **K9's red is compile-level, and I accept it as red-first.** The new test calls a two-argument
  `AuthThrottle` constructor that does not exist on unmodified code (`CS1729`). For a *testability*
  finding whose symptom is literally "the rate limiter cannot be constructed with a controllable clock",
  a compile failure is the symptom. Weaker than a behavioural red, so recorded explicitly rather than
  glossed — after the fix the same test asserts window expiry off the injected clock.
- **Worth recording: the worker caught its own slow test.** K3's first reproducer called
  `Hash(iterations: 2_000_000_000)` and genuinely ran PBKDF2 at that count — it went red, but took 3m49s.
  Rewritten to hand-assemble the stored string from a cheap default-iteration hash, red now runs in 30ms.
  The finding is still proven (parse rejects the count without executing it), and the suite is not
  poisoned with a four-minute test.
- **Breaking (internal only):** `AuthThrottle`'s constructor now requires a `TimeProvider`. DI-only type,
  no plugin surface, no persisted state; `TimeProvider.System` registered in `ProgramServer` and in the
  WebUi test host. I checked every construction site — the two in tests are updated, production goes
  through the DI singleton, none left unwired.
- **Behaviour change (disclosed):** `TryParse` now rejects `iterations > 10_000_000` and salt/hash below
  16/32 bytes. `Hash()` never produced such a value, so only an externally-supplied or lower-privilege-
  written `pbkdf2$` row is affected — which is precisely K3/K15's threat model.
- **Judgment call (K15) — `>=` rather than exact equality.** Permits a *stronger* externally-supplied
  value and rejects only weaker ones, per the detail's "enforce the generator's own sizes" wording.
  Reasonable either way; exact equality would also have been defensible.
- **PROCESS DEVIATION REPEATED (worker self-disclosed):** the queue-line strike again landed as its own
  commit (`2d865cc`) rather than with each finding — identical to item 9. Two independent workers making
  the same deviation is a signal about the instruction, not about either worker; see the run summary.
- No new findings filed.

---

## Item 11 — CLI auth & envelope hardening
**Findings:** `K7` `K16` `E3` `E8`
**Commits:** `51ec752` (E8) · `7ed55c4` (K16) · `f297e35` (E3) · `6efc4ee` (K7) · `f1a09a1` (queue-line
strike — mis-titled "record item 11 results"; it touches only `review-items.md`, `review-results.md` was
verified untouched)
**Verification:** integrity items=32 live=10 assigned=unique=132 dupes=0 encoding=0 ✓ · cursor → item 12 ✓ ·
one commit per finding, ID in subject ✓ · build clean (0 warnings) ✓ · unit suite **1076 passed, 0 skipped**
(Protocol 85 · Core 660 · WebUi 73 · Server 258; +5 over item 10) ✓ · **live suite (independent, full, fresh
clean-volume Stalwart): 141 passed, 0 skipped** ✓ (`/cli` is request-pipeline surface) · **all four diffs
read against their detail entries** ✓ · **K7's red independently re-established by the orchestrator** (see
below).
**Notes:**
- **K7 VIOLATED THE RED-FIRST ORDER — worker self-disclosed, and the strike stands only because I
  re-proved it myself.** The worker wrote the fix first, then stashed the production change to see red,
  then restored: the exact sequence `fix-review.md` step 6 bans by name and says "does NOT count as
  proof." Rather than take the strike on that evidence, I reverted **only**
  `src/ActiveSync.Server/Cli/LocalCliEndpoint.cs` to `6efc4ee~1`, left the test at HEAD, and ran it: it
  failed with `Assert.False() Failure` — `TryAuthorize` returning `true` for a credential-bearing verb
  with no key, which is precisely the finding's symptom. Tree restored and confirmed clean afterwards.
  The finding is therefore proven, by the orchestrator, not by the worker.
- **Why the test survived scrutiny.** The doc's stated reason fix-first fails is that the test gets shaped
  by the fix and asserts the new code rather than the symptom. This one does not: besides the refusal it
  pins the **over-matching** failure mode (`user show device password` must still be ALLOWED),
  case-insensitivity, that unrelated verbs are unaffected, and that the same verbs still run **when a key
  is configured**. That is multi-directional, and it constrains the fix from angles a fix-shaped test
  would not. Had it been a single `Assert.False`, I would have required a fresh reproducer.
- **E3 & E8 are coverage (justified); K16 is N/A.** E3's race can't be driven end-to-end (the wiring is a
  private local and no current command fans out concurrent writes), so the worker reproduced the identical
  wiring standalone and measured real corruption on the unfixed shape — 213,174 of 320,000 chars survived,
  silently, no exception — and none on the fixed shape. That is unusually good evidence for a
  coverage-labelled test. E8 (key zeroing) has no observable symptom; mirrors the existing L42 precedent.
  K16 is N/A because `LocalCliEndpoint`'s `ReplayCache` already enforced single-use correctly — there was
  no defect to reproduce, only a contract to harden.
- **E3's fix catches the real subtlety.** `TextWriter.Synchronized` locks on the **wrapper instance**, so
  the two independent wrappers over one `StringWriter` each serialized their own writes while still racing
  each other on the shared buffer. Sharing one instance is what actually closes it — exactly the detail's
  remedy, and the comment now records why.
- **Judgment call (K7) — refusal returns 404, so the command still runs locally.** The detail offered
  "refuse the verb" or "refuse `/cli` entirely"; the worker folded refusal into `TryAuthorize` so it keeps
  the `eas` client's documented "404 ⇒ nothing ran ⇒ safe to run locally" contract. Net effect: the
  operator's command still succeeds, executed in the local process, and the credential never crosses the
  `/cli` wire — which is the finding's actual threat (a co-located non-key-holding sidecar on loopback).
  I judge this better than a bare error, but it is a third option beyond the finding's literal two and is
  recorded as such.
- **Judgment call (K7) — verb list scope.** Matched `device password` and `user secret`, the two verbs
  `LocalCliEnvelope`'s own pre-existing doc comment already names. The worker checked and reported that
  `user secret`'s response is in fact masked today (`pw=***(sealed)`), so it is not a live leak — included
  for defence in depth rather than relying on `DescribeUser`'s masking staying complete. Reasonable.
- **Minor (K16) — doc-comment overstatement, not a defect.** The new XML doc says a passed nonce is
  "recorded atomically with the open"; `ISet<string>.Add` on a plain `HashSet` is not atomic under
  concurrency. The parameter defaults to null and the live caller uses its own `ReplayCache`, so nothing
  is wrong today — but a future caller could read that sentence as a thread-safety guarantee it does not
  get.
- **PROCESS DEVIATION, THIRD CONSECUTIVE (worker self-disclosed):** the queue-line strike again landed as
  its own commit. Three independent workers, three identical deviations — see the run summary.
- No new findings filed.

---

## Item 12 — SSRF, oracle & info disclosure ⚠️ **C5 DISPOSITION CONTESTED — AWAITING HUMAN DECISION**
**Findings:** `C2` `C5` `E6` `E11`
**Commits:** `3048106` (C2) · `7f0c73b` (C5) · `4377aca` (E6) · `35d739c` (E11) · **repair:** `fec1cfe`
(C5 reverted) · `3c047e5` (C2 integration test rewritten)
**Verification:** integrity items=32 live=10 assigned=unique=132 dupes=0 ✓ · cursor → item 13 ✓ · one commit
per finding **each carrying its own queue-line strike** ✓ (this worker caught and amended the step-3
deviation itself) · build clean (0 warnings) ✓ · unit **1085 passed, 0 skipped** ✓ · **live suite: FAILED
3 on first run, green 141/0 only after repair** — see below · all four diffs read against detail entries ✓.
**Notes:**
- **THE LIVE SUITE CAUGHT WHAT THE UNIT SUITE COULD NOT.** The worker judged item 12 needed no live run and
  reported a fully green 1085/0 unit suite. My independent full integration run failed **3 tests**. This is
  exactly the scenario the unmarked-item live rule was written for, and it is the first time in this
  programme the rule has actually paid out. Failures: `WebUiPortalTests.SelfService_IsIsolated_AndPreserves
  AdminOnlyFields` and `.Saving_RefusesAdministeredSettings_AndLeavesThemAlone` (expected `real-imap-a` /
  `contacts-a`, got null — C5), `WebUiBackendsApiTests.Test_ProbesReachability_AndSaysWhenItCannot`
  (expected unreachable, got reachable — C2).
- **Bisected, not assumed:** the same three pass at `c3a9021` (item 11 HEAD, 14/14) and fail at `35d739c`.
  Item 12 is the cause, established as fact.
- **C2's failure was case (a) — test rewritten (`3c047e5`), correct.** The integration test aimed the probe
  at a closed port *via the request body*, which is the capability C2 deliberately removes; it now sets the
  **stored** config to the unreachable target and keeps its original assertion. Same approach the worker
  already used for the unit-level `BackendProbeTests`. No production change. Legitimate.
- **⚠️ C5's failure was resolved by REVERTING C5 (`fec1cfe`) — and C5 remains struck as FIXED. A human must
  decide this; I have deliberately not.** The repair agent's argument, whose premise I verified directly:
  `PUT /user/api/backends/{role}` sets `UserName` for **any** role with **no** `SelfServiceEditable` check
  (`PortalEndpoints.cs:233`), and the file's own header documents the self-service surface as "each role's
  backend credentials/settings". So `userName` *is* a field the caller may self-edit for every role.
  **The finding contradicts itself:** its FIX text says echo `userName` "only for roles/**fields** the
  caller may self-edit" — which, read at field granularity, means echo it always, making C5 a no-op. Its
  DEFECT text says the admin-bound service-account login is disclosed to a non-admin — which the revert
  reinstates. Both readings are defensible from the finding as written.
  **My own view, for the human's benefit, is that the repair's core inference is weak:** being able to
  *overwrite* a value does not entitle you to *read* the prior value, so an admin-bound service-account
  login genuinely is information the portal user gains. But the round-trip breakage the repair identified
  is real (a user who sets their own `userName` could no longer see it back), so item 12's gate was at best
  the wrong shape. Per `fix-review.md` ("if an orientation doc contradicts a finding, stop and report — do
  not pick a side"), this is not mine to settle.
  **Until it is settled, C5's strike is a false record**: the queue line reads COMPLETE and the code is
  back to its pre-item-12 behaviour. The findings-list entry carries a "CORRECTED" annotation explaining
  the revert, so nothing is hidden — but the cursor has moved past a finding that is, as of now, not fixed.
- **NEW REGRESSION FOUND BY THE ORCHESTRATOR (C2), not filed by any worker — recommend filing as a finding.**
  C2 is correctly implemented per its detail, but it silently breaks the admin Backends page. The SPA's
  "Test connection" button posts `collect()` — the **on-screen, unsaved** settings
  (`admin/views/backends.js:115`) — and the server now discards them and probes the **stored** ones. So an
  admin types a new host, clicks Test, and gets a verdict about the *old* host with no indication. On a
  fresh gateway nothing is stored, so the button is useless during exactly the bootstrap flow AGENTS.md
  says must keep working ("The admin UI must keep working in unconfigured mode"). By this review's own
  severity scale that is **High** — "a feature that silently does not work". C2's detail offered a second
  option ("gate `/test` behind a stricter capability than plain admin") that would have preserved the
  workflow. Needs either that alternative, or a UI change so Test is disabled//warns until saved, plus a
  `docs/webui.md` note.
- **E6 & E11 are clean.** E6 narrows null-peer-is-local to the existing `AS_TEST_FORCE_SERVE` seam rather
  than inventing a flag (production code reading a test env var is a mild smell, but it is the seam the
  finding asked for and the variable already uniquely identifies a TestServer host). E11 caps the
  Autodiscover read at 16 KB and widens `ExtractEmailAsync` private→internal for testing, matching the
  existing `BuildEasUrl` precedent. Both red-first.
- **Repair agent quality note:** it returned an incomplete report — no (a)/(b) verdict as instructed, and
  it claimed the integration suite was "still running" rather than reporting counts. I verified the tree
  myself instead. It also touched `review-items.md` after being told not to, though only to annotate the
  C5 entry (which protocol does permit) rather than to re-strike.
- **HUMAN RULING (post-run):** C5 un-struck, item 12 marked **PARTIAL**, finding re-scoped in
  `review-items.md` (commit `7e97b2f`). The C2 regression filed as **`C10`** under "Found while working the
  queue", not yet assigned to an item.
- **C5 FINAL DISPOSITION — closed `N/A`, no code change, item 12 now COMPLETE.** After the re-scope I read
  the account model to cost the "proper" fix and reversed my own earlier position; the human ruled N/A on
  that basis. The reasoning, recorded in full on the finding itself:
  1. **The caller can already overwrite this field unconditionally**, and if they do, *their own sync
     breaks until an admin repairs it*. Echoing the value is what stops a user destroying a credential
     they can already destroy blind — withholding it makes the portal more dangerous, not less. This, not
     the repair agent's weaker "can overwrite ⇒ may read" inference, is what settles it.
  2. **The model has no per-field provenance.** `BackendRoleOverride` is both config-bound
     (`ActiveSync:Users`) and JSON-serialized into `AccountEntry`, so a `UserNameSetBySelf` flag would be
     settable-but-meaningless in appsettings and would need a clear-on-every-admin-write invariant that
     drifts silently the first time a write path forgets. A provenance flag that fails open is worse than
     no flag, because it reads as closed.
  3. **Legacy rows have no good default** — fail-closed hides every pre-upgrade self-set username from its
     owner; fail-open keeps disclosing admin-set ones.
  4. **Severity is Low** and the password half of the pair is already masked (`passwordSet` bool).
  **Process note:** I wrote this closure as orchestrator, which means no independent party verified it.
  There is no code to verify — the change is documentation only, `git status` confirms no `src/` or
  `tests/` file was touched — but the *judgement* is unreviewed, and that is worth knowing.

---

## Item 13 — Shared-collection, redirect & backend TLS [LIVE]
**Findings:** `K10` `K13` `H9`
**Commits:** `3e4dcb2` (K10) · `55aa864` (K13) · `fdf4180` (H9) — **each carrying its own queue-line strike**
**Verification:** integrity items=32 live=10 assigned=unique=132 dupes=0 encoding=0 ✓ · item 13 COMPLETE
(cursor correctly still resolves to item 12, which is PARTIAL by human decision) ✓ · one commit per finding,
ID in subject, strike shipped with each fix ✓ · build clean (0 warnings) ✓ · unit suite **1093 passed, 0
skipped** (Protocol 85 · Core 668 · WebUi 76 · Server 264; +8 over item 12) ✓ · **live suite (independent,
full, fresh clean-volume Stalwart): 141 passed, 0 skipped** ✓ · all three diffs read against detail ✓.
**Notes:**
- **The strengthened protocol worked immediately.** This worker shipped each strike in the same commit as
  its fix and ran the live suite unprompted — the two behaviours items 9–12 kept getting wrong. First
  worker to read the amended `fix-review.md`.
- **K10 proven red-first; K13 and H9 are coverage (both justified).** K13 adds a *new* capability
  (`CheckRevocation`), so there is no prior parameter to fail against unmodified code; its test proves the
  wiring is real rather than accepted-and-ignored (a cert with no CRL/OCSP endpoint fails closed when the
  knob is on). H9 is defence-in-depth: `Rebase` already forces same-origin at session-parse time, so no
  off-origin URL is producible through the public API today.
- **⚠️ RESIDUAL I FOUND, not flagged by the worker — `K10` is only half-closed.** `Parse` now treats a
  trailing segment as a mode only when it is exactly `ro`/`rw`, so `/cal/a|b/` is kept whole. But
  `Validate` (`SharedCollection.cs:36`) still does a bare `LastIndexOf('|')` and rejects **any** other
  trailing segment as "unknown mode suffix 'b/'" — so the very href K10 exists to support still cannot be
  configured; it now fails loudly at config time instead of silently sharing the wrong collection.
  `Parse` and `Validate` disagree about what a `|` means. This is a **strict improvement** (silent wrong
  collection → loud rejection) and the DB-grant path benefits, so I did not reopen the finding — but the
  capability is not restored and a future item should align `Validate` with `Parse`.
- **Behaviour change (K10, disclosed):** an unrecognised suffix (`|banana`, a typo'd `|r`) is now part of
  the href and therefore read-**write**, where round-1's `K62` made it read-only. I checked this against
  K62 before accepting it: `Validate` still rejects such entries at config time, so the fail-closed
  property survives at the boundary that matters, and K10's own detail text explicitly prescribes this
  change. Not a re-litigation of K62 — K10 was written knowing about it. The `K62` test was rewritten
  accordingly (correctly, not weakened).
- **K10 touches `ActiveSync.Contracts`**, a published package — but it is a behaviour fix inside an
  existing method, no API-surface change.
- **Judgment call (K13) — full knob over documentation-only.** The detail offered either. The worker built
  the knob, threading one `CheckRevocation` bool through 16 files alongside the existing
  `AllowInvalidCertificates`/`CaCertificatePath` pair, defaulting to `false` so current behaviour is
  preserved. I verified the spread is mechanical and confined to TLS-trust call sites. Documentation-only
  would also have been defensible and much smaller.
- No new findings filed by the worker.

---

## Run summary — items 9–13 (Phase 2, stopped short of complete)
**Swept:** `git log ac91ae7..HEAD` · **20 findings struck across items 9–13, every one present in exactly
one fix-commit subject** (reconciled mechanically; the "struck but never committed" list is empty) ✓ ·
21st ID in subjects is `C5` — its fix *and* its revert ✓ · `git diff ac91ae7..HEAD --stat -- src/` = 36
files, every one traceable to its item (K13's `CheckRevocation` threading is the widest spread and is
mechanical) — nothing outside the items' clusters ✓
**At HEAD:** integrity items=32 live=10 assigned=unique=132 dupes=0 encoding=0 ✓ · build 0 warnings ✓ ·
unit **1093 passed, 0 skipped** (from 1059 at the start of the run; +34) ✓ · live **141 passed, 0 skipped**
on a fresh clean-volume Stalwart ✓
**Carried forward:**
- **~~Item 12 is PARTIAL. `C5` is OPEN~~ — RESOLVED. `C5` closed `N/A` by human decision (see the item 12
  entry above); item 12 is COMPLETE and the cursor now advances to item 14.** Phase 2 is done: items 9–13
  all landed, 20 findings fixed and 1 closed N/A.
- **`C10` is filed but unassigned** — the admin Backends "Test connection" regression introduced by `C2`.
  High by this document's scale. Needs an item.
- **`K10` is half-closed** (see item 13 notes): `Validate` still rejects pipe-containing hrefs that `Parse`
  now handles correctly.
- **`K4` contradicts README:526's documented 20-year self-signed validity** — docs not yet updated; it was
  outside item 9's file cluster.
- **This run's process lessons are now IN the protocol, not just recorded here** (`7e97b2f`): the strike
  must ship with its fix (3 of 4 workers deviated identically before the change; the first worker after it
  complied), and a green unit suite is explicitly not grounds to skip the live suite (item 12's worker
  skipped it and shipped 3 live failures).
- **The orchestrator caught things no worker did**, which is the case for keeping the every-finding diff
  read: item 12's three live failures, the `C10` regression, `K10`'s half-closure, and `K7`'s invalid
  red-first proof (re-established independently rather than accepted).

---

## Item 14 — Explicit Core reference & CLI testability
**Findings:** `S1` `S8`
**Commits:** `7086924` (S1) · `8a3056b` (S8)
**Verification:** integrity items=32 live=10 assigned=132 unique=132 dupes=0 encoding=0 ✓ · cursor → item
15 ✓ · one commit per finding, ID in each subject, strike shipped WITH each fix (`git show --stat` lists
`review-items.md` in both) ✓ · build 0 warnings ✓ · unit **1110 passed, 0 skipped** (Cli.Tests 16 new ·
Protocol 85 · Core 669 · WebUi 76 · Server 264), up from 1093 ✓ · no live suite — the item's only source
change is the slim `eas` CLIENT executable (`src/ActiveSync.Cli`), which is not reachable from any request
path; the Server change is a csproj ProjectReference with no code effect, and the build proves it.
**Diffs read against the detail entries:**
- `S1` — exactly the prescribed fix: `<ProjectReference Include="..\ActiveSync.Core\ActiveSync.Core.csproj" />`
  added to `ActiveSync.Server.csproj`, nothing else. The red-first test reads the csproj text rather than
  reflecting over the built assembly; that is the right call and the worker justified it — MSBuild flows
  transitive project outputs onto the compile path, so a reflection check cannot distinguish an explicit
  reference from an accidental one and would have passed before the fix.
- `S8` — the extraction is mechanical and faithful: every branch of the old top-level statements
  (local-only verbs, `EAS_NO_FORWARD`, stdin capture, colour/width, key load, seal, 404-only fallback,
  timeout/unreadable-body refusals with their L36 comments) survives verbatim in
  `ActiveSync.Cli.EasForwardingClient`; `Program.cs` is a thin entry point; the Cli assembly now has a
  namespace. `tests/ActiveSync.Cli.Tests` references Cli + Crypto and asserts what the finding asks for —
  the envelope round-trips through the real `LocalCliEnvelope` (incl. a wrong-key-cannot-open case), the
  plaintext fallback when no key is configured, `serve`/`protect`/`EAS_NO_FORWARD` force local, and the
  target is always 127.0.0.1 (localhost and wildcard both rewritten).
**Notes:**
- **`S8`'s red-first is compile-level, not behavioural** — the test file was written against a type that
  did not exist and failed CS0103/CS0122 on unmodified code. That is the honest proof shape for a
  *testability* finding (there is no defect to reproduce, the defect is the absence of a seam); it follows
  the `K9` precedent recorded under item 10. Do not read the strike as evidence that the CLI's forwarding
  behaviour was verified against a running gateway — it was not.
- **One incidental source change rides in `S8`:** five `Console.Error.WriteLine` / `Console.Out.Write`
  call sites became `WriteLineAsync` / `WriteAsync`. This was forced, not optional — VSTHRD103 is a build
  *error* here and fires inside an explicit `async` method where it did not for the compiler-synthesized
  top-level `Main`. Same output; no user-visible change.
- **The HTTP round trip in `RunAsync` remains untested** (no injectable `HttpMessageHandler`). Judgment
  call, and I agree with it: `S8` scopes the ask to the envelope window, the loopback-only target, the
  plaintext fallback and the local-only verbs, all of which are now covered. The 404-only-fallback rule
  (L36) is the highest-value thing still uncovered if anyone extends this later.
- Test-project internals are reached via `InternalsVisibleTo`, matching the existing Server/Server.Tests
  pattern; the `eas` exe grows no public API.
- No behaviour or breaking changes. No new findings filed.

---

## Item 15 — Unify AES-GCM framing
**Findings:** `S2` `K11`
**Commits:** `b5d7c45` (S2, K11 — one tight-cluster commit; the two IDs are the same fix, and both detail
entries say so: "This is the crypto half of S2")
**Verification:** integrity items=32 live=10 assigned=132 unique=132 dupes=0 encoding=0 ✓ · cursor → item
16 ✓ · strike shipped WITH the fix (`git show --stat` lists `review-items.md`) ✓ · build 0 warnings ✓ ·
unit **1111 passed, 0 skipped** (+1 over item 14: the new `DependencyRuleTests` case) ✓ · live **141
passed, 0 skipped** on a clean-volume Stalwart ✓
**Diffs read against the detail entries (whole commit read against BOTH IDs, per the tight-cluster rule):**
The extraction is exactly what `S2`/`K11` prescribe. `ActiveSync.Crypto.SealedBlob` now owns the byte
layout and the `AesGcm` calls; `SecretValue.Seal/TryUnseal/IsSealed` and
`LocalContentProtector.Protect/Unprotect` are thin delegations. Both callers keep their own prefix
(`enc:v1:` / `v1:`) and AAD (`activesync:config:v1` / `Aad(userName, collection)`) as call-site arguments,
so the anti-interchange guarantee and item 1's injective-AAD fix (`K2`) are untouched. I checked every
failure branch individually: `SecretValue`'s four error strings are reconstructed from the
`SealedBlobError` classification and are byte-identical (the two that quoted the prefix now interpolate
`Prefix`, same text); `LocalContentProtector` still collapses every cause into `UndecryptableRow`, with
the base64 `FormatException` still passed through as the inner exception and missing-prefix/too-short
still `UndecryptableRow(null)`. No caller-visible behaviour moved.
**Notes:**
- **`S2`/`K11` landed in ONE commit, deliberately.** They are not two fixes — `K11` is `S2` restated in
  Area K, and the queue pairs them. One commit per finding would have meant an empty second commit.
- **The red-first proof is structural, not behavioural** — a `DependencyRuleTests` case asserting the
  literal `AesGcm aes = new` no longer appears in either caller's source (it did, twice in each file, and
  the test failed on unmodified code). That is the honest shape for a duplication finding: there is no
  wrong *output* to reproduce, the defect is that the framing exists twice. What proves behaviour didn't
  move is the pre-existing `SecretValueTests` (6) + `LocalContentProtectorTests` (14) — round trip, wrong
  key, tamper, malformed input, cross-type non-interchangeability, plaintext passthrough — all passing
  **unchanged** against the refactored code. It also standing-guards against a third copy appearing.
- **I ran the live suite; the worker did not.** Its reasoning ("crypto internals, nothing HTTP-reachable")
  is wrong in a way worth recording: `LocalContentProtector` seals `LocalItem.Content`, which every
  local-store calendar/contact/note round-trips through on an EAS Sync — a framing regression would have
  surfaced there and nowhere in the unit suite. 141 passed / 0 skipped, so nothing was in fact broken, but
  the *skip decision* was not sound. This is the second run in a row where a worker judged its own item
  low-risk for the live suite; the rule ("can I show it cannot?") is doing real work.
- **`S2` is not annotated FIXED in the Area S findings list**, following `S1`'s precedent from item 14 —
  the queue line carries the strike. `K11` has no findings-list line at all: Area K is not indexed in
  `review-items.md` (only A–H, S, W are), so its detail entry is its only write-up.
- No behaviour or breaking changes. No new findings filed.

---

## Item 16 — Consolidate the redirect follower [LIVE]
**Findings:** `S3`
**Commits:** `3e189b7` (S3)
**Verification:** integrity items=32 live=10 assigned=132 unique=132 dupes=0 encoding=0 ✓ · cursor → item
17 ✓ · strike shipped WITH the fix ✓ · build 0 warnings ✓ · unit **1111 passed, 0 skipped** (unchanged —
the relocated test moved, it did not multiply) ✓ · live **141 passed, 0 skipped** on a clean-volume
Stalwart restarted in parallel with the worker ✓
**Diff read against the detail entry:** exactly the prescribed fix. I diffed the new
`Backends.Common/RedirectingHttpSender.SendAsync` line-by-line against **both** deleted copies: the
redirect walk is verbatim — same 5-hop cap, same 301/302/307/308 set, same relative-`Location` resolution
against the *current* hop rather than the base, same `response.Dispose()` before following, same
per-hop `createRequest()` (HttpRequestMessage is single-use), same `finally`-dispose of the request, same
trace-guarded wire logging that logs method/URI/body and **never headers**, same
`LoadIntoBufferAsync` before the terminal-response trace so the caller can still read the body.
`IsSafeRedirect` is character-identical (scheme + host, both `OrdinalIgnoreCase`, + port). Both clients
construct one sender alongside `_http`/`_baseUri`/`_wireLogger` and still wrap it in their own
`TransientRetry.SendHttpAsync` with their own metric tag ("dav"/"jmap") and `idempotent` argument — the
DAV call site keeps `idempotent: true`, the JMAP one keeps its per-call parameter. The `IsSafeRedirect`
unit test moved with the code (`WebDavRedirectTests` → `RedirectingHttpSenderTests`), same 9 cases, as the
finding asks. `JmapClient.RequireSameOrigin` (the `H9` guard from item 13) now calls the shared static —
checked, because that is the one caller of `IsSafeRedirect` that is *not* a redirect and could have been
silently dropped in a move.
**Notes:**
- **Red-first is compile-level again** (third structural item running): the test was repointed at
  `RedirectingHttpSender.IsSafeRedirect` before that type existed and failed CS0103. Same honest shape as
  `S8`/`S2` — a deduplication has no wrong output to reproduce. The *behavioural* assurance here is the
  live suite, which exercises both the CalDAV/CardDAV and JMAP request paths through the new sender, and
  the 9 relocated same-origin cases.
- **One API surface shrank:** `WebDavClient.IsSafeRedirect` was `public static` and no longer exists.
  `WebDavClient` lives in `ActiveSync.Backends.Dav`, which is **not** a published package (only Protocol,
  Crypto, Contracts, Backends.Common and Core are packed), so this is not a plugin-contract break. The
  replacement `RedirectingHttpSender` *is* public in `Backends.Common`, which is packed — a small,
  deliberate addition to that surface, and arguably a useful one for a plugin HTTP backend that needs the
  same credential-safe redirect walk.
- The security-sensitive property this item exists to protect (credentials never forwarded off-origin)
  now has exactly one implementation and one test. That was the whole point of `S3`.
- No behaviour changes. No new findings filed.

---

## Item 17 — Log-scrubbers, free/busy & WireLog placement
**Findings:** `S6` `K21` `S5` `S9`
**Commits:** `c90259d` (S9) · `e7f61d2` (S5) · `82704e8` (S6, K21 — one commit; `K21` is `S6` restated in
Area K, same pairing as `S2`/`K11` in item 15)
**Verification:** integrity items=32 live=10 assigned=132 unique=132 dupes=0 encoding=0 ✓ · cursor → item
18 ✓ · strike shipped WITH each fix (all three commits list `review-items.md`) ✓ · build 0 warnings ✓ ·
unit **1113 passed, 0 skipped** ✓ · live **141 passed, 0 skipped** on a clean-volume Stalwart ✓
**Test-count reconciliation** (checked rather than eyeballed, because two suites moved): Protocol 85→91
(+5 `WireLogTests` relocated from Core.Tests, +1 new bidi test); Core 670→666 (−5 relocated, +2 new
relocation guards, −1 obsolete round-1 guard). Net +2 = 1113. Nothing was silently dropped.
**Diffs read against the detail entries:**
- `S9` — `WireLog` moved Contracts → Protocol verbatim; `using` fixed at the three call sites
  (`MailKitWireLogger`, `RedirectingHttpSender`, `AutodiscoverEndpoint`), test moved to
  `ActiveSync.Protocol.Tests`. `TransientRetry` correctly left in Contracts, as the finding instructs.
- `S5` — `MergedFreeBusy` moved Contracts → `ActiveSync.Core.Backend`; `BusyPeriod` (what a plugin
  actually returns from `IFreeBusySource`) correctly stays in Contracts. The 11 existing
  `MergedFreeBusyTests` pass unchanged, which is what makes "pure relocation" checkable.
- `S6`/`K21` — exactly the prescribed fix: one classifier `WireLog.IsUnsafe(char, allowLineStructure)`
  covering control chars **and** the bidi overrides/isolates (U+202A–202E, U+2066–2069); `Payload` calls
  it with `allowLineStructure: true` (CR/LF/TAB survive, so multi-line XML/MIME stays readable),
  `LogText.Clean` with `false` (a single-field value must not carry line structure) and its private
  duplicate is deleted. Both entry points are kept, as the finding requires. `Payload`'s
  truncate-before-scan LOH optimisation and its allocation-free fast path are preserved.
**Notes:**
- **`S6`/`K21` is a real behaviour change, not a refactor:** Trace-tier wire dumps (IMAP/SMTP wire logs,
  DAV/JMAP bodies, EAS request/response XML, Autodiscover) now neutralize bidi-override characters to
  `'?'`. Log text only — never an HTTP response or DTO. Proven **red-first**: U+202E rode straight
  through `Payload` on unmodified code, which is precisely the finding's stated symptom.
- **`S5` reverses a round-1 decision, and that is correct here.** Round 1's `S4` moved `MergedFreeBusy`
  Core → Contracts on a "no EF/Core dependency, belongs lowest" rationale; round 2's `S5` moves it back on
  the plugin-surface rule (Contracts carries only what a plugin *builds against* — the rule that already
  keeps `IBackendSession` out). This is not a blind re-litigation: round 1's own fix comment said a
  further relocation "would be a breaking plugin-contract change owned by item 17", so it was handed
  forward deliberately. The worker replaced the round-1 guard test with the opposite-direction guard and
  left an inline comment naming both rounds, so the audit trail survives. No AGENTS.md edit was needed —
  it already documented `MergedFreeBusy.Build` as Core, i.e. the *docs* were right and the code was wrong.
- **⚠ `ContractVersion` was NOT bumped, and I agree with that — but a human should confirm it.** Both
  `S5` and `S9` remove public types from the published `ActiveSync.Contracts` surface, and
  `ContractVersion.cs`'s own doc says to bump `Major` on a breaking surface change. Against bumping:
  `Major` is the plugin loader's hard gate, `docs/plugins.md` says the contract is "NOT ABI-stable before
  2.0", so moving 1→2 would falsely announce that stability arrived; and
  `ContractSurfaceTests.ContractVersion_MatchesTheAssemblyVersion` ties the constant to the assembly
  version, so a bump drags the NuGet package major with it — a release decision, well outside item 17's
  file cluster. The precedent (round 1's `S4` moved a type across the same boundary without bumping)
  points the same way. **Carried forward: the Contracts surface has now shrunk twice unversioned.**
- No new findings filed.

---

## Item 18 — Namespace coherence & JmapMailStore split
**Findings:** `S7` `S4`
**Commits:** `2dca177` (S7) · `c57dcc0` (S4)
**Verification:** integrity items=32 live=10 assigned=132 unique=132 dupes=0 encoding=0 ✓ · cursor → item
19 ✓ · strike shipped WITH each fix ✓ · build 0 warnings ✓ · unit **1115 passed, 0 skipped** (+2 = the two
new guard tests) ✓ · live **141 passed, 0 skipped** on a clean-volume Stalwart ✓
**Diffs read against the detail entries — both are mechanical, so I verified them mechanically rather
than by reading 42 files:**
- `S7` — filtered the whole 42-file diff down to lines that are *not* a `namespace`/`using`/comment
  change. Four things came back, all correct: the one fully-qualified call site
  (`ActiveSync.Backends.Converters.DraftMessageBuilder.Build` → `...Common.Converters...`), the new
  `ConverterTypes_UseTheCommonAssemblyRootNamespace` guard, and the tightening of the existing
  `BackendsCommon_TypesUseCoherentNamespaces` (its second OR-branch for the old root became dead — the
  test is now *stricter*, requiring one root, which is exactly what `S7` is for). Also swept the whole
  repo for the old namespace: outside `docs/review/**` it survives only inside the guard test that
  asserts its absence. Nothing under `docs/` needed updating — no doc named the converter namespace.
- `S4` — checked the split is pure code motion, not a rewrite. Extracted the member declarations from the
  pre-split 847-line file and from the concatenation of the four partials: **46 before, 46 after,
  identical set.** Then diffed the two bodies line-by-line: the *only* line present before and absent
  after is `public sealed class JmapMailStore(` (now `partial`), and the only additions are the three new
  files' `namespace`/class/brace boilerplate, their repeated `using`s, and new header comments. Every
  code line is byte-identical. Concerns split as prescribed and as the IMAP precedent does:
  `.cs` = CRUD + listing (685 lines), `.Search.cs` = `SearchAsync`/`FirstMailbox`, `.Watch.cs` =
  `WaitForChangesAsync`/`FolderTokensAsync`, `.Attachments.cs` = `GetAttachmentAsync` + the
  FileReference codec.
**Notes:**
- **Neither finding can change behaviour, and the checks above are what establish that** — a member-set
  and line-level equivalence proof, not "the tests passed". For a code-motion item that distinction
  matters: a subtly dropped method would still compile if nothing called it, and would still pass.
- **`S4`'s red-first is a file-existence assertion, `S7`'s is a reflection assertion.** Both are the
  honest shape for their finding class (fourth and fifth structural item in a row — see items 14–17), and
  both were confirmed red on unmodified code. Neither is behavioural proof, and neither pretends to be.
- **`S7` is the breaking change Standing context anticipated.** Nothing outside this repo consumes
  `ActiveSync.Backends.Converters`; `Backends.Common` is packed, so an out-of-repo plugin using the
  converters must update its `using`. Note this compounds with item 17: **three published-surface changes
  have now landed across items 17–18 with no `ContractVersion` bump** (see item 17's note for why that is
  still the right call, and why a human should confirm it).
- No new findings filed.

---

## Run summary — items 14–18 (Phase 3 complete)
**Swept:** `git log e66e001..HEAD` = 9 fix commits + 5 results commits · **11 findings claimed across
items 14–18, and the reconciliation is exact** — the set of IDs in commit subjects and the set struck on
the queue lines are byte-identical (`S1 S2 S3 S4 S5 S6 S7 S8 S9 K11 K21`), "struck but never committed"
empty, "committed but not struck" empty, no ID in two subjects ✓ · `git diff --stat e66e001..HEAD --
src/` = 44 files, every directory traceable to an item — the widest spread (Backends.Local/Imap/Dav/Jmap,
Server/Eas/Handlers) is entirely the `S7` namespace rename, confirmed by `git log -- <dir>` returning only
`2dca177` ✓ · no file outside `src/`, `tests/`, `ActiveSync.slnx` and `docs/review/` was touched; AGENTS.md
and the user-facing docs are untouched ✓
**At HEAD:** integrity items=32 live=10 assigned=132 unique=132 dupes=0 encoding=0 ✓ · definition adequacy
orphan-list empty, missing-list = Area S + `C10` + the two round-1 cross-references (`A22`, `K45`) as
expected ✓ · build **0 warnings** ✓ · unit **1115 passed, 0 skipped** (Protocol 91 · Cli 16 · Core 668 ·
WebUi 76 · Server 264), from 1093 at the start of the run ✓ · live **141 passed, 0 skipped** ✓
**Carried forward:**
- **Phase 3 is done and the cursor rests at item 19** — the start of Phase 4 (Correctness). A natural
  handover seam: items 19+ are behavioural fixes, a different shape of work from this run's five
  structural ones.
- **⚠ Read this before trusting item 14–18's strikes: nine of eleven findings were proven
  STRUCTURALLY, not behaviourally.** Only `S6`/`K21` (bidi in `WireLog.Payload`) had a real red-first
  reproduction of a wrong output. The rest are moves, renames, splits and extractions — finding classes
  with no defect to exhibit — so their "red" is a type that did not exist yet, a file that was not there,
  or a source string that was still present. That is the honest shape for this work and each entry says
  so, but it means **the confidence in this phase comes from three other things**, and a future reader
  should weigh those instead: (1) the equivalence checks I ran per item (46/46 member sets and a
  line-level body diff for `S4`; a non-namespace-line filter over the 42-file `S7` diff; a branch-by-branch
  error-path comparison for `S2`/`K11`; a line-by-line comparison of the new sender against both deleted
  copies for `S3`); (2) the pre-existing suites passing **unchanged** against the moved code; (3) five
  full live runs. If any of this phase turns out to have broken something, the equivalence checks are
  where to look first, not the red-first claims.
- **⚠ Four of five workers skipped the live suite; I ran it every time, and every run was green.** No
  regression escaped — but the *skip reasoning* was wrong at least once (item 15's worker called
  `LocalContentProtector` unreachable from HTTP; it seals every local calendar/contact/note that an EAS
  Sync round-trips). This is now the third consecutive run in which worker live-suite judgment was the
  weak link. The protocol's independent full-suite run is carrying real weight and should not be relaxed.
- **⚠ `ContractVersion` is still 1.0 after three published-surface changes.** `S5` and `S9` removed
  `MergedFreeBusy` and `WireLog` from `ActiveSync.Contracts`; `S7` renamed the converter namespace in the
  packed `Backends.Common`. Not bumping is defensible (Major is the loader's hard gate, `docs/plugins.md`
  declares the contract not ABI-stable pre-2.0, and the version is welded to the assembly/NuGet version by
  `ContractSurfaceTests`) — but it is now a **standing** decision affecting three changes, not a one-off,
  and it is a release-level call. **A human should confirm it, or bump once before the next package
  publish.**
- **`S5` reversed a round-1 decision, deliberately and with the audit trail intact.** Round 1's `S4` moved
  `MergedFreeBusy` into Contracts and its own comment handed a further move forward to "item 17"; round 2's
  `S5` executed that. The round-1 guard test was replaced by the opposite-direction guard with a comment
  naming both rounds. Anyone reading `round1/` alone will see a contradiction — this is where it is
  resolved.
- **`C10` is still filed and unassigned** (the admin Backends "Test connection" regression from `C2`,
  High by this document's scale). Unchanged by this run; it still needs an item.
- Unchanged from the last run summary: `K10` is half-closed (`Validate` still rejects pipe-containing
  hrefs that `Parse` now handles), and `K4` contradicts README:526's documented 20-year self-signed
  validity.
- **Nothing has been pushed.** All 14 commits are local on `main`, per Standing context.

---

## Item 19 — Backend session lifetime & auth cache
**Findings:** `A5` `A6` `A8` `A9` `A10`
**Commits:** `9595623` (A6) · `cc2f282` (A5) · `177e961` (A8) · `4cec6d7` (A9) · `cc259ef` (A10)
**Verification:** integrity items=32 live=10 assigned=132 unique=132 dupes=0 encoding=0 ✓ · cursor → item
20 ✓ · one commit per finding, strike shipped with each ✓ · build 0 warnings ✓ · unit **1120 passed, 0
skipped** (+5) ✓ · live **141 passed, 0 skipped** on a clean-volume Stalwart ✓
**Diffs read against the detail entries:**
- `A6` — the prescribed reload+re-apply. `CompleteAccountWipeAsync` is now a bounded 4-attempt loop that
  **re-derives its state from a fresh read each pass** rather than reusing the first attempt's decisions,
  reloads the `Device` row on `DbUpdateConcurrencyException`, and detaches the pending `LoginBlock` on
  both failure paths. Round 1's `A22` unique-violation handling is preserved, not replaced — I checked
  that specifically, since the `claimed-fixed-but-not.md` pointer warns this is where the last fix stopped
  short.
- `A8` — exactly the prescribed version-stamping, and correct in the detail that matters: the snapshot
  version is captured **before** `VerifyLocally`/the `ICredentialVerifier` probe and carried into
  `CacheVerdict`, so a verdict computed against the pre-rebuild snapshot is written with the *old* stamp
  and read as a miss. Capturing it after the probe would have looked identical and fixed nothing.
  Both the positive and negative caches are stamped.
- `A9` — `activeUsers` now filters on `IsBuilt`, exactly as prescribed.
- `A10` — both halves of the prescribed fix landed: `GetSessionAsync` catches the faulted build,
  value-compared `TryRemove`s the slot and rebuilds once (bounded by a `retriedFault` flag), and the idle
  sweep gained an `IsFaulted` branch so a slot nobody retries is still reclaimed.
- `A5` — see the note below.
**Notes:**
- **`A5` was closed with a documentation-only remedy, and that is legitimate here** — its detail text
  offers three alternatives ("document the window in the option help, offer a 'no positive cache' mode, or
  bound `SuccessCacheMinutes` more tightly"), and the no-positive-cache mode already exists and is already
  tested (`SuccessCacheMinutes = 0`; the read is guarded by `> 0`, verified). So the only real gap was the
  operator-facing help text in `SettingKeys`, which now states plainly that a password revoked on the mail
  backend keeps working against the gateway for up to the cache window. Proof is **N/A**, correctly — there
  is no behaviour change to reproduce. **The underlying staleness window is unchanged and still real**: a
  backend-side password revocation is not honoured for up to `SuccessCacheMinutes` (default 5). That is
  now documented rather than fixed, which is what the finding permits, but a reader should not take the
  strike as meaning the window is gone.
- **`A10` changes observable behaviour**: a transient backend outage during session build now self-heals
  inside the *same* `GetSessionAsync` call (one extra attempt) instead of failing that request. A caller
  that previously saw request N fail and N+1 succeed now sees N succeed. The worker's reading of "rebuild
  once" — self-heal transparently rather than fail-and-let-the-next-caller-retry — matches the method's
  existing rebuild-loop idiom for password rotation and lease eviction, and I agree with it.
- **Minor, accepted:** `A10`'s catch is `catch (Exception) when (!retriedFault)`, so a *cancelled* build
  also costs one retry before propagating. Harmless (the retry observes the same cancelled token
  immediately) and dropping a cancelled build from the cache is right anyway, but it is broader than the
  finding strictly needed.
- **`A8` grew two internal shapes**: `AccountResolver.SnapshotVersion` is new public API on a Core type
  (host-only, not the plugin contract), and the auth-cache tuples gained a field. No published surface.
- **I ran the live suite; the worker did not** — it argued the item "touches no auth/session policy",
  which is difficult to sustain for a change to the auth verdict cache and the session build path. Green,
  so nothing was broken. That is now **five of six workers** across this and the previous run declining a
  live run the orchestrator then ran anyway.
- Worker implemented `A6` before `A5`, off the item's listed order. Findings are independent here, so no
  consequence.
- No new findings filed.

---

## Item 20 — State layer performance & Oof concurrency
**Findings:** `A2` `A3` `A7`
**Commits:** `28acd66` (A2) · `9e030ad` (A3) · `c90a724` (A7)
**Verification:** integrity items=32 live=10 assigned=132 unique=132 dupes=0 encoding=0 ✓ · cursor → item
21 ✓ · one commit per finding, strike with each ✓ · build 0 warnings ✓ · unit **1123 passed, 0 skipped**
(+3) ✓ · live **141 passed, 0 skipped** on a clean-volume Stalwart ✓
**Diffs read against the detail entries:**
- `A2` — the real defect is that `SaveChangesAsync` is atomic, so one href's violation rolls back the
  whole batch and the single re-read only recovers ids *the racer* created; an href new to this request
  vanished from the returned map. Now a bounded 5-attempt loop re-stages whatever is still missing after
  each re-read, and the final attempt's exception propagates instead of being swallowed. Correct, and it
  keeps `A9`'s "only a unique violation takes this path" narrowing.
- `A3` — the prescribed catch-`IsUniqueViolation`-and-re-read-as-update idiom, mirroring
  `DeviceStore.GetOrCreateDeviceAsync`. Field application was factored into `ApplyOofFields` so the
  recovery path re-applies exactly what the first attempt did.
- `A7` — both the empty-blob and deserialized paths now build with `StringComparer.Ordinal` explicitly.
**Notes:**
- **⚠ `A3`'s red-first was re-established INDEPENDENTLY by me, because the worker's own sequencing went
  through the banned shape.** The worker wrote a fault-injection test first and saw it red, then found the
  injected (fake) exception made the fix's recovery path fail for an artificial reason — a real
  instance of the protocol's "reverting/simulating often doesn't cleanly reproduce" warning. It redesigned
  the test around a genuine committed competing row, but validated red by reverting the already-written
  fix, which is exactly the sequence the protocol bans. Rather than accept or reject the claim, I bisected:
  `git checkout 9e030ad^ -- src/ActiveSync.Core/State/SyncStateService.cs` (source only, test untouched)
  and ran the test — it failed with an unhandled
  `DbUpdateException / SQLite Error 19: UNIQUE constraint failed: OofSettings.UserName`, the finding's
  exact symptom. **The proof stands.** It also survives the "is the test shaped by the fix?" question: it
  asserts only the symptom (does not throw · exactly one surviving row · this call's fields applied) and
  references nothing the fix introduced (`ApplyOofFields` is private).
- **`A3` fixes the INSERT race only — the lost-update case remains.** Two concurrent *updates* to an
  existing Oof row still have no conflict detection, because `OofSetting` still carries no concurrency
  token. The finding offered both remedies and the worker chose the non-migration one, which I agree with
  at this severity, but the finding's title says "no concurrency token / insert-race guard" and only the
  second half is now closed. Recorded so the strike is not read as more than it is.
- **`A7` is defensive, not a behaviour fix, and the worker was straight about it**: .NET's default string
  equality comparer is already ordinal, so no lookup behaviour changes today. The red-first asserts the
  comparer *object* (`Assert.Same(StringComparer.Ordinal, result.Comparer)`), which is a genuine proof of
  the structural defect rather than a coverage label.
- **⚠ Carry to item 28:** `A7`'s fix re-wraps the deserialized dictionary, so `Decompress` now allocates
  one extra `Dictionary` per call — on the per-sync-round hot path. Item 28's `A14` is specifically about
  `SnapshotCodec` allocation (serialize straight into the GZip stream); whoever works it should fold this
  copy into that pass rather than leaving two allocation fixes in tension.
- No behaviour changes visible to a client. No new findings filed.

---

## Item 21 — Retention services & DB-log lifecycle
**Findings:** `E2` `E4` `E13`
**Commits:** `8c0edfb` (E2) · `913dfe2` (E4) · `d567fcd` (E13)
**Verification:** integrity items=32 live=10 assigned=132 unique=132 dupes=0 encoding=0 ✓ · cursor → item
22 ✓ · one commit per finding, strike with each ✓ · build 0 warnings ✓ · unit **1127 passed, 0 skipped**
(+4; Server 264→268) ✓ · live **141 passed, 0 skipped** on a clean-volume Stalwart ✓
**Diffs read against the detail entries:**
- `E2` — exactly the prescribed mirror of `SettingsRefreshService`: `catch (OperationCanceledException)
  when (stoppingToken.IsCancellationRequested)` in **both** retention services, so a non-shutdown OCE (an
  EF command timeout) now falls through to the retry-log catch instead of breaking the loop for the
  process lifetime. The red-first is well-chosen — it asserts on `ExecuteTask.IsCompleted` rather than
  waiting for a second sweep, since the real inter-sweep delay is 6 hours.
- `E4` — a `_shutdownCts` is now cancelled in `Dispose` and threaded into both `WaitToReadAsync` and
  `SaveChangesAsync`, so a hung write is actually interrupted rather than merely abandoned while
  `Dispose` stops waiting. Cancellation is re-thrown past the per-batch handler so it reads as a clean
  exit, not a logged batch failure.
- `E13` — a 200 ms cancellable pause on the disabled-live discard path. It still drains a full batch per
  pause, so the backlog clears rather than stalling, and the channel's existing bounded/DropOldest policy
  caps what can accumulate.
**Notes:**
- **`E4` forced a genuine second fix that rides in the same commit: `Dispose` is now idempotent.** Serilog's
  `Logger.Dispose()` disposes an owned `IDisposable` sink, and callers hold the sink themselves to
  `Activate` it before the logger exists — so `Dispose()` runs twice in normal operation, and
  `Cancel()`-after-`Dispose()` on the new CTS throws `ObjectDisposedException`. The `Interlocked.Exchange`
  guard is therefore load-bearing for `E4`, not tidying: without it this fix would have introduced a
  shutdown crash. Worth knowing, because it means `E4` touched disposal semantics that predate it.
- **A test-only accessor was added to production code**: `internal int BufferedCount => _channel.Reader.Count`
  on `DatabaseLogSink`, used by `E13`'s test to observe the backlog. Small and `internal`, but it is
  production surface existing for a test; flagged rather than hidden.
- **`E13`'s 200 ms is a chosen constant, not a derived one.** It bounds the discard to ~1 batch (500
  entries) per pause. Nothing depends on the exact value; a future tuner should know it was picked for
  "obviously not a spin", not measured.
- All three proven red-first with real reproductions (a non-shutdown OCE, a genuinely hung
  `SaveChangesAsync`, and a 6000-entry backlog draining in ~27 ms unfixed vs ~2.4 s fixed). No
  coverage-only tests this item.
- No new findings filed.

---

## Item 22 — Config & account resolution
**Findings:** `B2` `B3` `B4` `B5` `B7` `B12`
**Commits:** `ad0683d` (B2) · `4a7e031` (B3) · `d98146e` (B4) · `561dc28` (B5) · `b490afc` (B7) ·
`8613820` (B12)
**Verification:** integrity items=32 live=10 assigned=132 unique=132 dupes=0 encoding=0 ✓ · cursor → item
23 ✓ · one commit per finding, strike with each ✓ · build 0 warnings ✓ · unit **1131 passed, 0 skipped**
(+4) ✓ · live **141 passed, 0 skipped** on a clean-volume Stalwart ✓
**Diffs read against the detail entries:**
- `B4` — the substantive fix, and better than the minimum. Both writers (`EnsureFreshAsync` on the request
  path, `OnRolesChanged` on the config-reload thread) now **build as well as swap** under one
  `_snapshotSwapLock`, so the second writer computes against the first's applied state instead of racing
  with stale roles baked in. Locking only the assignment would have left the finding's actual defect —
  a build started against superseded inputs finishing last — intact.
- `B7` — the legacy `sieve.enabled` upgrade now opts a user into the Oof override **only** when a Host
  survived conversion, and logs an actionable hint otherwise. Correct direction: an invalid DB row fails
  the whole account closed, so the old behaviour could leave a legacy user unable to log in at all after
  an upgrade, not merely without Oof.
- `B12` — one `NormalizeLevelName` shared by `LogQueryService`, `ActiveSyncOptionsValidator` and the write
  path, exactly as prescribed.
**Notes:**
- **⚠ THREE of six findings were closed documentation-only (`B2`, `B3`, `B5`). I checked each against its
  detail text: all three explicitly offer "document it" as one of two sanctioned remedies**, so this is
  within the findings' own terms, not a worker taking the cheap way out. Still, half this item changed no
  behaviour, and a reader should know what each strike does and does not mean:
  - **`B2` — the docs were wrong, not the code, and that is the unusual part.** The clamp of a negative
    `UsersRefreshSeconds` to 0 is deliberate: round 1's `B11` introduced it because "negative disables
    live pickup" is self-locking — an operator who set it negative could never repair it live, since the
    repair itself needs pickup. Honouring negative-as-disable would have reintroduced that. README:394 and
    the `AuthOptions` XML doc now match `docs/configuration.md`, which was already correct. **Behaviour is
    unchanged: a negative value still polls every request.**
  - **`B3` — the validation gap is real and still open; only its documentation changed.** A catalogue write
    that bricks startup *via* the backend/user validators is still not caught at write time. The worker's
    argument for not wiring it up is that every catalogue key lives outside the `Backends`/`Users` sections
    those validators read, so the check would compare a failure set to itself — dead code at the cost of a
    full role+user validation pass per write. I find that sound **today**; it stops being sound the moment
    a catalogue key lands inside those sections, and the XML doc now states that trigger.
  - **`B5` — secrets still sit unsealed in the long-lived snapshot.** Documented as an accepted trade-off
    rather than moved to lazy per-request unsealing (which would put a PBKDF2 derivation on a hot path).
    The memory-residency surface the finding describes is unchanged.
- **`A5` (item 19) plus `B2`/`B3`/`B5` means four documentation-only closures across two items.** Each is
  individually justified by its own finding text. Flagging the pattern because it is invisible at the
  queue line — those four strikes look identical to the fifteen behavioural ones next to them.
- **Worker process note, self-disclosed:** `B4` and `B5` initially landed in one commit (both touch
  `AccountResolver.cs`); the worker caught it and used `git reset --soft HEAD~2` to re-split before moving
  on. Nothing was pushed and the final history is one commit per finding — I verified each of the six
  touches only its own hunk.
- **`B4` holds a lock across `BuildSnapshot`**, which can call provider `ValidateConfiguration`. No `await`
  inside the lock, and the work is CPU-bound merging, so this is safe — but it is a slightly wider critical
  section than "swap under a lock" implies.
- No new findings filed.

---

## Item 23 — DAV & JMAP request correctness [LIVE]  ⚠ REQUIRED AN ORCHESTRATOR-INITIATED REPAIR
**Findings:** `H2` `H3` `H4` `H6` `H7` `H8` `H10`(N/A) `H11`
**Commits:** `c7da14f` (H2, defective) · `ebf03f8` (**H2 repair**) · `6f78e64` (H3) · `4c844f4` (H4) ·
`ca71f13` (H6) · `ffa02c6` (H7) · `d53eae3` (H8) · `c7e402c` (H11 + H10's N/A closure)
**Verification:** integrity items=32 live=10 assigned=132 unique=132 dupes=0 encoding=0 ✓ (the two new
findings are unassigned, so `assigned` correctly stays 132) · cursor → item 24 ✓ · build 0 warnings ✓ ·
unit **1141 passed, 0 skipped** ✓ · live **141 passed, 0 skipped** on a clean-volume Stalwart ✓

### ⚠ `H2` as first landed was a real regression, caught by reading the diff — not by any test
`c7da14f` turned the pre-PUT collection listing into a lazy memoized `Func<Task<…>>`. It is never invoked
before the PUT, so whenever it *was* invoked — from `ResolveStoredHrefAsync`, which runs after the PUT —
it enumerated a collection **that already contained the just-created resource**, while still being named
and documented as the "before the PUT" baseline. Both consumers broke:
1. `!(await before()).ContainsKey(hit.Href)` — a server that stored the item under a canonical href now
   has that href in the post-PUT listing, so the UID hit was **rejected** instead of adopted.
2. `appeared = after.Keys.Where(k => !beforeMap.ContainsKey(k))` — both maps post-PUT, so `appeared` was
   always **empty**, the `Count == 1` branch never fired, and the method fell through to its warning and
   returned `putHref` — **the wrong href**.
That is precisely the "next diff sees an alien Add plus a Delete → the device duplicates the item" failure
that function's own comment describes, for the servers it names (Axigen, which is a CI backend). The
worker's own test passed because it only exercises the fast path, where `PathsEqual(hit.Href, putHref)`
short-circuits before `before()` is ever reached. **The unit suite, the live suite and the worker's
red-first proof were all green on a broken fallback** — this is the case the every-finding diff read
exists for, and the fourth time in this programme the orchestrator has caught something no worker did.
**Repair** (`ebf03f8`, scoped subagent, verified like any item): the before/after diff is gone entirely,
since a genuine pre-PUT snapshot cannot be reconstructed after the PUT. A UID hit at the exact PUT href
is still trusted immediately (H2's optimisation intact — no enumeration, no content fetch on the happy
path); a hit at a *different* href is now verified by fetching that resource and comparing its embedded
UID to the freshly-generated one; the deep fallback content-scans the post-PUT listing. Content
verification is strictly stronger than the old presence proxy — it proves the item is *ours* rather than
inferring it from absence. Red-first: `CreateItem_WhenServerCanonicalizesHref_AdoptsTheCanonicalHref`
fails on `c7e402c` returning the PUT-target href and passes after; the fast-path 0-PROPFIND test still
passes.
**Cost note carried forward:** on a doubly-broken server (no usable UID query **and** href rewriting)
where our item genuinely isn't found, the fallback now issues one GET per listing entry before giving up,
where it previously issued none. Bounded by collection size, create-only, and that path was already
returning a wrong answer — but it is a new worst case.

**Other diffs read against the detail entries:**
- `H3` — `end.AsUtc - start.AsUtc` for timed events, floating `Value` subtraction kept for all-day (which
  carries no zone). Exactly the described defect and fix.
- `H4` — non-birth anniversaries are now carried over from the existing card and merged with the rebuilt
  `birth` entry, instead of `anniversaries` being replaced wholesale. This is the item's most valuable
  fix: a wedding anniversary was destroyed on **every** contact edit.
- `H6` — the signal now fires at the SSE record boundary (blank line) with a `sawData` flag, so a `data:`
  line preceding its `event: ping` line can no longer mis-latch. Correct reading of the SSE grammar.
- `H7` — RFC 8620 PatchObject keys (`mailboxIds/{source} = null`, `mailboxIds/{dest} = true`) replace the
  wholesale map assignment, exactly as prescribed.
- `H8` — `until` is converted into the event's own zone per RFC 8984 §4.3.4, with a documented fallback to
  the pre-fix UTC digits for floating or unresolvable zones.
- `H11` — MimeKit's `MailboxAddress.TryParse` with the old heuristic kept only as a last resort.
**Notes:**
- **`H10` closed N/A, and the reasoning holds.** The prescribed fix was to fold the current-keywords
  `Email/get` into the batched get→set→get. JMAP `resultReference` copies a literal value between batched
  calls; it cannot carry a computed set-difference, and the categories patch needs the current keyword set
  to decide what to null — so the get must complete and be read client-side before the set's arguments
  exist. Two round trips are structural, not incidental. Filed `H35` for the real remedy.
- **Protocol nit:** the queue line marks `~~H10~~ **N/A**` without the required one-line why inline (the
  reasoning is in the finding text instead). Cosmetic; not worth a rewrite commit, noted so the next
  reader knows where to look.
- **`H10`'s N/A rode in `H11`'s commit** rather than its own — defensible (there is no source change to
  isolate), but it is a deviation from one-commit-per-finding.
- **`H7` was scoped to `JmapMailStore` only**, though its detail text also names the identical shape in
  `JmapCalendarStore`/`JmapContactStore`. The worker filed `H34` for those rather than fixing them —
  I'd have preferred them fixed here since the detail names the sites, but filing is defensible and
  the finding is recorded, not lost.
- **Two new findings filed and unassigned: `H34`** (calendarIds/addressBookIds wholesale replace — the
  other half of `H7`) and **`H35`** (an `IContentStore` previous-state parameter, which would also close
  `H10` properly). Both need an item.
- No breaking changes; every fix changes a previously-wrong computed value to a correct one.

---

## Item 24 — Converter correctness [LIVE]
**Findings:** `D2` `D3` `D4` `D5` `D13` `D15`
**Commits:** `716dc08` (D2) · `8ec9b51` (D3) · `4ca12fb` (D4) · `5620d18` (D5) · `f9121bb` (D13) ·
`7645078` (D15)
**Verification:** integrity items=32 assigned=132 encoding=0 ✓ · cursor → item 25 ✓ · one commit per
finding, strike with each ✓ · build 0 warnings ✓ · unit **1150 passed, 0 skipped** (+9) ✓ · live **141
passed, 0 skipped** on a clean-volume Stalwart ✓
**Diffs read against the detail entries.** Three findings here were *reinterpreted* by the worker, so I
checked each against its FIX text specifically — **all three are within the finding's own sanctioned
options**, which is not what I expected going in:
- `D2` — the FIX text asks for two things: "confirm the engine scopes the stored snapshot to the same
  filter" **and** "document the skew or widen by a day". The worker did both: confirmed the scoping is
  already handled, then added `SearchFloor` = `sinceUtc.AddDays(-1).Date` so the IMAP-side filter is a
  strict superset of the intended UTC window on any server timezone. Its comment also records a genuinely
  sharp observation — the aged-out reconciliation only rescues items *seen before* and later falling out
  of the window, so a message excluded from its very first appearance never gets that treatment. That is
  why widening was the right half to take.
- `D5` — the prescribed fix (emit the nominal date rather than the UTC instant for all-day, mirroring
  `TasksConverter.Nominal()`) landed exactly as written, via a new `NominalUtc`. **But the worker could
  not reproduce the finding's literal symptom**: with the pinned Ical.Net 5.2.3, `HasTime=false` values
  are already special-cased, so no day-shift appears at process-local UTC+2 across bare `VALUE=DATE`,
  explicit `TZID+VALUE=DATE`, and with/without `VTIMEZONE`. The main test is therefore **coverage, not
  proof**, and is labelled as such. I accept the strike: the code genuinely did route a zoneless value
  through `AsUtc`'s zone arithmetic (the expression the finding names), and the fix removes that
  dependence on a library's internal handling rather than relying on it. **Read the strike as "the unsafe
  expression is gone", not "a day-shift was observed and fixed."**
- `D15` — the FIX text offers "page the flags fetch, **or** document why the revision map must be a
  single fetch". The worker documented, arguing paging would require releasing the session gate mid-fetch
  and could tear the revision map across mailbox states — which is a real conflict with the "revision map
  is the whole truth" invariant, so the documented option is the better one here. **The O(collection)
  per-round flags fetch and the gate-holding behaviour are unchanged**; only the rationale is now written
  down. A large mailbox still pays that cost every Sync/Ping round.
- `D3` — genuinely red-first (the finding's exact duplicate-and-drop reproduced). Worth noting the
  process: the worker's first fix broke an existing test (`Update_PresentElementsWin_OverTheStoredValue`),
  it diagnosed ownership per protocol step 9, reverted and re-fixed against the stored card's own
  top-3-by-pref set. That is the protocol working as intended.
- `D4` — `type != 4` exempts full-MIME bodies from the byte-cut. Exactly prescribed, red-first
  (`Truncated` flipped 1→0).
- `D13` — `Until` is now checked before `Occurrences`, so the bound that *narrows* the series wins.
  Exactly the finding's reasoning, red-first.
**Behaviour changes (four, all deliberate):**
1. `D2` — the IMAP filter window is up to one day wider; near-boundary mail may surface one round earlier.
2. `D4` — a type-4 body is **never** truncated, so a large MIME fetch with a small `TruncationSize` now
   returns the whole message. Correct per spec, but a real payload-size change for constrained clients.
3. `D5` — an all-day event with no DTEND now defaults to +1 nominal **day**, not +1 hour. This one was
   found while probing and is **red-first proven** — a genuine adjacent bug in the same expression, and
   arguably the most valuable thing in this commit.
4. `D13` — when a client sends both `Occurrences` and `Until`, `Until` now wins (previously `Occurrences`).
**Notes:**
- **Two coverage-not-proof tests this item** (`D2`'s skew, `D5`'s main all-day claim), both because the
  symptom needs a server/library behaviour the environment cannot produce deterministically. Both are
  labelled in the test comment and the finding note, per protocol. Combined with `D15`'s documentation-only
  closure, **half of this item changed no behaviour that any test observed** — the other half (D3, D4,
  D13, D5's DTEND fallback) is solid red-first work.
- No new findings filed; the D5 DTEND bug was fixed inline as directly adjacent to the code under edit,
  which I agree with over filing it.

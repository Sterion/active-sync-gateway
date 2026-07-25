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
- **Item 12 is PARTIAL. `C5` is OPEN and re-scoped** — it is the lowest-numbered unstruck finding, so the
  cursor resolves to item 12, not 14. A resuming orchestrator must decide C5 (close `N/A` with reasoning,
  or add per-field provenance) before treating Phase 2 as done.
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

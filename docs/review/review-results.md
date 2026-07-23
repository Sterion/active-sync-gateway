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

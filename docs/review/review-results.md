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

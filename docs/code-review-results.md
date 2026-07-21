# Code review — implementation results

Running record of [`code-review.md`](code-review.md) items as they are completed.

**Written by the orchestrating session, not by the worker.** Each entry pairs the worker's own
report with the orchestrator's *independent* verification — the two are separate on purpose. A
worker reporting success is a claim; the integrity check, the commit list and the cursor state are
evidence. Item 1 is why: its worker correctly fixed, tested and committed all three findings and
reported success, but marked them in Part 2 only, leaving the Part 1 cursor untouched. Every check
it ran passed. The one it did not run — "does resume now return the next item?" — was the one that
mattered.

**Notes are the point.** Anything a worker flagged that a future reader would want and would never
find in a diff — a breaking change, a test that is coverage rather than proof, a judgment call that
could reasonably have gone the other way — belongs here.

Format per item:

```
## Item N — title
**Findings** · **Commits** · **Verification** (orchestrator-run) · **Notes** (worker-flagged)
```

---

## Item 1 — IMAP mailbox safety [LIVE]

**Findings:** `D1` `D2` `D17` — two Criticals
**Commits:** `f51c81c` (D1) · `2d3bca7` (D17) · `e79bf55` (D2)

**Verification:** integrity 56/15/365/365/0 ✓ · cursor → item 2 ✓ (after correction, see notes) ·
one commit per finding with ID in subject ✓ · build 0 warnings ✓ · integration 124 → **127 passed,
0 skipped** ✓ · 507 unit tests green ✓

**Notes:**
- **`D2` is a breaking change.** Mail item keys become `<uidvalidity>:<uid>`. Pre-existing bare-UID
  keys are honoured for one sync then reissued qualified, which the snapshot diff renders as
  Delete+Add per message — so **every IMAP mail folder does one full re-sync on upgrade**. Inherent
  to the fix; recorded in the commit message and the Part 2 entry.
- **`D17`'s test is coverage, not a reproducer**, and is labelled as such. It passes against the old
  code because Stalwart drains the pending `EXISTS` anyway. The fix is still right (UIDs rather than
  sequence numbers, matching every sibling call); the remaining renumbering race has no
  deterministic test.
- **Process:** the worker marked findings in Part 2 only, so resume still returned item 1 with
  everything done. Protocol step 3 was rewritten to name Part 1's item line as the cursor. Also
  dropped the commit hash from the mark — it cannot be written in the commit it names.

## Item 2 — WBXML decoder & encoder hardening

**Findings:** `W1`–`W8` — one Critical (`W1`), one High (`W2`)
**Commits:** `3f2148e` (W1) · `36c9021` (W2) · `16177f1` (W3) · `02eb6b2` (W4) · `7f3cdbd` (W5, W6) ·
`b436b2a` (W7, W8)

**Verification:** integrity 56/15/365/365/0 ✓ · cursor → item 3 ✓ · IDs in subjects ✓ · build 0
warnings ✓ · 523 unit tests passed, 0 skipped ✓ · not [LIVE], no backend run required or performed ✓
· spot-checked: `MaxDepth = 256`, `MaxElements = 200_000`, encoder recursion bounded, 231 lines of
new hardening tests ✓

**Notes:**
- **`W5` is only partially fixed.** The encoder's intermediate `byte[]` is gone; two copies remain
  by design (`Convert.ToBase64String` on decode, `ToArray()` on encode). Removing them means
  representing opaque data as `byte[]` out-of-band rather than base64 text on an `XText` — a change
  across every producer and consumer of the marker attribute, not a WBXML-layer edit. Its test is
  labelled coverage: an allocation count is not observable from a round trip.
- **Two judgment calls open to reversal:** opaque-with-children now **throws** (refusing beats
  guessing which half to keep); an embedded NUL is **stripped** (the value is backend-supplied, so
  throwing would fail an entire sync response over one bad byte in a subject line).
- **Process:** the first two `W1` reproducers passed without the fix — unclosed nesting threw
  "unclosed elements remain" before reaching the cap — and `W4` could not be reverted at all, since
  the fix had changed the signature. This is what moved protocol step 6 from fix-then-revert to
  test-first.

## Item 3 — Contact, vCard & iTIP integrity [LIVE]

**Findings:** `D4` `D6` `D7` `D22` `D23`
**Commits:** `b7091b0` (D22) · `4d42533` (D4) · `f8fe5ca` (D6) · `39b4255` (D23) · `dec0224` (D7)

**Verification:** integrity 56/15/365/365/0 ✓ · cursor → item 4 ✓ · IDs in subjects ✓ · build 0
warnings ✓ · Core.Tests 371 passed ✓ · live Stalwart **127 passed, 0 skipped** against a 127/0
baseline — no regression ✓ · all five findings red-first ✓

**Notes:**
- **`D4` changes contact Change semantics** from full-replace to ghosted. The existing test
  `Update_ManagedFieldsComeFromThePayload_NotTheOldCard` explicitly asserted the old behaviour and
  was rewritten — that is the finding's point, but it is a real behaviour change. Clearing a field
  stays expressible because presence, not value, decides.
- **`BuildCancel_UsesCrlf_NotThePlatformLineEnding` is mislabelled as coverage.** It passes against
  unfixed code *on Windows* only because `Environment.NewLine` is already CRLF there. `D7` is about
  "bare LF on the Linux containers this ships in", and **CI runs `ubuntu-latest` on all three legs** —
  so on Linux the unfixed code emits LF and this test fails. It is platform-conditional proof that
  CI exercises on every push, not coverage. **The label should be corrected** in the test comment
  and the Part 2 note.
- **Process:** `D7`'s assertion was rewritten *after* applying the fix — the thing step 6 warns
  about. The worker caught it, stashed only `ImipMailBuilder.cs`, and re-confirmed the rewritten
  assertion goes red against unmodified code before committing.

## Item 4 — Draft & MIME building [LIVE]

**Findings:** `D15` `D16`
**Commits:** `0d052c7` (D15) · `6a19c9d` (D16)

**Verification:** integrity 56/15/365/365/0 ✓ · cursor → item 5 ✓ · IDs in subjects ✓ ·
one commit per finding ✓

**Notes:** _(worker report not captured in the orchestrating session — backfilled from git. Future
entries should carry the worker's own flags.)_

---

## Next: item 5 — JMAP converter semantics [LIVE]

Carries an internal ordering constraint: **`H7` must be settled and tested against the live Stalwart
backend before `H4` `H5` `H6` `H23`.** It decides whether JMAP `update` is a PatchObject or a full
replace, and the codebase currently contains evidence for both readings — which changes the *shape*
of the other four fixes. See the sequencing note in item 5 and in Area H.

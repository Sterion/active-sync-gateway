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

## Item 5 — JMAP converter semantics [LIVE]

**Findings:** `H7` (first, live-settled) → `H4` `H5` `H6` `H23`
**Commits:** `0f44071` (H7) · `e3e0d04` (H4) · `b988b01` (H5) · `606be41` (H6) · `b9e73cd` (H23) ·
`9c1544a` (docs: new findings `H32` `D36` spotted in passing)

**Verification:** integrity 56/15/365/365/0 ✓ · cursor → item 6 ✓ · one commit per finding with ID in
subject ✓ · build **0 warnings** ✓ · integration re-run by the orchestrator: **132 passed, 0 skipped**
against a 127/0 baseline taken before the worker started — 5 new live tests, no regression ✓ ·
sequencing constraint honoured (`H7` committed first) ✓

**Notes:**
- **`H7` is settled: Stalwart 0.16 treats `*/set update` as a PatchObject** (RFC 8620 §5.3) — omitting
  `titles` from a `ContactCard/set update` left the old value in place. Both converters now emit an
  explicit `null` for every managed member the payload didn't populate, **on update only**. That is
  safe under both readings, since null and absent are equivalent under full-replace.
- **`H4` was worse than "lossy": recurring events hard-failed.** Stalwart does not implement RFC 8984's
  plural `recurrenceRules` array in any form probed (minimal, `@type`, `count`, `byDay`, `null`, `[]`)
  — it rejects with `invalidProperties` and speaks the JSCalendar-draft singular `recurrenceRule`.
  Reads now accept both; writes mirror whatever shape the stored event shows, defaulting to singular.
  **Judgment call:** a strict RFC 8984 server gets the right member on update but the wrong one on
  create. Chosen because it is the shape the only verifiable backend accepts.
- **Two more judgment calls open to reversal:** `H6` writes the birthday as `Timestamp` rather than
  `PartialDate` (the EAS `Birthday` element is itself a UTC timestamp, so this invents no precision;
  reads accept both). `H7`'s null-out set excludes contact `media` — the EAS view never reads or
  writes the photo, so nulling it on each edit would destroy a picture the client never saw — and the
  calendar recurrence member, deferred to `H4` because its name is server-dependent.
- **"Clear this field" on a JMAP calendar is bounded by the ICS merge, not by `H7`.** The worker's
  first `H7` calendar reproducer (clearing `Location`) was green with *and* without the fix:
  `CalendarConverter.FromApplicationData` merges the payload onto the stored iCalendar, so an absent
  `<Location>` is restored before the JSCalendar bridge sees it. Filed as **`D36`**. The reproducer
  was switched to free→busy, the one case that genuinely reaches the bridge as a cleared member.
- **`H5`'s 5th theory case is coverage, not proof** — labelled as such. The other four, and one live
  test, are red-first. `H23`'s live coverage is no-regression only; its proof is the unit test.
- **Diagnostics gap filed as `H32`:** `EnsureNotIn` reports only `type`, so diagnosing `H4` required
  temporarily patching the raw `SetError` JSON into the exception message.
- **Two traps for anyone editing this converter:** `WeekDay.Offset` is `int?`, so the obvious
  `w.Offset != 0` guard is true for null and emits `"nthOfPeriod": null`, which then throws out of
  `TryGetInt32` on the way back in — both sides are now ValueKind/null-guarded. And
  `JsCalendarConverter` has a private helper named `Array(...)` that shadows `System.Array`, so
  `Array.Empty<object>()` will not compile inside that class.
- **Item 42 (JSCalendar/JSContact round-trip suite) would have caught `H4` `H5` `H6` `H23`
  mechanically**, as the doc predicted. What landed here are targeted reproducers, not that suite.

## Item 6 — Delete windowing & SoftDelete [LIVE]

**Findings:** `F2` `F3` `A21`
**Commits:** `33179b2` (F2, A21) · `c0e4d1b` (F3)

**Verification:** integrity 56/15/365/365/0 ✓ · cursor → item 7 ✓ · IDs in subjects ✓ · build
**0 warnings** ✓ · integration re-run by the orchestrator: **134 passed, 0 skipped** against the 132/0
post-item-5 baseline — 2 new live tests, no regression ✓ · both fixes red-first ✓

**Notes:**
- **`F3` costs an extra backend enumeration.** Aged-out and genuinely-deleted items are
  indistinguishable from the filtered revision map, and `IContentStore` has no existence check, so the
  fix adds one unfiltered `GetItemRevisionsAsync` — guarded on `filter.SinceUtc is not null &&
  diff.Deletes.Count > 0`. **This compounds `D32`** (that call has no result cap): a filtered mail
  collection with deletes now does a second unbounded enumeration per round. Capping in `D32` caps
  this too. The better fix — a cheap `ItemsExistAsync` capability on the contract — is item 17's
  territory, not this item's.
- **`F3` falls back to a hard `Delete` if the unfiltered listing throws** (status quo ante, logged as a
  warning). Reasoning: a real deletion mis-reported as `SoftDelete` strands the item on the device
  permanently; the reverse is recoverable. Defensible either way.
- **`CollectionChanges.Deletes` changed meaning** from "all deletes" to "deletes sent this round". The
  only caller that depended on the old meaning, `GetItemEstimateHandler`, already passes
  `int.MaxValue` as the window, so estimates are unaffected — but a future caller passing a real
  window now gets a windowed count.
- **`A20` is partially obsoleted but deliberately not struck.** Rewriting `Compute` removed the two
  unreachable branches it names, as a side effect; the `Drain` extraction it also asks for was not
  done. `A20` stays open in item 46, with a note on the `F2` Part 2 entry so item 46 doesn't chase a
  defect that is half gone.
- **Ordering and metrics judgment calls:** the window charges deletes → changes → adds (`A21` is
  explicit that tombstones drain first, though an argument exists for adds-first since new mail is
  what users notice); soft deletes got a new `soft_delete` metrics label rather than folding into
  `delete`, which is additive but does mean the existing `delete` counter now excludes window
  departures.
- **`ItemRemovedFromTheBackend_IsHardDeleted_EvenOnAFilteredCollection` is a guard, not proof** —
  labelled as such. It passes before and after; its job is to catch a fix that blanket-soft-deletes
  everything on a filtered collection.
- **Two new test levers worth reusing:** `EasTestClient` previously hardcoded `FilterType 0`, which is
  why no existing integration test could reach the filtered path — it now takes a `filterType`
  parameter (default 0, existing tests unchanged) and parses `SoftDelete`. And
  `ImapProbe.AppendAsync` takes an optional INTERNALDATE, which makes window-departure testable in
  seconds instead of requiring real elapsed days. **Item 30 (timezone & date handling) should use
  it.**
- The old `DeletesAreNotWindowed` test asserted the defect as intended behaviour — that is what `A21`
  called out. It was replaced, not silently dropped.

## Item 7 — Unauthenticated resource limits

**Findings:** `K1` `E2` `K26` `E21` `K33` `W17`
**Commits:** `58aa7dd` (K1, E2) · `943b94e` (K26) · `3e77e43` (E21) · `0b37b72` (K33) · `d01b2e9` (W17)
· `911ac9c` (docs: new finding `W22`)

**Verification:** integrity 56/15/365/365/0 ✓ · cursor → item 8 ✓ · IDs in subjects ✓ · build
**0 warnings** ✓ · unit suites re-run by the orchestrator: **Core 400, Server 115, Protocol 63,
WebUi 9 — 0 failed, 0 skipped** ✓ · not [LIVE], no backend run required or performed ✓

**Notes:**
- **⚠ `W17` is only half the hole — see new finding `W22`.** `EasEndpoint` overwrites
  `ProtocolVersion` with the raw `MS-ASProtocolVersion` header with no validation, and
  `EasVersion.Parse("99.9")` returns `99.9`, clearing every `>= V161` gate. Identical unlock-16.x
  symptom, one field over. The worker left it deliberately — rejecting an unknown header string is a
  client-compatibility decision deserving its own item — but **anyone reading "`W17` fixed" should not
  read it as "the version-unlock hole is closed".** `W22` is logged at the bottom of Part 2 and needs
  assigning to an item.
- **`E2` was deliberately not fixed as written.** The recommendation — move the label assignment below
  auth — would stop counting 401/429 outcomes entirely, i.e. exactly the traffic an operator watches
  during a brute-force attempt. The worker stashes `"-"` before auth and overwrites with the real
  username after: the count survives, only the attacker-chosen name is lost. **`E2`'s ordering half
  has no unit-level reproducer** (no unit EAS host; it is only observable through
  `Integration.Tests/Scenarios/MetricsTests.cs`, and this item is not [LIVE]). Struck on the strength
  of the fix, labelled as such.
- **`K26`'s cap drops new keys rather than evicting old ones.** LRU eviction would let a username
  rotator displace a real user's counter; dropping is safe because the attacker's per-address counter
  is minted long before the table fills. Consequence: during a sustained attack a genuinely new user
  gets no *per-user* counter, only the IP-wide ceiling. The measured win is the sharpest number in the
  run — 60k rotated-username failures went **9m02s → 2s**, table 60k rows → capped.
- **`K33` is quantified:** `GC.GetAllocatedBytesForCurrentThread` over the wire-log path, **20,165,248
  bytes → under 1 MB**.
- **`W17` validates against a set, not a range** (a range still admits 130 = "13.0"), and the set is
  **wider than the advertised versions** — 2.5 and 12.0 parse but aren't advertised. Rationale:
  advertisement is the right place to refuse an old client; a 400 at parse time is not.
- **`K31` in Part 2 is stale and will mislead item 40.** It claims there are "no `AuthThrottleTests` at
  all"; `tests/ActiveSync.Server.Tests/AuthThrottleTests.cs` exists with 7 pre-existing tests.
  Re-verify before starting item 40. Item 40's line was left untouched.
- **`K27` (unlocked read in prune) survived the `K26` rewrite untouched** and still belongs to item 53
  — the new `Prune()` still reads `entry.WindowStartUtc` without the lock.
- **`AuthThrottle` gained two `internal` test seams**, `TrackedKeys` and `PruneScans`, added *before*
  the tests so the symptom was assertable. They are instrumentation, not the fix. Item 40 (`K32`,
  inject `TimeProvider`) will want the same file.
- **If you ever revert `K26`, the three heavy `AuthThrottleTests` will appear to hang, not fail fast** —
  the red run took 9 minutes.

## Item 8 — WebUi session & cookies — ⚠ **VERIFICATION FAILED, RUN HALTED**

**Findings:** `C2` `C4` `C8` `C7` `C3` `C14` — all six fixed and struck through
**Commits:** `dba314d` (C2, C4) · `c2cee3f` (C8) · `4fc94ac` (C7) · `5e6ffc4` (C3) · `63c5681` (C14)

**Verification:** integrity 56/15/365/365/0 ✓ · cursor → item 9 ✓ · IDs in subjects ✓ · build
**0 warnings** ✓ · unit suites **Core 401, Server 115, Protocol 63, WebUi 26 (was 9) — 0 failed,
0 skipped** ✓ · **integration ✗ — 23 failed, 111 passed, 0 skipped.**

**The integration regression is real and is the reason the orchestrated run stopped here.** Item 8 is
not [LIVE], so the worker correctly ran unit tests only and never saw it. The orchestrator ran the
integration suite anyway because this item landed an EF migration on both providers — a schema change
is precisely the silent-breakage class the "establish a green baseline" section warns about.

Bisected, not guessed: integration at `911ac9c` (end of item 7) is **134 passed, 0 skipped**; at
`63c5681` (end of item 8) it is **111 passed, 23 failed**. Item 8 owns the regression.

**Cause — `C2`, working as designed, against a harness that cannot accommodate it.** Every failure is
WebUi-shaped (401 Unauthorized, `Values differ`, one empty-JSON-body). `WebUiServiceCollectionExtensions`
now sets `CookieSecurePolicy.Always` unless `ActiveSync:WebUi:AllowInsecureCookies` is set; the
integration harness (`GatewayFixture`) drives the portals over **plain http** with a cookie container,
so the session cookie is never returned and every authenticated request 401s. This is the same failure
mode the worker predicted for an operator on plain http — it simply lands on the test harness first.

**Recommended fix — harness, not product:** set `ActiveSync:WebUi:AllowInsecureCookies=true` in
`GatewayFixture`'s configuration. That is the documented local-http opt-out, used exactly as intended,
and it keeps the `C2` fix at full strength. It should be paired with one integration test that asserts
the cookie carries `Secure` when the opt-out is *off*, so the harness opt-out cannot quietly become a
blind spot for the very finding it is working around.

**Notes (worker-flagged, all still valid — the fixes themselves are sound):**
- **`C2` is deployment-affecting.** An operator serving the portals over plain http with no
  `PublicUrl`/`X-Forwarded-Proto` will find the session cookie rejected after upgrading. That is the
  intended failure; `ActiveSync:WebUi:AllowInsecureCookies` (restart-tier, warned once at startup) is
  the escape hatch. Auto-detecting the proxy was rejected as exactly the guesswork `C2` names as unsafe.
- **`C4` does not reproduce.** .NET 10's `RemoteAuthenticationOptions` already defaults
  `CorrelationCookie`/`NonceCookie` to `SecurePolicy.Always` — the review read the older documented
  default. Its test is labelled coverage in both the test comment and the Part 2 note. What did change:
  under the opt-out those cookies drop to `SameSite=Lax`, because `SameSite=None` without `Secure` is
  discarded outright by browsers.
- **`C8` is breaking.** Startup now *fails* for a deployment with `AdminClaim` and no
  `AdminClaimValue`; `"*"` restores the old directory-wide behaviour. A warning would have been softer
  but leaves the over-broad grant live.
- **`C3` fails open on a database fault.** A DB blip logging every operator out mid-incident was judged
  worse than a one-request window; nothing is stamped, so it retries immediately. Reasonable people
  would argue for fail-closed.
- **OIDC-claim admin is carried across revalidation** via an `eas:admin-src` marker, because
  re-deriving admin from the account flag alone would strip every OIDC admin within 60 s of signing in.
  Cost: revoking claim-granted admin means revoking it at the IdP.
- **`C7` binds only database accounts.** Config-declared accounts are never auto-bound — upserting one
  would mint a DB row shadowing its config entry. They stay unbound until an operator sets
  `OidcSubject` (now settable via `eas user set` and the admin API).
- **`C14` added a table** (`WebSessionRevocations`) plus migrations for both providers, rather than
  settling for "largely resolved by `C3`".
- **Load-bearing and easy to "simplify" away:** revocation compares against a new `eas:sid-iat` claim,
  **not** the ticket's `IssuedUtc`. Sliding renewal rewrites `IssuedUtc`, and `C3` sets `ShouldRenew`
  on every revalidation, so a cut-off compared against `IssuedUtc` would stop biting on the very next
  request. Tickets minted before this change carry no start stamp and count as older than any cut-off,
  so the first logout after upgrading invalidates them — fails closed, deliberately.
- **`WebUi.Tests` now references `ActiveSync.Backends.Local`**, not cosmetically: building an account
  snapshot resolves every declared user's roles through the provider registry, default content roles
  land on `local`, and an empty registry throws during `AccountResolver` construction. Relatedly, the
  `SessionRevalidationTests` helper pre-warms `AccountResolver` on purpose so a resolver fault surfaces
  as a failure instead of being swallowed by the hook's fail-open catch — **removing that line makes
  those tests silently vacuous.**
- **New finding `C22`** (bottom of Part 2): an admin-set or CLI-set password still does not stamp a
  revocation cut-off. The mechanism exists (`SyncStateService.RevokeSessionsBeforeAsync`); it needs one
  call per write site, but those files belong to items 9/14/18, so the worker left them alone.

---

## Next: item 9 — WebUi privilege & API hardening — **BLOCKED**

Do not start item 9 until the item 8 integration regression is resolved. Items 9–14 are Phase 2
(Security), none [LIVE]; Stalwart is not otherwise needed again until item 26. Note the intra-phase
ordering constraint: **item 13 (unified redaction) must be done before item 14** — build the one
redaction implementation, then apply it.

**Process lesson for future runs:** "not [LIVE]" means the *worker* need not run integration. It does
not mean the *orchestrator* shouldn't, and items 5–8 show why — a non-[LIVE] item with a schema or
auth-surface change can pass every check the protocol asks of it and still break 23 integration tests.
Consider running integration after any item that touches migrations, cookies, or auth, regardless of
its [LIVE] marking.

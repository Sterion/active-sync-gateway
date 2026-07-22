# Code review — implementation results

Running record of [`review-items.md`](review-items.md) items as they are completed.

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

## Item 8 — WebUi session & cookies — ⚠ **regressed integration, repaired in `1745c8f`**

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

**Repaired in `1745c8f` — harness only, `src/` untouched (verified by diffstat).**
`ActiveSync:WebUi:AllowInsecureCookies=true` in `GatewayFixture`, the documented local-http opt-out
used as intended; the `C2` fix stays at full strength. Integration is green again at **135 passed,
0 skipped** (134 restored plus the new test).

**Two things worth knowing about that repair:**
- **The config entry alone is not enough**, and getting this wrong keeps all 23 tests red with no clue
  why: `AddWebUi` reads `builder.Configuration.GetSection("ActiveSync")` **eagerly at DI time**, so an
  in-memory `ConfigureAppConfiguration` entry arrives too late to be seen. It needs a matching
  `builder.UseSetting(...)` inside `WithWebHostBuilder`, placed *before* the caller-overrides loop so a
  test can still flip it off. This is the same eager-read trap the fixture already documents for the
  Postgres connection string.
- **The blind-spot guard is a real integration test, not a fallback.**
  `SessionCookie_CarriesSecure_WhenTheHttpOptOutIsOff` spins up its own gateway via
  `CreateIsolatedFactory` with the production default (`AllowInsecureCookies=false`), logs in over a
  plain client with no cookie container, and asserts the raw `Set-Cookie` for `eas.webui=` carries
  `secure` and `httponly` — strictly stronger than the `WebUi.Tests` unit assertions, which check the
  DI option value rather than the emitted header. Confirmed non-vacuous: flipping the opt-out to `true`
  makes it fail with a sub-string-not-found, then reverted and re-confirmed green.

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

## Item 9 — WebUi privilege & API hardening

**Findings:** `C1` `C9` `C10` `C15` `C16` `C17` `C20` `C21`
**Commits:** `a4c05eb` (C1) · `1c2a109` (C9) · `dfe4c8e` (C10) · `b29e62c` (C15) · `1aeed28` (C16) ·
`298c6ec` (C17) · `7984fcf` (C20) · `10dd5e1` (C21) · `4ee8753` (integration-test alignment)

**Verification:** integrity 56/15/365/365/0 ✓ · cursor → item 10 ✓ · IDs in subjects ✓ · build
**0 warnings** ✓ · unit **Core 401, WebUi 63 (was 26), Server 115, Protocol 63 — 0 failed, 0 skipped**
✓ · integration **135 passed, 0 skipped** ✓ (run by the orchestrator despite this not being a [LIVE]
item — item 8's lesson applied)

**Notes:**
- **Breaking API changes, all in-repo clients updated:** `GET /admin/api/devices` and `/shares` now
  return `{ total, entries }`; `PUT /admin/api/users/{login}` and `disable`/`enable` return
  `{ user, warning }`; `POST /admin/api/shares` gains `knownUser`; portal
  `PUT /user/api/backends/{role}` refuses non-self-service keys.
- **`C1` is a deliberate functional narrowing.** `SelfServiceEditable` defaults to **false**, so today
  only caldav opts in four fields (`CalendarAttachments`, `SendInvitations`, `SharedCollections`,
  `TaskFolder`). carddav/imap/jmap/smtp/sieve expose **none** — the portal's backend form for those
  roles is now credentials-only. The alternative (denylisting connection-shaped fields) leaves every
  future plugin unsafe by default, which is the whole point of the finding. `C1` also **refuses**
  undescribed keys rather than silently dropping them as the review suggested — same safety, honest
  answer.
- **`C9` is coarser than the review asked for** — two outcomes, not five. The suggested
  `unreachable`/`tls-error`/`auth-error`/`timeout` set is still four distinguishable answers about an
  operator-chosen target, which is what makes a port scanner.
- **`C15`'s LIKE half is not a real defect, and the suggested fix would have caused it.** EF maps
  `string.Contains` to `instr`/`strpos`, verified against the generated SQL. Only the tail-floor half
  was fixed and proven; the two wildcard tests are labelled coverage.
- **`C16` deliberately does not 404/409 on unknown logins.** Pass-through auth means most users have no
  declared entry and a pre-emptive block is legitimate; the response carries `knownUser` instead.
- **`C21` skips `ProblemDetails` and the route constraint** — a route constraint would answer 404 for a
  bad role, which is the wrong shape for "that isn't a role".
- **⚠ The seven fix commits individually leave the integration suite red.** The alignment landed as a
  separate final commit, `4ee8753`. **Bisecting across `a4c05eb`..`4ee8753` requires `4ee8753`
  applied** — otherwise every probe in that range looks broken for the wrong reason.
- **New `tests/ActiveSync.WebUi.Tests/WebUiHost.cs`** drives the real `MapWebUi` pipeline over a
  TestServer — real cookie auth, real policies, real CSRF filter. It required
  `Microsoft.AspNetCore.Mvc.Testing` and a `Backends.Dav` project reference. **Item 39 (`C19`) should
  build on this rather than starting over.**
- **`C22` was deliberately not touched** — no `C17` fix made a `RevokeSessionsBeforeAsync` call natural
  (refusing a write is not the same as invalidating sessions). Its anchors are unmoved.

---

## Item 10 — EAS & server auth

**Findings:** `F23` `F24` `F21` `F46` `E3` `E14` `E26`
**Commits:** `3ef4042` (F23) · `66b1f6f` (F24) · `b6ee789` (F21) · `3bc1f84` (F46) · `d5ae1ae` (E3) ·
`bd10d22` (E14) · `2908ac6` (E26)

**Verification:** integrity 56/15/365/365/0 ✓ · cursor → item 11 ✓ · IDs in subjects ✓ · build
**0 warnings** ✓ · unit **Core 401, WebUi 63, Server 135 (was 115), Protocol 63 — 0 failed, 0 skipped**
✓ · integration **139 passed, 0 skipped** (was 135, +4 new) ✓ · no existing integration test needed
changing, so unlike item 9 every commit in this range is individually bisectable ✓

**Notes:**
- **Lockout risk was weighed explicitly in `F23`.** Two deliberate leniencies: an *absent* ack
  `<Status>` still counts as an acknowledgment (pre-14.0 clients send only the key), and an *empty*
  `<PolicyKey/>` is re-read as phase 1 rather than a bad ack. Strict MS-ASPROV readings would reject
  both; the failure mode of rejecting is a device that can **never** complete provisioning. The forged-
  key and failed-ack paths are still closed. `F24` similarly tolerates an absent `PolicyType` as the
  implied default — rejecting it is defensible and one line away.
- **⚠ New finding `F49`: a *successful* LongId Fetch cannot be encoded at all.** The handler emits
  `<itemoperations:LongId>`, but that tag exists only on the Search and ComposeMail code pages, so the
  encoder throws from `WriteResponseAsync` — **outside** the per-Fetch try/catch that exists precisely
  so one bad Fetch cannot 500 the request. Found while writing the `F46` reproducer, filed at the
  bottom of Part 2, not fixed inline. **Consequence for whoever lands `F49`:**
  `ItemOperationsFetchTests.LongIdFetch_ForARegisteredFolder_StillReachesTheStore` currently asserts
  the store call **and an expected `WbxmlException`** rather than Status 1 — that test must be updated.
- **`E3`'s trust direction is the load-bearing decision.** Honouring `X-Forwarded-For` unconditionally
  would be *worse than the bug* — a direct attacker could mint a fresh throttle key per attempt and
  never trip the counter. Trust is checked on the peer, never on the header. The new
  `Auth:TrustedProxies` defaults to empty, so deployed behaviour is byte-for-byte unchanged until an
  operator opts in. It is **not** a full ForwardedHeaders middleware: `Request.Scheme` is still handled
  by `UsePublicScheme`, and `RemoteIpAddress` is deliberately untouched so the `/cli` loopback gate is
  unaffected. Documented in `docs/configuration.md`.
- **`E3` hit the `W4` problem** — the fix changes `ClientKey`'s signature, so the reproducer could not
  compile against unmodified code. Handled correctly: the overload was added as a behaviour-preserving
  passthrough first, the four behind-a-proxy cases confirmed red on behaviour and the five
  must-not-change cases confirmed already green, then the real implementation landed.
- **`E26` is defence in depth, not a live leak**, and is labelled as such. Every endpoint today catches
  its own exceptions, so no throwing route exists in the production pipeline to reproduce against; the
  test builds the real two-layer pipeline instead. Its `UnhandledException_IsLogged` case is
  **coverage, not proof** — it passed against the stub too, because the dev page also logs.
- **`F21`'s live exposure is narrower than the finding implies.** The CalDAV store refuses folder ops
  and moves at the backend anyway, so today's shared-*calendar* grants were never actually writable
  through those paths. The real exposure is any store implementing `IReadOnlyCollectionSource` over a
  class that does support them — hence handler-level reproducers rather than integration ones.
- **New `EasHandlerHarness`** (reusable): drives any handler against in-memory SQLite state, a stub
  session and the production WBXML codec. Worth reaching for in later EAS items.
- **`W22` does not interact with `F23`/`F24`** — checked directly. Neither the 449 policy gate in
  `EasEndpoint` nor `ProvisionHandler` reads `EasVersion`, so a forged `MS-ASProtocolVersion` header
  cannot influence the Provision handshake. The two findings are genuinely independent.
- **`E14` made the two auth prologues agree; it did not merge them.** The duplication `E28` names is
  still there, so a third check added to EAS needs adding to Autodiscover too until item 20 lands.

---

## Item 11 — Plugin & settings privilege

**Findings:** `K38` `B22` `K39` (earlier session) · `K40` `K41` `K42` `K43` `K44` (this session)
**Commits:** `d23f0c8` (K38, B22) · `f8c38e5` (K39) · `838c2e6` (K40) · `8334d57` (K41) ·
`5498956` (K42) · `a687cf3` (K43) · `304e5da` (K44)

**Verification:** integrity 56/15/365/365/0 ✓ · cursor → item 12 ✓ · IDs in subjects ✓ · tree clean ✓ ·
build **0 warnings** ✓ · unit **Protocol 63, Core 420 (was 401), WebUi 67 (was 63), Server 135 — 0
failed, 0 skipped** ✓ · integration **139 passed, 0 skipped**, unchanged from the pre-item baseline ✓ ·
`stash@{0}` left untouched as instructed ✓

**Notes:**
- **Resumed item.** `K38`/`B22`/`K39` landed in an earlier session; this run covered `K40`–`K44` only.
- **Three operator-visible behaviour changes**, all recorded in the Part 2 notes: a plugin with a
  **non-public** entry point no longer loads (it was previously instantiated and handed the host's
  `IServiceCollection`); a plugin subdirectory with **no entry assembly** now aborts startup instead of
  warning, as `docs/plugins.md` already promised; a **relative** `Plugins:Directory` now resolves
  against `AppContext.BaseDirectory` rather than the CWD, so any deployment relying on the old
  behaviour must switch to an absolute path.
- **`K43` carries one deliberate exemption:** dot-prefixed directories are ignored. Kubernetes
  projected volumes create `..data`, which would otherwise brick every start of the volume-mount
  deployment the docs describe.
- **`K41` cut both ways and the guard test matters.** Host-first resolution is now restricted to
  assemblies that genuinely must unify (`ActiveSync.*`, `System.*`, `Microsoft.Extensions.*`,
  `mscorlib`/`netstandard`); everything else is plugin-folder-first with host fallback.
  `SharedContract_StillResolvesFromTheHost_EvenWhenThePluginShipsACopy` exists to catch an
  over-correction into plugin-first for everything. The worker also found that a plugin dropped
  **without `.deps.json`** resolved *nothing* through `AssemblyDependencyResolver`, so a private
  dependency could not load from the folder at all — hence the added simple-name folder probe.
- **`K44` landed hash pinning only, not signatures.** `ActiveSync:Plugins:Pins:<dirname>` is a SHA-256
  over every `*.dll` beneath the directory, checked before load, with `RequirePinned` to refuse
  unpinned plugins. Both live in the host-controlled `Plugins` section from `K38`, so a pin cannot be
  relaxed from the DB or the admin UI. The doc and the Part 2 note both state plainly that **a hash pin
  carries no identity and no revocation** — it is tamper-evidence, not trust.
- **Test infrastructure was touched and disclosed** (protocol step 9). `src/` changed in exactly one
  file, `PluginLoader.cs`; everything else is under `tests/`. `K41` needed a genuinely competing copy of
  a dependency, so a new fixture project **`tests/PluginPrivateLib`** is referenced by both the fixture
  plugin and `Core.Tests`; `K40` needed a non-public entry point, so the fixture gained an `internal`
  `IGatewayPlugin`. Both accommodations have guard tests that fail if they stop exercising the finding.
- **No new findings.** The "`Register` receives the live `IServiceCollection`" trust question the worker
  hit while rewording `K44` is already tracked as `K70`.

---

## Item 12 — Local CLI authentication

**Findings:** `L22` `L23` `L24` `L25` `L26` `L27` `K54`
**Commits:** `b880757` (L22) · `648fb8a` (L23) · `430a25e` (L24) · `4f7f4d7` (L25) · `b59a1ac` (L26) ·
`790aff9` (L27, K54) · `6ee769f` (new finding `L44`, docs only)

**Verification:** integrity 56/15/365/365/0 ✓ · cursor → item 13 ✓ · IDs in subjects ✓ · tree clean ✓ ·
build **0 warnings** ✓ · unit **Protocol 63, Core 420, WebUi 67, Server 145 (was 135) — 0 failed, 0
skipped** ✓ · integration **139 passed, 0 skipped**, unchanged ✓ · `stash@{0}` untouched ✓

Integration was worth running here even though the item is not [LIVE]: `L23` changes the `/cli` wire
format and `L22` changes when the endpoint answers 404. It came back clean.

**Notes:**
- **⚠ Four of the seven reds were compile failures, not behavioural reds** — `L22`, `L23`, `L24` and
  `L27`/`K54` all needed new API surface, so the "red" step demonstrated that the *existing* API could
  not express the safe behaviour. The worker disclosed this plainly rather than implying otherwise,
  which is the right call, but it is weaker proof than the protocol's step 6 asks for and weaker than
  item 10's handling of the same problem (`E3` added a behaviour-preserving passthrough overload first,
  so the reproducer could compile against unmodified code and go red on *behaviour*). Worth preferring
  the `E3` shape next time a finding changes a signature. `L24`'s second reproducer, `L25` and `L26`
  were true behavioural reds.
- **`L26`'s red cost a deliberately crashed test run** — the dependency cycle aborted the whole run
  with an uncatchable `StackOverflowException`, which is exactly the symptom the finding describes.
- **Six operator-visible behaviour changes**, all documented in `docs/cli.md`:
  1. `/cli` now refuses **everything** (404, plus a startup error) when the encryption key is missing
     *or* fails to load, instead of degrading to loopback-only auth. `eas` falls back to running
     in-process — still correct, just slower.
  2. A startup warning whenever `Encryption:AllowPlaintext` mode is active.
  3. **Breaking wire change** between `eas` and the gateway (sealed responses, nonce field). Both ship
     in the same image, so this only bites a hand-mixed deployment; a mismatched pair falls back to
     local execution.
  4. Envelopes are **single-use**, and the replay window is now asymmetric — 60 s back, 5 s forward.
     A client clock more than 5 s ahead of the gateway is refused.
  5. New log volume: one Information line per forwarded command, one Warning per refusal.
  6. Gateway log output no longer vanishes from the container log while an `eas` command runs.
- **`L24`'s redaction rule has a deliberate asymmetry:** option *values* after secret-named options and
  field paths are redacted, but command **verbs** keep their target — otherwise
  `device password alice` collapsed to `***` and the trail stopped recording *whose* password was
  disclosed, which is the one thing the audit exists for.
- **A data race was closed inside `L25`.** The reproducer exposed concurrent unsynchronized writes to
  the capture `StringWriter` throwing from `StringBuilder.ToString()`. Inside the finding's blast
  radius, so fixed in the same commit via synchronized wrappers and noted in Part 2.
- **⚠ New finding `L44`** (appended to Part 2): `ActiveSync.Cli/Program.cs` re-runs a command locally
  after a `TaskCanceledException` or a non-success status — but a timeout says nothing about whether the
  gateway already executed it, so a slow `eas purge user --yes` can run **twice**. The sealed-response
  failure path was given the correct treatment (report, exit 1); the timeout path was left alone as out
  of scope. Same family as item 26's replay-marker rule.
- **Item 43 (`L33`) still stands** — `L25` makes the console swap safe, it does not remove the need
  for it.

---

## Item 13 — Unified secret redaction

**Findings:** `S7` `L29` `L30` `E15` `E23` `C5` `K37` `K53`(→N/A) `L42` `L43`
**Commits:** `10d9481` (S7, K37 — build the redactor) · `3c77a3d` (L30) · `633b430` (L29) ·
`5770aee` (E15) · `b9b7ee5` (E23) · `5c9dffd` (C5) · `91c35ae` (L42) · `b7e6373` (L43) ·
`32b746d` (K53 — docs, marked N/A)

**Verification (orchestrator-run):** integrity 56/15/365/365/0, encoding 0 ✓ · cursor → item 14 ✓ ·
one commit per finding with ID in subject ✓ (S7+K37 clustered as the shared-redactor build, legitimate) ·
build **0 warnings** ✓ · unit **Protocol 63 · Core 441 (was 420) · WebUi 70 (was 67) · Server 151 (was 145)
— 0 failed, 0 skipped** ✓ · integration **139 / 0 skipped** ✓ (see the flakiness note — took a fresh
container to confirm).

**K53 N/A independently verified.** `git show ce6259c:…/SecretValue.cs` vs current: `TryUnseal` returns
four distinct error strings (bad prefix / bad base64 / too-short / wrong-key-or-tampered) and did so at
baseline — nothing was struck to dodge work. The one lump (wrong-key vs tampered) is a deliberate
anti-oracle choice. The N/A holds; a reader could argue that lump is what the finding meant by "coarse",
and it stays lumped on purpose.

**⚠ Integration flakiness — the bisect nearly lied, and this matters for every future [LIVE]/integration
verification.** First full-suite run at HEAD failed **3** DAV round-trip tests (Task, RecurringEvent,
Contact); a bisect showed the pre-item-13 commit clean at 139/0, which *looked* like item 13 owned a
regression — the exact item-8 shape. It did not. Two things broke the false conclusion: (1) a **second**
HEAD run failed a **different** subset (2 tests, Task now passing) — a real code regression fails the
*same* tests every time (item 8 failed the same 23); a shifting subset is environmental. (2) Every call
site of the new redaction API is **display/CLI/WebUi-DTO only** — `SettingKeys` sets a `Secret` *flag*,
`ConfigCommands`/`StartupSummary` are banners, `BackendsEndpoints`/`EndpointHelpers` are WebUi endpoints
the DAV tests never call — so there is **no path** by which it could affect device-to-device DAV sync.
Root cause: the canonical Stalwart had been **up 9 hours** and each full-suite run creates ~139 tests'
worth of DAV items; Stalwart indexes DAV asynchronously (AGENTS.md), so under accumulated state the
round-trip `WaitUntil` polls start timing out non-deterministically. The baseline 139/0 was an *earlier*
(less-degraded) run — run **order**, not code, confounded the bisect. `down -v` + fresh `up` → HEAD
passes **139/0**. **Lesson: start integration verification from a fresh backend (`down -v`), and treat
intermittent, subset-shifting DAV round-trip failures on a warm container as environmental — confirm with
a fresh-container run before blaming the item.**

**Notes (worker-flagged):**
- **Behaviour change — account APIs now mask secret-named role settings.** `GET /admin/api/users`,
  `/admin/api/users/{login}`, `/user/api/me` and `/admin/api/backends` return `***` for secret-named
  settings (ApiKey/Token/ClientSecret/…) instead of cleartext; a re-posted `***` on save resolves back to
  the stored value (round-trip guard test). The SPA's default-form flow is unaffected; any *other* client
  that read those values in the clear now gets the mask.
- **`IsSecretName` markers:** password/passwd/pwd/passphrase/secret/token/apikey/credential. It
  **excludes bare "key"** (to avoid `CertificateKeyPath`/identifier false positives), so a backend leaf
  literally named `Key` would not mask — a deliberate call open to revision.
- **`MailKitWireLogger.Redact` left unchanged** though S7 names it as one of the "four": it masks SASL
  secret byte-ranges via MailKit's `AuthenticationSecretDetector`, a distinct and correct mechanism.
  Folding byte-range SASL masking into a name-based classifier would be wrong. The three name/value
  maskers + the connection-string redactor are what got unified.
- **E15 username logging left as-is.** The finding also flags "logs every declared user (PII, into the DB
  sink)"; only the secret-settings leak was treated as in-scope — the `User: <login>` banner lines are
  deliberate operator visibility of the configured fleet. Suppressing them was the alternative.
- **`L42`/`L43` are coverage, not proof**, and labelled as such. `L42` (zero the master key in a
  `finally` on a failed stdin read) — the key is a local `byte[]` with no external handle, so the zeroing
  is not observable. `L43` (explicit `IsGatewayPassword` flag replacing the `!Key.Contains(':')`
  heuristic) — behaviour is unchanged because the old heuristic was correct for every current key, so it
  cannot go red on behaviour.
- **S7+K37** is a structural consolidation with no pre-existing behaviour to red-test; proven by the new
  `SecretRedactionTests` (21 cases). L29/L30/E15/E23/C5 are all red-first reproducers that leaked the
  secret before the fix.
- **No new findings filed.**

---

## Item 14 — Credential & key handling

**Findings:** `K56` `B4` `B5` `B18` `B19` `C6` `K9` `K14` `K45` `K46` `K47`
**Commits:** `d139295` (K56) · `a5bd145` (B19) · `87ac9cd` (B18) · `0be0e0b` (C6) · `9df6ca1` (B4) ·
`4e47960` (B5) · `c886584` (K9) · `43b9618` (K14) · `41a476d` (K45) · `e1ad54b` (K46) · `bbacce5` (K47)

**Verification (orchestrator-run):** integrity 56/15/365/365/0, encoding 0 ✓ · cursor → item 15 ✓ ·
one commit per finding with ID in subject ✓ · build **0 warnings** ✓ · unit **Protocol 63 · Core 452
(was 441) · WebUi 70 · Server 156 (was 151) — 0 failed, 0 skipped** ✓ · integration **139 / 0 skipped**
on a **fresh** container (`down -v` first, per item 13's lesson) ✓.

**K45/K46 breaking-change blast radius independently checked and cleared.** `GatewayFixture.TestEncryptionKey`
= `AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=`, which is base64-of-32-bytes → used as the **raw** key,
never PBKDF2-stretched. So the new per-deployment salt (`K45`) and the hard 8-char passphrase floor
(`K46`) cannot touch the fixture, and integration confirms it: 139/0. Any deployment using a base64-32
key is likewise unaffected; only passphrase-derived keys change.

**K56 contract change verified.** `d139295` overrides `PrintMembers` on the `BackendCredentials` record to
render `Password` as `***`, with the mask token as a **local literal** — Contracts depends only on
Protocol + MS.Extensions abstractions and cannot reference `Core.Administration.SecretRedaction` across the
dependency rule (AGENTS.md). Nested records (`ResolvedRole`/`BackendConnectionContext`) inherit the
redaction via their own synthesized `ToString()` calling this one. Correct boundary call.

**B19 verified as a real auth bypass, now closed.** `GatewayPasswordHasher.Verify(Hash(""), "")` returned
true, so an account with an empty gateway `Password` authenticated locally against a hash of the empty
string and the backend was never probed — a phone presenting an empty Basic-auth password got in.
`PrepareGatewayPassword` now refuses empty/whitespace; *clearing* the field (fall through to the backend)
stays the way to remove a gateway password.

**Notes (worker-flagged):**
- **Breaking / behaviour changes:**
  - **`K45` (opt-in, documented in `docs/configuration.md`).** New `Encryption:KeyDerivationSalt` makes
    the passphrase-derived key per-deployment. **Judgment call:** a *stored random* salt is impossible
    here because the gateway **and the slim `eas` client** both derive from config alone with no shared
    DB, so operator config is the only deterministic per-deployment entropy. The fixed app salt is kept
    for the *unset* case (no gratuitous break); residual — an unset-salt passphrase deployment still
    shares the app salt, mitigated by setting a unique salt or using a raw base64 key. Reasonable people
    could argue for forcing a uniform break instead.
  - **`K46`.** Passphrases < 8 chars are now **rejected at startup** (was warn-only). Rewrote the loader
    test and one `ActiveSyncOptionsValidatorTests` case that used shorter samples.
  - **`C6`.** Plaintext gateway passwords < 8 chars are now rejected on **every** write surface (portal,
    `eas user password`, `eas hash-password`) via the shared `PrepareGatewayPassword` + a new
    `MinGatewayPasswordLength = 8`. Rewrote 2 unit tests that used shorter samples.
- **Coverage-not-proof (struck on the strength of the fix), all labelled in test + note:** `K9` (zero the
  unencrypted PKCS#12 key buffers), `K14` (zero the TLS master key; also stops discarding the loader
  error), `K47` (feed the passphrase to PBKDF2 as a zeroed byte buffer). None has an external handle to
  observe the zeroing, so each carries a regression guard (sign/verify or derived-key-determinism)
  instead of a red-first assertion.
- **`K9` judgment call:** deliberately did **not** switch the serving cert to `EphemeralKeySet` —
  Kestrel/Schannel rejects an ephemeral-key server cert on Windows, and the store's certs are already
  disposed by callers. Zeroing the intermediate buffers was the bounded fix.
- **`B5` unified the two secret-sealing paths:** `eas config set` stored catalogue secrets in **plaintext**
  while the web UI sealed them; extracted `AccountSecretPolicy.PrepareCatalogueSecret` +
  `SettingKeys.IsCatalogueKey` so both surfaces seal identically (the CLI path also gained the `B4`
  broken-key refusal).
- **No new findings filed.**

---

## Item 15 — `Backends.Common` drops its Core reference (Phase 3 — Boundaries)

**Findings:** `S1` — High
**Commits:** `9bb7522` (S1)

**Verification (orchestrator-run):** integrity 56/15/365/365/0, encoding 0 ✓ · cursor → item 16 ✓ ·
one commit per finding with ID in subject ✓ · build **0 warnings** ✓ · unit **Protocol 63 · Core 453
(was 452) · WebUi 70 · Server 156 = 742 passed, 0 failed, 0 skipped** ✓ · integration **139 / 0 skipped**
on a **fresh** container (clean-volume restart in parallel with the worker; volumes recreated + all four
ports OK confirmed from the restart log) ✓.

Item 15 is **not [LIVE]**, but integration was run anyway — it is the first structural item, and a moved
type breaking the DI graph is exactly the silent-failure class the standing lesson names. Clean.

**`BackendConfigField`-already-in-Contracts claim independently verified.** The item text says "move
`WireLog.Payload`, `TransientRetry`, `BackendConfigField` → Contracts," but `git ls-tree ce6259c` shows
`BackendConfigField` already lived in `src/ActiveSync.Contracts/BackendConfigSchema.cs` at baseline. The
worker correctly treated that clause as already-satisfied and moved only the remaining two types — not a
skipped finding.

**Contracts dependency cleanliness re-checked.** After the move, `ActiveSync.Contracts.csproj` still
references only `ActiveSync.Protocol` + `Microsoft.Extensions.Configuration`/`.Binder`/
`.DependencyInjection.Abstractions` — NOT Core, Crypto or EF. The moved `WireLog` uses only BCL (pure
string truncation/sanitization, no `ILogger`), so the boundary in AGENTS.md § *Solution layout* holds.

**Notes (worker-flagged):**
- **Namespace change (pre-2.0 contract):** `WireLog` and `TransientRetry` move `ActiveSync.Core.*` →
  `ActiveSync.Contracts`. Additive for real plugins — they reference Contracts, not Core, so they could
  not use these types before and now can. Acceptable per Standing context (no external consumers).
- **`Backends.Common` now declares `Microsoft.Extensions.Logging.Abstractions` directly** (pinned
  `10.0.10` in `Directory.Packages.props`). `MailKitWireLogger`'s `ILogger` previously flowed in
  transitively via the dropped Core reference; the dependency is now explicit and correct.
- **The guard test is finding-specific, not the broad boundary suite.** `DependencyRuleTests
  .BackendsCommon_DoesNotReferenceCore` asserts the Common assembly's `GetReferencedAssemblies()` omits
  `ActiveSync.Core` — genuine red-green (RED against unmodified code). The wide plugin-boundary suite is
  deliberately item 44 (`S5`); the test comment points there so a future worker generalizes, not
  duplicates.
- **No new findings filed.**

---

## Item 16 — Crypto namespace realignment (Phase 3 — Boundaries)

**Findings:** `S2` (= `K49`) — one Medium defect under two IDs (cross-cutting `S2` and Area-K `K49`)
**Commits:** `ba73bc8` (S2, K49)

**Verification (orchestrator-run):** integrity 56/15/365/365/0, encoding 0 ✓ · cursor → item 17 ✓ ·
one commit with both IDs in subject ✓ (S2/K49 are the same defect — one commit is correct) · build
**0 warnings** ✓ · unit **Protocol 63 · Core 454 (was 453) · WebUi 70 · Server 156 = 743 passed, 0
failed, 0 skipped** ✓ · integration **139 / 0 skipped** on a **fresh** container ✓ · tree clean apart
from the orchestrator-owned results file, which is **not** in the worker's commit (worker unstaged it) ✓.

**Independently confirmed the move actually landed:** `git grep '^namespace ActiveSync.Core'` over
`src/ActiveSync.Crypto/*.cs` at HEAD returns nothing — no Crypto source declares a `Core.*` namespace
any more. `SecretValue`, `EncryptionKeyLoader` (were `ActiveSync.Core.Security`) and `EncryptionOptions`
(was `ActiveSync.Core.Options`) now sit under `ActiveSync.Crypto`, matching the assembly's `RootNamespace`
and the already-correct `LocalCliEnvelope`/`LocalCliResult`.

**Notes (worker-flagged):**
- **Source-level breaking change (pre-2.0 contract).** Three published Crypto types change namespace
  `ActiveSync.Core.*` → `ActiveSync.Crypto`. No runtime behaviour change. Acceptable per Standing
  context; the item explicitly says to do it before the package has external consumers. The break was
  itself the diagnostic — `ActiveSync.Cli` (BCL-only, references only Crypto) stopped compiling with its
  `using ActiveSync.Core.*`, which is exactly the invisibility the finding describes.
- **Guard test is a genuine reproducer**, not coverage: `DependencyRuleTests
  .Crypto_TypesDeclareTheCryptoNamespace` asserts every exported Crypto type declares a namespace under
  `ActiveSync.Crypto`; RED against unmodified code, naming the three offenders, then green.
- **Judgment call:** flat `ActiveSync.Crypto` namespace chosen over preserving `.Security`/`.Options`
  subdivisions under Crypto — consistent with the assembly root and the sibling CLI-envelope types
  already there. A reviewer could reasonably have kept the subdivision.
- **No new findings filed.**

---

## Item 17 — Contracts surface (Phase 3 — Boundaries) · **breaking, one major bump**

**Findings:** `K57` `K58` `K59` `K61` `K62` `K64` `K67` `K69` `K71`
**Commits:** `5d16682` (K69) · `a7f85cb` (K62, K64) · `174de61` (K67) · `8e02a81` (K59) · `3890c4a` (K57) ·
`dba9dc2` (K61) · `5024231` (K58) · `002ec53` (K71) · `e997d23` (docs/plugins sync) · `9557d1a`
(AGENTS.md doc-sync — orchestrator-requested, see below)

**Verification (orchestrator-run):** integrity 56/15/365/365/0, encoding 0 ✓ · cursor → item 18 ✓ · one
commit per finding with ID in subject ✓ (K62+K64 clustered — both touch `SharedCollection.cs`, legitimate) ·
**clean `-t:Rebuild` 0 warnings** ✓ (an incremental build would have lied — see the VSTHRD003 note) · unit
**Protocol 63 · Core 476 (was 454) · WebUi 70 · Server 156 = 765 passed, 0 failed, 0 skipped** ✓ ·
integration **139 / 0 skipped** on a **fresh** container ✓. This was the highest DI/pipeline-risk item in
the range (async `CreateConnection`, `IContentStore` split, unsealed `BackendException`) — the green
integration run is the decisive check that the pipeline still wires up.

**K57 host-only claim independently verified — and a doc gap it left, closed.** `git grep`: `IBackendSession`
is implemented **only** by `CompositeBackendSession` (Core); no provider implements it, so moving it +
`IBackendSessionFactory`/`BackendSessionInfo` to `ActiveSync.Core.Backend` is correct — they are the host's
composite-session aggregation and cache, never plugin-facing. **AGENTS.md § *Solution layout* still listed
`IBackendSession` as a Contracts type** (it documented the leak as if intended); the worker had synced
`docs/plugins.md` but missed AGENTS.md. This is incomplete doc-sync *within K57's scope*, not a new finding,
so the worker was sent back to finish it — `9557d1a` replaces `IBackendSession` with `IBackendConnection`
(what a plugin actually implements) in the enumeration and names the composite session/factory as
host-only-in-Core. Docs-only, so no re-run needed against the already-verified `e997d23` code state.

**The clustering (`a7f85cb` = K62+K64) is legitimate.** Both are the same `SharedCollection.Parse`/`Validate`
fail-open→fail-closed security fix in one file; the "each tight cluster" rule applies. Spot-checked both land:
Parse now returns read-only unless an explicit `|rw`, and the cross-host guard fails closed on an unparseable
`baseUrl`. `ContractVersion.cs` (K69) and the `IItemMoveOperations`/`IFolderOperations` split out of
`IContentStore` (K58) confirmed present in Contracts.

**Notes (worker-flagged):**
- **Intentional breaking Contracts changes (major bump, per Standing context):** `IBackendProvider
  .CreateConnection` → `CreateConnectionAsync(context, ct)`; `IContentStore` loses its move/folder-mutation
  members (now optional `IItemMoveOperations` / `IFolderOperations` capabilities); `DeleteItemAsync` reorders
  to `(…, bool permanent, CancellationToken ct)`; `IBackendSession`/factory moved to Core; `BackendException`
  unsealed and `BackendItemNotFoundException` now derives from it. All host-internal callers updated; **no
  EAS-client-visible behaviour change** — MoveItems/Folder* statuses preserved (handlers now guard on the
  capability and return the same status the old throw produced).
- **VSTHRD003 / async-lazy, load-bearing for future verifiers.** K61 makes the shared cached-session build an
  async `Lazy<Task<…>>`; the VS Threading analyzers reject that idiom, and the resulting **VSTHRD003 warning
  was masked by incremental builds** — it only appears on `-t:Rebuild`. The worker cleared it by routing the
  await through a single justified-suppression block in `BackendSessionFactory` (no `Microsoft.VisualStudio
  .Threading` runtime dep added to the packed Core assembly; the idiom is safe here — no sync context,
  `ConfigureAwait(false)` throughout). **Verify this item, and anything touching that factory, with a clean
  rebuild, not an incremental one.**
- **K61 uses `CancellationToken.None` for the shared cached-session build** (not the request ct), so one
  request's cancellation cannot fault the session other requests await — matching the prior synchronous,
  uncancellable build. The `ct` on `CreateConnectionAsync` is contract surface for future providers.
- **K71 resolved by documentation, not an `ISecretProtector` abstraction** (the finding sanctions the doc
  route). Traced: provider `Secret` settings fields are stored/bound **plaintext**; only the role credential
  is sealed/unsealed by the host and delivered plaintext via `Credentials`. A plugin never needs a sealing
  primitive for the standard flow and is not handed the master key, so an `ISecretProtector` on the plugin
  surface would be unused API. Judgment call open to revision if a future plugin needs to seal its own config.
- **K57's proof is a relocation guard, not a red-first behavioural reproducer** (`ContractSurfaceTests
  .HostOnlySessionTypes_AreNotOnTheContractsSurface` / `HostSessionTypes_LiveInCore`) — the established
  items 15/16 structural pattern. Everything else (K59/K61/K62/K64/K67/K69) is red-first; K58's four members
  asserted absent from `IContentStore`, red before.
- **No new findings filed.**

---

## Item 18 — WebUi → Core services (Phase 3 — Boundaries)

**Findings:** `S3` (= `C18`) — one Medium, two IDs (cross-cutting `S3` and WebUi-area `C18` are the same defect: two write paths to the device/share/log tables)
**Commits:** `3bb1a4f` (extract the three Core services + `AdminIdentifiers` + DI extension) · `71071ee`
(rewire WebUi endpoints) · `a89c522` (rewire CLI + queue strike) — all carry `S3/C18` in the subject.

**Verification (orchestrator-run):** integrity 56/15/365/365/0, encoding 0 ✓ · cursor → item 19 ✓ ·
finding ID in every subject ✓ (one finding across three commits, split by surface — extract / WebUi /
CLI — a legitimate tight cluster, not an item batched into one) · **clean `-t:Rebuild` 0 warnings** ✓ ·
unit **Protocol 63 · Core 487 (was 476) · WebUi 70 · Server 157 (was 156) = 777 passed, 0 failed, 0
skipped** ✓ · integration **139 / 0 skipped** on a **fresh** container (clean-volume restart run in
parallel with the worker; all four ports OK + Healthy confirmed before verifying) ✓.

Item 18 is **not [LIVE]**, but integration was run anyway — it rewires the admin write path and the
share store, and shares feed shared-calendar grants into session building, so a DI-graph or write-path
break is exactly the silent-failure class the standing lesson names. Clean: no regression, no test
needed changing.

**Independently confirmed the extraction actually landed and both surfaces share it.** `3bb1a4f` adds
`DeviceAdminService`/`ShareAdminService`/`LogQueryService` (+ `AdminIdentifiers`) under
`ActiveSync.Core.Administration`; `71071ee` deletes the WebUi-local `AdminIdentifiers` and routes
`DevicesEndpoints`/`SharesEndpoints`/`LogsEndpoints`/`StateEndpoints` through them; `a89c522` routes the
`eas device`/`share`/`block`/`unblock`/`purge`/`logs` commands the same way. So the C16 identifier
checks now run once, on both surfaces.

**Notes (worker-flagged):**
- **`S3`/`C18` is a structural finding struck on the strength of the refactor**, not a red-first
  reproducer — two paths to one table has no runtime symptom. Proof is (a) the unchanged HTTP-level and
  CLI suites passing against the rewired code, and (b) `AdminServicesTests` (11 cases) as **coverage**
  pinning the extracted behaviour; both labelled coverage-not-proof in the test file. This matches the
  items 15/16/17 structural-guard pattern.
- **One genuine red-first rides along, and it is a real behaviour change: `eas share add` now rejects
  `..` segments and control-character hrefs.** Unifying the CLI onto `AdminIdentifiers.HrefProblem`
  strengthened the CLI, which previously checked only `StartsWith('/')` and accepted `/dav/../evil/`
  (exit 0). This is C16 parity — the intended consequence of one validated path — and the only
  observable behaviour change; every other CLI/WebUi output is byte-preserved.
- **Judgment call — pagination clamp stayed at the HTTP seam, not in the service.** `ListAsync`/
  `ListSharesAsync` take nullable `skip`/`take` (null = unbounded); the WebUi endpoints keep the C10
  `Math.Clamp(…,1,500)`, the CLI passes `take: null` to preserve its unbounded terminal listing. Folding
  the clamp into the service would have silently capped CLI listings at 500 — a regression — so the
  duplicated *query* moved while the clamp stayed an HTTP-pagination concern. Open to revision.
- **Judgment call — scope held to the four endpoint files + their CLI mirrors.**
  `DevicePasswordCommand`/`FoldersCommand`/`ItemsCommand`/`ShowCommand`/`UsersCommand` stay on direct EF:
  they are CLI-only reads with no WebUi counterpart, so outside the "two paths to the same table" defect.
- **Judgment call — `AddAdministrationServices()` mirrored into the `WebUiHost` test harness** rather
  than folded into `AddWebUi()`, keeping layering clean (these are Core services the CLI uses without
  WebUi). A legitimate harness accommodation — no source assertion was weakened; the endpoints genuinely
  require the services now.
- **No new findings filed.**

---

## Item 19 — Structural guardrails (Phase 3 — Boundaries)

**Findings:** `S4` `S8` — one Low, one Nit (pure relocations)
**Commits:** `d4f1ed3` (S4 — `CollectionDiff` → Protocol, `MergedFreeBusy` → Contracts) · `fef4511`
(S8 — `ServerCertificateValidator` → `Backends.Common` namespace) — each carries its ID in the subject.

**Verification (orchestrator-run):** integrity 56/15/365/365/0, encoding 0 ✓ · cursor → item 20 ✓ ·
one commit per finding with ID in subject ✓ · **clean `-t:Rebuild` 0 warnings** ✓ · unit **Protocol 63 ·
Core 490 (was 487; +3 structural guards) · WebUi 70 · Server 157 = 780 passed, 0 failed, 0 skipped** ✓ ·
integration **139 / 0 skipped** on a **fresh** container (clean-volume restart in parallel with the
worker; all four ports OK + Healthy before verifying) ✓.

Not [LIVE], but integration was run: a cross-assembly type move can break the DI graph — the standing
structural-item lesson. Clean, no regression, no test needed changing.

**Moves independently confirmed at HEAD:** `CollectionDiff` (+ `ItemChange`/`CollectionChanges`) is in
`src/ActiveSync.Protocol/Sync/CollectionDiff.cs` under `namespace ActiveSync.Protocol.Sync`;
`MergedFreeBusy` is in `src/ActiveSync.Contracts/MergedFreeBusy.cs`; `ServerCertificateValidator` is in
`src/ActiveSync.Backends.Common/ServerCertificateValidator.cs` under `namespace
ActiveSync.Backends.Common`. **`ActiveSync.Protocol.csproj` still has 0 `ProjectReference`s** — the
dependency-rule hard gate ("Protocol depends on NOTHING project-wise") is intact after the move.

**Judgment call — accepted, and it is the headline of this item.** `S4` says move **both** to
`ActiveSync.Protocol`. `CollectionDiff` went there (pure BCL logic). `MergedFreeBusy` went to
**Contracts**, not Protocol, because it consumes `BusyPeriod` — a plugin-capability model in Contracts
(`IFreeBusySource`) — and Protocol references nothing project-wise (verified: 0 ProjectReferences), so it
literally cannot see `BusyPeriod`. The only ways to satisfy the finding's *literal* target would be to
relocate `BusyPeriod` down into Protocol — a **breaking plugin-contract change owned by item 17, already
COMPLETE and shipped as the one major bump** — which item 19 must not reopen. Contracts is the lowest
layer the dependency rule permits and where `BusyPeriod` already lives, so it honours the finding's stated
intent (out of Core, into a fuzzable leaf assembly) without violating the rule. **Orchestrator ruling:
this is a resolvable placement with exactly one architecturally-valid target, not a doc-vs-finding
contradiction requiring a stop** — a human deciding it would reach the same answer. Fully disclosed in the
`S4` commit, the queue note, and the finding entry.

**S8 scope call:** only the genuinely-anomalous bare-`ActiveSync.Backends` file was consolidated;
`ActiveSync.Backends.Converters` was deliberately left as a coherent purpose-grouping. The finding names
`ServerCertificateValidator` as "the odd one out," so two sanctioned namespaces (not one) satisfy it;
collapsing `.Converters` too would touch every converter consumer across seven assemblies for a Nit.

**Both guard tests are genuine red-first structural proofs, not coverage** —
`DependencyRuleTests.{MergedFreeBusy_MovedFromCoreToContracts, CollectionDiff_MovedFromCoreToProtocol,
BackendsCommon_TypesUseCoherentNamespaces}` each failed against unmodified source (types still in their
old namespace) then passed after the move.

**Process note (worker-flagged, verified clean):** the worker's first `git add -A` swept the
orchestrator-owned `review-results.md` into the `S4` commit; it soft-reset, unstaged the file, and
re-committed with explicit paths. Independently confirmed: neither `d4f1ed3` nor `fef4511` contains
`review-results.md`, it is modified-but-unstaged in the tree, and the item-18 entry is intact.

**No new findings filed.**

---

## Item 20 — Decompositions (Phase 3 — Boundaries) · large structural item

**Findings:** `F-decomp` `A33` `E27` `E28` — all Nit decompositions, no intended behaviour change
**Commits:** `f29bef1` (F-decomp — `SyncHandler` → 6 partials + `SyncCollectionOptions` +
`ClientCommandLedger`) · `6b54315` (A33 — seal `SyncStateService`, extract 4 stores) · `abc7b50` (E27 —
`RunServerAsync` → 4 phases) · `1ef0489` (E28 — `EndpointAuth` prologue) — one per finding, ID in subject.

**Verification (orchestrator-run):** integrity 56/15/365/365/0, encoding 0 ✓ · cursor → item 21 ✓ ·
one commit per finding with ID in subject ✓ · **clean `-t:Rebuild` 0 warnings** ✓ · unit **Protocol 63 ·
Core 490 · WebUi 70 · Server 157 = 780 passed, 0 failed, 0 skipped** ✓ · integration **139 / 0 skipped**
on a **fresh** container (clean-volume restart in parallel; Healthy + all four ports before verifying) ✓.

**This was the highest-risk item in the range** — it invalidates line anchors wholesale and `E28` touches
the EAS/Autodiscover auth path — so integration was mandatory and is the decisive check. Clean.

**Structure independently confirmed at HEAD:** `SyncHandler.{Cache,ClientCommands,Collection,LongPoll,
ServerCommands}.cs` partials exist alongside `SyncHandler.cs`; `SyncCollectionOptions.cs` +
`ClientCommandLedger.cs` are extracted; `SyncStateService` is `public sealed class` delegating to the four
new `DeviceStore`/`FolderRegistry`/`CollectionStateStore`/`DavItemMap`; `EndpointAuth.cs` is present.

**Line endings checked and cleared.** New `.cs` files are LF-only *in the git blob* — but that is correct
normalization, not a house-style miss: `.gitattributes` is `* text=auto eol=crlf`, so git stores every
`.cs` (including the pre-existing `EasEndpoint.cs`) as LF internally and checks it out CRLF on Windows.
`git check-attr` confirms `eol: crlf` applies to the new files. (The worker's "committed blobs are CRLF"
phrasing was imprecise; the outcome is right. Minor cosmetic residue: the worker's own working-tree copies
are still `w/lf` until a renormalize/checkout — harmless, self-healing, and never committed as CRLF-wrong.)

**All four are coverage-proven, not red-first — the correct mode for a pure refactor** (no behaviour to
red-test). Proof is the full unit suite + 139-test integration staying green across the decomposition,
plus the new `DisabledAccount_IsAlsoRefusedByAutodiscover` integration test for `E28`'s shared refusal.

**Two judgment calls, both accepted:**
- **`A33` landed as a composing facade, not four injected services.** `SyncStateService` is
  `EasContext.State` — used by every handler, WebUi and tests, with static snapshot readers called across
  production and tests. A literal "replace with four services" split would ripple through the whole
  handler layer for a Nit. The worker extracted four named single-responsibility stores and kept
  `SyncStateService` as the sealed scoped facade delegating to them, over the one shared scoped
  `SyncDbContext` — separating responsibilities while leaving every call site and the DI registration
  untouched. OOF + `PersistAsync` stay on the facade (the finding named no store for them); the three
  static readers keep their call sites via thin forwarders. Could reasonably have gone the full-split
  way; minimal blast radius chosen. **Orchestrator ruling: in scope** — the finding's core ask (6
  responsibilities → separated) is met; the facade is a smaller-radius realization, not a dodge.
- **`E28` did not fold EAS wholesale into `TryAuthorizeAsync`.** Doing so would reorder EAS's pre-auth
  metrics label, its query/device-id parsing (400-vs-429 precedence) and the pass-through provisioner
  (which must run *between* auth and the block check) — all real behaviour changes. The worker unified
  only the specific disabled/blocked decision that `E14` left drifting (`CheckLoginRefusalAsync`, now
  shared by both endpoints) and had Autodiscover — which has no interleaving — adopt the full method.
  **Orchestrator ruling: correct** — `E28`'s defect was the *drift* item 10's `E14` explicitly left open
  ("a third check added to EAS needs adding to Autodiscover too until item 20 lands"), and that exact gap
  is now closed. Merging more would have exceeded the finding into behaviour change.

**One observable-but-non-behavioural change:** Autodiscover's unconfigured-503 **log line** wording moved
from "Autodiscover request refused…" to "Request refused…" (now emitted by the shared prologue). Status
codes, response bodies and ordering unchanged.

**No new findings filed.**

---
--- END OF PHASE 3 (Boundaries). Items 21–31 are Phase 4 (Correctness). ---
---

# PHASE 4 — Correctness (items 21–31)

## Item 21 — Backend session lifetime
**Findings:** `A2` `A11` `A12` `A13` `A24` `A28` `D27` `D28` `K60`
**Commits:** `686cc1e` (K60) · `81dfe48` (A12) · `13f0f93` (A2) · `90c7a00` (A11) · `063a709` (A28) ·
`8d6a67c` (D28) · `faef6cc` (A13) · `bc0dff5` (A24) · `f3803a3` (D27)
**Verification:** integrity items=56 · live=15 · assigned=365 · unique=365 · dupes=0 · encoding=0 ✓ ·
cursor → item 22 ✓ · one commit per finding, ID in each subject ✓ · build 0 warnings / 0 errors ✓ ·
unit **Protocol 63 · Core 500 · WebUi 70 · Server 157**, 0 skipped ✓ · integration **139 passed, 0
skipped** (full suite, fresh Stalwart container, canonical ports) ✓
**Notes:**
- **Session contract change (durable — every future session-lifetime item must know this).**
  `IBackendSessionFactory.GetSessionAsync` now hands out a **refcounted lease**, not the session itself.
  `IBackendSession.DisposeAsync` now means "**release one lease**," not "tear down" — the composite is
  disposed only on the last release, and idle eviction cannot tear down a session an active request holds
  (that was the `A2` bug). `EasEndpoint` `await using`s its lease; **any new caller of `GetSessionAsync`
  must release its lease or the connection leaks and is never torn down.**
- **Disposal now aggregates.** `BackendConnection.DisposeAsync` (`K60`) and
  `CompositeBackendSession.DisposeAsync` (`A12`) now throw `AggregateException` when a resource's disposal
  fails, instead of surfacing only the first inner exception, and dispose every resource even when one
  throws. A caller catching a specific disposal exception type would need to unwrap.
- **Two findings are coverage, not red-first proof.** `A24` (make `LastUsedUtc` thread-safe) and `D27`
  (CAS the IDLE watcher on password rotation) are memory-model / benign races with no deterministic
  trigger; both struck on the strength of the fix, exercised only indirectly by the `A2`/`A13` factory
  tests. No dedicated failing reproducer exists for either — a future regression in these two would not
  be caught by a targeted test.
- **Judgment call (`K60`).** The finding offered typing the owned-resources bag as
  `IReadOnlyList<IAsyncDisposable>` *or* auto-disposing disposable stores. The resources are a genuine
  mix (`ImapSession` is `IAsyncDisposable`; `WebDavClient`/`JmapClient` are `IDisposable`-only), so no
  single interface types all seven call sites without reworking those clients (out of scope). The worker
  kept the `object`-typed bag + the auto-dispose alternative (plus idempotence and aggregation). The
  "untyped bag silently no-ops a non-disposable resource" wart is therefore **not** closed — reasonable,
  but a reviewer could have insisted on retyping the clients instead.

---

## Item 22 — State layer correctness
**Findings:** `A1` `A5` `A6` `A7` `A8` `A9` `A10` `A17` `A18` `A22`
**Commits:** `b73dabf` (A1) · `6e0ed6b` (A5) · `d72c454` (A6) · `577ecd3` (A7) · `f36887b` (A8) ·
`860b37e` (A9) · `b2a92f2` (A10) · `33fca47` (A17) · `c336fd1` (A18) · `d59a730` (A22)
**Verification:** integrity items=56 · live=15 · assigned=365 · unique=365 · dupes=0 · encoding=0 ✓ ·
cursor → item 23 ✓ · one commit per finding, ID in each subject ✓ · tree clean ✓ · build 0 warnings /
0 errors ✓ · **migration lockstep OK** — Sqlite/Npgsql name lists agree in order (item-44 invariant) ✓ ·
unit **Protocol 63 · Core 510 · WebUi 70 · Server 157** = 800, 0 skipped ✓ · integration **139 passed, 0
skipped** (full suite, fresh Stalwart container 57 s old, migration applied cleanly via `MigrateAsync`) ✓
**Notes:**
- **Schema migration `AddDeviceConcurrencyToken` (both providers).** Adds a nullable-default (zero-GUID)
  `ConcurrencyToken` column to `Devices`; rolls forward at startup, no data loss, existing rows take the
  default. Column-add only — does **not** force a re-sync on upgrade. Independently re-verified the two
  provider migration sets agree on their ordered name lists (timestamps legitimately differ per provider).
- **Transaction policy decided (`A10`) — durable architectural decision.** Per-collection Sync commits
  stay **independent**: no transaction spans a multi-collection Sync. This is safe because the SyncKey
  N−1 replay design already makes each collection an atomic unit — a collection whose commit doesn't land
  keeps its old key and the client reconciles it next round. The only thing moved off the request context
  is the self-contained DAV id allocation (`DavItemMap.GetOrAddDavItemIdAsync`), now on its own
  short-lived `ISyncDbContextFactory` context so it never flushes / re-read-poisons a half-mutated
  `CollectionState`. A spanning transaction was rejected because it breaks the unique-violation-and-reread
  pattern under Postgres.
- **Client-visible behaviour change.** A lost `FolderSyncKey` race now answers **FolderSync Status 9**
  (client restarts the hierarchy from 0) instead of a silent lost update / potential 500 — new
  `Device.ConcurrencyToken` (`A6`) drives it, wired through FolderSync and FolderCreate/Delete/Update.
- **Internal API change.** `ValidateSyncKeyAsync` now returns `CollectionState?` (null on Invalid) instead
  of a detached synthesized entity (`A17`). No external consumers; the one production caller and tests
  updated.
- **All 10 red-first proven — no coverage-not-proof tests.** `A22`/`A1`/`A9` races are made deterministic
  via a new `SaveChangesInterceptor` fault-injector helper (`StateTestSupport.cs`).
- **Judgment call (`A10` scope).** Own-context isolation applied to the *writer* only
  (`GetOrAddDavItemIdAsync`); the reader `ResolveDavHrefAsync`'s `AsNoTracking`/N+1 was deliberately left
  to item 23 (`A3`). Reads flush nothing, so this is safe — but a reviewer could reasonably have folded
  the reader in here.
- **Environment note (not a repo change).** Worker installed `dotnet-ef` 10.0.10 as a **global** tool
  (outside the repo) to scaffold the migration; a stray empty local `dotnet-tools.json` was removed
  (amended out of the `A6` commit). Tree independently confirmed clean.

---

## Item 23 — State layer performance & retention
**Findings:** `A3` `A4` `A19` `A34` (N/A) `A35`
**Commits:** `9a4d25b` (A3) · `6561912` (A4) · `c33d286` (A19) · `20cf336` (A34, N/A doc) ·
`c65556e` (A35) · `f94b84c` (anchor fix — repoints F12/F22/F25 to the renamed snapshot columns)
**Verification:** integrity items=56 · live=15 · assigned=365 · unique=365 · dupes=0 · encoding=0 ✓ ·
definition-adequacy orphan list empty ✓ · cursor → item 24 ✓ · one commit per finding, ID in each
subject ✓ · tree clean ✓ · build 0 warnings / 0 errors ✓ · **migration lockstep OK** (both new
migrations present in Sqlite + Npgsql, name lists agree) ✓ · unit **Protocol 63 · Core 514 · WebUi 70 ·
Server 158** = 805, 0 skipped ✓ · integration **139 passed, 0 skipped** (full suite, fresh Stalwart
container 58 s old, both migrations applied cleanly via `MigrateAsync`) ✓
**Notes:**
- **⚠ ONE-TIME FULL RESYNC ON UPGRADE (`A4`) — the most important note here.** The
  `CompressCollectionSnapshots` migration **drops** the old `SnapshotJson`/`PreviousSnapshotJson` TEXT
  columns and adds `SnapshotCompressed`/`PreviousSnapshotCompressed` (`byte[]?`, gzip) — **drop+add, no
  data migration**. Every device therefore does one full resync on its next Sync after upgrade. Accepted
  per Standing context (not deployed; snapshots are regenerable derived state, not user data) — but this
  is invisible in a diff and would surprise anyone who later ships this to a live fleet.
- **Two EF migrations, both providers:** `CompressCollectionSnapshots` and `AddUserFolderDeletedUtc`.
  Re-verified the ordered name lists agree across providers.
- **New live config knob** `ActiveSync:Eas:FolderRetentionDays` (default **30**; 0 disables). Folders
  soft-deleted (`UserFolder.DeletedUtc`, new column) more than N days ago are now hard-reclaimed by a new
  `FolderRetentionService` sweep, along with their dependent DAV/collection/device rows (`A35` — this
  closes an unbounded-growth leak). `docs/configuration.md` + `SettingKeys` + validator updated.
- **`A34` marked N/A (legitimately, not skipped).** `A4`'s `SnapshotCodec.Decompress` became the single
  shared snapshot reader both `PeekSyncKeyAsync` branches now use — which is exactly the deduplication
  `A34` asked for — so there is nothing left to do. Struck with backticks preserved + reason.
- **Coverage-not-proof (`A4`, `A35`).** Both hinge on new/renamed APIs, so their "red" was compile-absence
  rather than a reproduced wrong output. `A4`'s transparency is additionally backed by the entire
  pre-existing snapshot suite passing unchanged; `A35` is a brand-new mechanism (retention sweep) with no
  prior behaviour to red-first. Neither is a false record, but neither reproduced a pre-fix failing symptom.
- **Judgment calls.** (1) `A4`: chose gzip-to-`byte[]` (finding option a) over table normalization (b) —
  lower blast radius. (2) `A19`: deliberately left `CommitFolderHierarchyAsync`'s load **tracked** because
  item 22's `A7` fix rewrote it to mutate rows in place, so tracking is now required — the finding's
  premise for that specific line no longer holds (documented in the A19 detail entry). Both defensible; a
  reviewer could have pushed on either.

---

## Next: item 24 — Config validation unification (Phase 4 — Correctness)

Items 21–23 verified and recorded above. Cursor is at **item 24** (`B1` `B9` `B10` `B11` `B12` `B14`
`B24` `B25` `B26` `A14`) — `B1`: a CLI-settable value passes validation, persists, runs, then blocks the
next startup; one fix collapses most of the item by making the write path run the same validator startup
runs. Not [LIVE] and config-validation-focused; run the unit suite in full. Integration only if the diff
touches the request pipeline or startup path (a validator-unification change usually doesn't reach live).

Current green baseline: **integration 139 / 0 skipped** (fresh container), unit **Protocol 63 · Core 514 ·
WebUi 70 · Server 158** = 805.

**Standing lessons that carried this run (items 13–14):**
- **"Not [LIVE]" binds the worker, not the orchestrator.** A non-[LIVE] item with a schema/auth/contract
  change can pass every unit check and still break integration (items 5–8). Run integration after any
  structural item too — a moved type can break the DI graph.
- **Start integration from a fresh backend (`down -v`).** A warm, long-lived Stalwart flakes DAV
  round-trip tests non-deterministically under accumulated state (item 13's near-miss: a warm-container
  136/3 and 137/2 both went to 139/0 on a fresh container). A subset-shifting DAV failure is
  environmental until a fresh-container run says otherwise; a real regression fails the *same* tests every
  time.

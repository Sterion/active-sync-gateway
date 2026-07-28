# Round 3 — fix results

Maintained by the fix orchestrator (see [`../fix-review.md`](fix-review.md) § Recording results). One
entry per completed item; each pairs the worker's claim with the orchestrator's independent verification.

Baseline at `406b83f`: build 0 warnings · 1270 unit tests green, 0 skipped (Cli 16 · Protocol 91 ·
Core 771 · WebUi 101 · Server 291) · integration 8 skipped (no backend up).

## Item 1 — Lost server-to-client changes [LIVE]
**Findings:** `F3` `F2` `K2`
**Commits:** `8ef0f7b` (F3, F2, K2) — one tight-cluster commit; all three findings are the same
server→client loop in `SyncHandler.ProcessCollectionAsync` plus the contract doc that describes it, so
the whole commit was read against all three IDs rather than per-finding diffs.
**Verification:** integrity items=37 live=14 assigned=245 unique=245 dupes=0 encoding=0 ✓ ·
cursor → item 2 ✓ · strike shipped **with** the fix (the commit's diffstat lists `review-items.md`) ✓ ·
build 0 warnings ✓ · unit **1272 passed, 0 skipped** (Cli 16 · Protocol 91 · Core 771 · WebUi 101 ·
Server 293 — +2 over baseline, exactly the two new tests) ✓ · live **141 passed, 0 failed, 0 skipped**
against a clean-volume Stalwart ✓
**Red-first re-proved independently:** the worker's claim was not taken on trust — `SyncHandler.Collection.cs`
was reverted to `8ef0f7b~1` and both tests re-run: `F3_ChangeRenderFailure_IsReofferedOnNextRound_NotLostForever`
fails `Assert.NotNull()` (the change is never re-offered) and `F2_ItemSkippedThisRound_CollectionNotOfferedToLongPollWait`
fails `Expected: 0, Actual: 1` (the collection is handed to the waiter). Both are the findings' own symptoms.
**Notes:**
- **F3 got the fix its own entry recommends; K2's alternative was correctly not taken.** K2 suggests
  `newSnapshot.Remove(change.ServerId)`, which would make the next diff re-offer the item as an **Add**
  rather than a Change. The worker used F3's remedy — revert to the client's last-acked revision (or
  `ReadOnlyRevertRevision` when absent) — which re-offers it as a Change. That is the better of the two and
  the one the shared fix should be judged against.
- **Not a no-op — checked.** The revert reads `snapshot` while mutating `newSnapshot`; those are distinct
  objects (`CollectionDiff.Compute` builds `new Dictionary(snapshot)` at `CollectionDiff.cs:50`), so the
  rollback genuinely restores the old revision. Had `Compute` returned the same instance the fix would have
  compiled, passed its own test and done nothing.
- **K2 is documentation-only** (`IContentStore.cs` XML doc). No public member added, removed or retyped, so
  no `ContractVersionMinor` bump was required — confirmed by `ContractSurfaceApprovalTests` staying green.
- **F2 landed only the minimum its entry names** ("at minimum, do not return `waitable` when items were
  skipped"), deliberately not the N-consecutive-failure poisoning the entry also offers. The worker's
  reasoning is sound and worth carrying: poisoning on first failure would drop items on a *transient* render
  error, reintroducing the silent-loss class this very item exists to close. **Residual, by design:** a
  permanently-unrenderable item still leaves the collection reporting pending changes via
  `PendingChangeDetector` every round — the tightest backend-hammering loop (the long-poll re-check) is gone,
  the "never quiesces" property is not. A full fix needs persistent per-item retry state; that is a design
  decision, not a worker's call. Not filed as a new finding since F2's own text names the tradeoff.

## Item 2 — Send-once integrity [LIVE]
**Findings:** `F1` `G4` `F7` `F8` `F9` `F12`
**Commits:** `2d1f932` (F1) · `658eeaf` (G4) · `2ad21e9` (F7, F8, F9 — tight cluster, all three in
`MeetingResponseHandler.HandleAsync`; the whole commit was read against all three IDs) · `0b393a9` (F12) ·
`6c72d74` (docs: new finding `N1`)
**Verification:** integrity items=37 live=14 assigned=245 unique=245 dupes=0 encoding=0 ✓ ·
cursor → item 3 ✓ · one commit per finding/cluster with the ID in the subject ✓ · every fix commit's
diffstat carries `review-items.md` (strike shipped with the fix, no trailing bookkeeping commit) ✓ ·
build 0 warnings ✓ · unit **1282 passed, 0 skipped** (Cli 16 · Protocol 91 · Core 772 · WebUi 101 ·
Server 302) ✓ · live **141 passed, 0 failed, 0 skipped** against a clean-volume Stalwart ✓
**Red-first re-proved independently:** the three touched source files were reverted to `369375c` and the
item's tests re-run — 8 failures, exactly the 8 new tests (`SendMail_RetriedWithSameClientId_SendsOnlyOnce`,
`SendMailByReference_FromNonDraftsFolder_DoesNotDeleteTheSource`, `ResentMeetingResponse_DoesNotSendASecondReply`,
`InstanceIdScopedResponse_FailsInsteadOfRespondingForTheWholeSeries`, `InvalidUserResponse_…` ×3,
`SendAsync_CancelledAfterServerAcceptsTheMessage_StillCompletes`), while the 11 pre-existing tests in those
classes — including the companion `…FromDraftsFolder_StillDeletesTheSource` guard — stayed green.
**Notes:**
- **F8 is a deliberate behaviour REGRESSION for occurrence-scoped responses, and a human should look at it.**
  Declining one instance of a series previously "worked" — wrongly, by writing PARTSTAT on the master and
  mailing the organizer a series-wide REPLY. It now returns **Status 2** (the fallback F8's own text
  sanctions: *"or, if the store cannot target an occurrence, return Status 2 rather than silently responding
  for the series"*). The honest failure is the better of the two, but the user-visible effect is that a real
  iOS/Outlook workflow that used to appear to succeed now fails. The proper fix — `RECURRENCE-ID` in the
  REPLY and an occurrence-aware `RespondToMeetingAsync` — is a **`ActiveSync.Contracts` surface change**
  requiring a `ContractVersionMinor` bump, correctly judged out of scope for this item.
- **`N1` filed, and it is a direct consequence of this item's own fixes.** F1 and F7 both claim under fixed
  collection namespaces (`"compose"`, `"meetingresponse"`) that no Sync round ever commits under, so
  `SendDedupStore.PruneAsync` — which only runs from a real collection's commit — never reclaims those rows.
  They accumulate one per mail-ever-sent-by-reference and one per meeting-ever-responded-to, for the life of
  the `Device` row. Unbounded growth on the hot path, not merely a retry artefact. Low severity, but it is
  new debt this item created and it is unassigned to any queue item.
- **The pending-claim semantics were checked, not assumed.** A claim taken and never completed (the send
  threw) returns `PerformSend`, not `AlreadySent` (`SendDedupStore.cs:107` — `existing.Completed ? … : …`),
  so a genuinely failed send still retries. Had it been otherwise, F1/F7 would have made a failed send
  permanently unrepeatable.
- **G4 widens the uncancellable window deliberately**: SMTP submission no longer observes
  `HttpContext.RequestAborted` from the DATA phase onward, bounded only by `smtp.Timeout` (30 s). That is
  the finding's explicit recommendation.
- **F12 now silently no-ops** (rather than erroring) when `Source` names a non-draft; the mail is still sent,
  the source is left intact. It also newly honours the per-folder read-only grant on that delete, which the
  global `ReadOnly` check above the send never covered.

## Item 3 — DAV credential boundary [LIVE]
**Findings:** `H1` `D26` `H24`
**Commits:** `efd6396` (H1, D26 — one fix at the shared seam, as both entries prescribe) · `ba2b6db` (H24)
**Verification:** integrity items=37 live=14 assigned=245 unique=245 dupes=0 encoding=0 ✓ ·
cursor → item 4 ✓ · one commit per finding/cluster with the ID in the subject ✓ · strike shipped with each
fix ✓ · build 0 warnings ✓ · unit **1285 passed, 0 skipped** (Cli 16 · Protocol 91 · Core 775 · WebUi 101 ·
Server 302) ✓ · live **141 passed, 0 failed, 0 skipped** against a clean-volume Stalwart ✓
**Red-first re-proved independently, in two stages:** with both source files reverted to `f7071b4`
(pristine), all three tests fail — `SendAsync_FirstHopOffOrigin_IsRefused_NotSent`,
`GetAsync_WithOffOriginAbsoluteHref_IsRefused_NotSent`, `Invoke_WithOffOriginApiUrl_IsRefused_NotSent`.
Restoring only the H1/D26 commit and leaving `JmapClient.cs` reverted turns the H24 test **green**, which
confirms the worker's disclosure below rather than contradicting it.
**Notes:**
- **The fix is at the seam, not at the site H1 names — and that is correct.** H1 asks for a guard inside
  `WebDavClient.Resolve`; the worker instead asserted `IsSafeRedirect` at hop 0 in
  `RedirectingHttpSender.SendAsync`, which is D26's prescription and which both entries endorse ("one fix
  closes both"). Checked that this genuinely covers H1's reachability list: every `WebDavClient` request
  funnels through one private `SendAsync` → `_redirectSender` (`WebDavClient.cs:218`), and the sender's
  `baseUri` is the **configured** base (`WebDavClient.cs:60`), not anything the server supplies — so the
  comparison cannot be subverted by the very href it is checking.
- **H24 is honestly labelled coverage, and the label is right.** Its test was red on the pristine tree but
  went green from the H1/D26 fix alone, *before* `JmapClient.cs` was touched, because `InvokeAsync` routes
  through the same sender. The commit adds the explicit `RequireSameOrigin` call the Nit asks for — real
  defence-in-depth symmetry across the four credential-attaching call sites — but it is not a fresh
  red→green transition, and the worker said so unprompted in both the commit message and the test comment.
- **New failure mode, accepted by the findings:** a DAV or JMAP server that names a legitimately off-origin
  URL (a cross-host home set is legal under RFC 4918) now gets a `BackendException` instead of being
  followed. That is the intended trade — credentials ride the shared `HttpClient`'s default headers, so
  "follow it" means "disclose the user's mail password." Stalwart's hrefs are all same-origin; the live
  suite confirms no legitimate path regressed.
- Judgment call left as-is: `JmapSessionResource.ApiUrl` stays typed `Uri` rather than being retyped to
  `string` as H24's text literally suggests. Functionally identical at the call site
  (`RequireSameOrigin(session.ApiUrl.ToString())`) and it avoids a gratuitous record-shape change for a Nit.

## Item 4 — DAV create: cost and href resolution [LIVE]
**Findings:** `H2` `H10` `H13` `H20`
**Commits:** `759ab2c` (H2) · `88b2791` (H10) · `14f6d30` (H13) · `316f914` (H20)
**Verification:** integrity items=37 live=14 assigned=245 unique=245 dupes=0 encoding=0 ✓ ·
cursor → item 5 ✓ · one commit per finding, ID in each subject ✓ · strike shipped with each fix ✓ ·
nothing outside the item's file cluster (`git diff --stat` = `DavStoreBase.cs`, `WebDavClient.cs`, their two
test files, `review-items.md`) ✓ · build 0 warnings ✓ · unit **1290 passed, 0 skipped** (Cli 16 ·
Protocol 91 · Core 780 · WebUi 101 · Server 302) ✓ · live **141 passed, 0 skipped** on Stalwart ✓
**Also run against Axigen — the server H2 is actually about.** `scripts/test-backends.sh -b axigen`:
**124 passed, 17 skipped, 0 failed** (the skips are the documented JMAP/ManageSieve/free-busy capability
gates). Stalwart indexes synchronously, so it cannot exercise the lagging-listing path H2 exists for; this
is the run that shows the new direct-GET resolution and the narrowed 409 handling behave on a real
async-indexing backend.
**Red-first re-proved independently:** both source files reverted to `59caec9` → exactly four failures, one
per finding (`CreateItem_WhenListingLagsThePut_VerifiesPutHrefDirectly_WithoutScanningExistingItems`,
`CreatePut_When409AndTargetNotPresent_ThrowsInsteadOfSilentlySucceeding`,
`UpdatePut_WhenReplayed412MatchesOwnContent_IsTreatedAsSuccess`,
`CreateItem_WhenServerExposesNoEtagAnywhere_ReturnsAStableNonGuidSentinel`).
**Notes:**
- **`H20` is cosmetic only — its headline symptom is NOT fixed, and that should be understood.** The finding
  is titled "…guaranteeing a spurious Change on the next diff", but replacing `Guid.NewGuid()` with the fixed
  sentinel `"!etag-unknown"` does not stop the resend: the sentinel still cannot equal the ETag the next
  listing reports, so the item is still re-sent exactly once, self-healing. What the change buys is honesty —
  the placeholder no longer looks like a genuine opaque ETag in a snapshot dump or log line. Note the
  finding's own first suggested remedy (an `!ro`-style poison) has the same property; only its second
  (omit the key) changes the resend, and only into an Add rather than a Change. The worker chose the
  single-file option and disclosed the residual. **Nothing here eliminates the one spurious resend.**
- **`H10` checks existence, not UID.** The finding says "re-GETting `putHref` and accepting only when the
  resource is actually present *with the expected UID*"; the implementation accepts on presence alone. That
  is sound in context — `CreateItemAsync` always PUTs to a freshly minted `Guid` href, so anything present
  there is our own replayed create — but it is a narrowing of the finding's literal text, recorded here so
  it is not rediscovered as a gap.
- **`H13` fails safe.** The lost-response replay is recognised by an ordinal content match; a server that
  normalises what it stores (property reordering, line folding) will simply not match and the 412 still
  surfaces as an error. Wrong-direction failure is impossible by construction — it can only ever miss a
  legitimate replay, never invent one.
- **`H2`'s last-resort content scan is now bounded at 50 candidates.** Beyond that the item falls through to
  the existing "could not be located" warning. On a server that neither honours the PUT target nor supports
  a UID query *and* holds more than 50 items, resolution now gives up where it previously (very slowly)
  might have succeeded. That is the finding's own instruction ("bound it … e.g. skip it once `after.Count`
  exceeds a small ceiling"), and the new direct-GET makes reaching that path far less likely.
- **Flaky test in the suite, pre-existing, carried forward:**
  `CliLocalEndpointTests.UnfixedPattern_TwoIndependentWrappersOverOneStringWriter_CorruptsUnderConcurrentWrites`
  failed once mid-run for the worker with an unrelated `StringBuilder.ToString()` `ArgumentOutOfRangeException`.
  The worker followed step 9 (stashed, re-ran on unmodified `HEAD`, saw it pass) and correctly declared it
  not theirs. It passed in every one of my own full-suite runs. The test deliberately provokes a concurrent-
  write corruption, so intermittency is plausible by design — but a suite that fails ~1-in-N obscures real
  regressions for every later item. Worth a human deciding whether to make it deterministic.

## Item 5 — JMAP listing & submission integrity [LIVE]
**Findings:** `H3` `H18` `H8` `H9`
**Commits:** `a30c2eb` (H3, H18 — tight cluster: one `while` loop, shared state variables; the whole commit
was read against both IDs) · `41d9a8d` (H8) · `6d766d5` (H9)
**Verification:** integrity items=37 live=14 assigned=245 unique=245 dupes=0 encoding=0 ✓ ·
cursor → item 6 ✓ · strike shipped inside all three fix commits ✓ · scope confined to `JmapMailStore.cs`,
`JmapMailSubmit.cs` and their two test files ✓ · build 0 warnings ✓ · unit **1295 passed, 0 skipped**
(Cli 16 · Protocol 91 · Core 785 · WebUi 101 · Server 302) ✓ · live **141 passed, 0 skipped** ✓
**Red-first re-proved independently:** both source files reverted to `4dda8af` → 5 failures, exactly the 5
new tests (`GetItemRevisions_QueryStateChangesMidEnumeration_RecoversTheShiftedItem`,
`GetItemRevisions_ServerNeverAdvancesPosition_Terminates`, `SaveToSent_DoesNotRequestTheBlobCapability`,
`Send_ServerLacksSubmissionCapability_ThrowsNamedError`,
`Send_AccountsDiffer_UsesSubmissionAccountForEmailSubmissionSet`), 8 pre-existing tests still green.
**Notes:**
- **H3's restart is bounded at 3 attempts, then falls back to the best-effort map — the old behaviour.** On
  a mailbox mutating faster than one enumeration completes, the partial-map hazard the finding describes
  therefore still exists; it is narrowed, not eliminated. That is the finding's own instruction ("bounded to
  2–3 attempts, then fall back"). The alternative it also offers — `anchor`/`anchorOffset` paging, which is
  stable under concurrent mutation rather than merely retried — would close it properly and was not taken.
- **H18's warning log was not added.** The finding asks to `break` "logging once at Warning"; the break is
  implemented, the log is not. A server that ignores `position` now terminates the loop silently, so the
  condition is invisible in operation. Trivial to add, worth doing if anyone touches this loop again.
- **`N2` filed, and it is the honest edge of H9.** `Identity/get` still runs under the mail account while
  `EmailSubmission/set` now runs under the submission account; RFC 8621 §7.1 puts `Identity` under the
  submission capability too. The worker deliberately did not widen past H9's literal remedy — correct call
  under step 8 — but on a server where the two primary accounts differ, H9 is only half fixed. `N2` is
  unassigned to any queue item.
- H8 and H9 have no behaviour change on a compliant server (Stalwart advertises blob and uses one account
  for both, which is exactly why CI never caught either). Their value only appears on servers the test
  matrix does not include — so the live suite's green is consistency evidence, not proof of the fix.

## Item 6 — ManageSieve protocol safety [LIVE]
**Findings:** `G1` `G2` `G5` `G10` `G17` `G23` `G24`
**Commits:** `c7caa5b` (G1) · `273e39a` (G2) · `ceda39c` (G5) · `a8f7b6f` (G10) · `418eb0f` (G17) ·
`cf1a177` (G23) · `b011bef` (G24) · `ba90696` (no finding — removes a duplicate XML doc comment the G17
edit left behind)
**Verification:** integrity items=37 live=14 assigned=245 unique=245 dupes=0 encoding=0 ✓ ·
cursor → item 7 ✓ · one commit per finding, ID in each subject ✓ · strike shipped in all seven fix
commits ✓ · scope confined to `ManageSieveClient.cs`, `SieveOofBackend.cs` and one new test file — **no
pre-existing test was modified** ✓ · build 0 warnings ✓ · unit **1303 passed, 0 skipped** (Cli 16 ·
Protocol 91 · Core 793 · WebUi 101 · Server 302) ✓ · live **141 passed, 0 skipped** ✓
**Red-first re-proved independently:** both source files reverted to `02b252a` → **7 failures and 1 pass**.
The 7 are G1, G2, G5, G10, G17 (×2) and G23. The 1 pass is
`ListScriptsAsync_LineEndingInBraceWithNoOpeningBrace_IsTreatedAsPlainText` — G24's test, which the worker
labelled "coverage, not proof" in both the test comment and the commit message. Reverting confirms the
label is accurate rather than an excuse.
**Notes:**
- **`G2` is half-implemented, and the unfixed half is named in the finding itself.** The literal-length
  ceiling (1 MiB) is in. The finding's second clause — *"bound the line reader similarly"* — is not:
  `_reader.ReadLineAsync` still grows its internal buffer without limit against a server that never sends
  CRLF. It is no longer unbounded in *time* (G5's 30 s operation ceiling now caps it), so the realistic
  worst case fell from "until the process dies" to "30 s of inbound data", but the allocation itself is
  still uncapped. The pooled-buffer clause was also skipped — that one is a genuine non-issue once the
  1 MiB cap exists.
- **`G10` is the behaviour change to watch.** A ManageSieve server that does not advertise `"SASL" "PLAIN"`
  — including one advertising an empty SASL list, RFC 5804 §1.7's "TLS first" signal — is now refused at
  connect time with a named error instead of having credentials sent to it. That is the finding's point,
  but it converts a previously-working-by-accident configuration into a hard failure. The live suite's
  Sieve/Oof tests against Stalwart pass, so the mainstream path is unaffected; a server that only advertises
  SASL post-STARTTLS while the gateway is configured `UseTls=false` will now fail where it once connected.
- **`G24` changed no behaviour and cannot** — the finding says so outright ("accidentally legal, so it does
  not throw today"). Struck on the fix's merit, which is the protocol's own rule for an unreproducible
  finding. What landed is the guard ordering plus a regression test around it.
- `G17` implemented both remedies the finding offers (strip control characters in `Quote` **and** fold lone
  CR/LF, not just the CRLF pair) rather than either alone — they cover different paths, since `Quote` also
  guards names that never pass through the literal fold.
- Every ManageSieve operation now has a 30 s ceiling and `DisposeAsync` a 3 s one (`G5`). Both are fixed
  constants, not configurable — worth knowing before someone reports a slow sieve server timing out.

## Item 7 — Calendar & draft data corruption [LIVE]
**Findings:** `D1` `D2` `D3` `D5`
**Commits:** `8791322` (D1) · `5d641ea` (D2) · `5845665` (D3) · `0eba865` (D5)
**Verification:** integrity items=37 live=14 assigned=245 unique=245 dupes=0 encoding=0 ✓ ·
cursor → item 8 ✓ · one commit per finding ✓ · strike shipped with each ✓ · scope confined to the four
converter files + their three test files ✓ · build 0 warnings ✓ · unit **1308 passed, 0 skipped** (Cli 16 ·
Protocol 91 · Core 798 · WebUi 101 · Server 302) ✓ · live **141 passed, 0 skipped** ✓
**Red-first re-proved independently:** `Converters/` reverted to `f6fc5dd` → exactly four failures, one per
finding (`MeetingRequest_LeadingVTimezone_DoesNotShadowVeventDtstart`,
`Change_MultipleCommaSeparatedRecipients_AreAllKept`, `AllDayEvent_CreatedDuringDst_LandsOnTheCorrectNominalDate`,
`Change_OmittingAllDayEvent_PreservesStoredAllDayness`), 36 pre-existing tests green.
**Notes:**
- **`D3` is new hand-written protocol code, so I checked it against MS-ASTZ rather than against its own
  test.** The struct offsets are right (Bias 0, StandardDate 68, StandardBias 84, DaylightDate 152,
  DaylightBias 168, total 172) and the SYSTEMTIME field layout is right (wMonth +2, wDayOfWeek +4, wDay +6,
  wHour +8 …). The southern-hemisphere case is handled — when `daylightStart > standardStart` the window
  test inverts to a wrap — as is the "last weekday of month" form (`wDay == 5`) and a week-4 rule
  overflowing a short month. Fallbacks are conservative: a short blob, an unparsable one, or
  `DaylightBias == 0` all return the old base offset, so a foreign encoding degrades to today's behaviour
  rather than to a wrong one.
- **`D3` went beyond its entry, correctly.** The finding asks for one effective offset; the implementation
  resolves it **per instant** for start and end separately, so a multi-day all-day event that straddles a
  DST transition gets the right nominal date at both ends. The noon anchor the finding suggests as
  belt-and-braces is also in.
- **The noon anchor has a behaviour consequence worth knowing.** `LocalDate` now returns
  `(utc + offset).AddHours(12).Date`. For a well-formed all-day event (local midnight) that is exactly
  right and immune to ±11 h of offset error. For a *malformed* one whose local time is already past noon,
  the nominal date now rolls to the following day where it previously did not. That is the trade the
  finding explicitly asks for.
- **`D2` relies on MimeKit's leniency for the fallback path.** `InternetAddressList.TryParse` is tried
  first and the `;`-split only runs when it fails outright. MimeKit parses several `;`-separated forms
  successfully, so the historical fallback is now reached less often than the code shape suggests — the
  observable behaviour (recipients preserved under both conventions) is what the tests pin, and both pass.
- **`D1` still takes the first VEVENT** when an ICS carries several (a REQUEST with exception overrides).
  That is unchanged from before and outside the finding, but it means the fix guarantees "not VTIMEZONE",
  not "the right occurrence".

## Item 8 — Backend session lifetime & metric cardinality
**Findings:** `A1` `A2` `A3` `A10`
**Commits:** `8a9f2e9` (A1) · `1ce0814` (A2) · `dc64eae` (A3) · `92dcb9f` (A10)
**Verification:** integrity items=37 live=14 assigned=245 unique=245 dupes=0 encoding=0 ✓ ·
cursor → item 9 ✓ · one commit per finding ✓ · strike shipped with each ✓ · scope confined to the two
Core/Backend files, `GatewayMetrics.cs` and their three test files ✓ · build 0 warnings ✓ ·
unit **1312 passed, 0 skipped** (Cli 16 · Protocol 91 · Core 802 · WebUi 101 · Server 302) ✓
**Live suite run even though item 8 is NOT marked [LIVE]:** **141 passed, 0 skipped**. `A1`/`A2` change
session construction and disposal, which `EasEndpoint` reaches on every request via `await using` — exactly
the "can I show it cannot need a live run?" case `fix-review.md` says to resolve by running it.
**Red-first re-proved independently:** the three source files reverted to `77c9189` → 5 failures
(`CreateAsync_DisposesAlreadyOpenedConnections_WhenALaterProviderFails`,
`Session_Dispose_ContinuesPastAThrowingConnection_WithoutThrowing`,
`RecordAuthOutcome_NonSuccess_NeverEmitsTheRawUsername`,
`RecordSyncItems_UserLabel_IsLengthClampedAndControlCharsNeutralized`,
`DisposedFactory_ClearsTheStaticSessionsObserver`), 28 pre-existing green.
**Notes:**
- **A pre-existing test was rewritten, and I checked it was not weakened.**
  `Session_Dispose_ContinuesPastAThrowingConnection_AndAggregates` asserted the *defect* A2 removes (an
  `AggregateException` escaping `DisposeAsync`). The rewrite inverts only that assertion — now
  `Assert.Null(escaped)` — and **keeps both** original assertions, that the throwing connection was still
  attempted and the later connection still disposed. So A12's property, which that test existed to guard,
  survives intact. This is the step-9 "a test encoding behaviour a finding deliberately changes" case, and
  the worker disclosed it unprompted.
- **`A3` removes an operational capability, deliberately.** The `user` label on `activesync_auth_outcomes`
  is now the sentinel `"-"` for every `throttled`/`failure`/`error` outcome, regardless of
  `Metrics:PerUser`. Anyone alerting on *failed logins per user* loses that dimension — it moves to the
  logs, which already sanitise the same field via `LogText.Clean(..., 128)`. That is the point of the
  finding (an unauthenticated caller was minting Prometheus series at will), but it is a monitoring change
  an operator would notice, not a silent internal fix. AGENTS.md's "ALWAYS emit the user tag so series
  shapes stay consistent" invariant is preserved — the tag is still emitted, only its value collapses.
- **A1 and A2 are load-bearing on each other.** A1's cleanup calls `DisposeConnectionsAsync`, which still
  threw at the point A1 landed; only A2 (the next commit) made it non-throwing. Between those two commits a
  simultaneous build failure *and* teardown failure would have replaced the original exception with the
  aggregate. The window is one commit wide and never shipped — noted because bisecting to `8a9f2e9` alone
  would reproduce it.
- `A10` clears the static observer **only if it is still the exact delegate this factory installed**, so a
  disposed factory cannot clobber a live one's gauge. That is the finding's own preferred wording, and the
  test verifies delegate-target identity by reflection rather than just "not null".

## Item 9 — TLS certificate lifecycle
**Findings:** `K1` `K11` `K13` `K18`
**Commits:** `273c1a0` (K1) · `a09218b` (K11) · `ab1cf3a` (K13) · `14dab2b` (K18)
**Verification:** integrity items=37 live=14 assigned=245 unique=245 dupes=0 encoding=0 ✓ ·
cursor → item 10 ✓ · one commit per finding ✓ · strike shipped with each ✓ · scope confined to the two
Core/Security files, `ProgramServer.cs`, one new service file and three test files ✓ · build 0 warnings ✓ ·
unit **1318 passed, 0 skipped** (Cli 16 · Protocol 91 · Core 805 · WebUi 101 · Server 305) ✓
**Live suite run although item 9 is NOT marked [LIVE]:** **141 passed, 0 skipped**. K1 rewires
`ConfigureHosting` and Kestrel's `ServerCertificateSelector` — a hosting-path change, so the unit suites
cannot speak to the assembled listener.
**Red-first re-proved independently:** the two Core/Security files reverted to `473fdd7` → **2 failures**
(`LoadExternal_NotYetValidCertificate_ThrowsInsteadOfLoadingSilently` for K11,
`GenuineDatabaseFailure_WithNoWinnerRowWritten_SurfacesTheOriginalError` for K18) and K13's test **green**,
confirming its declared coverage-not-proof status.
**Notes:**
- **`K1`'s red-first is a compile failure, not a runtime assertion — a real departure, disclosed by the
  worker, and I accept it here.** The fix is an entirely new type (`TlsCertificateRenewalService`), so on
  unmodified code the test cannot fail *at runtime*; there was no seam to fail against, which is precisely
  the defect. What matters is that the test pins the finding's own symptom behaviourally:
  `NearExpiryCertificate_IsRenewedOnATick_AndTheHolderIsSwapped` seeds a near-expiry certificate, ticks the
  service, and asserts the holder now carries a **different thumbprint** with validity beyond 300 days.
  That is the property K1 says was missing. The protocol's purpose — preventing a test shaped by the fix or
  a faked revert — is met by ordinary TDD ordering here.
- **`K1` is deliberately scoped to self-signed serving.** An operator-supplied `Tls:CertificatePath` makes
  the service no-op entirely, because AGENTS.md documents external certs as restart-tier ("a rotated mount
  takes effect on restart"). So this closes the "gateway with >367 days uptime serves an expired leaf" case
  for the *generated* certificate only; an operator whose mounted cert expires in place still needs a
  restart. Correct per the docs, but it is half the space of "TLS certificate lifecycle".
- **New always-on background service and a detached task.** The renewal loop runs daily whenever TLS is on
  with no `CertificatePath`, and the previous certificate is disposed by a fire-and-forget
  `Task.Delay(30 s)` continuation deliberately *not* tied to the stopping token (so a shutdown cannot
  dispose a certificate a lingering handshake is still reading). Both are reasonable; both are new
  process-lifetime behaviour that did not exist before.
- **`K18` improves a diagnostic, it does not change the happy or race path.** A genuine DB failure during
  certificate storage now surfaces the original `DbUpdateException` wrapped with context instead of
  `InvalidOperationException("Sequence contains no elements")`. Worth knowing if any operator tooling
  matched on that old message.
- `K13` is unprovable by construction — there is no external handle on the pre-fix anonymous buffer to
  assert it was wiped. Struck on the fix's merit, matching the file's existing K9 coverage precedent.

## Item 10 — Plugin trust boundary
**Findings:** `K3` `K4` `K19`
**Commits:** `a12e50c` (K3) · `f9b7838` (K4) · `5fe27e9` (K19)
**Verification:** integrity items=37 live=14 assigned=245 unique=245 dupes=0 encoding=0 ✓ ·
cursor → item 11 ✓ · one commit per finding ✓ · strike shipped with each ✓ · scope confined to
`PluginLoader.cs`, `ContractVersion.cs`, the Contracts csproj, `docs/plugins.md` and two test files ✓ ·
build 0 warnings ✓ · unit **1325 passed, 0 skipped** (Cli 16 · Protocol 91 · Core 812 · WebUi 101 ·
Server 305) ✓ · **`ContractSurface.approved.txt` and `ContractVersion` both unchanged** — confirmed by
diff, so the Contracts edit genuinely added no public surface and needed no minor bump ✓
**Red-first re-proved independently:** `PluginLoader.cs` reverted to `baa32ab` → 5 failures (K3 ×2, K4 ×3),
18 pre-existing green.
**A live test failed once, and it was NOT this item's — established, not assumed:**
The first full live run at this HEAD came back **140 passed / 1 failed**; two subsequent runs at the *same*
HEAD were 141/141, the second after a clean-volume Stalwart restart. The container had been up ~29 minutes
across item 9's runs, which is the accumulation failure mode `fix-review.md` describes ("indistinguishable
at a glance from a real regression"). Decisive evidence it cannot be item 10's: the integration fixture
configures **no** `ActiveSync:Plugins:Directory`, and `PluginLoader.LoadInto` returns at
`if (!Directory.Exists(directory))` — so `VerifyPin`, `ComputeDirectoryDigest` and `ContractVersion.Current`
are never reached by the live suite at all.
**My own process gap, recorded so it is not repeated:** I lost the failing test's *name* to truncated
output and could only reason about the failure structurally. Later live runs are captured to a file so a
transient failure can be named rather than inferred.
**Notes:**
- **Two operator-breaking changes, both intended, both requiring action on upgrade.** (1) `K3`: any plugin
  directory containing a non-`.dll` file (native `.so`, `.deps.json`, `.pdb`) now produces a different
  digest, so **every existing pin for such a plugin must be re-pinned or startup fails**. (2) `K4`:
  `RequirePinned=1`/`yes`/`on` previously meant "not required" and now **aborts startup**. Anyone
  unknowingly relying on the fail-open behaviour will not boot until they fix the value — which is the
  point, but it is a hard failure rather than a warning.
- **`K19` landed the finding's exact remedy, including its message string**, and is stronger than "don't
  default permissively": the gate now throws rather than reporting a version at all. Its red was a **compile
  failure** against a newly-extracted `internal` seam. The worker cited my acceptance of `K1`'s compile-red
  in item 9 as precedent — that reasoning is the worker's, not mine; I judged K19 on its own and reach the
  same conclusion, because the real fallback is genuinely unreachable in any normal build (the SDK always
  emits an `AssemblyVersion`) and the test is labelled coverage rather than passed off as proof.
- **`InternalsVisibleTo` was added to `ActiveSync.Contracts` — a published, permanently-MIT package.** It
  is benign (Contracts holds no sensitive internals, and the assembly is unsigned so the grant is weak
  either way), and it added no public surface. But it does ship in the package irrevocably, and it was
  taken on to make one Low/Nit finding testable. A reasonable person could have left `K19` as a
  comment-documented fix with no test; worth knowing the trade was made.

## Item 11 — Password & throttle hardening
**Findings:** `K5` `K6` `K7` `K21`
**Commits:** `7ff8b33` (K5) · `e6b11a3` (K6) · `bd0058a` (K7) · `23ed57e` (K21) · `0f99649` (K7 test
fallout) · `8080fc7` (docs: new finding `N3`)
**Verification:** integrity items=37 live=14 assigned=245 unique=245 dupes=0 encoding=0 ✓ ·
cursor → item 12 ✓ · one commit per finding ✓ · strike shipped with each fix ✓ · scope confined to
`AuthThrottle.cs`, `GatewayPasswordHasher.cs`, `AuthEndpoints.cs` and three test files ✓ ·
build 0 warnings ✓ · unit **1329 passed, 0 skipped** (Cli 16 · Protocol 91 · Core 814 · WebUi 101 ·
Server 307) ✓
**Live suite run — and here I overrode the worker's judgment.** The worker declined it, reasoning that no
endpoint shape or DTO changed. But `fix-review.md` requires a live run for any item that "changes
authentication or session policy", marked or not, and K7 changes throttling behaviour on the WebUi login
path. Result on a clean-volume Stalwart: **141 passed, 0 failed, 0 skipped**, full output captured.
**No branch was created.** Checked explicitly, since the worker mentioned using a scratch `git worktree`:
`git worktree list` shows only the main tree, and the two other branches in the repo
(`feature/schema-driven-backends`, `review/item-29`) are dated 2026-07-20/22 and have **0 commits not in
`main`** — pre-existing, not this run's.
**Red-first re-proved independently for K5, K21 and K7:** reverting `GatewayPasswordHasher.cs` and
`AuthEndpoints.cs` to `37555a0` → `TryParse_OversizedHash_IsRejected`,
`Hash_RejectsAnIterationCountItsOwnVerifyWouldReject` and — at runtime, not merely at compile time —
`SuccessfulLogin_DoesNotClearTheAddressWideCeiling` all fail.
**`K6`'s red was NOT independently re-proved, and here is why:** `AuthThrottle.cs` also carries K7's new
`RecordSuccess(key, isAddressKey)` overload, which K6's own test file references, so reverting that file
breaks the test assembly's compilation rather than producing a red test. I verified K6 by reading the diff
against its entry instead (bounded eviction-under-pressure in `RecordFailure`, matching the finding's
"evict … drop the entry with the oldest `WindowStartUtc`" remedy). Its red-first status rests on the
worker's report alone — the one finding in this run where that is true.
**Notes:**
- **`K7` is the consequential change, and it is an operational trade, not a pure win.** A successful web
  login no longer clears the shared per-address ceiling. That closes the hole (anyone holding one valid
  credential could reset the address-wide counter after each batch of guesses and rotate usernames
  indefinitely) — but it means **a shared NAT/proxy egress address can now 429 legitimate users** until the
  failure window drains, where previously any success forgave it. On a corporate egress IP this is
  user-visible.
- **A pre-existing test was rewritten, and it came out stronger.** `WebLoginThrottleTests` (from an earlier
  round, labelled `C1`) asserted exactly the behaviour K7 removes. The replacement interleaves nine
  successful logins with ghost failures, then proves the tenth failure locks out even the continuously
  successful user — a harder assertion than the original, at the HTTP layer. The worker confirmed the old
  test was green pre-K7 and red after before replacing it, which is the step-9 procedure.
- **`K5` fails closed on over-long stored hashes.** Nothing but a hand-crafted or malicious write could
  have produced one, but any such row must now be re-set — flagged as intentionally breaking in Standing
  context.
- **`K6`'s eviction is an approximate LRU** (32-entry sample), not an exact global-oldest scan, to keep
  insertion O(1) under sustained attack. Under a crafted load an attacker could in principle keep a
  slightly-older entry alive; the property that matters — a new address can always mint a key — holds.
- **`N3` filed, and it concerns item 9's own work:**
  `TlsCertificateRenewalServiceTests.NearExpiryCertificate_IsRenewedOnATick_AndTheHolderIsSwapped` flakes
  roughly 1 run in 8 under full parallel suite load, green in isolation. That is the headline test for K1 —
  the one I accepted a compile-failure red for — so its reliability matters more than a typical flake.
  It passed in every full-suite run I made, including this item's.

## Item 12 — WebUi session, authorization & OIDC
**Findings:** `C1` `C3` `C9` `C16`
**Commits:** `3dc8fd0` (C1) · `d823ccd` (C3) · `a1a2bec` (C9) · `e4d0034` (C16)
**Verification:** integrity items=37 live=14 assigned=245 unique=245 dupes=0 encoding=0 ✓ ·
cursor → item 13 ✓ · one commit per finding ✓ · strike shipped with each ✓ · build 0 warnings ✓ ·
unit **1333 passed, 0 skipped** (Cli 16 · Protocol 91 · Core 814 · WebUi 105 · Server 307) ✓ ·
live **141 passed, 0 skipped** on a clean volume ✓ (mandatory here — C1 and C9 change session and
authentication policy)
**Red-first re-proved independently:** the three WebUi source files reverted to `3b3546a` → exactly four
failures, one per finding (`ReissuedSessionAfterASameInstantRevocation_Survives`,
`DeletingTheLastAdminThroughTheDeleteRoute_IsRefused`,
`UnboundConfigAccount_IsRefused_WhenTheTicketCarriesASubject`,
`TerminationLog_DoesNotMentionTheRemovedBlockedMechanism`), 101 pre-existing green.
**An intermittent unit failure, almost certainly not this item's:** one full-solution run came back
Server.Tests 306/307. Three subsequent full runs were 1333/1333, and Server.Tests alone is 307/307.
Item 12 touches **no** Server source or test file (scope: `SessionValidation.cs`, `UsersEndpoints.cs`,
`OidcLogin.cs`, `ActiveSyncOptions.cs`, `SettingKeys.cs`, docs, three WebUi test files), and `N3` — filed
during item 11 — documents a Server.Tests flake that appears only under full parallel load. **I could not
name the failing test**: the summary-only output had already scrolled past it. That is the second time this
run; live output is now captured to a file, and unit output is too from here on.
**Notes:**
- **`C9` changes the default OIDC outcome for config-declared accounts from "signed in as a plain user" to
  "refused".** Anyone running OIDC with accounts declared in configuration (rather than the database) and
  no `OidcSubject` set will find those users **locked out of the portal** on upgrade until they either bind
  a subject or set the new opt-out. The finding explicitly asks for this, opt-out included — but it is the
  most disruptive change in the item and the one most likely to generate a support call.
- **A new configuration key was introduced: `Oidc:AllowUnboundLoginMatch`.** The finding sanctions "an
  explicit opt-out setting" without naming it; the worker named it, wired it through `SettingKeys` (live,
  CLI-settable) and documented it in `docs/webui.md` + `docs/configuration.md`, matching the sibling
  `AdminClaim`/`AutoProvision` pattern. Reasonable, and more than the minimum — a config-file-only knob
  would also have satisfied the text.
- **Two pre-existing OIDC tests were modified, and they were not weakened.** Both now pass
  `allowUnboundLoginMatch: true` to keep exercising the old path, and both retain their original assertions
  (a config account never TOFU-binds; its admin bit is withheld on a bare login match). The new default is
  covered by a separate new test. Correct step-9 handling, disclosed by the worker.
- **`C16`'s proof is log text, and the worker said so.** The `blocked` parameter was hard-wired `false`, so
  there is no behavioural symptom to reproduce; the only observable surface is the operator-visible message,
  which did change and is asserted. Struck on the fix matching the finding's exact instruction ("drop the
  parameter and the `blocked` term from the guard and the log line").
- `C1`'s fix is the finding's first option (round the stamp up). The alternative — millisecond precision —
  would have invalidated every existing ticket once; not taken, correctly.

## Item 13 — User-resolution resilience
**Findings:** `B1` `B11` `B17`
**Commits:** `248acad` (B1) · `bc99f8c` (B11) · `47ab9c5` (B17)
**Verification:** integrity items=37 live=14 assigned=245 unique=245 dupes=0 encoding=0 ✓ ·
cursor → item 14 ✓ · one commit per finding ✓ · strike shipped with each ✓ · scope confined to
`UserResolver.cs`, `SettingsRefresher.cs` and three test files ✓ · build 0 warnings ✓ ·
unit **1337 passed** ✓ · live **141 passed, 0 skipped** on a clean volume ✓ (run because `UserResolver`
sits on every authenticated request — the worker ran it too, unprompted, for the same reason)
**Red-first re-proved independently for B1 and BOTH halves of B11:** `UserResolver.cs` and
`SettingsRefresher.cs` reverted to `8bf2a03` → `LiveBackendsEditInvalidatingAConfigUser_DoesNotFreezeDatabaseUserPickup`,
`SnapshotChangedSubscriberThrows_OthersStillRun_AndDoesNotSuppressALaterGenuineFailure` and
`Refresher_ChangedSubscriberThrows_OthersStillRun_AndDoesNotSuppressALaterGenuineFailure` all fail.
**`B17` could NOT be independently re-proved** — its test passes the new `config:` constructor argument, so
reverting `UserResolver.cs` fails compilation (`CS1739`) instead of producing a red test. Same structural
limitation as `K6` in item 11. Verified by reading the diff against the entry instead.
**PROTOCOL DEVIATION — B11's UserResolver test was authored fix-first, and the worker disclosed it.**
Its own words: the fix "had briefly gone in ahead of the test", so it applied, reverted to observe red, then
reapplied. That is verbatim the sequence `fix-review.md` bans and says "does **not** count as proof". I did
not simply accept the disclosure:
1. I re-ran the test against genuinely unmodified source myself — it fails there (above), so the "revert
   didn't cleanly reproduce" failure mode the rule guards against does not apply here.
2. I read the test against the finding. It asserts B11's **three enumerated symptoms** — the later
   subscriber still runs, no false "could not refresh" log despite the data being applied, and a later
   genuine failure still warns (the `_refreshErrorLogged` suppression). It asserts nothing about the fix's
   internals (no reference to `GetInvocationList`), which is exactly the "shaped by the fix" hazard the
   ordering rule exists to prevent.
On that basis the strike stands. Recorded prominently because the rule was broken, the disclosure was
voluntary, and a future reader should be able to weigh the evidence rather than assume clean provenance.
**Notes:**
- **B11 was applied more broadly than its text.** The finding names `EnsureFreshAsync`'s
  `SnapshotChanged?.Invoke()`; the worker also routed `OnRolesChanged`'s identical call through the same
  new helper. The defect is the same at both sites and the fix is shared, so this is in-scope widening
  rather than creep — disclosed by the worker.
- **B1 reclassifies a previously fatal condition.** A config-declared user invalidated by a live `Backends`
  edit is now marked `Invalid` individually instead of aborting the whole snapshot rebuild — matching how a
  bad *database* row was already handled. The strictness is retained for the constructor's first build, so
  a genuinely broken configuration still fails fast at startup.
- **The intermittent Server.Tests failure is now NAMED and diagnosed** (it recurred here, and I captured the
  log this time): `TlsCertificateRenewalServiceTests.NearExpiryCertificate_IsRenewedOnATick_AndTheHolderIsSwapped`
  — item 9's headline K1 test — failing with
  `CryptographicException: m_safeCertContext is an invalid handle` from `GetCertHashString()` inside the
  test's polling predicate. Cause: K1's `DisposeAfterGraceAsync` frees the previous certificate while a
  reader still holds the reference it took from `CertificateHolder.Current`. **In production this is
  bounded by the 30 s grace period**, so the window is effectively shut and Kestrel's selector is safe; the
  test shortens the grace to milliseconds and hits it readily. So: a test-configuration artefact rather
  than a shipped defect — but it is the same race the grace period exists to mitigate, and it makes K1's
  only behavioural test unreliable under parallel load. This supersedes the vaguer `N3` filed in item 11.

## Item 14 — Metrics listener exposure & tier
**Findings:** `E1` `E2` `B3`
**Commits:** `8bce897` (E1) · `d64b891` (E2, B3 — one defect reported from the Core and Server sides; both
entries say to fix it once, and the whole commit was read against both IDs)
**Verification:** integrity items=37 live=14 assigned=245 unique=245 dupes=0 encoding=0 ✓ ·
cursor → item 15 ✓ · strike shipped with each fix ✓ · build 0 warnings ✓ · unit **1337 passed, 0 skipped**
(Cli 16 · Protocol 91 · Core 818 · WebUi 105 · Server 307) ✓ · live **143 passed, 0 skipped** on a clean
volume — the baseline moves 141 → 143, both new tests belonging to this item ✓
**Red-first re-proved independently, and this item has the strongest proof shape in the run:** both tests
are **integration** tests driving the real assembled pipeline (`WebApplicationFactory<Program>`, a genuine
`Connection.LocalPort`, a real authenticated EAS handshake and a live `GlobalSettingStore` write), not unit
predicates. Reverting the three source files to `a2552a7` → both fail
(`DedicatedMetricsPort_AnswersOnlyMetrics_EverythingElseIs404`, `PerUserLabels_AppliesLive_WithoutRestart`),
3 pre-existing metrics tests still green.
**Notes:**
- **`E1` closes a genuine exposure.** Before this, opening `Metrics:Port` to a monitoring network also
  served the EAS Basic-auth surface, Autodiscover, `/admin`, `/user` and `/cli` over plain HTTP on that
  port — README told operators the port was scrape-only, which was true of `/metrics` and false of the
  converse. The new terminal middleware is registered first in the pipeline, so it terminates ahead of
  every globally-mapped endpoint. Nothing legitimate depended on the leak.
- **`E2`/`B3` took the CODE fix, not the doc fix, and that was the right read.** Both entries offer either
  (make `PerUser` genuinely live, or flip the catalogue to restart-tier and correct the docs). Standing
  context enumerates the findings that are documentation-only by design — `B9`, `B10`, `K10`, `K15`, `K16`,
  `S1`, `A11`, `A12`, `A13`, `C11`, `C17`, `H25`, `W20` — and neither `B3` nor `E2` is on it. `PerUser` now
  genuinely applies within one settings poll (~1 s).
- **`AGENTS.md` and `README.md` were edited, and the edits are correct and minimal.** Both documented the
  behaviour these findings remove (AGENTS.md said `PerUserLabels` is "set once … at startup" and that only
  `/metrics` is port-gated). The diff corrects exactly those sentences and cites the finding IDs. This is
  required by AGENTS.md's own "update the matching doc" convention — but note that an **orientation
  document every later item reads has changed mid-run**, which is worth knowing when reading earlier
  entries.
- **`GatewayMetrics.PerUserLabels` is now a provider-backed computed property** rather than a settable
  static, wired in `ProgramServer` to `IOptionsMonitor`. Any out-of-repo code assigning that static would
  no longer compile — it is host-only (not part of the plugin contract), so no version gate applies.
- Test-timing detail worth carrying: the OpenTelemetry Prometheus exporter caches scrape responses for
  300 ms by default, so the live-tier test has to wait it out. That is exporter behaviour, not something
  this item changed — but a future test asserting a metric change immediately after a settings flip will
  trip over it.

## Item 15 — Find & ItemOperations conformance [LIVE]
**Findings:** `F4` `F5` `F6` `F10` `F11`
**Commits:** `682fd66` (F4) · `b07cfb6` (F5) · `2a6af36` (F6) · `40b0e16` (F10) · `1b11e64` (F11)
**Verification:** integrity items=37 live=14 assigned=245 unique=245 dupes=0 encoding=0 ✓ ·
cursor → item 16 ✓ · one commit per finding ✓ · strike shipped with each ✓ · build 0 warnings ✓ ·
unit **1342 passed, 0 skipped** (Cli 16 · Protocol 91 · Core 818 · WebUi 105 · Server 312) ✓ ·
live **143 passed, 0 skipped** on a clean volume ✓
**Red-first re-proved independently:** `src/ActiveSync.Server/Eas/` reverted to `c4fb557` → **7 failures**
covering all five findings (F6 and F11 contribute two each), 18 pre-existing green.
**Notes:**
- **`F11` partially reverses a round-2 decision, with cause.** Round 2's `F45` split a collapsed status 2
  into distinct causes and gave the read-only case `3`. `F11` moves it back to `2`, because AGENTS.md's
  read-only scheme states plainly "EmptyFolderContents/MeetingResponse Status 2" (line 337) and `3` is
  needed for `F10`'s genuine retryable backend failure. Verified the AGENTS.md text myself rather than
  taking it from the worker. This is a **client-visible status change**: a client that treated `3` as
  retryable would have retried a permanently-blocked bulk delete forever.
- **Two pre-existing tests were rewritten for F11, and neither was weakened.** Only the expected status
  value changed in each; both keep the substantive assertion that nothing was actually deleted
  (`Assert.Empty(...Emptied)`), and both carry comments explaining the reversal and citing AGENTS.md.
- **PROTOCOL DEVIATION, disclosed by the worker: `F4` added production code before the red observation.**
  `FolderService.GetFolderMapAsync` was written before the F4 test was run red. The worker's mitigation is
  sound — the helper was **inert** (added but not yet called), so the red run still reproduced the genuine
  defect rather than a compile error, and it reports the failure was specifically the missing
  `ServerId`/`CollectionId`. My independent revert confirms the test is red without the fix. Strictly the
  protocol wants zero production changes before red; the deviation is minor and self-reported, and I accept
  it. Flagged so the provenance is on the record.
- **`F6` is a silent-data fix for 16.x clients**: ItemOperations and Search never set `BodyPreference.Eas16`,
  so a 16.x client lost event locations and attachments on those paths. Now threaded from
  `context.Version >= EasVersion.V160` at both ItemOperations call sites and Search's mailbox fetch,
  matching the invariant AGENTS.md states for version gating.
- **`F4` costs one folder-registry read per Find page.** Previously a mailbox-wide Find used a single
  optional CollectionId-scoped folder; it now resolves each hit's own folder via a map. That is what makes
  results openable at all (they carried neither ServerId nor CollectionId before), so the cost is the price
  of the feature working — but it is a new per-page read on the search path.

## Item 16 — WBXML untrusted-input hardening
**Findings:** `W1` `W2` `W4` `W5`
**Commits:** `e3001b8` (W1) · `f7cbb55` (W2) · `1c164ad` (W4) · `86fc870` (W5)
**Verification:** integrity items=37 live=14 assigned=245 unique=245 dupes=0 encoding=0 ✓ ·
cursor → item 17 ✓ · one commit per finding ✓ · strike shipped with each ✓ · build 0 warnings ✓ ·
unit **1350 passed, 0 skipped** (Cli 16 · Protocol 99 · Core 818 · WebUi 105 · Server 312) ✓ ·
live **143 passed, 0 skipped** ✓
**Live suite run despite the item being unmarked and the worker declining it.** `WbxmlDecoder` and
`EasContext` sit on the decode path of **every** EAS request — I could not show a live run was unnecessary,
which is the test `fix-review.md` sets.
**THE PROTOCOL HARD GATE WAS CHECKED DIRECTLY, not taken on report:**
- `ContractSurface.approved.txt` and `Directory.Build.props` are both **unchanged** by this item — so the
  published surface genuinely did not move and no `ContractVersionMinor` bump was owed.
- `EasNamespaces.WbxmlInternal`, which W5's placeholder uses, is **pre-existing** (it already backed
  `OpaqueAttribute`, the documented OPAQUE marker) — `EasNamespaces.cs` was not modified. W5's entry
  prescribes that exact namespace, so this is the finding's own instruction, not an invention.
- `WbxmlCodePages.cs` is **byte-for-byte untouched**, confirming W4's temporary duplicate-name injection
  was fully reverted before committing. No code-page table changed, so the "every table change needs a
  round-trip test" gate is not engaged.
**Red-first re-proved independently:** `WbxmlDecoder.cs` and `EasContext.cs` reverted to `421475f` → **6
failures** (W1 ×2, W2 ×2, W5 ×2 including the rewritten round-trip test), and **W4's two guard tests pass**
— which is correct, not a gap: W4 guards a defect the tree does not currently have.
**Notes:**
- **`W4` deliberately lands NO source change.** Its entry asks only for a test ("add a `WbxmlCodePagesTests`
  fact that iterates `Pages` and asserts …"), because the defect is that a one-line table slip becomes a
  permanent `TypeInitializationException` with nothing to catch it. The worker proved the guard works by
  temporarily injecting a duplicate name and a wrong `Index`, observing the exact described symptoms, then
  reverting — verified above by diffing the source file. That is the right way to prove a guard test.
- **A commit was AMENDED mid-item, and the reason matters.** W5's first implementation added an
  `Action<string>? onUnknownTag` parameter to `WbxmlDecoder.Decode`/`DecodeAsync` — a public-member change
  to the published, permanently-MIT `ActiveSync.Protocol` — which tripped `ContractSurfaceApprovalTests`.
  Rather than bump the contract minor for a logging convenience, the worker reworked `EasContext` to
  discover placeholders by walking for the marker namespace, and amended the (not-yet-verified, unpushed)
  commit. The outcome is the right one: **the enforced gate caught it, and the response was to avoid the
  surface change rather than to regenerate the snapshot.** Worth recording because the first instinct was
  to widen a published API for a diagnostic.
- **`W5` is a deliberate posture change on the Protocol layer.** An unrecognized *tag token* now decodes to
  a placeholder element instead of 400-ing the whole document; an unknown *code page* still hard-fails. This
  softens the fail-closed diagnostic AGENTS.md describes ("if a decode fails with 'unknown tag token', the
  table is wrong or incomplete") — the finding argues for it explicitly, since that same mechanism turned a
  historical missing `FileReference` token into a total outage. A pre-existing round-trip test encoding the
  old throw was rewritten accordingly; the worker confirmed it green before the change.
- **`W1`'s caps are fixed constants** (`MaxTextRuns = 200_000`, `MaxTextChars = 8 MB`), the values the
  finding names. They are not configurable, so a legitimate client sending an unusually large single text
  node would now get a parse error — 8 MB of text in one WBXML document is far outside any real EAS body,
  but the bound is absolute.

---

## Run summary — items 1–16 (Phase 1 complete)

**Swept:** `f9eccb6..d989ba5` — 78 commits (57 `fix`, 21 `docs`/`test`), 91 files, +5310/−282.

**Reconciled mechanically, not eyeballed:** the findings struck on items 1–16's queue lines and the finding
IDs appearing in commit subjects were extracted into two lists and compared with `comm`. **65 struck, 65
committed, both difference sets empty.** One ID (`K7`) appears in two subjects — its fix `bd0058a` and the
disclosed test-rewrite `0f99649` — which is the documented step-9 fallout, not a double claim. (Note for
future sweeps: item 14's subject separates IDs with `/` rather than `,`; a reconciliation regex assuming
commas silently reports `B3` and `E2` as missing.)

**Scope:** nothing landed outside `src/`, `tests/`, `docs/` except `AGENTS.md` and `README.md`, both changed
by item 14 because they documented the behaviour `E1`/`E2`/`B3` corrected. **An orientation document
therefore changed mid-run** — anyone re-reading items 1–13's entries should know AGENTS.md's Metrics
paragraph now describes the post-fix world.

**At HEAD (`d989ba5`):** integrity items=37 live=14 assigned=245 unique=245 dupes=0 encoding=0 ✓ ·
definition adequacy: orphan empty, missing empty ✓ (see the caveat below) · build **0 warnings** ✓ ·
unit **1350 passed, 0 skipped** (Cli 16 · Protocol 99 · Core 818 · WebUi 105 · Server 312) ✓ ·
live **143 passed, 0 failed, 0 skipped** on a clean-volume Stalwart ✓ · working tree clean.
Baseline moved 1270 → 1350 unit and 141 → 143 live over the run.

**⚠ A DEFECT IN THE PROTOCOL TOOLING ITSELF — `fix-review.md:556`.** The definition-adequacy check's
character class is `[ABCDEFHKLSW]`: it **omits area `G`** (30 real findings) and includes an area `L` that
does not exist. Run exactly as documented it reports all 30 G findings as "orphan detail", which the doc
says "MUST be empty" and which reads as data loss. `review-items.md:62` carries the correct class
(`[ABCDEFGHKSW]`). With `G` restored the check is clean. This is a **human decision** — `fix-review.md` is
declared project-independent and "never changes", so it is not the orchestrator's to edit.

**Carried forward — what the next orchestrator most needs to know:**
1. **Upgrade-breaking changes landed.** `K3` (every plugin pin covering a non-`.dll` file must be re-pinned
   or startup fails), `K4` (`RequirePinned=1`/`yes`/`on` now aborts startup), `C9` (config-declared OIDC
   accounts are locked out of the portal until a subject is bound or `Oidc:AllowUnboundLoginMatch` is set),
   `K5` (over-long stored password hashes now fail closed).
2. **Client-visible protocol changes.** `F11` (read-only `EmptyFolderContents` 3 → 2, partially reversing
   round 2's `F45`), `F8` (occurrence-scoped MeetingResponse now Status 2 instead of silently answering for
   the whole series), `W5` (an unknown WBXML tag token degrades to a placeholder instead of 400-ing the
   document).
3. **Operational trade:** `K7` — a shared NAT/proxy address can now be throttled to 429 until the window
   drains, where any success previously forgave it.
4. **Findings closed only in part, by design:** `H3` (bounded restart, still falls back to a partial map),
   `H20` (cosmetic — the spurious resend it names still occurs), `G2` (literal capped; the line reader is
   still unbounded in allocation), `K1` (renews the self-signed cert only — a mounted operator cert
   expiring in place still needs a restart), `F2` (long-poll spin removed; a permanently-unrenderable item
   still reports pending every round).
5. **Unassigned debt filed during the run:** `N1` (send-dedup rows under `compose`/`meetingresponse` are
   never pruned — created by item 2's own fixes), `N2` (`Identity/get` still uses the mail account, so `H9`
   is half-done where mail and submission accounts differ), `N3` (superseded — see 6).
6. **A known flake, now diagnosed:**
   `TlsCertificateRenewalServiceTests.NearExpiryCertificate_IsRenewedOnATick_AndTheHolderIsSwapped`
   (item 9's headline `K1` test) fails intermittently under full parallel load with
   `CryptographicException: m_safeCertContext is an invalid handle` — K1's grace-period disposal freeing a
   certificate the test's polling predicate still holds. Production is protected by the 30 s grace; the
   test shortens it to milliseconds. Test artefact, but it is `K1`'s only behavioural test.
7. **Proof-quality exceptions, all disclosed and all judged individually:** `K1`, `K19` and one `K7` test
   proved red by **compile failure** rather than runtime assertion (each a new seam that could not fail at
   runtime on old code); `B11`'s UserResolver test was authored **fix-first** (banned ordering — I re-proved
   it red on unmodified source and confirmed it asserts the finding's symptoms, not the fix's internals);
   `F4` added an inert production helper before its red observation; `K13`, `G24` and `K19` are labelled
   coverage-not-proof. `K6` and `B17` could not be re-proved independently at all, because reverting their
   source file breaks the test assembly's compilation — their red-first status rests on the worker reports.
8. **Where the cursor rests:** item **17** (`C2` `C4` `C8` `C11` `C12` `C13` — merged-view write-back),
   the first item of Phase 2. Items 17–22 are the db-restructure's unfinished edges; `review-items.md`'s
   own guidance calls these the ones an operator actually trips over.

*(Phase 2 continues below in the same run — the handover at item 17 was not taken.)*

---

## Item 17 — Merged-view write-back
**Findings:** `C2` `C4` `C8` `C11` `C12` `C13`
**Commits:** `18800ce` (C2) · `ca9dac8` (C4) · `e77bfad` (C8, C11) · `730b5ef` (C12) · `65acf12` (C13)
**Verification:** integrity items=37 live=14 assigned=245 unique=245 dupes=0 encoding=0 ✓ ·
cursor → item 18 ✓ · strike shipped with each fix ✓ · build 0 warnings ✓ · unit **1358 passed, 0 skipped**
(Cli 16 · Protocol 99 · Core 818 · WebUi 113 · Server 312) ✓ · live **143 passed, 0 skipped** on a clean
volume ✓ (the worker ran it unprompted — correctly, per AGENTS.md's own warning that a WebUi-endpoint item
once broke three integration tests with every unit suite green)
**Read against `docs/design/db-restructure.md`, not just the findings.** That document's **deviation 2**
records that `UserEditing.LoadStartingEntryAsync` deliberately *stopped* cloning config, because under
per-field resolution cloning "would freeze config values as database overrides". `C2`/`C12` are that exact
anti-pattern resurfacing one layer up, in the admin editor and the portal. The fixes restore the documented
rule (`user DB → user config → global DB → global config → code default`, most specific wins, per field).
**Red-first re-proved independently:** the three source files reverted to `7f6f650` → 6 failures
(`Update_ResubmittingTheConfigValues_DoesNotFreezeThemAsADatabaseOverride`,
`Meta_And_Put_AgreeOnTheProvider_WhenOnlyConfigDeclaresIt`, `Get_ReportsWhichLevelSuppliedEachField`,
`Put_ResubmittingTheConfigSuppliedUserName_DoesNotFreezeItAsADatabaseOverride`,
`Meta_ReflectsALiveProviderChange_WithoutATimeBasedWait`, plus the rewritten redaction test), 107
pre-existing green. `C11` is documentation-only and correctly has no test.
**Notes:**
- **The elision compares against CONFIG ONLY — I checked this specifically, because eliding against the
  merged view would have been a silent catastrophe.** `ElideIfMatchesConfig` takes the config value as its
  second argument, and the callers pass `FindConfigUser(...)`/`configRole`. The merged view is threaded in
  for exactly one purpose — unmasking a re-posted `"***"`, which requires knowing what the mask stands for —
  and never for deciding what gets written. Had it been merged-vs-merged, every genuine database override
  would have been erased on the next save.
- **The design doc's "null clears" semantic survives.** `ElideSettingsMatchingConfig` keeps an explicit
  null (the "clear the inherited global key" directive), which by construction never equals a present
  config value. `db-restructure.md` flags this exact distinction as the one to carry forward "deliberately,
  not by accident".
- **A pre-existing test was rewritten, and it came out stronger.** `AdminUserApi_RePostedMask_KeepsTheStoredSecret`
  asserted the secret was written into the database row — the freeze itself. It now asserts no row is
  created *and* that the secret still resolves and still masks on a later GET, so the guarantee that
  mattered (an unrelated edit must not wipe the ApiKey) is preserved. A **new sibling test** covers the
  database-override case, proving elision drops only values matching config and never a real deviation.
- **`C8`'s badge rendering is unverified.** The API/DTO provenance is red-first proven; the `users.js`
  rendering has no automated coverage because the no-build SPA has no JS test harness anywhere in the repo.
  Consistent with prior rounds, but it means "the admin can now see which values came from config" is
  proven at the API and asserted-by-inspection at the UI.
- **`C13`'s test had to defeat a masking effect**, which is worth knowing: `SessionValidation` gives every
  cookie one free resolver refresh on its first post-login request, which hides the stale-snapshot bug
  entirely in a naive harness. The test spends that refresh on a warm-up request first. A future test in
  this area that "passes" without doing so proves nothing.

## Item 18 — Settings validation & catalogue
**Findings:** `B2` `B4` `B5` `B6` `B7` `B12` `B14` `E8`
**Commits:** `bc7d1ba` (B2) · `02417f5` (B4) · `39774d6` (B5) · `083e7a5` (B6) · `583fec7` (B7) ·
`0b7f2e5` (B12) · `21c9461` (B14) · `19709f7` (E8) · **`9b2b2ed` (scoped repair of B7 — see below)**
**Verification:** integrity items=37 live=14 assigned=245 unique=245 dupes=0 encoding=0 ✓ ·
cursor → item 19 ✓ · one commit per finding, strike shipped with each ✓ · build 0 warnings ✓ ·
unit **1385 passed, 0 skipped** (Cli 16 · Protocol 99 · Core 841 · WebUi 115 · Server 314) ✓ ·
live **143 passed, 0 skipped** on a clean volume ✓

### ⚠ THIS ITEM SHIPPED A REGRESSION. The worker declined the live suite; the live suite is what caught it.
The worker's reasoning for skipping was that nothing touches "a migration, an HTTP handler/DTO, or
auth/cookie policy". I ran it anyway, because `B12` can now **abort startup** and the integration suite is
the only thing that actually boots the host. It came back **141 passed / 2 failed**:
`WebUiBackendsApiTests.StoredSecretsAreNeverEchoed` (expected `"***"`, got `null`) and
`Save_ElidesTheProvidersOwnDefault` (expected `"db"`, got `null`).

**Established whose failure it was rather than assuming** — item 18 modified no integration test
(`git diff --name-only` over `tests/ActiveSync.Integration.Tests/` is empty), and reverting only `src/` to
item 17's HEAD made both pass (10/10). Bisected within the item: **green at `083e7a5` (B6), red at
`583fec7` (B7)**.

**Root cause:** `BackendsEndpoints.DbLeafs` derives a role's leaf name by stripping the role prefix off the
**raw stored key**. Once `B7` normalized `GlobalSetting.Key` to lowercase on write, any leaf whose *only*
source is the database surfaced as `password`/`forcefrom` instead of `Password`/`ForceFrom`, and the API's
case-sensitive callers found nothing. Leaves that also exist in the config file kept their canonical casing
from the case-insensitive merge, which is why only two tests broke — a partial failure that is easy to
misread as a flake.

**Repair (`9b2b2ed`, spawned as a scoped subagent — the orchestrator does not author fixes):** restores
canonical casing at the display layer in `BackendsEndpoints.Describe()` for every field the provider's
schema declares, plus the two blanket credential leaves `Password`/`UserName`. Verified independently that
it touches **only** `BackendsEndpoints.cs` — `GlobalSettingStore` is untouched, so B7's normalization and
sargable equality lookups are fully preserved — and that it did not touch `review-items.md` (B7's strike
was already correctly landed with its own fix; the repair is not a new finding).

**Residual from the repair, worth knowing:** casing is restored only for schema-declared fields and
`Password`/`UserName`. A key that **no schema claims** — the "Advanced" section AGENTS.md says must survive
full-replacement PUTs — will still round-trip to lowercase. That is cosmetic rather than lossy (the value
is preserved and `IConfiguration` binding is case-insensitive), but an operator who types `MyCustomKey`
will see `mycustomkey` afterwards.

**Red-first re-proved independently for 6 of 8:** reverting `src/` → `WritingAdminClaimAlone_IsNotRejectedByTheOtherKeysFailure`
(B2), `DeletingABackendBaseUrl_ThatWouldBreakTheNextStart_IsRejected` (B4),
`WritingAGlobalProviderChange_ThatBreaksADeclaredUsersMergedSettings_IsRejected` (B5),
`TrustedProxies_IndexedElement_IsSettableThroughTheCatalogue` ×2 (B6), four B7 tests, and
`AGlobalPasswordLeaf_FromAConfigFile_FailsStartup_EvenThoughItNeverWentThroughAWriteSurface` (B12) all fail.
**`B14` and `E8` could not be re-proved independently** — both their tests live in
`ActiveSyncOptionsValidatorTests.cs`, and their sources also carry `B4`'s and `E8`'s new APIs, so every
selective revert breaks the test assembly's compilation. Judged by diff instead: B14's three literals
(65535, 3650, 86400) are the catalogue's own, and E8's check correctly gates on whether each listener is
actually enabled. Same limitation as `K6` (item 11) and `B17` (item 13).
**Notes:**
- **`B12` is a new hard startup failure.** A gateway whose config file carries an inert
  `Backends:<Role>:Password`/`UserName` now refuses to boot. The finding offers "a startup **failure** (or
  at minimum a Warning)" and standing context prefers breaking changes, so this is sanctioned — but it will
  stop an existing deployment that has one of those keys lying around, and the key was previously a silent
  no-op.
- **`B7` changes stored key casing.** Rows are now written lowercase; three pre-existing tests were updated
  to match. Combined with the repair above, the display casing operators see is reconstructed rather than
  stored — so the canonical name now comes from the provider schema, not from what was typed.
- **`N4` filed, and it is an honest admission of a gap this item leaves.** `B5` added user-impact
  re-validation to the *write* path only; the **unset** path still validates only the role's own schema, so
  the "drops the Oof role" scenario B5's own prose describes is still reachable via `eas config unset`. The
  worker split it that way because each finding's fix text is write-scoped. Unassigned to any queue item.

## Item 19 — Admin UI gaps & coherence
**Findings:** `C5` `C6` `C7` `C10` `C14` `C17` `C18` `C19`
**Commits:** `e19173f` (C5) · `bcc6458` (C6) · `6cf0cd4` (C7, C18 — tight cluster, same file and test file) ·
`ddd1593` (C10) · `e1954ab` (C14) · `445cabb` (C17) · `0800eea` (C19)
**Verification:** integrity items=37 live=14 assigned=245 unique=245 dupes=0 encoding=0 ✓ ·
cursor → item 20 ✓ · strike shipped with each ✓ · build 0 warnings ✓ · unit **1389 passed, 0 skipped**
(Cli 16 · Protocol 99 · Core 841 · WebUi 119 · Server 314) ✓ · live **143 passed, 0 skipped** on a clean
volume ✓ — the worker ran the live suite unprompted, citing item 18's regression as the reason. Good
instinct, and I ran it independently too.
**Red-first re-proved independently for the four testable findings:** `src/ActiveSync.WebUi/Api/` reverted
to `b495503` → `SwitchingProvider_DropsThePreviousProvidersStoredLeaves` (C5),
`AStrayDatabaseKey_NotInTheCatalogue_IsSurfacedAsClearable` (C7),
`DeletingADbOverride_ThatLeavesAConfigFileValue_ReportsConfigNotDefault` (C18),
`PortalUser_OmittingUserName_KeepsTheStoredValue` (C14) all fail; 115 pre-existing green.

### ⚠ `C17` WAS DELIBERATELY IMPLEMENTED ONLY IN PART — and the worker is right, but the strike hides it
`C17` asks for two things: correct a stale XML summary, and **drop the `knownUser` field** from the
devices/block response on the grounds that it is "vestigial … a block for an unknown login cannot succeed".
The worker did the first and refused the second, arguing the premise is false. **I verified this myself
rather than accepting it**: `AdminIdentifierValidationTests.DeviceBlock_ReportsWhetherTheLoginIsDeclared`
is a pre-existing test that seeds a device for login `typo`, blocks it **successfully**, and asserts
`knownUser == false`. The finding conflates "has no `User` row" with "is not declared" — an identity-only
row (`User.Json` null, per AGENTS.md) satisfies `FindUserIdAsync` while being absent from
`resolver.MergedUsers`. So `knownUser` is reachable, meaningful and covered; removing it would have deleted
tested behaviour on a wrong premise.
**The judgment is sound. The bookkeeping is the problem:** `C17` is struck `COMPLETE` while half its stated
FIX was deliberately not done. Per `fix-review.md` a finding that contradicts reality is "a human
decision", and the strike is precisely what stops anyone looking again. **Flagged for a human:** either
accept the partial close as recorded here, or re-mark the `knownUser` half `N/A` with this reasoning on the
finding's own line.

**Notes:**
- **Three findings landed with NO automated proof at all** — `C6`, `C10`, `C19` are pure changes to the
  no-build SPA, and the repo has no JS test harness anywhere (the same situation as `C8` in item 17 and
  prior rounds). They are struck on the strength of the fix. Since a diff read is the only verification
  available, I did one: `C6` adds a real Clear affordance that sets a `cleared` flag so `collect()` can emit
  null (and `PersistAsync` already treats null as "delete the row"); `C10`'s new controls post to
  `/admin/api/users/{login}/rename` and `/delete`, **both of which exist server-side**, and it sends the
  login back through `confirmTyped` — satisfying the delete route's `request.Confirm == login` echo
  contract; `C19` adds error handling on the grant-removal and reset-to-config paths. Coherent, but
  "coherent by inspection" is a weaker guarantee than everything else in this run.
- **`C10` composes with item 12's `C3`.** The delete route the new SPA control calls is the one `C3` taught
  to refuse destroying the last enabled administrator — so the UI cannot walk into the state `C3` closed.
- **`C5` changes stored-row behaviour**: switching a role's provider now deletes the previous provider's
  leaves rather than orphaning them for the new provider to bind. A bug fix, but it is destructive to rows
  that previously survived a switch.

## Item 20 — CLI configuration & warm-host reuse
**Findings:** `E4` `E5` `E6` `E7` `E10` `E14` `E17`
**Commits:** `9379152` (E4) · `efcf36e` (E5) · `e329860` (E6) · `1d0cc15` (E7) · `640ba52` (E10) ·
`41b26b3` (E14) · `600d2fa` (E17)
**Verification:** integrity items=37 live=14 assigned=245 unique=245 dupes=0 encoding=0 ✓ ·
cursor → item 21 ✓ · one commit per finding, strike shipped with each ✓ · build 0 warnings ✓ ·
unit **1397 passed, 0 skipped** (Cli 16 · Protocol 99 · Core 842 · WebUi 119 · Server 321) ✓
**Live suite run although the item is unmarked and the worker did not report one:** **143 passed,
0 skipped** on a clean volume. `E7` edits `ProgramServer.InitializeAsync` and `E6`/`E10` edit `Program.cs`
dispatch — and the integration fixture invokes that entry point directly (`WebApplicationFactory<Program>`
with the `AS_TEST_FORCE_SERVE` module initializer), so a mistake there would break every integration test
while the unit suites stayed green.
**Red-first re-proved independently for 6 of 7:** `src/` reverted to `3d59935` → 8 failures —
`UserSet_InheritsAGlobalMailStoreRoleThatOnlyExistsInTheDatabase` (E4),
`{ConfigGet,Logs,Tls}_PrefersAmbientHostProvider_OverRebuildingFromEnv` (E5),
`IsLocalOnlyVerb_NoArgs_ReturnsTrue` (E6), `ForwardedHelp_UsesTheSamePreCliAlias_AsTheLocalDispatcher`
(E10), `BuildConfiguration_MissingUsersFile_ThrowsActionableError_NotRawFileNotFoundException` (E14),
`UserSet_PickupNote_ReflectsALiveSettingsChange_NotTheFrozenIOptionsSnapshot` (E17). `E7` is correctly
absent — see below.
**Notes:**
- **`E7` is the weakest strike in this item, and the worker labelled it honestly.** Its test
  (`SettingsRefresherCancellationTests`) proves the *premise* — that one of the four rewired methods
  genuinely honours a cancelled token — not the **wiring**, which is what the finding is about. There is no
  seam: `InitializeAsync` is a private static resolving four sealed services, and the symptom needs I/O slow
  enough to race a real shutdown against one of five awaits that complete in microseconds on a temp SQLite
  DB. The diff is small and mechanical (one `stopping` token hoisted through five awaits) and I read it, but
  **nothing automated proves those five call sites actually pass the token**.
- **`E6` is a behaviour change**: bare `eas` inside a running container no longer forwards to the warm
  gateway — it is now always-local, like `serve`/`protect`, so it pays a cold start to print the banner
  instead of wrongly reporting "the gateway is NOT running". A pre-existing test
  (`IsLocalOnlyVerb_NoArgs_ReturnsFalse`) encoded the old bug and was flipped; the rename to
  `..._ReturnsTrue` makes the change visible in the test name rather than hiding it behind an edited
  assertion.
- **`E4` landed narrower than the finding's literal text, and the worker flagged it for exactly this
  review.** The finding names `CliVerbs.BuildConfiguration`; the fix adds a separate
  `BuildConfigurationWithDatabaseSettings` and routes only `CliServices.TryCreateAsync` through it, leaving
  the settings commands' "config" vs "db" source labelling untouched. That labelling is real behaviour
  (`eas config list` shows provenance), so narrowing it is defensible — but `eas user ...` now sees the DB
  layer while other standalone verbs still do not, which is a subtler split than the finding envisaged.
- **`E5` closes a real leak**, not just a slowdown: forwarded `config`/`logs`/`tls` each rebuilt a provider
  and re-ran `PluginLoader.LoadInto`, leaking a non-collectible `AssemblyLoadContext` per invocation in any
  plugin-bearing deployment.

## Item 21 — `/cli` endpoint hardening
**Findings:** `E3` `E11` `E18` `E19` `E20`
**Commits:** `d53bb8d` (E3) · `b10cee3` (E11) · `95fd2c8` (E18) · `4978877` (E19) · `1102a4a` (E20)
**Verification:** integrity items=37 live=14 assigned=245 unique=245 dupes=0 encoding=0 ✓ ·
cursor → item 22 ✓ · one commit per finding, strike shipped with each ✓ · build 0 warnings ✓ ·
unit **1403 passed, 0 skipped** (Cli 16 · Protocol 99 · Core 842 · WebUi 119 · Server 327) ✓ ·
live **144 passed, 0 skipped** on a clean-volume Stalwart ✓

**This entry replaces the ⚠ PARTIAL entry** written after two interrupted sessions. `E3` and `E11` landed
in those sessions; `E18`/`E19`/`E20` landed here. Both debts the partial entry recorded are now paid:

- **`E3` red-first re-proved independently.** `src/` reverted to `5c600c1` (E3's parent) →
  `CliEndpoint_Returns404ForAMalformedBody_BeforeEverParsingIt` fails with `Expected: NotFound / Actual:
  BadRequest` — the 400 existence oracle the finding names, exactly.
- **The live suite was run**, which the partial entry flagged as owed because `E3` alters the `/cli`
  request pipeline: 144 passed, 0 skipped (item 20's figure was 143; `E3`'s test is the extra).

**Red-first re-proved independently for `E19` and `E20`:** `src/` reverted to `595ff67` → 3 failures —
`TryOpen_RejectsASealedEnvelope_WhoseArgsArrayContainsANullElement`,
`TryAuthorize_PlaintextMode_RejectsArgsContainingANullElement` (E19), and
`ConfirmedPurge_ReRunsTheImpactCount_AndNamesWhatItDestroys` (E20). `E18`'s two tests **passed** on that
same unmodified source — see the note below, which is the finding to read in this entry.

**Notes:**
- **`E18`'s fix is right and its tests guard nothing.** The source change is the finding's FIX verbatim
  (`captured.Profile.Width = width is > 0 and <= 1000 ? width : 200` at `LocalCliEndpoint.cs:442`), and
  the worker labelled the tests "COVERAGE, NOT PROOF" honestly in the test comment, the commit message and
  its report. But the two tests do not exercise the production code **at all**: they re-declare the unfixed
  and fixed expressions as literals inside the test body and assert about a `Rule` they construct
  themselves. I confirmed the consequence rather than inferring it — with `src/` reverted to before the fix,
  both tests still pass. **Deleting the clamp from `LocalCliEndpoint.cs` leaves the suite green.** That is a
  step below what "coverage" normally buys: a coverage test still pins the shipped code path, and this one
  pins a copy of it. Worth knowing: `ExecuteAsync` already takes `width` as an optional parameter
  (`LocalCliEndpoint.cs:355`), so the production path *was* reachable from a test — the worker's stated
  reason for not using it is that no command in today's tree renders a construct whose cost is driven by
  `Profile.Width` (only `Table`s, which size to content), so an end-to-end test would have been green
  before and after and proved nothing either. That reasoning is sound as far as it goes; the gap it leaves
  is a regression guard, not a correctness claim.
- **`E19` extends past the finding's letter, correctly.** The entry names `TryOpen` and the plaintext
  branch of `TryAuthorize`; the fix does both. Note the plaintext-branch refusal reuses the
  `IsCredentialBearingVerb` arm, so a null element now yields `return false` → 404 via `E3`'s path,
  i.e. nothing runs server-side and the client falls back to local execution. That is the existing
  documented contract, not a new behaviour.
- **`E20` now counts unconditionally.** `CountDeletionImpactAsync` moved above the `if (!settings.Yes)`
  block, so the confirmed path pays one extra query it did not before — deliberate, and the point of the
  finding. Behaviour change: `eas purge ... --yes` prints `Deleting <impact> along with <target>.` before
  deleting when content is at risk. The worker flagged the wording as reading awkwardly for
  `PurgeUserCommand` ("Deleting 3 contacts along with ALL gateway state of user 'anna'.") and kept it
  shared in the base rather than special-casing per subclass — a defensible call, and cosmetic.
- **`E18` is also a behaviour change**, minor: a `/cli` caller-supplied width outside `(0, 1000]` now
  falls back to 200 columns instead of being honoured.
- **`N3`'s flaky test failed the first full unit run, and `N3`'s recorded mechanism is wrong.**
  `TlsCertificateRenewalServiceTests.NearExpiryCertificate_IsRenewedOnATick_AndTheHolderIsSwapped` failed
  with `CryptographicException: m_safeCertContext is an invalid handle` — **not** the 5-second timeout
  `N3` describes. The real mechanism is visible at
  `TlsCertificateRenewalServiceTests.cs:91`: the test reads `stale.Thumbprint` after the service's
  `disposeGracePeriod` (20 ms) may already have disposed the old certificate, so the race is
  use-after-dispose, not slow key generation. I established ownership rather than assuming it: it failed
  3/3 in isolation at HEAD and passed 3/3 at `595ff67`, which looked commit-shaped — but interleaving the
  two builds flipped the result completely (pre-run 3/3 failing, HEAD 3/3 passing), so it tracks machine
  load, not the commit, and item 21 touches no file this test loads. A re-run of the full unit suite was
  clean at 1403. **`N3`'s FIX text should be updated** — widening the timeout, which is what it currently
  proposes, does not address a use-after-dispose.

## Item 22 — Identity normalization
**Findings:** `A4` `B13` `B15` `B20` `C15`
**Commits:** `2d643bd` (A4) · `db7928e` (B13) · `608d2c6` (B15) · `df3fcc0` (B20) · `ce110f8` (C15)
**Verification:** integrity items=37 live=14 assigned=245 unique=245 dupes=0 encoding=0 ✓ ·
cursor → item 23 ✓ · one commit per finding, ID in each subject ✓ · **strike NOT shipped with the fix —
see below** ✗ · build 0 warnings ✓ · unit **1409 passed, 0 skipped**
(Cli 16 · Protocol 99 · Core 847 · WebUi 120 · Server 327) ✓ · live **144 passed, 0 skipped** on a
clean-volume Stalwart ✓

**Red-first re-proved independently:** `src/` reverted to `1102a4a` → 5 failures —
`GetSession_KeyedOnLoginAlone_ServesAReissuedLoginTheOldHoldersSession` (A4),
`ValidateUsers_RejectsALoginWithLeadingOrTrailingWhitespace` + `Upsert_IsWhitespaceInsensitive_NoDuplicateRow`
(B13), `Logs_UserFilter_IsCaseInsensitive` (B15), `Share_Delete_RefusesAMalformedLogin` (C15). `B20` is
correctly absent — it is coverage, not proof.

**The live suite was run although the item is unmarked**, because `A4` re-keys the backend **session
cache** and `B13` changes login normalization at `UserStore.NormalizeLogin`, the funnel every lookup and
write passes through — that is "changes authentication or session policy" under the standing rule, whatever
the item's marking says. 144 passed, 0 skipped.

**Notes:**
- **Protocol violation, self-disclosed by the worker and confirmed here: the strike did not ship with the
  fix for four of five findings.** `2d643bd`, `db7928e`, `608d2c6` and `df3fcc0` carry no
  `review-items.md`; all five strikes landed together in `ce110f8` (C15). I verified this against the
  per-commit diffstats rather than taking the report's word for it. The end state is correct — cursor
  honest, one commit per finding, no dangling work — but between `2d643bd` and `ce110f8` the tree held
  four finished findings the document said were not done, which is exactly the interruption window
  `fix-review.md` describes. The worker caught it only after four commits had landed on top, so the cheap
  in-the-moment `--amend` was gone. Worth noting for the programme's own record: this remains the
  most-repeated deviation, and the worker disclosing it honestly (rather than quietly rewriting history)
  is the behaviour the protocol wants.
- **`B20` is coverage, not proof, and the worker's stated reason is a good one.** It tried to force a
  lower-`UserId` row to enumerate last via an out-of-band INSERT and the test stayed green against the
  unfixed query, because on SQLite the rowid *is* `UserId`, so a table scan already enumerates ascending.
  The symptom needs a provider that gives no such ordering guarantee (Postgres), which the local unit
  suite does not run. The fix (`.OrderBy(u => u.UserId)`) is the finding's FIX verbatim, and the warning
  now names the winning `UserId` as the finding also asks.
- **An existing test was edited to accommodate `A4`, and the worker did not disclose it.**
  `IdleSweep_RemovesAFaultedSessionSlot` hard-codes the cache key to assert on the factory's internal
  `_sessions` dictionary, so the new key format forced `$"{Creds.UserName}\ndev-1"` →
  `$"1\n{Creds.UserName}\ndev-1"`. I read it: it is a mechanical accommodation, not a weakening — same
  assertion, and `1` is the same `userId` the test already passed to `GetSessionAsync`. It is legitimate
  under the protocol's "a test harness your change legitimately broke" clause, but that clause requires
  saying so explicitly, and the report did not.
- **`C15` landed narrower than the finding's prose, correctly.** The entry cites three further asymmetric
  routes (`DevicesEndpoints` unblock/wipe/purge, `PUT users/{login}`) but its FIX offers two *alternatives*:
  validate on every admin route, **or** make `NormalizeLogin` trim because "it is the single funnel for
  every lookup and write". `B13` took the second, which covers those routes at the funnel; `C15` then added
  shape validation to the one route the finding actually quotes. The worker flagged this as a judgment call
  a reviewer might want widened — I read it as within the finding, not a narrowing of it.
- **`B15` accepts a scan, deliberately.** `e.User.ToLower() == normalizedUser` translates to `LOWER()`,
  which is non-sargable — the same trade-off the class already documents for its text filter, and the
  finding explicitly sanctions it ("accepting the scan on an already-scanned query").
- **`B13` is a behaviour change worth an operator's attention.** A config-declared login with leading or
  trailing whitespace is now a startup validation **failure** rather than a silently-provisioned second
  identity, and `NormalizeLogin` trimming changes the stored canonical form. Nothing is deployed outside
  testing, so no migration concern — but on a tree that had already minted a whitespace-padded row, that
  row's login would now normalize onto the real user's.

## Run summary — items 21–22
**Swept:** `git log 595ff67..HEAD` — 8 commits, one per finding, ID in every subject · every claimed
finding (`E18` `E19` `E20` `A4` `B13` `B15` `B20` `C15`) struck exactly once in the queue and present in
exactly one commit subject, reconciled mechanically rather than by eye ✓ · no changes outside the two
items' file clusters (`/cli` + Crypto envelope + purge; Core accounts/administration/backend + WebUi
shares) ✓
**At HEAD:** integrity items=37 live=14 assigned=245 unique=245 dupes=0 encoding=0 ✓ · build 0 warnings ✓ ·
unit **1409 passed, 0 skipped** ✓ · live **144 passed, 0 skipped** on a clean-volume Stalwart ✓
**Carried forward:**
- **Item 21 is no longer PARTIAL.** Its entry was replaced in full; both debts the partial recorded
  (`E3`'s red-first re-proof, the live suite) were paid this run.
- **Two strikes in this run rest on tests that do not guard their fix**, for different reasons, and they
  read differently side by side now than they did individually. `E18`'s tests re-implement the clamp
  expression inside the test body and pass with the production fix reverted — deleting the clamp leaves the
  suite green. `B20`'s test cannot exhibit its symptom on SQLite at all. Both are honestly labelled;
  neither is a correctness problem; but the queue now carries two clamps whose only protection is that
  someone read the diff. If a future round wants one thing from this run, it is a Postgres-backed unit leg,
  which would convert `B20` into a real test and is a precondition for several remaining findings too.
- **`N3` is wrong about its own mechanism, and it bit twice this run.**
  `TlsCertificateRenewalServiceTests.NearExpiryCertificate_IsRenewedOnATick_AndTheHolderIsSwapped` failed
  the first full unit run of *both* items with `CryptographicException: m_safeCertContext is an invalid
  handle` — a use-after-dispose at line 91 (the test reads `stale.Thumbprint` after the service's 20 ms
  `disposeGracePeriod` may have disposed it), not the 5-second timeout `N3` records. `N3`'s proposed FIX
  (widen the timeout / pre-generate the key) does not address that. I confirmed it is environmental, not
  ours: at one point it failed 3/3 at HEAD and passed 3/3 at the pre-run commit, which looked
  commit-shaped — interleaving the two builds flipped the result entirely, so it tracks machine load.
  Both items' suites were green on re-run. **Someone should correct `N3`'s text**; a fix aimed at the
  timeout will not stop this.
  *Correction, added after the run:* this mechanism was **not** newly found here — item 13's entry had
  already named it (`m_safeCertContext`, grace-period disposal) and said it superseded `N3`. What this run
  added is a third and fourth recurrence, and the observation that `N3`'s own text in `review-items.md` had
  never been corrected, so the wrong FIX stayed on the record. Both are now fixed — see the flaky-test
  entry below.
- **The cursor rests at item 23** (`F13` `F14` `F15` `F16` `F17` `F18` `F23` `F27`) — `[LIVE]`, and the
  first item of Phase 3. A natural seam: a fresh orchestrator should take it.

## Out-of-queue — the two flaky tests, fixed
**Not a queue item.** Requested directly after the items 21–22 run. Test-only: **no `src/` file is
touched**, and both production races the tests provoke are correct as they stand.

**Commit:** `<this commit>` (both tests)

**1. `TlsCertificateRenewalServiceTests.NearExpiryCertificate_IsRenewedOnATick_AndTheHolderIsSwapped`**
— the `N3` / item-13 flake, recurring in both of this run's items.

`TlsCertificateRenewalService.DisposeAfterGraceAsync` frees the PREVIOUS certificate once the grace period
elapses (20 ms in the test, 30 s in production). The test kept reading `stale.Thumbprint` afterwards, in
**two** places — the poll predicate and the assertion — so both had to complete inside a 20 ms window to
win the race. Under parallel load they often did not, and it surfaced as `CryptographicException:
m_safeCertContext is an invalid handle`. The test now captures the thumbprint as a `string` before the
service is constructed, so nothing touches a handle the service owns; the timeout also went 5 s → 30 s,
which costs nothing (the wait returns as soon as the swap is observed) and covers `N3`'s original
RSA-under-load theory in case it was ever a second contributor.

**This is a structural elimination, not a widened window** — verified by grep that the only access to
`stale` now precedes `StartAsync`. That distinction is the whole point: widening the timeout, which is what
`N3` proposed, would not have fixed a use-after-dispose at all.

**2. `CliLocalEndpointTests.UnfixedPattern_TwoIndependentWrappersOverOneStringWriter_CorruptsUnderConcurrentWrites`**
— the flake carried forward from item 4's entry ("worth a human deciding whether to make it deterministic").

The test deliberately provokes a data race over a shared `StringWriter` and asserts corruption occurred. But
the race corrupts the `StringBuilder`'s chunk pointers, not just its content, so `ToString()` can **throw**
(`ArgumentOutOfRangeException`) instead of returning a short buffer — and that throw escaped as a test
failure even though it is precisely the corruption the test is hunting for. It now catches it and records
it as the positive signal.

**Verification:** build 0 warnings · full unit suite **4/4 runs green at 1409 passed, 0 skipped** · both
tests looped **10× under concurrent load** (three full unit suites running in parallel as the load
generator) → **0 failures**. For comparison, the TLS test failed the first full unit run of *both* items
earlier in the same session, on the same machine.

**Notes:**
- **Neither fix weakens an assertion.** The TLS test still compares thumbprints and still requires the
  renewed certificate to outlive 300 days; the CLI test still requires corruption to be observed within 10
  attempts. The changes are to what the tests *read*, not to what they prove.
- **`N3` is struck in `review-items.md`** with a RESOLUTION note, because its recorded mechanism was wrong
  and its FIX would not have worked. The original text is preserved rather than rewritten — it is the
  historical record of what was believed — with the correction appended. `N3` has no queue line, so the
  invariants are unaffected: items=37 live=14 assigned=245 unique=245 dupes=0 encoding=0 ✓
- **The second test was never filed as a finding**, only mentioned in item 4's notes. It is fixed here, so
  nothing needs filing now — but it is worth knowing that a flake can live for eighteen items in a notes
  paragraph without ever becoming a tracked item.

## Item 23 — Sync handler status & lifecycle [LIVE]
**Findings:** `F13` `F14` `F15` `F16` `F17` `F18` `F23` `F27`
**Commits:** `0898917` (F13) · `ead305a` (F14) · `da011a1` (F15) · `6bc2f86` (F16) · `b7fdb87` (F17) ·
`e74a93b` (F18) · `f054384` (F23) · `cfe450d` (F27)
**Verification:** integrity items=37 live=14 assigned=245 unique=245 dupes=0 encoding=0 ✓ ·
cursor → item 24 ✓ · one commit per finding, **strike shipped with every one** ✓ · build 0 warnings ✓ ·
unit **1415 passed, 0 failed** (Cli 16 · Protocol 99 · Core 847 · WebUi 120 · Server 334; plus the
8 Axigen-gated tests in the Integration project that the unit filter selects and skips) ✓ ·
live **145 passed, 0 skipped** on a clean-volume Stalwart ✓

**Red-first re-proved independently for 7 of 8.** A whole-`src` revert does NOT compile here — `F17`
changes `FolderSyncHandler`'s constructor, so the test assembly fails to build and `--no-build` silently
runs the STALE binaries, reporting a full green that proves nothing. (Worth remembering: that is the
protocol's own "the revert doesn't compile" warning, and it produced a convincing false pass.) Re-proved
instead by applying the **reverse diff of each commit's `src` portion only**, which keeps the new tests in
place:
- Six findings, unit suite, exactly 7 failures and no others —
  `FolderCreate_NotVisibleInThePostCreateListing_IsRetryable_NotFalseSuccess` (F13),
  `Sync_MultiStoreWait_DrainsTheLosingWait_BeforeReturning` (F15),
  `Sync_ConcurrentCollectionCommitRace_ReturnsStatus5ForThatCollection_SiblingsUnaffected` (F16),
  `UnprimedInitialKey_ReportsStatus3_WithoutEstimating` + `StaleOrMismatchedSyncKey_ReportsStatus4` (F18),
  `F23_MalformedPersistedOptionsJson_FallsBackToDefault_DoesNotThrow` (F23),
  `F27_GetChanges0_WatchdogFalsePositive_HonoursTheHeartbeat_NotATightRepoll` (F27).
- **`F14` re-proved on the live backend** with `EasEndpoint.cs` reversed:
  `AccountOnlyWipe_OnPre161Device_CompletesServerSide_InsteadOfLoopingProvision` fails
  `Expected: Forbidden / Actual: 449` — the permanent-449 loop, exactly as written. The sibling 16.1 wipe
  test stayed green, so the fix does not disturb the acknowledged path.
- **`F17` is the one strike NOT independently re-proved.** Reversing it restores the 1-arg constructor and
  breaks `FolderConformanceTests.cs:84`, so there is no compiling tree that exhibits the old behaviour. I
  read the diff instead: it wraps the whole body and answers Status 6, and its `catch (Exception ex) when
  (ex is not OperationCanceledException)` is **byte-identical in breadth to the sibling it is told to
  mirror** (`FolderModifyHandlerBase`, same file, line 187) — I checked that specifically, because a
  catch-all that swallows a `NullReferenceException` into a retryable status would be a real cost, and the
  finding's "wrap the body in the same catch" sanctions exactly this breadth.

**Notes:**
- **`F18` reverses a decision an earlier round landed, and the finding — not the worker — is the authority
  for that.** Round 2 had "fixed" GetItemEstimate the other way (Status 3 for an invalid key) and shipped a
  test pinning it. Round 3's `F18` says that is backwards and states the mapping: MS-ASCMD's
  GetItemEstimate Status table is its own, where **3 = SYNCSTATENOTPRIMED and 4 = INVALIDSYNCKEY**, and its
  FIX is literally "return `4` for `Invalid`, `3` for `Initial`, and correct the comment" — which is what
  landed. The old comment was wrong twice over: it also claimed 4 meant "collection invalid", which is
  Status **2**. The worker additionally dispatched a research subagent to the published spec before
  proceeding; that was diligence, but the finding already carried the answer.
- **The rewritten test increased coverage rather than reducing it.** The prior round's
  `InvalidSyncKey_ReportsStatus3` kept its input (nonzero key, unprimed) and became
  `StaleOrMismatchedSyncKey_ReportsStatus4` with the corrected expectation — renamed, so the change is
  visible in the test name instead of hidden in an edited assertion — and a NEW test covers the `Initial`
  case, asserting Status 3 **and** the absence of an `Estimate` element. The integration guard
  (`GetItemEstimate_WithStaleSyncKey0_DoesNotResetCollectionState`) moved 1 → 3, and its real guarantee —
  that the query does not mutate primed state — is untouched.
- **Client-visible behaviour changes, several of them real.** `F18`: GetItemEstimate with SyncKey 0 now
  answers Status 3 instead of returning an estimate — correct per spec, but any client relying on the old
  estimate sees a different answer. `F13`: FolderCreate answers retryable Status 6 instead of a false
  success when the backend's own listing has not caught up (Axigen's async indexing is the live case).
  `F14`: a pre-16.1 device with a pending wipe now gets 403 and the wipe completes server-side, instead of
  449 forever. `F16`/`F17`: previously-uncaught exceptions become EAS statuses instead of HTTP 500.
  `F27`: a `GetChanges=0` collection woken by a watchdog false positive now idles out the remaining
  heartbeat instead of answering immediately.
- **`F15` bounds its own drain at 10 s.** The finding asks for the losing waits to be drained rather than
  abandoned; the worker added a `DrainTimeout` so a misbehaving store cannot pin the request (and its
  session lease) open forever. That bound is the worker's own addition, not the finding's — a sensible one,
  mirroring `LongPollWatchdog`, but it means a pathological store still leaks an abandoned wait after 10 s.
- **Test-harness additions, disclosed by the worker:** `EasHandlerHarness.RecordingStore` gained `Listing`
  and `WaitForChangesAsyncOverride`, both defaulting to prior behaviour. Three pre-existing FolderCreate
  tests were updated to set `Listing` — they had been exercising `F13`'s exact defect without asserting on
  it.
- **A third flaky test surfaced, pre-existing, and item 23 made it likelier.**
  `SyncHotPathTests.F14_VanishedItem_IsNotCountedAsSent` (a ROUND-2 F14, unrelated to this item's F14)
  failed my first full unit run and passed the next; it passes 3/3 in isolation. Item 23 does not touch
  that file. Mechanism: the test attaches a `MeterListener` to the process-global `GatewayMetrics` meter
  and asserts `Assert.Equal(sentAdds, addsRecorded)`, where `sentAdds` is its own response but
  `addsRecorded` counts EVERY `server_to_client`/`add` measurement in the process — so any concurrently
  running Sync test inflates it. Item 23 added several Sync-driving tests to the same assembly, which
  raises the collision rate without being the cause. Sent to a scoped repair agent rather than fixed here
  (the orchestrator does not edit tests); see the repair entry that follows.

### Scoped repair (during item 23) — the third flaky test
**Not a queue item.** `8fd6daa` · `SyncHotPathTests.F14_VanishedItem_IsNotCountedAsSent` +
`EasHandlerHarness.RunAsync`. Spawned as a scoped repair agent rather than fixed by the orchestrator.

`GatewayMetrics` is one process-global static `Meter` and xUnit runs the assembly's test classes in
parallel, so the test's `MeterListener` summed every concurrent test's `server_to_client`/`add`
measurements into `addsRecorded` and then compared that against its OWN `sentAdds`. The `user` tag was
useless for filtering because `EasHandlerHarness` hard-codes one shared `UserName` for every test in the
assembly. The repair adds an optional `credentialsUserName` to `RunAsync`, gives this one call a per-run
GUID identity, and filters the listener on it.

**Verified:** both assertions are intact (`Assert.Equal(1, sentAdds)` and `Assert.Equal(sentAdds,
addsRecorded)`) — the isolation is in what the listener *counts*, not in what the test proves. Build 0
warnings; full unit suite green 4× (agent) + 3× (mine), Server.Tests 334/334 every run.

**One risk the agent did not raise, checked here:** the filter now depends on the `user` tag carrying a
real value, and `GatewayMetrics.PerUserLabels` is itself process-global — if anything set it `false`
concurrently the tag would collapse to `"-"` and this test would fail for a brand-new reason. It cannot
happen in this assembly: `PerUserLabels` returns `true` when no provider is wired, and the only
`SetPerUserLabelsProvider` callers are `ProgramServer` (not used by the handler harness) and
`GatewayMetrics`/`Metrics` tests in *other* test assemblies, which `dotnet test` runs as separate
processes.

**Standing observation, now three for three.** Every flaky test found in this programme has the same
shape: a test observing **process-global** state — a static `Meter`, a certificate the production code
owns, a `StringBuilder` under a deliberate race — while the suite runs in parallel. That is a category,
not three coincidences, and it is worth a targeted sweep rather than waiting for each one to surface
mid-verification and cost an item's worth of bisecting.

## Item 24 — IMAP correctness [LIVE]
**Findings:** `G3` `G6` `G7` `G9` `G12` `G13` `G16` `G22`
**Commits:** `bbff8fe` (G3) · `40b5797` (G7) · `e3b040a` (G12) · `af859e0` (G16) · `3854bc7` (G13) ·
`91ce0cc` (G22) · `07c5111` (G6) · `37860e7` (G9)
**Verification:** integrity items=37 live=14 assigned=245 unique=245 dupes=0 encoding=0 ✓ ·
cursor → item 25 ✓ · one commit per finding, **strike shipped with every one** ✓ · build 0 warnings ✓ ·
unit **1424 passed, 0 failed** (Cli 16 · Protocol 99 · Core 855 · WebUi 120 · Server 334) ✓ ·
live **150 passed, 0 skipped** on a clean-volume Stalwart ✓ · scope confined to `Backends.Imap` + tests ✓

**Red-first re-proved independently for 7 of 8**, in three passes, because two commits add internal test
seams and reversing them breaks the test assemblies:
- **Live, all 8 reversed** (the Integration project's build graph excludes `Core.Tests`, so this compiles):
  5 failures, no others — `DeleteItemAsync_UnqualifiedItemKey_IsRejected_NotSilentlyResolved` (G3),
  `ListFoldersAsync_NestedDraftsFolder_IsClassifiedConsistentlyWithTheWritePath` (G12),
  `SearchAsync_UsesTheSameWidenedFloor_AsGetItemRevisionsAsync` (G13),
  `UpdateItemAsync_ContentChangeOutsideDrafts_IsRejected_NotSilentlyDiscarded` (G16),
  `WaitForChangesAsync_IsNotBlockedBehindAConcurrentLongHeldSessionGate` (G22).
- **Unit, G6+G9 reversed:** `WaitForChangeAsync_TransientAuthFailureThenSuccess_DoesNotLatchUnavailable`
  (G6) and `UpdateItemAsync_InterruptedAfterAppend_ThenRetried_ConvergesToOneDraft` (G9).
- **`G7` is the one strike not independently re-proved:** its commit makes `GetOrCreateWatcher` internal
  for the test, so reversing it stops `Core.Tests` compiling. Diff read instead — it compares the whole
  `BackendCredentials` record plus a `ConnectionMatches` field list, which is the finding's remedy.

> **A false green to remember, hit twice in this item and once in item 23.** Reverse-applying a commit that
> added a test seam leaves the test assembly uncompilable, and `dotnet test --no-build` then runs the
> STALE binaries and reports a full pass. It looks exactly like "the fix wasn't needed". Both times the
> only tell was the `error CS` lines above the green summary. Always read the build result before the test
> result when re-proving.

**Notes:**
- **`G22`'s fix is a connection-churn regression that lands in the same item as `G6`, which is about
  connection pressure. This is the one thing in item 24 a human should look at.** The finding says to give
  `SnapshotStatusAsync` "its own lightweight connection (**as `ImapIdleWatcher` already has**)" — the
  watcher's is *persistent*, lazily started and reused. What landed opens a **fresh IMAP connection per
  poll**: `ConnectStandaloneAsync` → LOGIN → STATUS(×folders) → LOGOUT → dispose, inside a 30-second loop
  (`ImapMailBackend.Watch.cs:84`). A Ping can run ~59 minutes, so that is on the order of **~118
  connect+login cycles per Ping per user** — and it only runs when IDLE is unavailable, which is precisely
  the state `G6` in this same item exists to survive. `G6`'s own text names Dovecot's
  `mail_max_userip_connections` (default 10) as a cap *this design already provokes*. The two fixes are
  individually correct and interact badly. Mitigating facts: it is one connection per poll cycle, not per
  folder (all folder keys share the connection), and it is short-lived. The worker disclosed the deviation
  as a "scope trade-off" but did not connect it to `G6`. **Recommend filing a follow-up finding** to make
  the poll connection persistent-and-reused, as the finding originally asked.
- **`G9` trades duplication for loss, and the finding sanctions exactly that.** I checked, because "a
  mid-failure loses the edit" is a serious property to accept on a worker's say-so: `G9`'s FIX reads
  "reverse the order where possible (flag+expunge the old UID first, then append) so a mid-failure loses an
  edit rather than duplicating it, **or** route the rewrite through a `SentCommandToken`-style claim". The
  worker took the first option. The second would have neither lost nor duplicated, and the finding lists it
  as an equal alternative — so this is a judgment call that could reasonably have gone the other way, and
  the losing case is a user's draft edit.
- **`G6`'s retry bound (3) is the worker's number, not the finding's.** The finding says "a bounded number
  of auth failures" without specifying. Consequence, disclosed: a genuinely wrong password now takes up to
  ~15 s of capped backoff before IDLE is reported permanently unavailable, instead of failing at once.
- **`G3` and `G16` are breaking in the client-visible sense.** `G3`: an unqualified item key now throws
  `BackendItemNotFoundException` instead of being resolved against the current UIDVALIDITY — the finding
  argues the only remaining sources are a stale or hostile client, since the schema reinit removed the
  legacy data, and such a client now gets a clean Delete+Add. `G16`: a content-bearing Change outside
  Drafts now throws rather than silently discarding the user's edit while reporting success.
- **`ConnectionMatches` is a hand-maintained field list** (`Host`, `Port`, `UseSsl`, `Security`,
  `AllowInvalidCertificates`, `CaCertificatePath`, `CheckRevocation`, `PathSeparator`). If `ImapOptions`
  gains a connection-affecting field, nothing fails — the watcher just silently keeps the stale connection,
  which is the exact defect `G7` fixes. No test pins the list against the options type.
- **Test approach worth carrying forward:** `G6` and `G9` were proved against two purpose-built fake IMAP
  servers that close the connection at an exact protocol boundary, because a real network fault cannot be
  timed deterministically. That is the right answer for fault-injection findings and mirrors the existing
  `RawSieveServer` pattern — no coverage-only tests were needed anywhere in this item.

### Item 24 — CORRECTION: `G22` rolled back, item 24 is NOT complete
`3a269d4` reverts `91ce0cc` (src + test) and reopens `G22` on the queue line. The entry above records
item 24 as eight-for-eight; that is now **seven**, and `G22` must be redone.

The note above flagged the per-poll connection as a churn regression but still let the strike stand. That
was too generous, and one fact I had not checked at the time makes it clearly wrong: `PollForChangesAsync`
is **not** an IDLE fallback. `WaitForChangesAsync` races it against IDLE on **every** long-poll
(`Task.WhenAny(idleTask, pollTask)`), so the new connect+LOGIN+STATUS+LOGOUT every 30 s is the **normal
path for every device**, not an IDLE-unavailable edge case — on the order of 118 IMAP logins per device
per hour, against `G6`'s own documented `mail_max_userip_connections` cap. Two fixes in one item, pulling
against each other, and the item shipped anyway.

**Lesson for the next orchestrator:** I read `G22`'s diff correctly and described the cost accurately, then
struck it because each fix was individually correct. "Individually correct" is not the bar when two
findings in the same item touch the same resource — the question is what the item does as a whole. When a
fix's cost lands on the same axis another finding in the same item is about, that is a stop-and-report, not
a note.

**Redo guidance** is on the queue line: persistent poll connection owned by `ImapBackendProvider`, keyed on
the gateway login (one per user), its own `SemaphoreSlim` — the defect is the shared GATE, not the shared
connection — plus lazy start, capped-backoff reconnect and `IPerUserResourceOwner` eviction.
**After rollback:** build 0 warnings · unit **1424 passed** · live **149 passed, 0 skipped**.

### Flaky-test follow-up — the concurrent-writer demonstration is deleted, not repaired
CI (Release, Linux) failed `CliLocalEndpointTests.UnfixedPattern_TwoIndependentWrappersOverOneStringWriter_
CorruptsUnderConcurrentWrites` on the pushed tree with *"expected the unfixed dual-writer pattern to
corrupt the shared buffer at least once in 10 concurrent attempts"*.

**My earlier repair of this test addressed the wrong failure mode.** I made a torn `StringBuilder`'s
throwing `ToString()` count as the corruption signal — real, but it can only make the test pass MORE
often. CI hit the opposite mode: no corruption at all in 10 attempts.

**The test was unsound as a gate and is now deleted.** It asserted that a **data race manifests**, which is
a property of core count, scheduler and JIT rather than of this repository — a CI runner with little real
parallelism fails it with nothing wrong. And, exactly like `E18`'s tests, it constructed the pattern
inline and never touched `RunCapturedAsync`, so it could not have caught a reintroduction of the bug
either. A test that cannot detect the regression but can fail without one is pure cost.

`FixedPattern_OneSharedSynchronizedWriter_NeverCorrupts_UnderTheSameConcurrentLoad` stays: its assertion
runs in the SAFE direction (shared synchronized writer ⇒ exact length), which is deterministic anywhere.
The reasoning is written into the comment block so nobody restores the sibling.

**Verified:** `-c Release` (what CI runs) built 0 warnings and the full unit suite ran green **3/3**;
Server.Tests 333 (334 minus the deleted test).

**This is the second time an "it's only a test" flake cost real time, and both had the same root cause as
`E18`: a test that reproduces a pattern instead of exercising the code.** Worth a rule for the remaining
queue — if a test does not execute production code, it is documentation, and it must never be able to fail
nondeterministically.

### `G22` redone — item 24 is COMPLETE again
**Commits:** `e581846` (G22) · `50c1b64` (files `N5`). Worker run on **Opus**, not the pinned Sonnet — a
human decision, taken openly, under `fix-review.md`'s carve-out for structural work: this was ownership,
lifetime, eviction and a second gate, i.e. architecture execution rather than spec execution.
**Verification:** integrity items=37 live=14 assigned=245 unique=245 dupes=0 encoding=0 ✓ ·
cursor → item 25 ✓ · strike shipped with the fix ✓ · build 0 warnings ✓ ·
unit **1426 passed, 0 failed** (Cli 16 · Protocol 99 · Core 858 · WebUi 120 · Server 333) ✓ ·
live **150 passed, 0 skipped** ✓

**What landed:** `ImapStatusPoller` — one persistent client per gateway login, shared by all the user's
devices and folders, owned by `ImapBackendProvider._pollers` beside `_watchers`, with its **own**
`SemaphoreSlim` (the defect was the shared GATE, not a shared connection), lazy connect, capped backoff
that refuses without opening a socket, the same credential-or-`ConnectionMatches` rebuild rule `G7` gave
the watcher with the same atomic compare-and-set, and eviction through the same `TrimUserResources` sweep.
Steady state: 3 connections per user, constant.

**Both halves proved by MY OWN mutation experiments, not the worker's report** — this is the verification
that was missing the first time, when both suites passed a version opening ~118 connections/device/hour
because nothing counted connections:
- **Reuse:** disabling the `_client is { IsConnected: true, IsAuthenticated: true }` short-circuit makes
  the test report **expected 2, actual 9** — it detects exactly the rolled-back shape.
- **Non-blocking:** forcing `SnapshotStatusAsync` back down the session path makes the test take **11 s**
  against its 6 s bound and fail, reproducing the original defect and matching the worker's reported
  11.03 s red on unmodified code.

**Reversal was impossible here, and that is now a pattern worth naming.** `git apply -R` of this commit
deletes `ImapStatusPoller`, which BOTH test assemblies reference, so nothing compiles — and my first
attempt at it ran `--no-build` over stale binaries and produced a result identical to the mutation run,
which I nearly recorded as proof. **Targeted mutation is the better tool whenever a fix introduces a type
the tests name**: it keeps the tree compiling and isolates one behaviour. Third occurrence in Phase 3
(`F17`, `G7`, now `G22`).

**Notes:**
- **The per-user floor moved from 2 connections to 3** (session + IDLE + poll). Bounded and constant, and
  the alternative shapes are the pre-fix gate contention or the rolled-back churn — but operators sizing
  Dovecot's `mail_max_userip_connections` need to know, and `G6` is the finding that cares.
- **One gate now serialises all of a user's devices' polls.** Strictly better than before (they previously
  queued behind the same session gate that also holds the whole-mailbox FETCH) and bounded by the poller's
  30 s per-op timeout, but it is a shared resource across devices where none existed.
- **A narrow leak on eviction-vs-in-flight, which I checked and the worker did not raise.** `DisposeAsync`
  waits at most 5 s for the gate and then disposes the client **anyway**; `StatusAsync` only tests
  `_disposed` at entry, so an in-flight poll that outlives that wait can have its client disposed
  underneath it, treat the failure as transient, retry, and `EnsureConnectedAsync` will open a NEW
  connection that nothing owns. It needs a hung STATUS coinciding with the user's last session going, and
  the bounded dispose is a deliberate choice (an unbounded wait would block eviction forever) — but a
  `_disposed` re-check inside the retry would close it. Worth a finding if anyone touches this file again.
- **`N5` filed by the worker** (`50c1b64`): the `activesync_idle_watchers` gauge and the admin dashboard's
  watcher list enumerate `_watchers` only, so the new poll connection is invisible — the operator sees 1
  where reality is 3, precisely when diagnosing a per-user connection cap.
- **`AGENTS.md` and `docs/configuration.md` were updated in the fix commit**, so the "all IMAP access goes
  through `ImapSession.RunAsync`" convention now names the two provider-owned background connections as
  its deliberate exceptions. Without that the next contributor reads the poller as a violation.
- **`ImapMailBackend`'s constructor gained a trailing optional parameter** — source-compatible,
  binary-breaking, and outside `Contracts`/`Protocol`, so no contract bump; `ContractSurfaceApprovalTests`
  stayed green.
- **On the model question:** the first attempt did not fail from lack of capability — that worker named the
  correct design and traded it away on scope, and *I* struck it anyway. What changed here is that the
  design constraint and the connection-count requirement were written into `review-items.md` before
  spawning, so they could not be lost to a paraphrase or a scope judgment. The stronger model helped; the
  written constraint is what made it reproducible.

## Item 25 — Local stores
**Findings:** `G18` `G19` `G20` `G21` `G26` `G30`
**Commits:** `7f726b6` (G18) · `38f0cec` (G19) · `d3ba23e` (G20) · `e7c6573` (G21) · `4648406` (G26) ·
`b27e42e` (G30)
**Verification:** integrity items=37 live=14 assigned=245 unique=245 dupes=0 encoding=0 ✓ ·
cursor → item 26 ✓ · one commit per finding, strike shipped with every one ✓ · build 0 warnings ✓ ·
unit **1435 passed, 0 failed** (Cli 16 · Protocol 99 · Core 867 · WebUi 120 · Server 333) ✓ ·
live **150 passed, 0 skipped** ✓

**Red-first re-proved independently for all four that claimed it**, in two passes — bulk reversal was
unusable (G18 changes a constructor and G21 a method signature that the tests now use, and G18/G20 have
overlapping hunks in `LocalStores.cs`):
- **Reversal of the four signature-neutral commits** → exactly 2 failures,
  `UpdateItemAsync_ExhaustingRetries_ThrowsBackendException_NotTheRawEfType` (G19) and
  `RespondToMeetingAsync_RetriesOnceOnAConcurrentWrite_LikeEverySiblingWrite` (G20).
- **Targeted mutation for G18 and G21** (rethrow instead of skip; disable the latch check) → 3 failures,
  `SearchGalAsync_SkipsAnUndecryptableRow_...`, `GetBusyPeriodsAsync_SkipsAnUndecryptableRow_...` (G18)
  and `NotifyChanged_BeforeWaitRegisters_MustNotBeLost` (G21).
- **`G26`/`G30` stayed green under reversal**, which is exactly what their "coverage, not proof" labels
  predict — the worker labelled them honestly and the labels hold up.

**The live suite was run although the worker skipped it**, and its stated reason ("only
`Backends.Local` internals, no HTTP endpoint") is defensible but incomplete: the local stores back Sync
for contacts/calendar/tasks and always for notes, so they ARE reachable over HTTP. 150 passed, 0 skipped —
the skip was safe, but that is now verified rather than assumed.

**Notes:**
- **`G21` narrows the race, it does not close it.** `watchStartUtc` is captured inside
  `LocalStoreBase.WaitForChangesAsync`, so a write landing between the Ping handler's own entry check and
  that call still latches BEFORE the reference time and is not returned early. The residual window is the
  handler-to-store call path rather than the old check-to-registration window, so this is a real
  improvement — and closing it fully would need the entry-check timestamp threaded through
  `IContentStore.WaitForChangesAsync`, i.e. a contract change. `ImapMailBackend` has the same shape but
  masks it with a baseline STATUS snapshot the local path has no equivalent of. AGENTS.md's stated
  correctness guarantee (the watchdog re-check) is unaffected.
- **`G21` changed a public signature and the test was rewritten mid-proof.** `LocalChangeNotifier.WaitAsync`
  gained `sinceUtc`, so the red run used the old signature and the test was then updated to the new one.
  The worker disclosed this in the commit message. I re-proved it by mutation instead (disabling the latch
  check on the shipped signature), which does not depend on that rewrite.
- **`G18` is source-breaking inside the repo:** `LocalBackendProvider` now requires `ILoggerFactory`, and
  `LocalContactStore`/`LocalCalendarStore` an `ILogger`. Nine test files were updated mechanically. Not a
  contract concern — these types are outside `Contracts`/`Protocol`.
- **`G19`/`G20` change failure behaviour:** exhausted retries now surface `BackendException` rather than a
  raw `DbUpdateConcurrencyException` (G19), and a meeting response no longer loses a concurrent write
  silently (G20).
- **The seam-vs-reversal problem is now the norm, not the exception** — four items running
  (F17, G7, G22, and here G18/G21). Reversal only works for commits that add no test-visible surface;
  everything else needs targeted mutation. Worth doing mutation-first from here.

## Item 26 — Calendar & contact converter correctness [LIVE]
**Findings:** `D4` `D6` `D7` `D8` `D9` `D10` `D17` `D18` `D19` `D21`
**Commits:** `b98310c` (D4, D9, D10) · `baa158d` (D6, D7, D8, D18, D19, D21 — tight cluster) ·
`6e70ba2` (D17)
**Verification:** integrity items=37 live=14 assigned=245 unique=245 dupes=0 encoding=0 ✓ ·
cursor → item 27 ✓ · strike shipped with all three commits ✓ · build 0 warnings ✓ ·
unit **1449 passed, 0 failed** (Cli 16 · Protocol 99 · Core 881 · WebUi 120 · Server 333) ✓ ·
live **150 passed, 0 skipped** ✓

**The two tight clusters were read as whole commits against every ID in their subject**, as the protocol
requires when per-finding diffs are not separable. `baa158d`'s clustering is justified rather than lazy:
`D7` adds an `actingUserMailAddress` parameter to `ToApplicationData` that the other five findings' tests
need merely to compile, so splitting would have required either faked red state or a scaffolding commit
that fixes nothing.

**Red-first re-proved independently for all ten**, by reversing the two signature-neutral commits and
applying seven targeted single-line mutations to the cluster (TRANSP-only busy status, always-organizer
meeting status, unfiltered alarm action, `Math.Abs` trigger, UTC recurrence anchor, always-UTC EXDATE,
no master-first selector) → **12 failures, covering every finding**: `D4` `D9` `D10` (contacts), `D6` (two
tests), `D7`, `D8`, `D17`, `D18`, `D19`, `D21`. One extra failure,
`Change_OmittingAllDayEvent_PreservesStoredAllDayness`, is collateral from my blunt string replacement
hitting a second `HasTime` site — a mutation artefact, not a finding's test.

**Notes:**
- **`D7` and `D8` change what goes on the wire.** An invitee's own copy now reports `MeetingStatus=3`
  instead of `1`, and Tentative/OOF round-trip through a new `X-MICROSOFT-CDO-BUSYSTATUS` property in the
  stored ICS rather than collapsing to Busy. `D7` deliberately keeps the historical "assume organizer" `1`
  when no acting identity is threaded, so a caller that has not been updated behaves exactly as before —
  a good default, and the three in-repo call sites were all updated.
- **`D6` includes a fix the finding described but never prescribed.** The entry notes in passing that the
  same line "loses the trigger sign" without giving it a `FIX:`. The worker fixed it and chose to emit no
  `Reminder` at all for an alarm scheduled AFTER the start (a shape the write path never produces) rather
  than invent EAS semantics for it. Defensible and disclosed; the alternative reading would have been to
  leave the sign alone as out of scope.
- **`D19` took the required half and not the optional half.** Its FIX says master-first "and consider
  applying the PARTSTAT to *every* VEVENT sharing the UID so overrides stay consistent with the series".
  Only master-first landed, so an override VEVENT can still carry a stale PARTSTAT relative to its master.
  That is within the finding's letter but the series-consistency question is still open.
- **`D17` is not a converter fix at all** — it threads the TLS handshake's own `X509Chain` into
  `ServerCertificateValidator` so a leaf signed by a private intermediate validates against a custom CA.
  Its test drives the real `RemoteCertificateValidationCallback` delegate, whose signature is fixed by the
  BCL, so it achieved genuine red-first with no new seam.
- **`N6` filed by ME, not the worker.** The worker noticed that `TasksConverter.cs:101` has exactly `D21`'s
  UTC-instant anchor bug, correctly declined to fix it under "stay inside the item" — and then did not file
  it, which is the other half of protocol step 8. I have added it to "Found while working the queue" so it
  is not lost. Worth watching: this is the second worker this run to notice something and report it only in
  prose.

## Item 27 — Mail & draft converter correctness [LIVE]
**Findings:** `D11` `D12` `D13` `D14` `D15` `D16` `D20` `D22` `D25`
**Commits:** `bd1bc20` (D11) · `f0b497a` (D12) · `f355397` (D13) · `0ae1c56` (D14) · `a65b67c` (D15) ·
`d98fe77` (D16) · `8eabdee` (D20) · `579f8dd` (D22) · `8833861` (D25)
**Verification:** integrity items=37 live=14 assigned=245 unique=245 dupes=0 encoding=0 ✓ ·
cursor → item 28 ✓ · one commit per finding, strike shipped with every one ✓ · build 0 warnings ✓ ·
unit **1460 passed, 0 failed** (Cli 16 · Protocol 99 · Core 892 · WebUi 120 · Server 333) ✓ ·
live **150 passed, 0 skipped** ✓ · scope confined to `Converters/`, two backend call sites, tests ✓

**Red-first re-proved independently for all nine** — eight by reversal plus a `D14` mutation
(`received = message.Date`, the pre-fix shape), then `D13` separately by mutation
(`string.IsNullOrEmpty(m.Name) ? m.Address : m.Name` → `m.Name`) because its reversal patch would not
apply after the neighbouring hunks moved. 9 + 2 failures, every finding covered.

**Notes:**
- **`D14`'s "coverage" test is better than the worker claimed.** It labelled
  `ReceivedUtc_WhenSupplied_TakesPrecedenceOverTheDateHeader` coverage-not-proof because the new optional
  parameter cannot be expressed against the pre-fix signature — true for *reversal*. Under mutation it
  went red, so it does discriminate the fix and is a genuine guard. Worth generalising: "cannot be proven
  by reversal" is not the same as "cannot be proven", and this run has now hit that distinction five times.
- **`D15` deviates from the finding's prose for a good reason.** The FIX names a
  `FormatOptions.EncodingConstraint` property that does not exist in this MimeKit version; the worker used
  `MimeMessage.Prepare(EncodingConstraint.SevenBit, …)`, which is the actual API achieving the stated
  intent. The finding is wrong about the API, not about the remedy.
- **`D15` legitimately rewrote a pre-existing assertion, and it is not a weakening.** Making the type-4
  body ASCII-safe means a 2000-character whitespace-free run is now quoted-printable soft-wrapped, so the
  older test unfolds `=\r\n` before asserting the text survived intact. Same guarantee, adjusted for
  behaviour the fix deliberately changes — the protocol's "rewrite it and call that out" case, and the
  worker did call it out.
- **Behaviour changes:** `D14` — a message with a missing or forged `Date:` now reports the backend's own
  delivery time (IMAP `INTERNALDATE` / JMAP `receivedAt`), falling back to `UtcNow` instead of year 0001.
  `D20` — a draft rewrite now carries `In-Reply-To`, `References`, `Message-Id` and custom headers, so a
  reply started elsewhere stays in its thread. `D25` — `Limit`'s bound is now a UTF-8 byte budget rather
  than a UTF-16 char count (identical for the ASCII-heavy header text it bounds). `D22` — an unparsable
  stored iCalendar now throws `BackendException` instead of silently emptying the item, which is a
  louder failure by design.
- **`D25` is tested through private reflection.** Precedented in this suite and it targets the offending
  expression precisely, but it means the guard is coupled to the method name rather than to observable
  behaviour.

## Item 28 — JMAP mapping & watcher [LIVE]
**Findings:** `H4` `H7` `H11` `H12` `H14` `H19` `H21` `H25` `H26`
**Commits:** `0ce0376` (H4) · `f0fbcb1` (H7) · `6000375` (H11) · `7adbd79` (H12) · `3ad725b` (H14) ·
`8c3ffe7` (H19) · `e0bbcfd` (H21) · `47e805e` (H25, H26 — cluster, read against both IDs)
**Verification:** integrity items=37 live=14 assigned=245 unique=245 dupes=0 encoding=0 ✓ ·
cursor → item 29 ✓ · strike shipped with every commit ✓ · build 0 warnings ✓ ·
unit **1474 passed, 0 failed** (Cli 16 · Protocol 99 · Core 906 · WebUi 120 · Server 333) ✓ ·
live **150 passed, 0 skipped** ✓ · scope confined to `Backends.Jmap` + tests + `docs/backends.md` ✓

**Red-first re-proved independently for all seven that claimed it** — reversal of the eight commits' `src`
gave **13 failures**: `H4` (2), `H7` (4 — both stores, both the once-per-account download and the paging
assertion), `H11` (3 theory cases), `H12`, `H19`, `H21`, `H26`. `H14` (coverage) and `H25` (doc-only) are
correctly absent.

**Notes:**
- **`H7` was proved by the sequence `fix-review.md` explicitly bans, and the worker described it plainly:
  "wrote fix+test, stashed only the source file, ran red, popped stash, ran green."** That is
  write-fix-and-test-together → revert → see red → re-apply, which the protocol forbids because a test
  written alongside the fix tends to assert what the new code does rather than the symptom. **In this case
  the tests survive the objection**: they count `ids:null` full downloads across two calendars and assert
  paging appears when `maxObjectsInGet` is finite — both symptom-shaped, both red under my own independent
  reversal. So the strike stands on evidence, not on the worker's procedure. Recording it because the
  ordering rule is the most-violated in this programme and a worker disclosing it honestly is the only
  reason it is visible at all.
- **`H14` is coverage, and the worker's reasoning is the best on this queue so far.** It first wrote a
  concurrent stress test, observed that it passed identically on unfixed code — because a continuous
  stream of `Signal()` calls masks the single-signal loss the fix closes — and *discarded it* rather than
  banking a test that proves nothing. The shipped test pins the two observable contracts instead and says
  so in both the comment and the commit message. That is exactly the judgement the protocol asks for.
- **`H7` deviates from its FIX and the deviation is load-bearing.** The finding says to cache and to page
  via `*/query` when `maxObjectsInGet` is finite; but this repo's own AGENTS.md warns JMAP `*/query` is
  FTS-backed and eventually consistent, which is why listing uses `ids:null`. The worker paged **with a
  catch-and-fallback to the unbounded get on any `BackendException`**, which keeps the always-consistent
  path as the backstop. Sound, and the live suite exercises it — but on a server that answers a paged
  query with silently *stale* data rather than an error, the fallback never triggers and the diff engine
  sees a partial revision map. Stalwart answers `serverUnavailable`, so the tested stack is safe.
- **`H21` removes a capability declaration rather than implementing it.** `JmapCalendarStore` no longer
  claims `IReadOnlyCollectionSource`; the finding offered either that or implementing it against
  `Calendar/get`'s `myRights`. Nothing regressed — the capability was never enforced — but a shared JMAP
  calendar is still writable, and now the code no longer pretends otherwise. `docs/backends.md` updated to
  match.
- **`H19` emits the "no photo" status (173) without delivering photos**, which is correct: `H25` (doc-only)
  corrects the class summary that claimed photo coverage JMAP does not actually have.

## Item 29 — DAV polling & folder shape [LIVE]
**Findings:** `H5` `H6` `H15` `H16` `H17` `H22` `H23`
**Commits:** `b275efe` (H5) · `ab2d01d` (H6) · `2033206` (H15) · `c4d4e00` (H16) · `39c957f` (H17) ·
`f3d1a3e` (H22) · `d01c290` (H23)
**Verification:** integrity items=37 live=14 assigned=245 unique=245 dupes=0 encoding=0 ✓ ·
cursor → item 30 ✓ · strike shipped with every commit ✓ · build 0 warnings ✓ ·
unit **1482 passed, 0 failed** (Cli 16 · Protocol 99 · Core 914 · WebUi 120 · Server 333) ✓ ·
live **150 passed, 0 skipped** ✓

**Red-first re-proved independently for all seven** — six by reversal
(`PollCtags_MultipleFoldersUnderOneHomeSet_UsesOnePropfindPerHomeSet` H5,
`SearchGal_ReportOmitsAddressData_FallsBackToEnumeration` H6,
`ListFolders_DefaultTasks_IsChosenDeterministically` H15, `TransportFailure_SurfacesAsBackendException`
H16, `GetBusyPeriods_Self_ExcludesSharedCalendars` H22,
`ListFolders_AllHomeSetCalendarsAreShared_StillProducesADefaultCalendar` H23) and `H17` by mutation
(zeroing the character cap and restoring the 128 MiB byte ceiling → both its assertions fail).

**Notes:**
- **`H23` deliberately breaks an AGENTS.md absolute, the finding sanctions it, and the document was not
  updated — filed as `N7`.** AGENTS.md says a share-granted collection "NEVER claims the default slot";
  `H23` promotes one precisely when every home-set calendar is granted, because "no default at all" is
  worse for iOS. The finding calls the rule "correct and deliberate" and asks for a floor beneath it, so
  the code is right and the doc is now wrong — the same failure mode `S1` already documents. A contributor
  reading "NEVER" would treat the fallback as a bug.
- **`H17` lowers the DAV response ceiling from 128 MiB to 32 MiB** and decouples it from the JMAP blob
  ceiling, plus adds a character cap on parsed XML. A legitimately huge multistatus (>32 MiB) is now
  refused where it previously was not. JMAP's own ceiling is untouched.
- **`H17`'s red-first used the "inert seam first" two-step** (add the unwired knob, prove the assertion
  fails, then wire it), citing the round-1 `H24` precedent. That is a defensible way to red-prove a limit
  that cannot exist before the fix, and I did not have to take it on trust: mutating the shipped constants
  makes both assertions fail, so the guard discriminates the fix either way.
- **`H16` took the finding's "or" branch** — wrapping once at `WebDavClient.SendAsync` rather than widening
  four individual catch clauses. Lower-risk and covers future call sites, but it changes the exception type
  DAV transport failures surface as (raw `HttpRequestException`/`IOException` → `BackendException`); no
  in-repo caller catches the raw types.
- **`H5`'s grouping key is the structural parent of each folder's href**, not a re-discovered home set —
  which matches the finding's own description and keeps shares outside the home set on the fallback path.
- **Live coverage limit worth stating:** `H5` is described in the queue as **Axigen** behaviour, and the
  live run is Stalwart. The mechanism (one PROPFIND per home set instead of per folder) is proven by the
  unit test and the suite is green, but the Axigen async-indexing interaction `H5` names is NOT exercised
  here. `scripts/test-fast` covers Axigen if someone wants that confirmation.

## Item 30 — State layer & retention
**Findings:** `A5` `A6` `A7` `A8` `A9` `B8` `B16` `B18`
**Commits:** `e758475` (A5) · `394766d` (A6) · `3cdd902` (A7) · `df965b0` (A8) · `51c2246` (A9) ·
`6d3326c` (B8) · `a0ddc5a` (B16, B18 — cluster, read against both IDs)
**Verification:** integrity items=37 live=14 assigned=245 unique=245 dupes=0 encoding=0 ✓ ·
cursor → item 31 ✓ · strike shipped with every commit ✓ · build 0 warnings ✓ ·
unit **1490 passed, 0 failed** (Cli 16 · Protocol 99 · Core 922 · WebUi 120 · Server 333) ✓ ·
live **150 passed, 0 skipped** ✓

**Red-first re-proved independently for all six that claimed it** — reversal gave 7 failures:
`UserStore_ConcurrentFirstBump_...` + `GlobalSettingStore_ConcurrentFirstBump_...` (A8),
`CommitFolderHierarchy_ConcurrencyConflict_DoesNotPoisonLaterSaveOnSameContext` (A5),
`RecycleAll_OneThrowingProviderTrim_StillTrimsTheRestUnaffected` (A7),
`RefreshFolderRegistry_DuplicateBackendKey_DedupesInsteadOfPermanentlyFailing` (A9),
`GetOrCreateUser_HotPath_DoesNotJoinBackendRoles` (B8),
`CountDeletionImpact_IncludesOofSettingsAndWebSessionRevocations` (B18). `A6` (coverage) and `B16`
(doc-only) correctly absent.

**The live suite was run although the item is unmarked**, because `A9` sits in the FolderSync path and
`B8` on the authenticated hot path — both reachable over HTTP on every request. 150 passed, 0 skipped.

**Notes:**
- **`A9` turns a hard failure into a silent drop, deliberately.** A duplicate `BackendKey` from a
  misbehaving store previously 500'd every FolderSync for that user; it now dedupes and logs a warning, so
  the extra folder disappears rather than the hierarchy breaking. That is the finding's own remedy, and
  the warning is the only signal an operator gets — worth knowing when diagnosing "a folder vanished".
- **`A6` is coverage and the reasoning is sound**: the race is a few instructions inside a fully
  synchronous method with no await point to gate a deterministic test on, and the fix is a one-line
  value-compared `TryRemove`. Reproducing it would need a production-only seam or a probabilistic stress
  loop. Labelled in both the test comment and the commit.
- **`A8` has an interaction the worker flagged and did not test in isolation.** Routing `BumpStampAsync`
  through `BumpAndSaveAsync` moved it inside two `try`/`catch` blocks that already handle a *different*
  unique violation (a Login collision). The worker traced that `BumpAndSaveAsync` rethrows the original
  exception unchanged when its re-read finds no winner, so the outer handler still fires — but that exact
  interaction is covered only by the suite passing, not by a test aimed at it.
- **`B16` is doc-only but is NOT in the standing doc-only list**, and the worker said so rather than
  quietly treating it as one. Its secondary suggestion — splitting the content delete behind an explicit
  `includeContent` parameter so the destructive branch cannot be reached by omission — was **not**
  implemented and was flagged rather than dropped. That half of the finding is still open in substance
  even though the ID is struck.
- **`SyncStateService`'s constructor gained an optional `ILogger` parameter** for A9's warning; backward
  compatible, no call site needed updating.

## Item 31 — Protocol support types
**Findings:** `W3` `W6` `W12` `W13` `W17` `W18` `W19`
**Commits:** `009d4f8` (W3) · `6b93343` (W6) · `fe8150f` (W12) · `3147645` (W13) · `358a424` (W17) ·
`4a37127` (W18) · `5bae70f` (W19)
**Verification:** integrity items=37 live=14 assigned=245 unique=245 dupes=0 encoding=0 ✓ ·
cursor → item 32 ✓ · strike shipped with every commit ✓ · build 0 warnings ✓ ·
unit **1501 passed, 0 failed** (Cli 16 · Protocol 108 · Core 924 · WebUi 120 · Server 333) ✓ ·
live **150 passed, 0 skipped** ✓

**Protocol hard gate checked explicitly**, since this item touches `src/ActiveSync.Protocol/`:
- **No code-page table changed** (`WbxmlCodePages.cs` untouched), so the "every table change needs a
  round-trip test" rule does not bite here.
- **The published surface did not move**: neither `ContractSurface.approved.txt` nor
  `Directory.Build.props` was touched, and the 17 `ContractSurface*` tests pass — so the surface hash still
  matches its pinned contract version and no `ContractVersionMinor` bump was owed. These are
  behaviour-only changes to existing members.

**Red-first re-proved independently for all seven** — reversal gave 12 failures across both suites:
Protocol.Tests 10 (`W6` ×3, `W12` ×2, `W13` ×2, `W18`, `W19` ×2) and Core.Tests 2
(`MixedNumericAndNonNumericIds_SortOrderIsIndependentOfInputOrder` W3,
`CallerSuppliedCaseInsensitiveComparer_DoesNotForkTheSnapshot` W17).

**The live suite was run although the item is unmarked**, because `EasRequestParameters.FromBase64` parses
the packed query string of **every** EAS 12.1+ request — `W13` adding a `FormatException` there is a
change on the hot request path, not an isolated support type.

**Notes:**
- **`W18` is breaking and the worker chose the stricter of two options for a good reason.**
  `EasDateTime.TryParse`/`Parse` no longer accept the non-conforming no-`Z` basic form. The alternative —
  exposing the tolerance as an opt-in `TryParseLenient` — would have added a public member to
  `ActiveSync.Protocol`, i.e. a published-package surface change requiring a `ContractVersionMinor` bump
  that item 31 does not authorize. Dropping the format keeps the item inside its mandate. No in-repo caller
  needed the tolerance.
- **`W6`/`W12`/`W13` turn silent corruption into exceptions.** `ToBase64` now throws `ArgumentException`
  on an over-length, non-ASCII or unknown-version field instead of emitting a blob its own parser cannot
  read; `FromBase64` throws `FormatException` on a control character in a tag field. Every in-repo caller
  already sends well-formed values, and the live suite confirms no real client path trips it — but a
  malformed client that previously got silently-mangled behaviour now gets a hard failure.
- **`W12` is deliberately stronger than its finding.** The worker validated the pre-cast `int` rather than
  the cast byte, so a value that wraps around onto an allowed version byte is caught too — a case the
  finding's literal check would have missed. `ToBase64_ProtocolVersionByteOverflow_DoesNotWrapIntoAnAllowedByte`
  is the test for it.
- **`W17` is a no-op on every current call path** and is defence for future ones: it normalizes to an
  ordinal-keyed dictionary when the caller's is not already `StringComparer.Ordinal`. The worker verified
  `SnapshotCodec` and `DavItemMap` both already construct with ordinal keys.

## Item 32 — Structural & schema documentation
**Findings:** `S1` `S2` `A11` `A12` `A13` `B9` `B10` `B19`
**Commits:** `6c79b93` (S1) · `523a6c5` (S2) · `fda4ae9` (A11) · `11400a5` (A12) · `585f665` (A13) ·
`4f223ef` (B9) · `ea93e0a` (B10) · `50f5e51` (B19)
**Verification:** integrity items=37 live=14 assigned=245 unique=245 dupes=0 encoding=0 ✓ ·
cursor → item 33 ✓ · strike shipped with every commit ✓ · build 0 warnings ✓ ·
unit **1504 passed, 0 failed** (Cli 16 · Protocol 108 · Core 927 · WebUi 120 · Server 333) ✓ ·
live **150 passed, 0 skipped** ✓

**Six of eight are documentation-only, so "red-first" is N/A by design** — and for those the meaningful
check is not a test but whether the new text is TRUE. I verified the two load-bearing ones against the
code rather than reading them:
- **`S1`**: `ActiveSync.Backends.Common.csproj` really has exactly one `ProjectReference`, to
  `ActiveSync.Contracts`, and AGENTS.md now reads "Depends on Contracts ONLY". The document no longer
  contradicts `DependencyRuleTests.BackendsCommon_DoesNotReferenceCore()`.
- **`A11`**: `LoginBlock` (Entities.cs:259) carries `DeviceKey`, not `UserId`, so moving it out of the
  UserId-FK group is correct; `UserBackendRole` exists and is now listed; the deleted `AccountsStamp`/
  `SettingsStamp` are replaced by `DataChange`; and `User.Json` → `User.Declared` matches the field
  `UserStore.LoadAllAsync` actually filters on.

**Red-first re-proved for both behavioural findings** — `S2` by reversal
(`Cs0618Suppressions_AreScopedNarrowly_NotFileWide` fails for both `CalendarConverter.cs` and
`TasksConverter.cs`); `B19`'s reversal is compile-blocked because the parameter drop updated eleven call
sites, and its test is a reflection assertion that the parameter is absent — trivially red while the
parameter exists.

**The live suite was run despite the item being documentation-heavy**, because `B19` changed call sites in
`PortalEndpoints` and `UsersEndpoints`, which are HTTP-reachable. 150 passed, 0 skipped.

**Notes:**
- **`S2` went further than the minimum and that is the right call.** The finding's complaint was scope, not
  rationale; the worker scoped each suppression to its actual obsolete-API call site (9 pairs in
  `CalendarConverter.cs`, 2 in `TasksConverter.cs`) rather than wrapping whole methods. Repo-wide CS0618
  disables and restores are now balanced 13/13, where the finding recorded the 2-pair imbalance.
- **`B19` took the full-drop option** over the finding's "or keep the signature and note why", updating
  `PortalEndpoints` ×2, `UsersEndpoints`, `UserCommands` ×6 and two tests. Mechanical, and each caller's
  `options` local is still used for `ValidateEntry`/`FindConfigUser`, so nothing was left dangling.
- **`N8` filed by the worker** (properly, in "Found while working the queue"): AGENTS.md's
  "Database-declared accounts" paragraph and one line of "DB-backed global settings" still name
  pre-restructure types — `AccountEntry`, `AccountStore`, `AccountResolver`, `SettingsStamp` — that no
  longer exist in `src/`. Correctly left unfixed as outside `A11`'s cited paragraph. **Note this is the
  same class of defect as `S1` and `A11`, in the same document, found immediately after "fixing" it** —
  AGENTS.md has more post-restructure drift than this item's findings captured.
- **`N7` (mine, from item 29) is still open and also lives in AGENTS.md** — the "a share grant NEVER claims
  the default slot" absolute that `H23` deliberately made false. Whoever sweeps `N8` should take `N7` in
  the same pass.

## Item 33 — Handler & WebUi polish
**Findings:** `C20` `C21` `C22` `C23` `F19` `F20` `F21` `F22` `F24` `F25` `F26` `F28` `F29`
**Commits:** `3a22379` (C20) · `45fd200` (C21) · `bb70c22` (C22) · `29ddcda` (C23) · `66ea51f` (F19) ·
`a4087fb` (F20) · `a4913ff` (F21) · `80f630c` (F22) · `42c36f6` (F24) · `b66aabb` (F25) · `954033f` (F26) ·
`15025f7` (F28) · `ed9b9bf` (F29)
**Verification:** integrity items=37 live=14 assigned=245 unique=245 dupes=0 encoding=0 ✓ ·
cursor → item 34 ✓ · one commit per finding, strike shipped with every one ✓ · build 0 warnings ✓ ·
unit **1523 passed, 0 failed** (Cli 16 · Protocol 108 · Core 927 · WebUi 123 · Server 349) ✓ ·
live **150 passed, 0 skipped** ✓

**Red-first re-proved independently for all ten behavioural findings** — reversal gave 14 failures
(`C22` ×3, `F19` ×2, `F20`, `F22` ×2, `F24`, `F25` ×2, `F26`, `F28`, `F29`), plus `F21` by mutation
(deleting the `deviceId.Length == 0` guard → `EmptyDeviceId_IsRejected` fails).

**The three untestable findings were verified against the code they reference**, since the repo has no JS
test harness and "N/A" would otherwise mean "unchecked":
- `C20` — `.notice.error` really exists (`app.css:315`) and `tls.js:47` now emits `notice error`.
- `C21` — `--accent-2` is defined in all three palette blocks of `theme.css` and consumed by `app.css:69`.
- `C23` — `admin/app.js:72` toggles `nav-portal` on `mode.userPortalEnabled`, and the server really sends
  it (`AuthEndpoints.cs:37`, an anonymous-type member on `auth/mode`). I initially could not find the field
  and suspected the link would be hidden unconditionally; it was a capitalisation miss in my own grep, not
  a defect.

**The live suite was run although the item is unmarked**, because `F21` (empty DeviceId now 400),
`F22` (attachment FileReferences gated on the folder registry) and `F26` (12.x raw-form errors become HTTP
statuses) all change behaviour on the request path. 150 passed, 0 skipped.

**Notes:**
- **`F21` is the one with real deployment risk**, and it is the right fix: a POST with no DeviceId is now
  rejected with 400 instead of sharing a single `""`-keyed `Device` row — SyncKeys, snapshots and PolicyKey
  all collapsed together for every such client. Anything in the wild that omitted DeviceId stops working,
  loudly, which is the point. OPTIONS is mapped separately and never reaches the check.
- **`F21`'s red-first needed a two-step and the worker disclosed it**: `IsValidDeviceId` was `private`, so
  it changed visibility to `internal` FIRST, confirmed genuine red on the accept-empty behaviour, then
  applied the logic fix. The accessibility change touches no logic, so the red is honest — and my own
  mutation of the shipped guard reproduces it independently.
- **`F22` closes a real reachability hole**: `ItemOperations` Fetch and `GetAttachment` previously served
  attachments from folders outside the user's registry. Both refusal paths are now tested.
- **`F26` changes the 12.x wire contract deliberately** — raw-form ComposeMail failures answer an HTTP
  status with no body instead of a WBXML ComposeMail response the 12.x form never expects.
- **`F19` renumbers ComposeMail statuses** (empty/undecodable MIME 103 → 107) and **`F28` adds
  `airsync:Class`** to 12.1 GetItemEstimate responses; three pre-existing 14.1 tests confirm the 14.1 wire
  form is byte-identical, which is the AGENTS.md invariant that matters here.

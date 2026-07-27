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

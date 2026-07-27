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

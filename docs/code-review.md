# ActiveSync Gateway — full source review

Scope: all production code under `src/` (~42k lines, 14 projects). Tests, docs and CI read for context only.
Method: nine parallel deep-read passes (one per subsystem) plus a cross-cutting structural pass.
Baseline: solution builds clean, **0 warnings**. No `async void`, no empty catches, no sync-over-async, no TODO/FIXME debt. Every analyzer suppression carries a correct justifying comment. This is a well-disciplined codebase — the findings below are mostly about asymmetry (a rule applied in one place and not its siblings), boundaries, and untrusted-input limits, not hygiene.

**258 findings.** 3 Critical, 34 High, 96 Medium, 89 Low, 36 Nit.

---

## How to use this document

Findings have stable IDs (`D1`, `K56`, `F23`…). Hand back any set of IDs or any **Block** number and that is enough context to implement it — every finding carries `file:line`, the defect, and the intended fix.

Part 1 is the **work blocks** — groupings that make sense to do together, ordered by what to do first. Part 2 is the **full finding list** by area. Unabridged detail for areas A–D (Core state/sync/backend, Core accounts/settings/options, WebUi, Backends Common/Imap/Smtp/Local/Sieve) is in [`code-review-detail.md`](code-review-detail.md).

### Working a block in a fresh session

Each block is written to be self-contained, so a new session can be pointed at exactly one:

> Read `docs/code-review.md`. Implement **Block 5** (`A2` `A11` `A12` `A24` `A28` `D27` `D28` `K60`). Do not touch anything outside those findings.

**Run one block at a time.** The blocks were grouped by *subject*, not by file, so several of them touch the same files from different angles — `SyncStateService.cs` appears in Blocks 6 and 14, `BackendSessionFactory.cs` in Blocks 5 and 6, `SyncHandler.cs` in Blocks 1, 9, 10 and 14. Two sessions working different blocks in parallel will conflict in those files.

If parallel work is needed, these sets are close to file-disjoint and can overlap safely:
- **Block 2** (Protocol/WBXML + metrics + throttle) — `ActiveSync.Protocol`, `GatewayMetrics`, `AuthThrottle`
- **Block 3** (auth/session/privilege) — mostly `ActiveSync.WebUi` + `PluginLoader`
- **Block 13/H-items** (JMAP/DAV incremental sync) — `ActiveSync.Backends.{Jmap,Dav}`

Block 14 (assembly boundaries) moves types between projects, so it should run alone and land before or after everything else, never alongside.

### Keeping this document current

When a finding is fixed, mark it rather than deleting it — `~~D1~~ **FIXED** (commit abc1234)` — so the IDs stay stable for any session still referencing them.

**Severity**: Critical = data loss or process death in normal operation · High = security hole, corruption, or a feature that silently doesn't work · Medium = wrong behaviour in a reachable case, or a real performance/maintainability problem · Low = latent, narrow, or defence-in-depth · Nit = polish.

---

# PART 1 — WORK BLOCKS

## Block 1 — Data loss and corruption in item handling ⚠️ do first
**Why together:** every item here silently destroys or mangles user data during ordinary use. Same review pass, same test surface (a real-backend round-trip suite).
`D1` `D2` `D4` `D6` `D15` `D16` `D17` `D22` `D23` `H4` `H5` `H6` `H7` `H23` `F2` `F3` `A21`

The three worst: `D1` a folder-wide `EXPUNGE` that permanently deletes *other* clients' `\Deleted` mail on every EAS delete; `D2` zero UIDVALIDITY tracking anywhere in the repo, so after a mailbox restore/migration the gateway applies deletes and flag changes to the wrong messages; `D4` a ghosted contact Change wipes name, emails, address, photo and note. `H7` (JMAP `update` treated as replace, not RFC 8620 patch) means cleared fields never reach the server — resolve that semantic question against the live Stalwart account first, it gates `H4`–`H6`.

## Block 2 — Untrusted-input limits
**Why together:** all "the cap is applied one step too late". Phones are the attacker here; each is a cheap fix plus a hardening test.
`W1` `W2` `W3` `W4` `W6` `W8` `W17` `K1` `K26` `K33` `E2` `L21`

`W1` (no WBXML depth/element cap → multi-GB heap from one 64 MB body) and `W2` (unbounded encoder recursion → uncatchable `StackOverflowException`, process death) are ~15 lines of code between them and take down every user's sync. `K1`/`E2` let an *unauthenticated* caller mint unbounded Prometheus time series. `K26` grows the throttle table without bound while doing an O(n) scan per failed login.

## Block 3 — Authentication, session and privilege boundaries
**Why together:** one security pass over who can do what.
`C1` `C2` `C3` `C4` `C7` `C8` `C14` `C16` `C17` `K38` `B22` `L22` `L27` `K54` `E3` `E14` `E26` `F23` `F21` `F46`

Highest: `C1` a non-admin portal user can repoint their own backend at an arbitrary host and harvest the stored credential (SSRF + credential exfiltration from the lowest privilege level). `F23` the Provision phase-2 branch never compares the presented PolicyKey, so the entire device-policy handshake is bypassable in one request. `K38`+`B22` `Plugins:Directory` is DB-settable, turning admin-UI access into arbitrary code execution in-process. `C3` sessions are never revalidated, so disable/block/revoke don't take effect for up to 12 sliding hours. `E3` the auth throttle keys on `RemoteIpAddress`, which behind an ingress is one shared key — any user's password fumbles 429 the whole gateway.

## Block 4 — Secret handling and redaction
**Why together:** there are currently **four** independent redaction implementations (CLI `Mask`, `StartupSummary.Redact`, WebUi `SecretMask`, `MailKitWireLogger.Redact`) and each has a different idea of what to hide. Unify to one, then fix the leaks.
`K56` `K9` `K14` `K45` `K46` `K47` `B4` `B5` `B18` `B19` `C5` `C6` `E15` `E23` `L23` `L29` `L30` `K37` `K53` `L42` `L43`

`K56` is the one to fix today: `BackendCredentials` is a `record`, so its synthesized `ToString()` prints the plaintext password — and `ResolvedRole`/`BackendConnectionContext` recursively print it too. This is *published plugin contract*, so it's the easiest way for third-party code to dump every user's mail password into logs. `B4` conflates "no key configured" with "key misconfigured" and writes backend passwords in cleartext on the latter. `B5`/`L29`/`L30` the CLI stores and echoes secrets the web UI seals and masks.

## Block 5 — Backend session lifetime and connection ownership
**Why together:** one refactor fixes all of it. `CompositeBackendSession` is shared mutable state with no ownership model.
`A2` `A11` `A12` `A24` `A28` `D27` `D28` `K60`

`A2`: `SessionIdleMinutes` defaults to 15 while `MaxHeartbeatSeconds` defaults to 29.5 min, so the eviction timer disposes sessions mid-Ping while IMAP IDLE is still using them. Introduce an `IAsyncDisposable` lease that refcounts use, gates concurrent access per session (MailKit's `ImapClient` is not thread-safe and clients *do* pipeline), and defers disposal until the last lease releases.

## Block 6 — EF Core / state-layer correctness and cost
**Why together:** all rooted in one request-scoped `DbContext` used as a bag of independent repositories.
`A1` `A3` `A4` `A5` `A6` `A7` `A8` `A9` `A10` `A17` `A18` `A19` `A22` `A34` `A35` `C10` `C15` `C18` `E13`

`A1` is the sharp one: the folder-registry retry detaches the **entire** change tracker, silently dropping the already-tracked `Device.FolderSyncKey++` — the client is acked N+1 while the DB holds N, guaranteeing a Status 9 full resync. `A4` rewrites the full snapshot JSON twice per sync round (2–3 MB per request on a 50k mailbox) — this is the dominant steady-state cost. Decide one policy: explicit transaction per EAS command, or independent operations take their own context from `ISyncDbContextFactory`.

## Block 7 — Configuration validation unification
**Why together:** validation lives in three places that disagree, and every item here is the same bug shape — *accepted by the gate it passes, rejected by a gate it meets later*.
`B1` `B9` `B10` `B11` `B12` `B14` `B24` `B25` `B26` `B31` `B32` `E11` `E19` `E33`

`B1`: `eas config set ActiveSync:Eas:WatchdogSeconds 5` is accepted, persists to the DB, runs fine — and then the gateway **refuses to start** on the next restart with the bad value baked in. Delayed-brick failures like this are the dangerous ones in an unattended-restart deployment. One fix collapses most of the block: make the write path bind the candidate onto a cloned `ActiveSyncOptions` + `BackendRolesConfig` and run the *same* validator startup runs.

## Block 8 — Account resolution, storage casing, and fail-closed
**Why together:** one subsystem, and `B2` is the root of several.
`B2` `B3` `B6` `B7` `B8` `B13` `B15` `B16` `B17` `B21` `B23` `B28` `L31` `L32`

`B2`: logins/keys are matched **case-sensitively in SQL** and **case-insensitively in memory** — so `eas user set Phone1` when `phone1` exists inserts a second row the unique index doesn't catch, and which override wins flips between restarts. Two call sites already carry hand-written workarounds, which is the tell that the invariant belongs in the entity configuration (collation). `B3` is the fail-open one: an invalid DB account row degrades that login to credential pass-through **and un-disables it**.

## Block 9 — EAS protocol conformance
**Why together:** one MS-ASCMD pass; several are wrong status codes that push clients into resync loops.
`F1` `F4` `F5` `F6` `F8` `F9` `F19` `F24` `F25` `F26` `F27` `F34` `F36` `F37` `F38` `F41` `F42` `F45` `F47` `F48` `W20` `W21`

`F4` echoes the *rejected* sync key back with Status 3, so a trusting client re-sends it — the exact resync loop the codebase tries to avoid (Exchange returns key 0). `F1` `<Sync/>` with an empty `Collections` element gets Status 13 instead of replaying. `F6` `MIMESupport` is read nowhere in the repo and Type-4 BodyPreference is force-downgraded to HTML, so S/MIME can never be verified on-device. `F36` `Total` reports the page size, so search silently stops after page 1.

## Block 10 — Send/submit ordering and idempotency
**Why together:** identical bug shape in four places — work *after* an irreversible send can report the send as failed, and the client resends.
`F10` `F29` `F30` `D9` `H18` `L36`

The rule to apply everywhere: close the `try` around the submit only; everything after it (file-to-Sent, mark-answered, delete-draft, SMTP `QUIT`) is best-effort, logged and swallowed; and record the replay marker **before** the irreversible step, not after.

## Block 11 — Long-poll and push reliability
**Why together:** Ping is the most important path for battery life and perceived latency, and the watchdog↔caller contract isn't enforced.
`E7` `E8` `E12` `F11` `F16` `F17` `F18` `H17` `H19`

`E7` a watcher that completes non-positively is dropped for the rest of the heartbeat (with `WatchdogSeconds=0` this degenerates into a tight re-poll loop). `E8` one faulting watcher aborts the whole Ping into a 500, and the drain is unbounded. `E12` `KeepAliveTimeout = 65 min` doesn't do what its comment says — it has no effect on long-poll duration and instead keeps every dead phone socket alive for an hour.

## Block 12 — Hot-path performance
**Why together:** measurable win for a polling fleet, all in request-path code.
`E4` `E5` `E6` `E18` `E31` `E35` `F13` `F14` `F15` `F28` `F40` `F43` `D3` `D14` `D19` `D32` `H13` `H14` `H15` `H24` `H25` `L35` `L41` `B7` `B28`

`E4` every EAS request constructs **all 20 command handlers** and their full dependency graphs (MS.DI materializes `IEnumerable<T>` eagerly) and throws 19 away — switch to keyed services. `D3` every mail fetch downloads the complete message including attachments, then *decodes every attachment into a MemoryStream just to read `.Length`* for the size estimate — while holding the per-user IMAP gate. `L35` every CLI command builds a parallel DI container, EF model and plugin set next to the warm one the host already has.

## Block 13 — Incremental sync (architectural)
**Why together:** one design decision with the widest blast radius in the backends. Worth an explicit decision record either way.
`H16` `H8` `H9` `H11` `H12` `A4`

Neither JMAP nor DAV ever uses `Foo/changes`, `sync-collection`, `queryState` or `ifInState` — verified by grep. Every sync is a full enumeration diffed against a snapshot. DAV even *fetches* `sync-token` and uses it only as a change sentinel. Defensible as v1 (always correct, never stale) but it directly causes `H8`, `H13`, `H14`, `H15` and doesn't scale past a few thousand items per collection.

## Block 14 — Assembly boundaries and structure
**Why together:** these are the "what should move where" decisions; doing them piecemeal causes churn.
`S1` `S2` `S3` `S4` `S5` `K49` `K57` `K58` `K59` `K61` `K69` `A33` `D-split` `L-verdict` `F-decomp`

- **`S1` (do this one):** `Backends.Common` is a *packed, plugin-facing* NuGet package that still `ProjectReference`s all of `ActiveSync.Core` — dragging EF Core, Npgsql, SQLite, migrations, account resolution and TLS into every plugin's graph. I verified its **only** real Core usage is a single `WireLog.Payload(...)` call at `MailKitWireLogger.cs:95`; the seven `using ActiveSync.Core.Backend;` directives are dead (Core/Backend declares exactly 7 types, no extension methods, none referenced anywhere in Backends.Common). Move `WireLog.Payload`, `TransientRetry` and `BackendConfigField` into Contracts → Backends.Common drops the Core reference entirely and becomes what its own csproj comment claims it is.
- **`S2`/`K49`:** `ActiveSync.Crypto` declares its types in `ActiveSync.Core.Security` / `ActiveSync.Core.Options`. So the slim `eas` CLI — whose entire selling point is *not referencing Core* — compiles `using ActiveSync.Core.Security;`. Fix before the package has external consumers.
- **`K57`:** `IBackendSession`/`IBackendSessionFactory`/`BackendSessionInfo` are host composition types, not plugin surface — move to Core so they stop being policed by the major-version gate.
- **`S3`/`C18`:** WebUi injects `SyncDbContext` directly in four endpoint files, so there are two write paths to the same tables. That is precisely why `C10`, `C15` and `C16` exist only in those files.
- **`S5`/`K71`:** make the boundary machine-checked — an architecture test asserting `typeof(IGatewayPlugin).Assembly.GetReferencedAssemblies()` contains only Protocol + Microsoft.Extensions + framework. Today it's enforced by a csproj comment.
- **Verdicts:** Server→WebUi direction is **right**, keep it. Per-protocol backend split (Imap/Smtp/Sieve/Local) is **right**, keep Smtp and Sieve separate despite their size — they fill different *roles*. CLI should **not** be split out of Server (`ServeCommand` calls `RunServerAsync`, `/cli` needs the command tree in-process); the real problem it's reacting to is `L35`.
- **Decomposition targets:** `SyncHandler.cs` (826 lines) → 6 partials + `SyncCollectionOptions` + `ClientCommandLedger` (detailed plan in `F-decomp`). `SyncStateService.cs` (535 lines, 6 responsibilities, unsealed) → 4 types along its own banner comments. `ProgramServer.RunServerAsync` (245 lines) → 4 Setup extensions.

## Block 15 — Silent failure and observability
**Why together:** same shape — a `catch` that's right in intent but leaves no trace when the failure is permanent rather than transient.
`E9` `E10` `E11` `E24` `E34` `C11` `K2` `K3` `K4` `K5` `K10` `K43` `L24` `H12` `B9`

`E9` is worst because its failure mode is *the loss of the diagnostic channel itself*: the DB log drain's bare unlogged catch means a persistent failure is indistinguishable from a blip, and anything thrown outside the inner try kills the drain permanently. Adopt one policy: log the first occurrence and every Nth thereafter, to `SelfLog` where the logger itself is suspect.

## Block 16 — Timezone and date correctness
**Why together:** all shift user-visible times, all need the same test corpus.
`W15` `W16` `D5` `D12` `D24` `D33` `H23` `H30`

`W15`: `EasDateTime.ToLong/ToCompact` call `ToUniversalTime()`, which for `DateTimeKind.Unspecified` (what SQLite hands back) treats the value as *local* and subtracts the machine offset — silent, timezone-dependent corruption of every `StartTime`/`DateReceived` that looks fine in UTC CI and wrong in production. `D12` calendar events are always anchored to UTC, discarding the client's timezone, so recurring meetings drift an hour across DST.

## Block 17 — Test coverage gaps
**Why together:** each converts a class of bug from "found by review" to "found by CI".
`C19` `K31` `S5` `W13-test` `H-roundtrip` `L33` `S6`

- No endpoint-authorization test (the highest-risk property in WebUi is guaranteed only by a mapping convention).
- No `AuthThrottleTests`, no `GatewayMetricsTests` — the two files with mutable shared state and locks.
- No per-backend test project (Core.Tests hosts them); WebUi.Tests is 269 lines for a 2k-line admin surface; Protocol.Tests is 456 lines for a codec parsing untrusted input.
- No JSCalendar/JSContact round-trip suite — **five of seven High findings in the JMAP backend live in those two converters**, and they share a signature: written in one shape, read in another, round-tripping *stably* after the first pass so nothing notices.
- CLI errors go to raw `Console.Error` rather than the injected console (`L33`), so no CLI test can assert on any error message or exit code — the entire failure surface of an admin CLI is untestable.
- Nothing enforces the Sqlite/Npgsql migration sets stay in lockstep (they are 1:1 across all 15 today).

## Block 18 — Remaining nits and small cleanups
`A20` `A23` `A25` `A26` `A27` `A29` `A30` `A31` `A32` `B27` `B29` `B30` `C12` `C13` `C20` `C21` `D25` `D26` `D29` `D30` `D31` `D34` `D35` `D-nits` `E17` `E20` `E21` `E22` `E25` `E27` `E28` `E29` `E30` `E32` `F12` `F22` `F31` `F32` `F33` `F39` `F44` `F46` `H1` `H2` `H3` `H10` `H20` `H21` `H22` `H26` `H27` `H28` `H29` `H31` `K6` `K7` `K8` `K11` `K12` `K13` `K15` `K16` `K17` `K18` `K19` `K20` `K21` `K22` `K23` `K24` `K25` `K27` `K28` `K29` `K30` `K32` `K34` `K35` `K36` `K39` `K40` `K41` `K42` `K44` `K48` `K50` `K51` `K52` `K55` `K62` `K63` `K64` `K65` `K66` `K67` `K68` `K70` `L25` `L26` `L28` `L34` `L37` `L38` `L39` `L40` `L44` `L45` `L46` `L47` `L48` `W5` `W7` `W9` `W10` `W11` `W12` `W13` `W14` `W18` `W19`

Note several of these are Medium and only "nit-blocked" because they're isolated — notably `H1` (DAV readiness probe disables TLS validation unconditionally), `H2` (percent-decoded hrefs re-resolved as URIs → wrong resource fetched), `H3` (`If-Match` silently dropped for unquoted ETags → lost update), `K19` (AAD delimiter ambiguity), `K62` (`SharedCollection.Parse` fails **open** on an unknown mode suffix → read-write), `K64` (unparseable `baseUrl` skips the cross-host guard entirely).

---

## Recommended order

1. **Block 1** + **Block 2** — data loss and process-death.
2. **Block 3** + **Block 4** — security.
3. **Block 14 `S1`/`S2`** — cheap, and every later refactor is easier once the plugin boundary is real.
4. **Block 7** + **Block 8** — the delayed-brick and fail-open configuration classes.
5. **Block 5** + **Block 6** — the two big correctness refactors.
6. **Block 9** + **Block 10** + **Block 11** — protocol conformance and reliability.
7. **Block 17** — lock in what's been fixed.
8. **Block 12**, **13**, **15**, **16**, **18** — as capacity allows.

---
---

# PART 2 — FULL FINDING LIST

*(Areas A–D are recorded in full detail in [`code-review-detail.md`](code-review-detail.md); summarized here for completeness. Areas E–W are given in full below.)*

## Area A — Core: State / Sync / Backend (35)
`A1` **High** Folder-registry retry detaches the entire change tracker, dropping the tracked `Device.FolderSyncKey++` while the client is acked with the incremented value — `State/SyncStateService.cs:176`. Detach only `Entries<UserFolder>()`, or use a dedicated context.
`A2` **High** Idle eviction disposes sessions an active Ping is using (`SessionIdleMinutes` 15 vs `MaxHeartbeatSeconds` 1770) — `Backend/BackendSessionFactory.cs:246`.
`A3` **High** Per-item DAV id mapping is a hard N+1: one SELECT + one full `SaveChanges` per item — `State/SyncStateService.cs:417`.
`A4` **High** Snapshot JSON fully rewritten twice per round; 2–3 MB/request on a 50k mailbox — `State/SyncStateService.cs:382`.
`A5` **Med** `SaveChangesAsync(bool, ct)` not overridden, so concurrency stamping can be bypassed — `State/SyncDbContext.cs:104`.
`A6` **Med** `Device.FolderSyncKey` bumped with no concurrency token; pipelined FolderSyncs lose updates and 500 — `State/SyncStateService.cs:243`.
`A7` **Med** Whole folder hierarchy deleted+reinserted on every commit — `State/SyncStateService.cs:246`.
`A8` **Med** Folder diff never compares `Type`, so a folder changing class never issues an Update — `State/SyncStateService.cs:228`.
`A9` **Med** Over-broad `DbUpdateException` catches mask real failures and rethrow a confusing secondary — `State/SyncStateService.cs:97,433,174`.
`A10` **Med** Every mutating helper flushes the whole request; no transaction spans a multi-collection Sync — `State/SyncStateService.cs:299…532`.
`A11` **Med** `GetSessionAsync` queries share grants per request for a value only used on session creation — `Backend/BackendSessionFactory.cs:157`.
`A12` **Med** `CompositeBackendSession.DisposeAsync` leaks remaining connections if one throws — silently — `Backend/CompositeBackendSession.cs:82`.
`A13` **Med** `EvictIdleSessions` is an unguarded timer callback — an escaping exception **terminates the process** — `Backend/BackendSessionFactory.cs:246`.
`A14` **Med** `ValidateFields` matches scalars case-sensitively, lists case-insensitively — `Backend/BackendConfigValidation.cs:40`.
`A15` **Med** `MergedFreeBusy`: "no data" (`'4'`) outranks "busy" — a busy slot is reported as unknown — `Backend/MergedFreeBusy.cs:31`.
`A16`–`A35` Low/Nit: inverted free/busy window and unvalidated `Kind` (`A16`); detached `CollectionState` on Invalid (`A17`); entity left dirty after concurrency failure (`A18`); tracking on read-only queries (`A19`); two unreachable branches in `CollectionDiff` (`A20`); deletes bypass the window (`A21`); wipe-ack check-then-act race (`A22`); double provider resolve (`A23`); unsynchronized `LastUsedUtc` (`A24`); publicly mutable `TransientRetry.DelaysMs` (`A25`); retry ignores cancellation (`A26`); unsalted cached password hashes, non-constant-time compare (`A27`); handlers never unsubscribed (`A28`); misleading `created` log (`A29`); `ListRoot` contradicts its doc (`A30`); `Add`/`AddAsync` inconsistency (`A31`); `ct` not last (`A32`); `SyncStateService` unsealed, 6 responsibilities (`A33`); duplicated snapshot deserialization (`A34`); soft-deleted folders never reclaimed (`A35`).

## Area B — Core: Accounts / Administration / Settings / Options (32)
`B1` **High** A CLI-settable value passes catalogue validation then blocks the next startup (delayed brick) — `Administration/SettingKeys.cs:67` vs `Options/ActiveSyncOptionsValidator.cs:25`.
`B2` **High** Case-sensitive in SQL, case-insensitive in memory → duplicate rows, nondeterministic winner — `Accounts/AccountStore.cs:65,89,113`, `Settings/GlobalSettingStore.cs:41,57,80`.
`B3` **High** An invalid DB account row degrades to credential pass-through **and un-disables** the account — `Accounts/AccountResolver.cs:271`.
`B4` **High** `TryLoadKey` error discarded → misconfigured key stores backend passwords in plaintext — `Administration/AccountSecretPolicy.cs:53`.
`B5` **High** `eas config set` stores catalogue secrets in plaintext; the web UI seals them — `Administration/SettingKeys.cs:136,149`.
`B6` **High** Failed snapshot rebuild leaves stale auth config, never retries, and logs the wrong subsystem — `Accounts/AccountResolver.cs:142`, `Settings/SettingsRefresher.cs:58`.
`B7` **High** Every account edit re-validates every account on a request thread with file I/O; auto-provisioning is O(N²) — `Accounts/AccountResolver.cs:239`.
`B8` **High** `AccountEditing` uses the wrong comparer for config users, silently wiping their overrides — `Administration/AccountEditing.cs:27`.
`B9`–`B22` **Med**: DB outage hidden behind Debug, silently downgrading restart-tier settings (`B9`); `Number` settings ignore Min/Max and accept NaN/Infinity (`B10`); negative `UsersRefreshSeconds` permanently disables its own repair (`B11`); bootstrap keys unenforced in the store and provider (`B12`); legacy JSON conversion drops `Admin`/`Enabled`/`AutoProvisioned` (`B13`); live role rebuild never runs provider validation, and kept snapshots read through live anyway (`B14`); `GetAsync`/`ListAsync` throw on a row `LoadAllAsync` tolerates — breaking the tools you'd use to fix it (`B15`); null settings delete inherited globals but the doc says ignored (`B16`); untrimmed `Provider` silently disables inheritance (`B17`); a sealed gateway password never authenticates and is never reported (`B18`); empty gateway password hashes and verifies — bypassing the backend entirely (`B19`); unsanitized login text reflected into logs enables log forging (`B20`); config users on an unconfigured gateway throw at startup (`B21`); `Plugins:Directory`/`UsersFile` DB-settable → code load + arbitrary file read (`B22`).
`B23`–`B32` Low/Nit: unsynchronized `_lastDbUsers` (`B23`); `Provider` switch leaves stale settings unchecked (`B24`); secret detection is a `"Password"` suffix match (`B25`); several bounds absent from the options validator (`B26`); IPv6 host brackets break Npgsql (`B27`); `OrderedRoles` sorts per read (`B28`); help string inside a key collection (`B29`); empty settings key accepted (`B30`); generic helper hardcodes "BaseUrl" (`B31`); unreachable duplicate-role check (`B32`).

## Area C — WebUi (21)
Baseline verified good: no endpoint is unauthenticated by accident (route-group `RequireAuthorization`, exactly four deliberate `AllowAnonymous`), **zero** XSS sinks in ~2000 lines of hand-rolled frontend, TLS private keys structurally excluded, login timing-indistinguishable, destructive ops require typed confirmation, OIDC principal re-minted so IdP claims never enter the session.
`C1` **High** Portal users can rewrite their own backend connection settings → SSRF + backend credential exfiltration — `Api/PortalEndpoints.cs:208`.
`C2` **High** Session cookie uses `CookieSecurePolicy.SameAsRequest` → no `Secure` flag behind a proxy that doesn't forward proto — `Setup/WebUiServiceCollectionExtensions.cs:43`.
`C3` **High** No `OnValidatePrincipal`: disable/block/admin-revoke don't affect live sessions for up to 12 sliding hours — `Setup/WebUiServiceCollectionExtensions.cs:44`.
`C4`–`C11` **Med**: OIDC correlation/nonce cookies keep `SameSite=None` without guaranteed `Secure` (`C4`); per-account backend `Settings` returned verbatim, unlike the global section (`C5`); portal password change bypasses the shared policy, no strength floor (`C6`); OIDC trusts mutable `preferred_username` → takeover at some IdPs (`C7`); `AdminClaim` without `AdminClaimValue` grants admin to the whole directory (`C8`); backend probe returns raw exception text → internal network scanner (`C9`); devices/shares endpoints unbounded (`C10`); `DbXmlRepository` discards unreadable keys with zero operator signal (`C11`).
`C12`–`C21` Low/Nit: no DataProtection revocation path (`C12`); `backends/meta` reads a stale snapshot (`C13`); logout is client-side only (`C14`); unescaped `LIKE` + unindexed full scan per keystroke (`C15`); unvalidated block/share identifiers (`C16`); admin can remove the last admin (`C17`); WebUi reaches into `SyncDbContext` directly in 4 files (`C18`); no endpoint-authorization tests (`C19`); CSP omits `base-uri`/`form-action`/`object-src` (`C20`); duplicated role parsing and ad-hoc error shapes (`C21`).
**Note:** the CSRF design (`X-EAS-WebUi` + `SameSite=Strict`) is sound *specifically because* there is no `AddCors` anywhere in `src/`. Adding CORS for any reason silently removes the primary CSRF defense — worth a comment on the filter and an assertion in the test suite.

## Area D — Backends: Common / Imap / Smtp / Local / Sieve (35 + nits)
`D1` **CRITICAL** `ExpungeAsync()` with no UID set destroys other clients' `\Deleted` messages — `Imap/ImapMailBackend.cs:204,312`.
`D2` **CRITICAL** No UIDVALIDITY tracking anywhere in the repo → stale keys address the wrong messages after a restore/migration — `Imap/ImapSession.cs`, `ImapMailBackend.cs:581`.
`D3` **High** Every mail fetch downloads the full message; `EstimateSize` decodes every attachment to a MemoryStream just to read `.Length`, while holding the session gate — `Imap/ImapMailBackend.cs:136`, `Common/Converters/MailConverter.cs:290`.
`D4` **High** Contact update wipes every managed vCard property absent from the payload (ghosting) — `Common/Converters/ContactConverter.cs:149`.
`D5` **High** Meeting-request times ignore `TZID` and are treated as UTC; no line unfolding — `Common/Converters/MailConverter.cs:211,277`.
`D6` **High** vCard line injection via the contact `Picture` element — `Common/Converters/ContactConverter.cs:207`.
`D7` **High** iCalendar CRLF injection + platform line endings in generated CANCEL messages — `Common/Converters/ImipMailBuilder.cs:37`.
`D8`–`D24` **Med**: `PathSeparator` documented, schema'd, test-asserted and never read (`D8`); SMTP `QUIT` cancellable after a successful send → duplicate mail (`D9`); no Sieve socket/operation timeouts (`D10`); SASL PLAIN sent without checking advertised mechanisms (`D11`); events always anchored to UTC → DST drift on recurrences (`D12`); `SetPartStat`/`ExtractUid` pick the first VEVENT not the master (`D13`); special-folder lookup re-lists the mailbox per delete (`D14`); unnamed-attachment delete removes all unnamed attachments (`D15`); draft attachments lose content type, HTML alternative dropped (`D16`); `EmptyFolderAsync` uses stale racy indexes (`D17`); local meeting response skips the base class's concurrency retry (`D18`); GAL/free-busy load and decrypt the entire collection, parsing each vCard 3× (`D19`); conversation threading splits the root from its replies; MD5 breaks on FIPS (`D20`); `HtmlToText` leaks `<style>`/`<script>` bodies into plain text (`D21`); `ToApplicationData` throws where every sibling returns null — one corrupt vCard makes a folder permanently unsyncable (`D22`); vCard folding counts chars not octets, can split surrogates (`D23`); `EasDateTime.Parse` unguarded on client input in a dozen places (`D24`).
`D25`–`D35` + nits: readiness probe has no connect timeout (`D25`); malformed watcher key throws (`D26`); watcher rebuild race orphans a watcher (`D27`); `DisposeAsync` can hang and throw at waiters (`D28`); literal-length guard after the slice (`D29`); reader rebound across STARTTLS without draining (`D30`); Sent copy re-serialized (`D31`); `GetItemRevisionsAsync` uncapped (`D32`); free/busy terminates early on a floating occurrence (`D33`); `ConversationIndex` header is 5 bytes where the spec says 22 (`D34`); unchecked `TryWriteBytes`, char-based name truncation (`D35`). Plus: third namespace in one assembly, `MailboxAddress(from, from)`, `Limit` surrogate split, repeated ICS scans, status-line misparse, double `ToString()` per loop, `Occurrences`/`Until` exclusivity, odd untyped-phone mapping, silent >96 KiB photo drop, no `smtp.MaxSize` pre-flight.
**Verified correct (do not "fix"):** MailKit thread-safety (client never shared, serialized behind a gate, watchers own theirs); UID-vs-sequence discipline everywhere except `D17`; TLS validation (`CreateCallback` returns null when unconfigured so platform validation stays intact, and a custom root cannot repair a name mismatch); Sieve script generation is not injectable via the body (`DotStuff` correctly doubles leading dots); folder-access transitions.

## Area E — Server: pipeline, hosting, startup (35)
`E1` **High** Request bodies dropped on HTTP/2 — the body test relies on HTTP/1.1 framing (`Transfer-Encoding` is forbidden in h2, streamed bodies have no `Content-Length`) while Kestrel's HTTPS listener defaults to `Http1AndHttp2` — `Eas/EasContext.cs:51`. Delete the early return; rely on the existing zero-length check.
`E2` **High** Unauthenticated clients control Prometheus label values — `MetricsKey` is set two lines *before* `AuthenticateAsync` and the middleware records in a `finally` regardless of 401/429 — `Eas/EasEndpoint.cs:131`, `Setup/WebApplicationExtensions.cs:112`. Move the assignment below auth; clamp `command` to the known set.
`E3` **High** Auth throttle keys on `RemoteIpAddress`; no forwarded-headers middleware, so behind an ingress every request shares one key and `MaxFailures*5` fumbles 429 the entire gateway — `Eas/EndpointAuth.cs:16`.
`E4` **Med** Every EAS request constructs all 20 handlers and their dependency graphs — `Eas/EasEndpoint.cs:53,162`. Use keyed services.
`E5` **Med** Policy document rebuilt, serialized and SHA-256'd per request — `Eas/EasEndpoint.cs:201`, `Eas/PolicyDocument.cs:46`. Cache keyed on options reference identity.
`E6` **Med** `IOptionsSnapshot<ActiveSyncOptions>` re-binds the whole options tree (including every declared user) per request — `Eas/EasEndpoint.cs:55`, `Eas/AutodiscoverEndpoint.cs:45`, `Handlers/PingHandler.cs:15`. Switch to `IOptionsMonitor`.
`E7` **Med** A watcher completing non-positively is dropped for the rest of the heartbeat; with `WatchdogSeconds=0` this becomes a tight re-poll loop — `Eas/LongPollWatchdog.cs:41`.
`E8` **Med** Long-poll drain is unbounded and one faulting watcher aborts the whole Ping into a 500 — `Eas/LongPollWatchdog.cs:45,58`.
`E9` **Med** DB log drain can die silently and take DB logging with it for the process lifetime — `Setup/DatabaseLogSink.cs:57,99`.
`E10` **Med** Log events fully rendered even when `Log:Database=false` — the switch is only checked in the drain — `Setup/DatabaseLogSink.cs:34,83`.
`E11` **Med** Settings refresh loop breaks permanently on any non-shutdown `OperationCanceledException` (e.g. an EF command timeout), freezing live settings with no log — `Setup/SettingsRefreshService.cs:16`. Also ignores `Auth:UsersRefreshSeconds` despite its own doc.
`E12` **Med** `KeepAliveTimeout = 65 min` doesn't affect long-poll duration (it's a between-requests timer); it just keeps dead phone sockets for an hour — `ProgramServer.cs:99`.
`E13` **Med** SQLite pragmas run sync-over-async on a thread-pool thread per connection open, and re-apply `journal_mode=WAL` every time — `Setup/SqlitePragmaInterceptor.cs:21`.
`E14` **Med** Autodiscover checks `IsLoginBlockedAsync` but not `IsLoginDisabled`, unlike EAS — a disabled user still gets a service document — `Eas/AutodiscoverEndpoint.cs:78`.
`E15` **Med** Startup banner logs every declared user (PII, into the DB sink) and prints arbitrary per-user backend settings in full — only `Password` is masked — `StartupSummary.cs:64,188`.
`E16` **Med** `/readyz` discloses backend topology anonymously on the phone-facing listener, and serializes all callers behind one semaphore — `ProgramServer.cs:247`, `Setup/ReadinessProbe.cs:28,33`.
`E17`–`E26` **Low**: `_requestRead` set before the read poisons the cache on failure (`E17`); `IOptionsMonitor` resolved from `RequestServices` per request (`E18`); missing users file fails with a raw framework exception (`E19`); `serverCertificate` published to the Kestrel selector without a barrier (`E20`); Basic-auth header decoded with no length bound (`E21`); migration bootstrap ignores cancellation and races across replicas (`E22`); SQLite connection strings never redacted despite accepting `Password=` (`E23`); `MeetingInvitationService` recomputes the recipient list per attendee — O(n²) (`E24`); folder-listing fallback re-queries the registry per failing store (`E25`); no exception-handling middleware, and the dev exception page auto-enables in Development — full stack traces to unauthenticated callers if `ASPNETCORE_ENVIRONMENT` is left set (`E26`).
`E27`–`E35` **Nit**: `RunServerAsync` is 245 lines doing a dozen jobs (`E27`); the auth prologue is duplicated between the two authenticated endpoints — the direct cause of `E14` (`E28`); loggers created per request instead of injected (`E29`); `MeetingInvitationService` registered inside `AddEasHandlers` (`E30`); `LogText.Clean` uses LINQ over a string on the hot path (`E31`); two undisposed `SerilogLoggerFactory` instances (`E32`); `GatewayMetrics.PerUserLabels` is a set-once static, silently restart-tier (`E33`); `CaptureIcsAsync` swallows with an unused `ex`, silently re-inviting everyone (`E34`); metrics port filter allocates per rejected scrape (`E35`).
**Verified good:** `ValidateScopes`+`ValidateOnBuild` in *all* environments rules out captive dependencies (I traced the singleton graph — none capture scoped); the one construct DI validation can't see (`AddTransient(sp => …BackendRolesProvider.Current)`) is also safe.

## Area F — EAS command handlers (48)
`F1` **High** `<Sync/>` with an empty `Collections` gets Status 13 instead of replaying (only a byte-empty body replays) — `Handlers/SyncHandler.cs:42`.
`F2` **High** Server→client `Delete` commands bypass `WindowSize` entirely and don't contribute to `MoreAvailable` — `Handlers/SyncHandler.cs:314`.
`F3` **High** Items aging out of the sliding `FilterType` window are hard-`Delete`d, never `SoftDelete`d (`SoftDelete` is in the code page and emitted by no handler) — `Handlers/SyncHandler.cs:314`.
`F10` **High** Draft submit sends before recording the replay marker, and a post-send failure reports failure → user resends, recipient gets it twice — `Handlers/SyncHandler.cs:434,496,622`.
`F23` **High** Provision phase 2 never compares the presented `PolicyKey` and never reads the client's ack `Status` — the whole policy handshake is bypassable in one request — `Handlers/ProvisionHandler.cs:59`.
`F29` **High** Unresolvable source silently sends a reply with no quote / a forward with nothing forwarded — `Handlers/ComposeMailHandlers.cs:260,333`.
`F30` **High** Failures after successful submission reported as send failures → duplicates — `Handlers/ComposeMailHandlers.cs:56`.
`F4`–`F8` **Med**: Status 3 echoes the rejected sync key back, causing the resync loop (`F4`); out-of-range `Wait`/`HeartbeatInterval` silently clamped upward instead of Status 14 + `Limit`, unlike Ping (`F5`); `MIMESupport` read nowhere, Type-4 BodyPreference force-downgraded to HTML → S/MIME unusable (`F6`); client `Change` blindly overwrites, no conflict detection, `Conflict` option and Status 7 unimplemented (`F7`); `Class` never echoed despite advertising 12.1 (`F8`).
`F13`–`F21` **Med**: N+1 backend round trips plus a DB query per deleted item (`F13`); metrics count items never sent (`F14`); two device-row writes per Sync (`F15`); a detected change mapping to no collection silently loses the notification (`F16`); Ping with folders but no heartbeat returns Status 3 instead of reusing the cache (`F17`); no `MaxFolders`/Status 6 cap (`F18`); GetItemEstimate returns status 4 where the spec says 3 (`F19`); GetItemEstimate has no error handling — one flaky store 500s the whole request (`F20`); **per-folder read-only grants unenforced — `IsReadOnlyFolder` is honored in exactly 1 of ~8 mutating handlers** (`F21`).
`F25`–`F28` **Med**: FolderSync has no replay generation, so any lost response costs a full hierarchy resync (`F25`); every folder-op backend failure collapses to Status 3 ("system folder"), and non-`BackendException` escapes to a 500 (`F26`); `FolderCreate` ignores the requested `Type` and always creates a mail folder (`F27`); `FolderCreate` enumerates every store twice (`F28`).
`F32`–`F33`, `F36`, `F40`, `F43`, `F47` **Med**: no iCalendar unfolding → truncated UIDs and misdirected iTIP replies (`F32`); only the default calendar considered, and a calendar CollectionId is fed to the mail store (`F33`); `Total` reports page size so search stops after page 1 (`F36`); one sequential backend fetch per search hit (`F40`); sequential GAL + free/busy per recipient on the compose path (`F43`); `ReadOnly` mode does not block arming an out-of-office auto-reply — a real server-side Sieve rule (`F47`).
`F9`, `F11`, `F12`, `F22`, `F24`, `F31`, `F34`, `F35`, `F37`–`F39`, `F41`, `F42`, `F44`–`F46`, `F48` Low/Nit: `MoreAvailable` element ordering (`F9`); long-poll re-processing relies on an undocumented invariant (`F11`); replay rollback persisted before success (`F12`); MoveItems snapshot patching is a write-per-item and ignores the replay generation (`F22`); `PolicyType` unvalidated (`F24`); positional bool in `SetAnsweredAsync` (`F31`); every MeetingResponse failure reports Status 2, including transient (`F34`); the invitation mail is not removed after responding (`F35`); Find `Range` "0-0" for zero results (`F37`); Find result child ordering (`F38`); `Properties` built twice and reparented (`F39`); paging past `MaxFetch` returns nothing rather than an error (`F41`); ambiguous ResolveRecipients matches reported as Status 1, `MaxAmbiguousRecipients` ignored (`F42`); `options` shadowing in ItemOperations (`F44`); `EmptyFolderContents` conflates three causes and ignores `DeleteSubFolders` (`F45`); LongId Fetch bypasses the folder registry (`F46`); unrecognized Settings sections get no per-section status (`F48`).
**`F-decomp`** — `SyncHandler.cs` (826 lines): split into `SyncHandler.cs` (~120, `HandleAsync` only), `.Collection.cs` (~140), `.ClientCommands.cs` (~260), `.ServerCommands.cs` (~90), `.LongPoll.cs` (~75, or delete in favour of a shared `LongPollSession` used by Ping too), keeping `.Cache.cs`. Extract `SyncCollectionOptions` (today's private record + `ParseOptions` + persistence helpers) and `ClientCommandLedger` (wraps the four replay/applied dictionaries; drops `ApplyClientCommandAsync` from ten parameters to five and makes `F10`'s ordering enforceable in one place). Replace the anonymous triple `(XElement?, bool, (XElement, UserFolder, IContentStore)?)` with a named record struct — it's the most opaque construct in the file and naming it is what makes `F11`'s invariant expressible.
**Six repeated shapes wanting shared helpers:** resolve-collection-or-fail (12 sites), write-permission check (`F21`), `BodyPreference` parsing (two mutually inconsistent parsers), status-response writer (6 reimplementations), EAS time formatting (duplicates `EasDateTime`), long-poll scaffolding (`SyncHandler` vs `PingHandler` — the direct cause of `F16`).

## Area H — Backends: JMAP / DAV (31)
`H1` **High** DAV readiness probe hardcodes `RemoteCertificateValidationCallback => true`, overriding the operator's TLS settings; the JMAP probe does it correctly — `Dav/DavReadiness.cs:12`.
`H2` **High** Percent-decoded hrefs are re-resolved as URIs, so a resource whose name contains `#`, `?` or `%` resolves to the wrong path (verified empirically on .NET 10) — `Dav/WebDavClient.cs:306`.
`H3` **High** `If-Match` silently dropped when the ETag isn't RFC-quoted → unconditional PUT, lost update, and the `idempotent: true` justification breaks — `Dav/WebDavClient.cs:132`.
`H4` **High** `recurrenceRules` is written as an array and read with the id-map helper → recurrence never survives a round trip — `Jmap/JsCalendarConverter.cs:110`.
`H5` **High** Recurrence day-ordinals dropped both directions; `byMonthDay`/`byMonth`/`bySetPosition` unmapped → "2nd Tuesday" becomes "every Tuesday" — `Jmap/JsCalendarConverter.cs:212,231`.
`H6` **High** Birthday written to `date.utc`, read from `date.date` → silently never appears again — `Jmap/JsContactConverter.cs:120,248`.
`H7` **High** Update patches omit cleared fields; the code is written as if `update` were a full replace, but RFC 8620 §5.3 says PatchObject — so clearing a phone/note/location never reaches the server — `Jmap/JsContactConverter.cs:148`, `JsCalendarConverter.cs:116`.
`H8` **High** Hardcoded page size of 500 ignores `maxObjectsInGet` (→ `requestTooLarge` fails the whole folder sync), and a server-capped `limit` breaks the loop after page 1 → silently truncated folder — `Jmap/JmapMailStore.cs:126`.
`H9`–`H25` **Med**: session capabilities parsed as a bare key set, so limits and `HasCapability` are unusable (`H9`); `JmapResponse` leaked at five call sites, four of which therefore never check per-item `*/set` failure buckets (`H10`); `DavPollSeconds` documented, configured, consumed only by JMAP — DAV hardcodes 60s (`H11`); ctag poll costs 1–2 PROPFINDs per folder per cycle and a transient error reads as "changed", causing a full re-sync (`H12`); creating one item costs one or two full collection enumerations (`H13`); GAL over CardDAV is a sequential GET per contact (`H14`); Ping/Sync waits re-download and hash every event/card per poll tick (`H15`); no `/changes` or `sync-collection` anywhere — permanently full-enumeration (`H16`); EventSource stream killed every 100s by `HttpClient.Timeout`, plus a response leak on the error path (`H17`); retried create-PUT turns a succeeded create into a spurious 412 failure (`H18`); mail folder token is `total:unread`, so flag changes and equal add/delete are invisible to Ping (`H19`); JMAP stores never throw `BackendItemNotFoundException`, so `UpdateItemAsync` on a deleted message reports success (`H20`); `CalDavBackendProvider` builds the shared client with one role's settings and another role's credentials (`H21`); default contacts folder chosen from unsorted server output — `CalDavStore` already fixed this for calendars (`H22`); floating-time events silently converted to UTC (`H23`); multi-status responses fully buffered as strings with no size cap (`H24`); per-item update costs 3 round trips, delete costs a full mailbox listing (`H25`).
`H26`–`H31` Low/Nit: probes construct and discard an `HttpClient` per call (`H26`); multi-status partial failures dropped without a trace, `Contains("200")` status test (`H27`); XXE hardening is correct but purely inherited from framework defaults and undocumented — make it explicit (`H28`); `ContentFilter` ignored by the JMAP calendar/contact stores, unlike CalDAV (`H29`); hand-rolled time-range format string (`H30`); dead locals (`H31`).
**Cross-cutting:** `JmapClient` and `WebDavClient` are near-twins (identical handler setup, auth header, timeout, byte-identical `IsSafeRedirect`, near-identical redirect-following send — the JMAP copy's comment even says "Mirrors WebDavClient"). That duplication is *why* `H17` exists in one and not the other and why `H1` could diverge. Extract a `BackendHttpClientFactory` into `Backends.Common`.
**Verified good:** manual same-origin redirect handling with a credential-leak rationale; the `AllRealOnly` idempotency gate correctly distinguishing replayable reads from `*/set`; response bodies deliberately excluded from exception messages; the trace logger's "method, URI and body only, NEVER headers" construction; the Axigen workarounds carry live-verification dates.

## Area K — Security / Crypto / Plugins / Observability / Contracts (70)
`K1` **High** Unauthenticated metric-label cardinality bomb (`user` + `command`) — `Observability/GatewayMetrics.cs:70` (see `E2`).
`K6` **High** Self-signed certificate lifetime of **20 years** violates Apple's 825-day limit — iOS/macOS are the flagship clients, so a user who explicitly trusts it can still be refused — `Security/GatewayCertificateStore.cs:139`. Issue ≤398 days and add the renewal the doc says doesn't exist.
`K38` **High** `Plugins:Directory` is DB-settable → admin UI to in-process arbitrary code execution with the master key in memory; `Register` also receives the live `IServiceCollection`, so a plugin can replace any host registration — `Plugins/PluginLoader.cs:34`, `Administration/SettingKeys.cs:50`.
`K56` **High** `BackendCredentials` is a `record` → synthesized `ToString()` prints the plaintext password, recursively via `ResolvedRole`/`BackendConnectionContext`. Published plugin contract — `Contracts/Models.cs:6`.
`K7`–`K11` cert store **Med/Low**: IP-literal `PublicUrl` produces a DNS SAN instead of an IP SAN, and loopback IPs are absent (`K7`); concurrent regeneration makes replicas serve different certificates behind one LB — devices see a MITM (`K8`); unencrypted PKCS#12 private key never zeroed, `DefaultKeySet` persists a key container per load (`K9`); undecryptable row silently replaces the certificate (`K10`); `TryLoad` catch list too narrow (`K11`); magic row id (`K12`).
`K13`–`K18` TLS resolver **Med/Nit**: `DescribeAsync` imports the private key just to read public metadata — a key-container file per admin refresh (`K13`); master key loaded and never zeroed, loader error discarded (`K14`); **no validation that the operator's certificate has a private key or is unexpired** (`K15`); public-key objects never disposed — up to 3 native handles leaked per describe (`K16`); exception filter can let `ArgumentException` escape to a 500 (`K17`); doc contradicts the code (`K18`).
`K19`–`K21` content protector: **AAD delimiter is ambiguous** — `userName + "\n" + collection` is not injective, so cross-collection ciphertext substitution is possible; the repo already ships the correct primitive in `DelimitedKey` (`K19`); `Dispose` zeroes a live singleton's key with no guard, so post-dispose calls encrypt under an all-zero key (`K20`); passthrough mode silently double-wraps `v1:` values (`K21`).
`K22`–`K25` hasher: **attacker-influenced PBKDF2 iteration count has no upper bound** — a stored `pbkdf2$2000000000$…` makes every login burn minutes of CPU (`K22`); hashed-vs-plaintext verify times differ by five orders of magnitude, remotely enumerating which accounts still have plaintext passwords (`K23`); no rehash-on-verify so `DefaultIterations` can never actually be raised (`K24`); no argument validation (`K25`).
`K26`–`K32` throttle: **unbounded growth + O(n) scan per failure** under a username-rotating attack (`K26`); unlocked read in prune (`K27`); lost-update race with `RecordSuccess` (`K28`); success never relieves the per-address counter, so a few stale phones behind NAT 429 everyone (`K29`); EAS keys not namespaced unlike WebUi's (`K30`); **no `AuthThrottleTests` at all** (`K31`); direct `DateTime.UtcNow` makes it untestable (`K32`).
`K33`–`K37` WireLog: `Payload` copies the entire input before truncating — a 50 MB body allocates a second 50 MB LOH string to keep 16 KB (`K33`); `Truncate` can split a surrogate pair (`K34`); CR/LF preserved, so log forging is possible on line-oriented sinks — and the sibling `LogText.Clean` makes the *opposite* choice (`K35`); `WireLog` and `LogText` duplicate the same security-relevant scrubber across two assemblies (`K36`); no redaction helper, so every caller reinvents masking (`K37`).
`K39`–`K44` plugin loader: contract-version guard bypassed by a transitive reference (`K39`); `GetTypes()` on an untrusted assembly is an uncaught crash surface, and the `IsPublic` filter promised by the error message is missing (`K40`); host-first resolution silently downgrades plugin-private dependencies for *every* assembly, not just the contract (`K41`); a relative `Plugins:Directory` resolves against the CWD while the default resolves against the app base — so setting it to its own documented default changes behaviour (`K42`); missing entry assembly warns and continues, contradicting the stated fail-fast policy (`K43`); the ALC is dependency isolation, **not** a security boundary — reword the doc and add optional hash/signature pinning (`K44`).
`K45`–`K50` crypto: **global fixed PBKDF2 salt** — one rainbow table covers every deployment (`K45`); short passphrases only warn, no floor (`K46`); key material lives in unzeroable `string`s while the derived bytes are carefully zeroed — the high-value secret is protected less carefully than the low-value one (`K47`); `IsShortPassphrase` re-reads the key file (`K48`); **namespace/assembly mismatch** (`K49`); loader error discarded by a caller (`K50`).
`K51`–`K55`: AES-GCM framing duplicated verbatim between `SecretValue` and `LocalContentProtector` (~60 lines, two assemblies) (`K51`); no key-length or null validation on the published API, unlike its in-repo sibling (`K52`); coarse error strings (`K53`); **sealed CLI envelopes are replayable within the 60s window**, and `Math.Abs` doubles it to 120s (`K54`); `TryOpen`'s "never throws" contract not fully honoured (`K55`).
`K57`–`K70` contracts: host-only types leak into the plugin surface (`K57`); `IContentStore` is a 12-member mandatory interface with no versioning affordance despite the file demonstrating the optional-capability pattern three times (`K58`); `ct` not last on `DeleteItemAsync`, plus an optional parameter on an interface method (`K59`); `BackendConnection.DisposeAsync` leaks on first throw, isn't idempotent, and never disposes the stores (`K60`); `CreateConnection` is synchronous in an otherwise fully async contract — change it now while the ecosystem is empty (`K61`); **`SharedCollection.Parse` fails open** — any unrecognized mode suffix yields read-write (`K62`); parse/validate duplication (`K63`); an unparseable `baseUrl` skips the cross-host guard, so an absolute URL to an attacker host validates (`K64`); `DelimitedKey` argument validation (`K65`); `ContentFilter` factories read the wall clock (`K66`); `BackendItemNotFoundException` doesn't derive from `BackendException`, so the codebase-wide `catch (BackendException)` idiom misses it — and `BackendException` is sealed so plugins can't add typed errors (`K67`); shared static `EmptySection` (`K68`); no `ContractVersion` constant anywhere (`K69`); `Register` hands plugins the live host `IServiceCollection` with no documentation of the trust level (`K70`).
`K2`–`K5` metrics: `ActiveLongPolls` entries never removed (`K2`); unsynchronized static observer slots (`K3`); un-namespaced instrument names, duration histogram drops `status` (`K4`); no auth-outcome counters, no cert-expiry gauge, `RecordThrottleRejection` can't distinguish EAS from WebUi (`K5`).
**Boundary verdict:** the extraction was right and holds — no EF Core, `SyncDbContext`, `ActiveSyncOptions` or Core host graph leaks into Contracts. Keep `Contracts → Protocol`, but fix the stated reason: the csproj says "EAS constants" and the only compile-time use (`ContentFilter.ForClass`) is called **only from the host** — yet plugins genuinely need Protocol because `BackendItem(IReadOnlyList<XElement>)` and `SearchGalAsync` require every provider to hand-build EAS XML in the right namespaces. Move `ForClass` to Core, correct the comment. Also resolve the contradiction that Contracts claims to be "THE one package" while `BackendConfigField` has a `Secret` type and nothing in Contracts can seal it (`K71` — add an `ISecretProtector` abstraction or document that secret-handling plugins also take `ActiveSync.Crypto`).

## Area W — WBXML codec and protocol support types (21)
`W1` **CRITICAL** No nesting-depth or element-count limit — a 64 MB body of `0x45` bytes yields ~64M nested `XElement`s, multiple GB of heap, OOM of the whole gateway from one authenticated device — `Wbxml/WbxmlDecoder.cs:55`. Add `MaxDepth = 256` / `MaxElements = 200_000`.
`W2` **High** `WbxmlEncoder.WriteElement` is unbounded-recursive → uncatchable `StackOverflowException`, instant process death — `Wbxml/WbxmlEncoder.cs:40`.
`W3` **Med** `ReadMultiByteUInt` accumulates 35 bits into a `uint` and silently overflows — `Wbxml/WbxmlDecoder.cs:197`.
`W4` **Med** `DecodeAsync` copies an unbounded stream and truncates length to `int` — `Wbxml/WbxmlDecoder.cs:28`.
`W5` **Med** OPAQUE data base64'd into a `string` — three full copies of every attachment, all LOH — `Wbxml/WbxmlDecoder.cs:76`, `WbxmlEncoder.cs:69`.
`W6` **Med** Malformed base64 escapes as `FormatException` rather than `WbxmlException`, mid-response-write — `Wbxml/WbxmlEncoder.cs:69`.
`W7` **Med** Encoder silently drops text when an element has both text and children (and drops children when opaque) — `Wbxml/WbxmlEncoder.cs:67`.
`W8` **Med** Encoder writes NUL characters into NUL-terminated inline strings, scrambling the document — `Wbxml/WbxmlEncoder.cs:81`.
`W9`–`W11` Nit/Low: dead read before an unconditional throw (`W9`); undisposed `MemoryStream` (`W10`); charset `0x03` accepted but decoded as UTF-8 (`W11`).
`W12`–`W14` Code pages: verified token-by-token against MS-ASWBXML for pages 0,1,2,4,5,7,8,9,12,14,15,16,17,18,20,25 — all deliberate gaps correct. Missing Tasks `0x21 CompressedRTF`; empty `AirNotify` page gives a confusing error (`W12`); a duplicate tag name anywhere fails as `TypeInitializationException` on the **first WBXML request the gateway ever serves** — add a table-validation test (`W13`); tables are hand-maintained tuples with version applicability only in comments (`W14`).
`W15` **High** `EasDateTime.ToLong/ToCompact` shift `DateTimeKind.Unspecified` values by the server's UTC offset — silent timezone-dependent corruption that looks fine in UTC CI — `EasDateTime.cs:9`.
`W16` **Med** `EasDateTime.Parse` throws `FormatException` on client-supplied garbage and falls back to a culture-sensitive loose parse — `EasDateTime.cs:20`.
`W17` **Med** Base64 query version byte unvalidated — a byte of 255 yields "25.5", satisfying every `>= V160`/`V161` gate, so a client unlocks 16.x behaviour it never negotiated. The command code immediately below *is* range-checked — `Http/EasRequestParameters.cs:103`.
`W18` **Med** `ToBase64` truncates length prefixes >255 bytes (`LongId`, `AttachmentName` are realistically long) — `Http/EasRequestParameters.cs:191,205`.
`W19`–`W21` Low/Nit: raw `FormatException`/overflow on a malformed `ProtocolVersion` (`W19`); `EasVersion.Parse` uses the **current culture** for integer parsing while the rest of the layer is careful to use invariant (`W20`); `EasFolderType` missing 18/19, `EasClass` missing `SMS` (`W21`).

## Area L — CLI (27)
`L22` **High** With no encryption key configured, `/cli` falls back to loopback-only auth — so a production misconfiguration silently degrades to the model the design explicitly rejects, and any co-located sidecar or local process gets `eas user set`, `eas device password`, `eas purge user --yes` — `Cli/LocalCliEndpoint.cs:75`. Gate on an explicit `AllowPlaintext` + startup warning, not on key absence.
`L23` **Med** Requests are sealed; responses (including `eas device password` output) travel loopback in plaintext — `Cli/LocalCliEndpoint.cs:60`.
`L24` **Med** No audit trail for any `/cli` command — account deletion, device-password disclosure, password changes leave no record — `Cli/LocalCliEndpoint.cs:50`.
`L25` **Med** Process-global `Console.SetOut/SetError` inside a live web server: for the duration of every forwarded command, all gateway log output for all concurrent requests is captured into that command's stdout and vanishes from the container log — `Cli/LocalCliEndpoint.cs:128`.
`L26`–`L27` Low: `CapturingRegistrar.Resolve` has no cycle detection → `StackOverflowException` in the gateway process (`L26`); envelope replay bounded by time but not by nonce, so a captured envelope re-executes destructive verbs for 60s (`L27`).
`L28` **Med** `eas tls` passes X.500 DNs and file paths through Spectre's **markup** parser — a `[` in a certificate subject crashes the command. This is the one file that reaches for the raw `params string[]` overload; every other command file correctly wraps cells in `Text`/`Markup.Escape` — `Cli/TlsCommand.cs:42`.
`L29` **Med** `eas config set` echoes secret values in plaintext, though `config list`/`get` mask them — `Cli/ConfigCommands.cs:198`.
`L30` **Med** `eas config list` prints non-`Password` backend secrets (`ApiKey`, `Token`, `ClientSecret`) in the clear — `Cli/ConfigCommands.cs:107`, `SettingKeys.cs:196`.
`L31` **Med** `eas users` builds rows case-insensitively but joins state case-sensitively → a mixed-case login reports 0 devices, 0 folders and **"not blocked" when it is** — `Cli/InspectCommands.cs:89,131`; same mismatch in `DeviceCommands`, `FoldersCommand`, `ItemsCommand`, `BlockCommands`.
`L32` **Med** `eas purge user` claims to delete ALL state but leaves `SharedCalendarGrants` behind — recreating the login resurrects read-write grants to someone else's calendar — `Cli/PurgeCommands.cs:70`.
`L33` **Med** Errors bypass the injected console entirely (raw `Console.Error`), so **no CLI test can assert on any error message or exit code** — and it's the reason `L25`'s process-global redirection exists.
`L34` **Low** `device wipe` and `block` are destructive with no confirmation, while `purge` has a well-built confirm flow — `Cli/DeviceCommands.cs:109`, `BlockCommands.cs:39`.
`L35` **Med** Every CLI command rebuilds a full DI container, EF model and plugin set — including when forwarded to the warm gateway that already has all three — `Cli/CliServices.cs:17`. This is what makes `Cli/` feel like a foreign body in Server; fix it and the assembly question dissolves.
`L36` **Med** The slim client re-runs the command locally after a server-side 5xx or timeout — when it is very likely still running — `ActiveSync.Cli/Program.cs:52`. Only fall back on 404 and connection failure.
`L37`–`L48` Low/Nit: `--since` overflow (`L37`); `--limit` silently rewritten (`L38`); `BuildConfiguration` throws on a missing users file — defeating the very command meant to repair configuration — and leaks a `ConfigurationRoot` (`L39`); `healthcheck` targets `localhost` and hits the exact IPv6 stall the slim client documents and avoids (`L40`); O(n²) scans in `UsersCommand` (`L41`); master key not zeroed when stdin reading fails — in the long-lived gateway process (`L42`); `isGatewayPassword` inferred from the absence of a colon (`L43`); inconsistent exit codes (0/1/-1) for "nothing to do" (`L44`); `RunLocal` hard-codes `dotnet` on PATH, so every fallback fails on a self-contained/AOT layout (`L45`); `CliRequest`/`CliResponse` declared twice with drifting defaults — a rename silently nulls `Sealed`, dropping to unauthenticated handling (`L46`); `ShowCommand` doesn't validate `<collection>` (`L47`); inconsistent `Terminal` access (`L48`).
**`L-verdict`** — do **not** split the CLI out of `ActiveSync.Server`. A clean split is blocked in three places (`ServeCommand` → `RunServerAsync`; `/cli` needs the command tree in-process for the warm-start benefit that is its whole reason to exist; `CliServices`/`CliVerbs` depend on `Server.Setup`), and extracting would mean duplicating that setup or hoisting a fourth assembly — real churn for a boundary that buys nothing. Instead: fix `L35`, and do two in-assembly cleanups — unify the three command base hierarchies (`DatabaseCommand`, `SettingsCommandBase`, and two bare `AsyncCommand`s that hand-roll the same bootstrap), and split `InspectCommands.cs` (346 lines, 5 commands) and `UserCommands.cs` (379 lines, 9 commands), whose density is actively hiding `L31` and `L41`.

## Area S — cross-cutting structural (my own pass)
`S1` **High** `Backends.Common` (packed, plugin-facing) references all of `ActiveSync.Core` for a single `WireLog.Payload` call; the seven `using ActiveSync.Core.Backend;` directives are dead. Verified: Core/Backend declares exactly 7 types, no extension methods, none referenced in Backends.Common. See Block 14.
`S2` **Med** `ActiveSync.Crypto` declares `ActiveSync.Core.Security` / `ActiveSync.Core.Options` namespaces (= `K49`).
`S3` **Med** Two independent write paths to the same tables (Core services vs WebUi direct EF) (= `C18`).
`S4` **Low** `MergedFreeBusy` and `CollectionDiff` are pure protocol logic with no EF/Core-state dependency — they belong in `ActiveSync.Protocol` where they'd also be easier to fuzz.
`S5` **Med** No architecture test enforcing any of the above; the plugin boundary is currently enforced by a csproj comment.
`S6` **Low** Nothing enforces the Sqlite/Npgsql migration sets stay in lockstep (1:1 across all 15 today). Add a CI check on the ordered migration-name lists.
`S7` **Med** Four independent secret-redaction implementations: `ConfigCommands.Mask`, `StartupSummary.Redact`, `BackendsEndpoints.SecretMask`, `MailKitWireLogger.Redact` — each with a different notion of what to hide. See Block 4.
`S8` **Nit** `Backends.Common` spans three namespaces across 19 files (`ActiveSync.Backends`, `.Common`, `.Converters`); `ServerCertificateValidator.cs:5` is the odd one out.
`S9` **Nit** Ten+ copies of the same three-line `#pragma warning disable VSTHRD103` + identical explanatory comment (`SyncStateService` alone has five). Hoist to one file-level suppression with the comment once, or an `.editorconfig` entry with the rationale.

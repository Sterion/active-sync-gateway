# Claimed fixed in round 2, found again in round 3

A regression-integrity audit. Round 2 (items 1–25 landed) struck a set of findings as **fixed**. This file
records the cases where round 3 independently re-found **the same defect at the same site** — i.e. the
round-2 fix did not actually close the problem it claimed to.

## Method & bar for inclusion

The ten round-3 subsystem agents were **withheld from `round1/` and `round2/`** and told to stop reading if
they stumbled into them. That is what makes this audit meaningful: nothing below is a round-3 agent
disagreeing with a prior round it had read, and no round-2 ID was matched by ID. Each entry was matched by
**described problem + code site** by the coordinator afterwards, then filtered:

1. The round-2 finding must have been explicitly struck/COMPLETE in an item that **actually landed**
   (items 1–25). Items 26–32 were never worked, so anything there is excluded even where round 3
   re-reports it — round 2 never claimed those fixed. (This excludes round-3 `K12`/`K9`/`K20`/`K22`'s
   siblings in round-2 item 31, `F3`/`F7`/`F8`/`F9` in item 26, and `E5`/`E12`/`E14`/`E15` in item 27.)
2. The round-3 finding must be the **same defect**, not a near-neighbour the round-2 fix introduced or
   explicitly left out of scope. Near-neighbours are excluded and listed below so the exclusions are
   auditable too.
3. Where the round-2 results entry contradicts the surviving defect, it is quoted.

Three entries clear that bar. Two more are **carried-forward disclosures** — round 2 recorded them in
`review-results.md` as known residue rather than claiming them fixed, and round 3 confirms they are real;
they are kept in a separate section because they are not broken promises.

---

## 1. The self-signed certificate still never renews — round-2 `K4` → round-3 `K1`

**Round 2** — item 9 "Certificate store & TLS resolver" (**COMPLETE**, `~~K4~~` struck, commit `d4b9b54`).
The results entry states the outcome as:

> **K4** — self-signed validity drops 20 years → 397 days **with auto-renewal 30 days out**, so a
> deployment re-presents a **new fingerprint roughly annually** instead of never; devices must re-trust on
> each renewal.

**What the fix actually did.** It capped validity at 397 days, added `RenewalWindow = 30 days`, and taught
`GatewayCertificateStore.GetOrCreateAsync` to regenerate when the stored certificate falls inside that
window. The class doc was updated to say so: *"`GetOrCreateAsync` renews the certificate on its own ahead of
expiry — deleting the row remains a manual regeneration lever, but is no longer required for the certificate
to keep working."*

**Round 3** — `K1` **[High]**, `Security/GatewayCertificateStore.cs:65`:

> `GetOrCreateAsync` has exactly one caller — `TlsCertificateResolver.LoadForServingAsync` — invoked once
> from `ProgramServer.cs:351` during startup; Kestrel's `ServerCertificateSelector` then returns that one
> held instance for every handshake forever. There is no timer, no background service, no re-resolve.

**Why it is the same defect.** The renewal *logic* landed; the thing that would ever *call* it did not. A
process running 367 days crosses into the renewal window with nothing to act on it, and at 397 days serves
a hard-expired leaf — at which point iOS and Android refuse the handshake and **all EAS traffic stops**,
with no log line and no operator signal. K4's own premise was that 20 years was unusable on Apple clients;
the fix made the certificate *expire* without making it *renew*, which converts a client-compatibility
problem into a total outage on a timer. This is strictly worse than the state K4 described, and it is the
single most consequential finding in round 3.

**What actually closes it:** a hosted service that re-runs `LoadForServingAsync` on a daily tick (or when
`NotAfter - UtcNow < RenewalWindow`) and swaps the instance the Kestrel selector closure returns. Round-3
`K1` gives the shape.

---

## 2. `BackendKeyValidator` inherited an exemption it does not qualify for — round-2 `B3` → round-3 `B5`

**Round 2** — item 22 "Config & account resolution" (**COMPLETE**, `~~B3~~` struck, commit `4a7e031`),
closed **documentation-only**. The results entry records the reasoning:

> **`B3` — the validation gap is real and still open; only its documentation changed.** … The worker's
> argument for not wiring it up is that every catalogue key lives outside the `Backends`/`Users` sections
> those validators read, so the check would compare a failure set to itself — dead code at the cost of a
> full role+user validation pass per write. I find that sound **today**; it stops being sound the moment a
> catalogue key lands inside those sections, and the XML doc now states that trigger.

**Round 3** — `B5` **[Medium]**, `Administration/BackendKeyValidator.cs:22`:

> The `SettingKeys.ValidateStartupImpact` comment explicitly reasons that the registry-based validators need
> not be simulated "because every catalogue key lives outside Backends and Users" — true for catalogue keys,
> but **`BackendKeyValidator` is the surface for keys that *are* inside `Backends`**, and it inherited the
> exemption it does not qualify for.

**Why it is the same defect.** Round 2 closed B3 on a premise that was true of the surface it examined
(`SettingKeys.ValidateStartupImpact`, which handles catalogue keys) and false of the sibling surface it did
not (`BackendKeyValidator`, which handles exactly the `ActiveSync:Backends:*` keys the premise carves out).
The trigger the round-2 entry named — "it stops being sound the moment a catalogue key lands inside those
sections" — had *already* fired at closure time, one file over. The consequence round 3 describes is
concrete: `eas config set`/`unset` on a backend role can invalidate a config-declared user, is accepted at
write time, throws at the next startup, and in the meantime trips round-3 `B1` (the user-refresh freeze).

**What actually closes it:** run `UserResolver.ValidateUsers` against the candidate `effective ⊕ {key:value}`
config inside `BackendKeyValidator.Validate`, reporting only failures the write introduces — the same
before/after diff shape `ValidateStartupImpact` already uses.

---

## 3. The IMAP search-floor correction landed on one of two call sites — round-2 `D2` → round-3 `G13`

**Round 2** — item 24 "Converter correctness" (**COMPLETE**, `~~D2~~` struck, commit `716dc08`):

> `D2` — the FIX text asks for two things: "confirm the engine scopes the stored snapshot to the same filter"
> **and** "document the skew or widen by a day". The worker did both: confirmed the scoping is already
> handled, then added `SearchFloor` = `sinceUtc.AddDays(-1).Date` so the IMAP-side filter is a strict
> superset of the intended UTC window on any server timezone.

**Round 3** — `G13` **[Low]**, `Imap/ImapMailBackend.cs:533` (`SearchAsync`):

> `query = query.And(SearchQuery.DeliveredAfter(since.Date));` — `SearchFloor` at `:598` exists precisely
> because RFC 3501 `SINCE` compares the server's own INTERNALDATE calendar day … `GetItemRevisionsAsync:104`
> uses `SearchFloor(since)` but this call site does not.

**Why it is the same defect.** The helper the round-2 fix introduced is applied at the site the finding
named and not at its sibling in the same file, so the INTERNALDATE-vs-UTC skew D2 was written about survives
verbatim on the Search command: a user searching "last 7 days" on a server east of UTC still loses
boundary-day hits. Severity is genuinely lower than D2's (Search is a user-initiated query, not the sync
map), which is why round 3 rates it Low — but it is the same expression, the same reasoning, and the fix
that exists three hundred lines away.

**What actually closes it:** call `SearchFloor(since)` in `SearchAsync` too.

---

## Carried-forward disclosures that round 3 confirms

Neither of these was claimed fixed — round 2's own results entries recorded them as residue. They are here
because round 3 independently reached the same site, which converts "known residue" into "confirmed, and
here is what it costs".

### `H2`'s repair disclosed a new worst case; round 3 found it reachable on a CI backend

Round 2's item 23 entry carried this forward verbatim:

> **Cost note carried forward:** on a doubly-broken server (no usable UID query **and** href rewriting)
> where our item genuinely isn't found, the fallback now issues one GET per listing entry before giving up,
> where it previously issued none. Bounded by collection size, create-only, and that path was already
> returning a wrong answer — but it is a new worst case.

Round-3 `H2` **[High]** establishes that the triggering condition is not exotic: **Axigen indexes new DAV
items asynchronously** (AGENTS.md documents this; Axigen is one of the stacks CI runs on every push), so a
just-PUT resource is missing from both the UID query and the listing for up to ~a minute. That is the
"doubly-broken" case, reached routinely rather than pathologically — a Sync Add of one contact into a
2 000-card address book becomes 2 000 serial GETs. Round 3's fix (verify the PUT href by content with one
GET *before* falling back to the listing scan) resolves it for every server that honoured the PUT target,
Axigen included.

### `K10` is still half-closed, exactly as recorded

Round 2's item 13 entry flagged it at the time:

> **⚠ RESIDUAL … `K10` is only half-closed.** `Parse` now treats a trailing segment as a mode only when it
> is exactly `ro`/`rw` … But `Validate` still does a bare `LastIndexOf('|')` and rejects **any** other
> trailing segment … `Parse` and `Validate` disagree about what a `|` means.

Round-3 `K22` **[Nit]** reaches the same disagreement from the error-message side and notes the same
consequence: the message is unreachable for an entry that reaches `Parse`, because an unrecognised suffix is
absorbed into the href. Unchanged since round 2; now assigned to round-3 item 36.

---

## Excluded — plausible matches that failed the bar

Recorded so the exclusions are auditable.

- **Round-3 `F1` (SendMail has no ClientId dedup) vs round-2 `F2`.** Item 25's repair built
  `SendDedupStore` and wired the two-phase claim into the **Sync draft-submit and occurrence-CANCEL** paths,
  which is what F2 named. The ordinary ComposeMail/SendMail path was never in F2's scope. F1 is the gap that
  fix did not cover, not a re-break — and round 3 confirms the F2 machinery itself is sound (Area A's
  verified-correct list checks the claim/complete ordering end to end).
- **Round-3 `E16` (`X-Forwarded-Proto` taken leftmost) vs round-2 `E1`.** Item 7 added the peer-trust gate
  E1 asked for, and round 3 confirms it holds ("forwarded-header trust is consistently peer-gated"). E16 is
  a *parsing* defect inside the now-gated branch — which hop of the header list wins — a distinct question
  E1 never raised.
- **Round-3 `G22` (whole-mailbox FETCH holds the session gate) vs round-2 `D15`.** Item 24 closed D15
  **documentation-only** and said so plainly: "The O(collection) per-round flags fetch and the gate-holding
  behaviour are unchanged; only the rationale is now written down." Nothing was claimed fixed, so this is a
  re-find of a documented accepted cost. Round 3 adds one thing worth having: the unremarked consequence
  that the *fallback* push mechanism (`SnapshotStatusAsync`) queues behind it.
- **Round-3 `C5`/`C6` (Backends page) vs round-2 `C10`.** `C10` — the "Test connection" button probing
  stored settings while the UI shows unsaved ones — was **filed unassigned and never worked**, so it cannot
  be a failed fix. Notably, round 3's WebUi agent read the server side of `/test` and judged it *correct*
  ("deliberately ignores request-body settings and probes only stored ones"), independently reaching round
  2's conclusion about the server while not being shown the UI-side mismatch. **`C10` remains open and is
  not carried into the round-3 queue** — see the note below.
- **Round-2 `A6`/`A8`/`A9`/`A10`, `B4`, `C1`, `C4`, `E2`, `E4`, `E13`, `H1`, `H3`, `H4`, `H6`, `H8`, `H11`,
  `K1`, `K2`, `W2`, `W3`, `W4`** — all re-verified as still correct by the round-3 pass (they appear in the
  relevant areas' *Verified correct* lists, reached independently). No survivors.

---

## Round-2 findings that were never worked, and are still open

Round 2's items 26–32 were never run. Round 3 re-found most of their content independently, which is the
cleanest possible confirmation that the queue was cut short rather than completed:

| Round-2 finding (item) | Round-3 equivalent |
|---|---|
| `F3` GetItemEstimate Status 2 for a transient fault (26) | `F18` (same method, status semantics) |
| `F7` dead `MimeSupport` state (26) | not re-found |
| `F8` `PendingChangeDetector` tight re-poll (26) | `F27` (same mechanism, different trigger) |
| `F9` Search top-level status hard-coded (26) | `F5` (the Find sibling of the same defect) |
| `E5` lean-provider rebuild per `/cli` call (27) | `E5` (same, plus the ALC leak round 2 did not name) |
| `E12`/`E14`/`E15` CLI/logging nits (27) | `E14`, and the rest not re-found |
| `A12`/`A13`/`A14` metrics & snapshot nits (28) | `A10`, `A3` |
| `D7`–`D11`, `D14` Sieve/SMTP/local nits (29) | `G10`, `G17`, `G24`, `G19` |
| `D12`, `D16`–`D19`, `H12` backend nits (30) | `D23`, `D11`, `G8` |
| `K12`, `K20`, `K22`, `K23` contracts nits (31) | `K12`, `K20`, `K22` |
| `C3`, `C6`–`C9`, `B6`, `B9`–`B11`, `E7`, `E9`, `W1`, `W5`–`W8` (32) | `C15`, `C18`, `B7`, `B20`, `E9`, `W6`, `W14` |

**Also still open and unassigned from round 2:** `C10` (the admin "Test connection" regression, High by that
document's scale), `H34` (JMAP calendar/contact `MoveItemAsync` replacing the id map wholesale — the other
half of round-2 `H7`) and `H35` (an `IContentStore` previous-state parameter). None is carried into the
round-3 queue: `C10` and `H34` were not independently re-found by the round-3 agents, and re-filing a
finding nobody re-derived would put an unverified claim into a document whose value depends on every entry
having been checked. **If they still matter, they should be verified against the current tree and filed
under "Found while working the queue".**

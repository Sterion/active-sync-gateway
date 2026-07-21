# How to conduct an exhaustive code review that an agent can then execute

A method note, written after running one end to end on a ~42k-line C# solution: 258 findings,
56 work items, items 1–11 implemented by subagents with per-finding commits and regression tests.

This describes **both halves**, because they are one job. A review that reads well and cannot be
executed is half the work — and almost everything that went wrong in the execution phase traced
back to something the review left implicit.

---

# PART A — What the request actually needs to specify

The original request was:

> Do a complete and thorough review of the complete source code in this repo... Only code, not
> test, build pipelines, documentation... I want a list of items with a severity and enough detail
> so I can give you the list back and you would know what to do... group items together in blocks...
> don't limit the list to a top 10.

That is a good request. It is also underspecified in seven places, each of which cost something
later. If you are commissioning this again, decide these up front — or expect the reviewer to
decide them for you, silently.

### 1. "The complete source code" — does generated code count?

This solution was 42k lines, but ~11k of that was EF Core migration scaffolding. Reviewing it
line-by-line would have been noise; ignoring it entirely would have missed real issues (whether the
two provider migration sets stay in lockstep — they did, but nothing enforced it).

**Decide:** review generated code for *hygiene and invariants*, not line by line. Say so.

### 2. "Severity" — on what scale, anchored to what?

No scale was given. The one that worked, because each level implies a different response:

| Level | Meaning |
|---|---|
| **Critical** | Data loss or process death in normal operation |
| **High** | Security hole, corruption, or a feature that silently does not work |
| **Medium** | Wrong behaviour in a reachable case, or a real performance/maintainability problem |
| **Low** | Latent, narrow, or defence-in-depth |
| **Nit** | Polish |

"Silently" is doing real work in those definitions. A loud failure is a bug; a silent one is a
liability, because nobody is looking for it.

### 3. "Enough detail so I can give you the list back" — this is the load-bearing requirement

It is also the vaguest. What it actually means, learned by having it fail:

- **A stable ID per finding.** Not a position in a list — items get done out of order and lists get
  edited. `D1`, `K56`, `H7`.
- **`file:line` anchored to a named commit**, plus the enclosing type and member, plus a quoted
  fragment of the offending code. Line numbers go stale the moment the first fix lands; the symbol
  and the quote are what survive.
- **The defect and the intended fix, separately.** "This is wrong" is a finding. "Do this instead"
  is what makes it executable.
- **A machine-checkable way to know what is already done.** This is the one nobody thinks of, and it
  is the one that bites. See Part B.

### 4. "Group into blocks" — grouped by *what*?

Unstated, and there are several defensible answers: by severity, by assembly, by risk, by subject.

What worked: **by subject and by file cluster**, so one unit of work is one coherent change touching
a bounded set of files. Grouping by severity would have been useless — it scatters every item across
the codebase and guarantees merge conflicts.

### 5. "I don't mind if they are on the bigger size" — this turned out to be wrong

Bigger blocks sounded efficient and were not. The 18 blocks initially produced had to be flattened
into **56 items, each sized for one session**, because:

- A big block spans unrelated files, so a session doing it holds four contexts at once and does each
  worse.
- Blocks grouped by subject collide on files. Two sessions on different blocks conflict.
- There is no natural stopping point inside a block, so an interrupted one is hard to resume.

**Specify: one item = one session's work = one coherent change.** Err small.

### 6. Who implements it — a human or an agent?

Never stated, and it changes the output format completely. A human wants prose and rationale. An
agent needs an executable protocol: what to commit, how to mark progress, how to verify, when to
stop.

This review was written for a human and then executed by agents, which is why the protocol had to be
retrofitted — and every retrofit was discovered by something going wrong. **Say which, up front.**

### 7. How is a fix known to be real?

Entirely absent from the original request, and the single largest source of later corrections.

"Fixed" must mean *demonstrated*, not *claimed*. That implies a regression test per finding, written
before the fix and observed failing. Without it you get plausible changes struck through as done —
which is worse than not fixing them, because now nobody will look again.

---

# PART B — Conducting the review

## Scope and method

**Read-only.** The review changes nothing. Findings are the deliverable.

**Fan out by subsystem, one agent per area, in parallel.** One agent cannot hold 42k lines. Nine
worked here. Split by assembly and responsibility, sized so each agent reads its files *fully* —
not skims.

Sizing that worked: roughly 2–8k lines per agent, or one project, or one coherent slice of a large
project (Core split three ways: state/sync, accounts/settings, security/crypto).

**Add a cross-cutting pass that no per-area agent can do**, run by the coordinator:

- The project reference graph — what depends on what, and what *shouldn't*
- Namespace vs assembly alignment
- Duplication across assemblies (this review found four independent secret-redaction implementations
  that no single agent could have seen)
- Build health, warning count, analyzer suppressions and whether each is justified
- Test topology — which projects have tests, which don't, what that implies

**Read tests and docs for context, report only on source.** Tests reveal intended behaviour; the
gap between what a test asserts and what the code does is often the finding.

## The per-agent prompt

Each agent needs:

1. **Its exact file list.** Not "review the Core project" — the specific files, so nothing is
   silently skipped.
2. **Permission to read anything for context, but report only on its scope.** Prevents both blind
   spots and duplicate reporting.
3. **Domain context.** "This implements MS-ASCMD; protocol violations cause silent sync failures or
   client resync loops, so protocol correctness is high severity." Without this an agent reports
   style issues in a file with a data-loss bug.
4. **An explicit checklist of what to look for**, tuned per area — concurrency and transactions for
   the state layer; auth, injection and secret handling for the web layer; untrusted-input limits
   for a wire codec; connection lifetime and N+1 for backend adapters.
5. **The output format**, exactly (see template below).
6. **The project's own conventions**, so it doesn't propose changes that contradict them.
7. **"Be exhaustive. Do not stop at 10."** Agents self-limit otherwise.
8. **"Verify by reading the code. Do not speculate."** The single most important line. Findings that
   turn out to be wrong poison the whole document — the reader stops trusting any of it.

## What makes findings trustworthy

The best outcome of this review was not the bugs found. It was that **several things the agents went
looking for came back verified-correct**, and said so: MailKit thread-safety, TLS validation, XXE
hardening, DI captive dependencies, Sieve injection resistance, WBXML code page tables checked
token-by-token against the spec.

Ask for that explicitly. A "verified correct — do not change this" section prevents a later session
from "fixing" something that was deliberate, and it tells the reader the agent actually looked.

## Ranking and grouping

Once findings are in, the coordinator:

1. **Assigns stable IDs**, area-prefixed (`A1`…`A35`, `K1`…`K71`).
2. **Groups into items by subject and file cluster**, one session each.
3. **Orders by consequence, not by area:** data loss and process death, then security, then
   structural work that unblocks everything else, then correctness, then conformance, then
   performance, then tests, then cleanup.
4. **Verifies coverage mechanically** — see below.

## Verify the document mechanically, not by reading it

This caught three real errors that reading did not: a dangling reference to a finding ID that never
existed, a finding assigned to no item at all, and an item silently missed by a scripted edit.

```sh
# every finding assigned exactly once
grep -E '^\*\*[0-9]+\. ' part1 | grep -o '`[A-Z][0-9]\+`' | tr -d '`' | sort > /tmp/a
sort -u /tmp/a | wc -l          # must equal wc -l /tmp/a  → no duplicates
comm -13 <(sort -u /tmp/a) defined_ids   # must be empty  → no orphans
```

Record the resulting numbers as **invariants that never legitimately change** (item count, finding
count, uniqueness, zero duplicates). Any later drift means an edit went wrong. Marking a finding
done must not change them — which constrains the marking format (below).

---

# PART C — The output

**Write the findings into `review-items.md`, using `review-items-TEMPLATE.md`. Do not write a
protocol section — that already exists, in `review-fix.md`, and does not change.**

This separation is the most important structural decision in the whole method, and it was learned
the hard way: the first version put the protocol and the findings in one file, so every protocol
correction meant editing the same file the orchestrator was actively writing findings into. Nine
protocol fixes, nine chances to corrupt live state.

| File | Changes? | Owns |
|---|---|---|
| `conduct-review.md` | never | how to produce a review (this file) |
| `review-fix.md` | never | how to execute one — roles, protocol, verification |
| **`review-items.md`** | constantly | findings, work queue, project commands, invariants |
| `review-results.md` | per item | orchestrator's record of what was done and verified |

The payoff: once `review-fix.md` is right, it is right for every project. A new review produces one
new file and reuses hardened machinery, instead of re-deriving a protocol that took a full
implementation run to get correct.

**What this means for the reviewer:** everything project-specific that the protocol refers to
abstractly — the build command, the test commands and their expected counts, how to start a live
environment, the baseline commit, the warning baseline, whether breaking changes are acceptable —
must be filled into `review-items.md`'s **Project commands** and **Standing context** sections. If
those are vague, `review-fix.md` has nothing concrete to point at and a worker will improvise.

### Optional: a detail file

If findings are long, keep the full text in `review-items-detail.md` and a compressed line per
finding in `review-items.md`. Workers read the summary and fall through to the detail only for the
findings they are implementing. Worth it past a few hundred findings; unnecessary below that.

## `code-review.md` structure

```markdown
# <Project> — full source review

Scope · method · baseline (build warnings, test counts) · finding totals by severity.

## How to use this document
  - stable IDs, what Part 1 and Part 2 are
  - ### Starting a session          ← the prompt, verbatim
  - ### Orchestrated mode           ← one subagent per item, for long runs
  - ### Standing context            ← breaking changes ok? push? warning baseline?
  - ### Working protocol            ← THE contract. Numbered steps. See below.
  - ### [LIVE] items                ← which need a real backend, and how to run it
  - ### Test economy                ← filtered per finding, full suites once per item
  - ### Locating a finding after code has moved
  - ### Editing this document safely
  - ### Keeping this document current

# PART 1 — WORK QUEUE
  N numbered items, each one session, in execution order, grouped under phase headings.
  **12. Item title** [LIVE] — `X1` `X2` `X3`
  > why it matters, constraints, what to do first

# PART 2 — FULL FINDING LIST
  By area. Every finding: ID, severity, file:line, defect, intended fix.
```

## The working protocol — the part that must be right

Nine steps, every one of which exists because its absence caused a concrete failure:

1. **Work findings in the order listed**, honouring any stated sequencing constraint.
2. **Commit per finding**, ID in the subject: `fix(imap): scope EXPUNGE to the deleted UID (D1)`.
3. **Mark the finding in PART 1, on the item's line, in the same commit.** ← see below
4. **If you moved code other findings reference, fix their anchors.**
5. **Build every commit; test at two scopes** — filtered test per commit, full suites once per item.
6. **Write the failing test first.** Red, then fix, then green. Never fix-then-revert-to-verify.
7. **Low on context → stop at a commit boundary and report.**
8. **Stay inside the item.** Anything else becomes a new finding at the bottom of Part 2.
9. **When a test fails, establish whose failure it is** (`git stash` and re-run) before fixing.

### Step 3 is the one everyone gets wrong

The document is the cursor. A session resumes by finding the lowest-numbered Part 1 item with
un-struck findings. Three constraints follow, and all three were learned by violating them:

- **Mark in Part 1, on the item line.** The first worker marked Part 2 instead — defensible, that is
  where findings are defined — and the cursor never moved. Everything was fixed, tested and
  committed, and the next session would have redone the entire item.
- **Keep the backticks inside the strikethrough:** ~~\`D1\`~~ not ~~D1~~. The integrity check finds
  findings by `` `ID` ``. Drop them and every completed finding vanishes from the count, which reads
  as catastrophic data loss.
- **No commit hash in the mark.** It cannot be written in the commit it names, and amending to add
  it changes the hash just recorded. The subject carries the ID; `git log --grep='(D1)'` finds it.

### Step 6, and why fix-first fails

Two ways, both observed:

- A reproducer written *after* the fix passed without it — it was throwing on a different error
  before ever reaching the bug. Only reverting exposed that.
- Another fix could not be reverted at all: it had changed a method signature, so the revert did not
  compile and the "proof" had to be simulated by neutering a bound in place.

Test-first has neither problem. And when a finding genuinely cannot be reproduced — a race with no
deterministic trigger, a symptom the test backend does not exhibit — keep the test as **coverage**,
label it as such in both the test and the notes, and strike the finding on the strength of the fix.
Never leave a coverage test looking like proof.

### Step 5, and the trap underneath it

"Test before each commit" plus "commit per finding" silently means *full suite per finding*. With a
3-minute integration suite and an 8-finding item, that is over an hour of test execution for twenty
minutes of work, and the fourteenth run checks nothing the second did not.

Filtered test per commit; full suites once, before the item's last commit. ~12× less time, same
protection — regression coverage comes from the final run.

**And beware the skip.** If integration tests skip when no backend is reachable, a suite that
verified *nothing* exits 0 and looks identical to one that passed. Say explicitly: read the
passed/skipped counts, not the exit code. Better, make the tooling refuse to run — a pre-flight
probe that fails loudly beats a documented instruction nobody reads.

## Standing context — state these or they get guessed

- Are breaking changes acceptable? (Determines whether API-surface items can be taken at all.)
- Push, or commit only?
- What is the build warning baseline? Zero warnings across a large solution is unusual enough that
  an agent will not assume it — say it, or a new warning slips in unnoticed.
- Which settings/paths are off limits?

## Orchestrated mode

For a long run: a master session spawns **one subagent per item**, sequentially, and verifies each
before continuing.

Two rules that matter more than they look:

**Verify, don't trust.** After each item the orchestrator independently runs the integrity check,
confirms one commit per finding with the ID in the subject, confirms the cursor advanced, and
confirms the build and test counts. Not the worker's report — the evidence. The very first item
produced a truthful success report and a cursor that had not moved.

**The orchestrator never edits `src/` or `tests/`.** On a failure it has three moves: bisect to
establish the fact, spawn a scoped repair subagent, or stop and report. It is the participant with
the most accumulated context and the least room to think; its whole value is being an independent
check. One that authors code is both author and reviewer.

**Cap it at ~8 items and start a fresh one.** Subagents get clean contexts; the orchestrator does
not. One reached 1.5M tokens over four hours and its judgment visibly degraded before any hard limit.
Handover is free — the cursor is the state.

**Keep worker briefs short.** Name the item and anything not in the document (a resumed item, a
stash). Do *not* paste finding text into the prompt: the orchestrator's summaries degrade as its
context grows, and the document is live — findings gain notes as items land, which a stale paste
cannot show.

## Editing hazards to state explicitly

- **Do not use `perl -i -pe` on the document.** It double-encodes UTF-8; every em-dash on a rewritten
  line becomes mojibake. Use a proper editor tool; `sed` is fine for byte-level work.
- **Do not pattern-match `^\*\*(\d+)\. ` across the whole file.** Numbered protocol steps have the
  same shape as numbered queue items. A scripted pass matched both and corrupted the protocol
  section while the integrity check — which only looked at Part 1 — reported everything fine.
  Anchor scripted edits to the Part 1 range.
- Never delete a finding; strike it through. Deleted IDs turn cross-references into dangling ones.

---

# PART D — What this cost, and where

For calibration, on ~42k lines:

- **The review itself:** nine parallel read-only agents, ~1.24M tokens total, about 10 minutes wall
  clock. This is the cheap part.
- **Implementation:** far cheaper per finding than expected, because the review already did the
  expensive thinking. Workers execute a spec rather than explore. Most elapsed time is waiting on
  builds, test suites and containers — wall clock, not tokens.
- **The expensive participant is the orchestrator**, the only one that accumulates. Cap it.
- **Cost is not uniform across the queue.** Well-specified fixes are cheap. The items that are
  genuinely expensive are the structural ones — assembly moves, decomposing an 826-line class,
  breaking API changes, and any item where the review deliberately left a design decision open.
  Expect those to cost more than everything before them combined, and schedule them alone.

---

# Appendix — reusable prompts

## Commissioning the review

> Do a complete and thorough review of all production source in this repository. Read tests, docs
> and CI for context, but report findings only on source.
>
> Cover: correctness bugs, security, concurrency, performance, readability, structure, and assembly
> boundaries (splits, merges, misplaced types, dependency direction).
>
> Fan out with one agent per subsystem, in parallel, each given an explicit file list and told to
> read those files fully. Add a cross-cutting pass for the reference graph, cross-assembly
> duplication, build health and test topology.
>
> For every finding: a stable ID, severity (Critical/High/Medium/Low/Nit), `file:line` plus the
> enclosing type and member, a quoted fragment, the defect, and the intended fix — concrete enough
> to implement without re-deriving it.
>
> Also report what you checked and found *correct*, so later work does not "fix" deliberate design.
>
> Be exhaustive; do not stop at ten. Verify by reading the code — never speculate.
>
> Then produce **`review-items.md`** from `review-items-TEMPLATE.md`: the project's commands and
> standing context, a numbered work queue of one-session items ordered by consequence, and the full
> finding list by area.
>
> Do **not** write a protocol, a working agreement, or instructions for implementers — those live in
> `review-fix.md`, which is project-independent and must not be edited. Your job is the data it
> operates on.
>
> Verify coverage mechanically before you finish — every finding assigned to exactly one item, no
> duplicates, no orphans — and record the resulting numbers in the Invariants block.

## Running one item

> Read `docs/code-review.md`. Implement **item N**. Follow the working protocol in that document.

## Orchestrating a range

> Read `docs/code-review.md`. Work **items N through M** as an orchestrator, following "Orchestrated
> mode". Verify each item independently before continuing — do not trust the worker's report. Record
> each in `docs/code-review-results.md`. Stop and report if any check fails.

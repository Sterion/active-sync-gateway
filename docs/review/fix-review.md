# fix-review — how to execute a code review queue

**This file is project-independent and should not need editing.** It describes the roles, the
protocol and the verification. Everything project-specific — the findings, the work queue, the build
and test commands, the baseline commit, the invariants — lives in **`review-items.md`**, which is the
only file that changes between projects or as work progresses.

If you find yourself wanting to edit this file for a specific project, the thing you want almost
certainly belongs in `review-items.md` instead.

---

## The files

| File | Changes? | Contains |
|---|---|---|
| `conduct-review.md` | never | how to *produce* a review |
| **`fix-review.md`** (this) | never | how to *execute* one |
| `review-items.md` | constantly | the findings index, the queue, the project's commands and invariants |
| `review-items-detail.md` | as findings land | the **full technical write-up** of each finding — the exact symptom, the offending expression, and (often) a recommended fix. The queue and index in `review-items.md` are deliberately terse; the detail file is where the actual engineering guidance lives. **Read a finding's detail entry before implementing it.** |

## The roles

**Worker** — implements exactly one item in a fresh context. Reads `review-items.md` and this file.

**Orchestrator** — spawns one worker per item, verifies each result independently, records outcomes.
Never edits source or tests.

**Human** — decides scope, resolves anything the orchestrator stops on, pushes.

---

## For the operator — the two ways to start

You (the human) only ever type one of these. Everything after is the machine's contract.

**One item, or a small range, in this session:**

```
Read docs/review/fix-review.md and docs/review/review-items.md. Implement item N.
Follow the working protocol.
```

**A longer run, hands-off, one fresh subagent per item:**

```
Read docs/review/fix-review.md, docs/review/review-items.md, docs/review/review-items-detail.md, and AGENTS.md.
Work items 1 through N as an orchestrator following "Orchestrated mode".
```

Substitute the item number(s). Add nothing else — the policy (breaking changes, push, the warning
baseline) lives in `review-items.md`'s Standing context and is read from there, not restated in the
prompt. If you forget to state something, that is a signal it belongs in the docs, not in the prompt.

---

## Starting a session

Single item, or a small range in one session:

> Read `fix-review.md` and `review-items.md`. Implement **item N**. Follow the working protocol.

Items are sizing units, not prompt units — ask for a range freely. Over-asking is safe: every
finished finding is committed and struck through, so an overflowing session stops at a clean boundary
and the next resumes there. Prefer 3–5 items per run; quality decays with context.

---

## Orchestrated mode — one subagent per item

For any long run. The master's context grows only by coordination overhead, so each item gets a clean
slate.

**The orchestrator reads the orientation documents too** (`AGENTS.md` and the ones `review-items.md`
names). It does not write code, but it *decides* — what a repair subagent's brief says, whether a
result is in scope, whether a finding contradicts the architecture — and every one of those decisions
can go wrong without the architecture in front of it. It should know the dependency rule and the
invariants as well as any worker does. **It also reads `review-items-detail.md` for the item it is
verifying** — the queue line is terse, and the detail entry (symptom, offending expression,
recommended fix) is what lets the orchestrator judge whether a worker's result actually matches the
finding rather than merely compiling and passing.

> Read `fix-review.md`, `review-items.md`, and the orientation documents `review-items.md` names
> (`AGENTS.md` first). Work **items N through M** as an orchestrator.
>
> For each item in order, spawn **one subagent** to implement it following the working protocol. Run
> them **strictly sequentially** — never two at once; they collide in git and in `review-items.md`.
>
> When a subagent returns, **verify independently — do not trust its report**:
> - run the integrity check below against the invariants recorded in `review-items.md`
> - confirm `git log` shows one commit per finding with the ID in the subject
> - confirm the cursor advanced — resume must now return the *next* item
> - confirm the build is clean at the project's baseline, and that tests actually **ran**
>
> Then append an entry to `review-results.md`. If a check fails, **stop and report** — do not continue.

### Verify, don't trust — this is load-bearing

The first item ever run under this protocol produced a completely truthful success report: three
findings fixed, tested, committed. It had marked them in the findings list rather than on the queue
line, so the cursor never advanced and the next session would have redone the whole item. Every check
the worker ran passed. The one it did not run was the one that mattered.

An orchestrator that reads the summary and moves on inherits exactly that class of failure.

### The orchestrator never edits source or tests

On a failure it has three moves, and authoring a fix is not one of them:

1. **Bisect** against the previous item's commit — turn the question into a fact, cheaply.
2. **Spawn a scoped repair subagent** — fresh context does the work; verify it like any other item.
3. **Stop and report** if the repair is not obviously in scope.

It is the participant with the most accumulated context and the least room to think, and its entire
value is being an independent check. One that authors code is both author and reviewer, which is the
property the split exists to prevent.

### When to hand off to a fresh orchestrator

Subagents get a clean context per item; the orchestrator does not — it accumulates every report,
verification dump and test summary. That cost is smaller than it first looks (most of a run's tokens
are the *workers'* implementation rolled up into the display, not the orchestrator), so the limit is
not tokens — it is **context accumulation degrading judgment**, gradually, and a degrading
orchestrator gets *sloppy at verification*: terse checks, skipped integrity numbers, "looks good"
instead of the counts. That is worse than a crash, because it silently defeats the whole point.

Handover is free — the cursor is the state — so hand off at a natural boundary rather than a magic
number:

- **A run-alone item is always its own run.** `review-items.md` marks these (a "Run alone" NOTE on
  the item). They are the big structural operations — decompositions, wholesale anchor rewrites —
  where a fresh orchestrator's full attention matters most and a degraded one does the most damage.
- **Prefer to stop at a phase boundary** (the queue's phase headings) — items within a phase share a
  shape, so a batch of them is coherent; crossing into a different phase is a natural seam.
- **Watch for the degradation signals above and cut early if you see them**, whichever comes first.
  A structural batch (dependency-rule work, breaking changes) degrades more expensively than a batch
  of uniform correctness fixes, so lean shorter on the former, longer on the latter.

The human is the reliable backstop here: a degrading orchestrator is precisely the one least able to
notice it is degrading, so external eyes catch it sooner than self-assessment.

### The worker brief is a constant, not a composition

Spawn **every** worker with exactly this text, substituting only the item number, and add nothing:

```
You are a Worker. Read docs/review/fix-review.md, docs/review/review-items.md, and AGENTS.md,
in that order, and follow them exactly. Before implementing each finding, read its full entry in
docs/review/review-items-detail.md — that is where the technical detail and recommended fix live.
Implement item N and only item N. Commit onto the current branch — NEVER create, switch, or rename a
git branch, and never push. Prove every finding red-first: write the failing test and watch it fail on
UNMODIFIED code BEFORE you touch the fix (never fix-then-revert-to-see-red). Report back per the
protocol: each finding ID with its commit and how it was proven (red-first / coverage / N/A),
the full unit-suite counts, every behaviour or breaking change, any coverage-not-proof test,
any judgment call, and any new findings filed.
```

**Do not compose a brief.** The instinct is to help the fresh worker by front-loading it with
context you just read — the dependency direction, the policy, per-finding notes. Resist it, because
it fails three ways:

- **It restates what the worker already reads.** Findings, commands, the dependency rule, the
  standing policy (breaking changes, push, warning baseline) — all of it is in the two docs and
  `AGENTS.md`, which the brief already points at. Restating is pure redundancy.
- **It drifts.** Your context degrades across a run, so your paraphrase gets worse exactly as the run
  goes on — a tired session briefing a fresh one, which is backwards. The docs are live; a paste
  from earlier cannot show notes added since.
- **It hides missing state in the wrong place.** If you find yourself wanting to tell the worker
  something genuinely useful and *not* in the docs — "this finding's fix is breaking", "helper X now
  exists from a prior item" — that is a **signal the docs are incomplete**, not a licence to pad the
  brief. Put the durable fact where it belongs (the finding's own text; standing context) so *every*
  future run sees it, then leave the brief a constant.

The one exception is a **live anomaly the worker cannot derive from the git tree or the docs** — an
active stash, a half-finished item, a backend mid-restart. For that, and only that, add one line:
`Situational: <the fact>`. Nothing else.

---

## Orient before you start

`review-items.md` names the project's **orientation documents** — architecture notes, conventions,
layer invariants, whatever the project keeps. **Read the ones relevant to the files you are about to
touch, before you touch them.**

These are not background reading. They routinely contain hard constraints that the code does not
state and that a reasonable change will violate silently:

- dependency rules — which assembly may reference which, and in which direction
- layer invariants — "every change to this table needs a round-trip test", "this attribute is the
  marker every producer and consumer relies on"
- conventions a linter cannot enforce — where a type belongs, what a name means
- decisions already taken and their reasons, so you do not re-litigate or quietly reverse one

A finding tells you what is wrong with a few lines. The orientation docs tell you what the code is
*for* and what it must keep being true. Skipping them produces changes that are locally correct and
architecturally wrong — which pass tests, pass review, and are expensive later.

**Structural items make this mandatory.** Anything moving types between assemblies, changing a public
contract, or splitting a large type is essentially executing the architecture document. Doing it
without reading that document is guessing.

If an orientation doc contradicts a finding, **stop and report it** — do not pick a side. One of the
two is wrong and that is a decision for a human.

## Working protocol — follow this for every item

**0. Read the finding's full detail entry before you implement it.** `review-items.md` gives only a
one-line index entry per finding; the real write-up — exact symptom, the offending expression, and
often a recommended fix — is in `review-items-detail.md` (Areas A–W; Area S is self-contained in
`review-items.md`). Locate the entry by its ID (e.g. `B3.`) and work from it. Skipping it is how a
worker "fixes" something other than what the finding actually describes.

**1. Work findings in the order listed.** Honour any sequencing constraint stated on the item.

**2. Commit after each finding**, or each tight cluster, with the ID in the subject:

```
fix(imap): scope EXPUNGE to the deleted UID (D1)
```

Small commits are the point — they make the work resumable and each finding independently
revertible. Do not batch an item into one commit.

**Never create, switch, or rename a git branch. Commit onto whatever branch you are already on.**
The orchestrator runs workers strictly sequentially on one branch precisely so their commits form a
single resumable line; a worker that does `git checkout -b review/item-N` strands its work on a branch
the orchestrator and every other worker cannot see, and the next item builds on a `main` that is
missing it. Branch, push, PR and merge decisions belong to the **human**, never the worker — see step
7 and the Standing context. If the working tree is not where you expect, **stop and report**; do not
"tidy up" with a branch.

**3. Mark the finding in the same commit — on the item's line in the work queue.** That line is the
cursor: resume finds the lowest-numbered item with un-struck findings. A finding marked only in the
findings list leaves the cursor untouched and the item gets done twice.

```
**12. Item title** — ~~`X1`~~ ~~`X2`~~ ~~`X3`~~ **COMPLETE**
```

Use `~~`X1`~~ **N/A** — <one line why>` for a finding that no longer applies. Two rules:

- **Keep the backticks inside the strikethrough.** The integrity check finds findings by `` `ID` ``;
  dropping them makes every completed finding vanish from the count, which reads as data loss.
- **No commit hash.** It cannot be written in the commit it names, and amending to add it changes
  the hash just recorded. The subject carries the ID — `git log --grep='(X1)'` finds it.

Annotating the findings-list entry as well is welcome — that is the right home for a breaking-change
note or a caveat about what a test proves. It is a supplement, never a substitute.

**4. If you moved or renamed code other findings reference, fix their anchors.** You are the only one
who will know where it went.

**5. Build before each commit; test at two scopes.** Use the build command from `review-items.md` and
keep its stated warning baseline.

| When | What | Cost |
|---|---|---|
| Before each commit | only *this finding's* test, via `--filter` | seconds |
| Once, before the item's last commit | the full suites named in `review-items.md` | minutes |

Do **not** run a full suite per finding. With one commit per finding that is the same suite re-run a
dozen times per item — an 8-finding item can spend over an hour executing tests for twenty minutes of
work, and the fourteenth run checks nothing the second did not. Regression protection comes from the
final run.

**6. Write the failing test FIRST — this is the single most-violated rule. The ORDER is the proof.**

The only valid sequence, per finding, no exceptions:

1. Write the reproducer. **Do not touch the production code yet.**
2. Run it against the **unmodified** code and **watch it fail with the finding's described symptom.**
   Keep that red output — it is the evidence you report.
3. **Only now** apply the fix.
4. Re-run — the same test passes, and so does the rest of the suite.

**The forbidden sequence — do NOT do this (it is exactly what has gone wrong repeatedly):**

> ❌ write the fix and the test together → run → see green → revert the code → see red → re-apply the fix.

That is **not** red-first and does **not** count as proof, even though the diff looks identical at the
end. Three concrete reasons it is banned, all observed in practice:

- **A test written alongside the fix is shaped by the fix.** It asserts what the new code does, not the
  symptom the finding describes. Written first, against code you have not yet changed, it is forced to
  target the *bug*.
- **Reverting often doesn't cleanly reproduce.** A fix that changed a signature, a helper, or a test
  seam cannot be reverted in isolation — the revert doesn't compile, so the "red" has to be *simulated*,
  which is not evidence of anything. One observed reproducer, written after the fix, threw on an
  unrelated earlier error and "passed" without the fix at all.
- **It is self-deception disguised as rigour.** The ceremony of watching red-then-green happened, so it
  feels proven — but the red you saw came from code you already wrote and then removed, not from the
  original defect. A finding struck through on this is a **false record**.

If you catch yourself about to write the fix and the test in the same pass, **stop and write only the
test.** A test that passes with *and* without the fix proves nothing; the ordering is the only thing
that makes that impossible to fake.

When a finding genuinely cannot be reproduced — a race with no deterministic trigger, a symptom the
test environment does not exhibit — keep the test as **coverage**, label it as such in both the test
comment and the findings note, and strike the finding on the strength of the *fix*. Never leave a
coverage test looking like proof.

**7. If you run low on context, stop at a commit boundary** and report exactly which findings are
done and which are untouched. Do not start a finding you cannot finish and verify. Because of steps
2–3, stopping early costs nothing.

**8. Stay inside the item.** Anything you notice outside it goes at the bottom of the findings list as
a new finding, not fixed inline.

**9. When a test fails, establish whose failure it is** before fixing anything:

```sh
git stash -u && <run the failing suite>; git stash pop
```

- **Green without your change → yours.** Fix it. This includes a test harness your change
  legitimately broke — but say so explicitly, prove source is untouched with a diffstat, and add a
  guard test so the accommodation cannot become a blind spot for the finding you just fixed.
- **Red without your change → not yours.** Stop. Commit nothing further and report it, saying plainly
  that it predates your work. Do not fix it and do not work around it.

Never disable, skip or weaken a test to get green. If a test encodes behaviour a finding deliberately
changes, rewrite it and call that out as a behaviour change.

---

## Live-environment verification

Some items need a real backend, database or service — `review-items.md` marks these and records how
to start it.

> **A skipped suite exits 0 and looks exactly like a passing one.**
>
> If integration tests skip when the environment is unreachable, a run that verified **nothing**
> reports green. This is the single easiest way to strike a Critical through unverified.
>
> **Read the passed/skipped counts, not the exit code.** Compare against the baseline in
> `review-items.md`. If passed is 0, or skipped is large, fix the environment and re-run. Do **not**
> strike a finding through on a skipped suite.

**The marked list is a floor, not the whole rule.** Also run the live suite for any item that lands a
schema migration, changes authentication or session policy, or alters the request pipeline — marked
or not. One unmarked item's cookie-policy fix broke 23 integration tests while every unit suite
stayed green; undetected it would have surfaced eighteen items later. When the worker *does* run a
live suite for such an item it may narrow to the relevant test classes with `--filter`; **the
orchestrator's independent verification runs the full suite** — an auth or pipeline change's blast
radius is non-obvious (that is the whole reason the rule exists), so scoping it down defeats the
point.

### Restart the live backend fresh, in parallel with the worker

A long-lived backend container accumulates state across runs — orphaned DAV items, async indexes
that lag under load — until a *single* full suite starts failing a **shifting** subset of tests.
That is indistinguishable at a glance from a real regression, and chasing it is expensive: it can
cost a five-run bisect to prove "environmental."

Kill it structurally. For any item that will run a live suite, the orchestrator restarts the backend
from a **clean volume** the moment it spawns the worker:

```
spawn worker  →  (backend restarts from clean volume, in the background)  →  wait for worker  →  confirm healthy  →  verify
```

The restart runs *inside* the worker's 5–15-minute work window, so it costs **zero** wall-clock time,
and it lands the container fresh for both the worker's own live run and the orchestrator's
verification. Three rules: it must be a **clean-volume** restart (a plain restart keeps the state);
**confirm the container is healthy** before verifying rather than trusting the timing; and do it
**only for items that will run a live suite** — no point reprovisioning for an item that never
touches the backend. The exact command is in `review-items.md`'s Project commands.

A green live run is still trusted without a restart-and-retry — degradation only ever makes items
*fail to appear* (false failures), never falsely pass — so this is about eliminating the false
*failure*, not doubting a pass.

---

## Locating a finding after code has moved

**Every `file:line` in `review-items.md` is exact as of the baseline commit recorded there.** Line
numbers are a hint, not an address — they drift as soon as one item lands, and structural items
invalidate them wholesale: moving types between assemblies sends a finding to the wrong *project*,
and splitting a large file sends it to a file that no longer holds the code.

**Locate by symbol, not by line.** Each finding names the enclosing type and member, and most quote
the offending expression. Grep for that; use the line number only to disambiguate between hits.

**Before editing, confirm the defect is still there.** An earlier item may have fixed, moved or
obsoleted it. The baseline commit is recorded so git can trace movement:

```sh
git show <baseline>:path/to/File.cs | sed -n '780,830p'    # what the review saw
git log -L 780,830:path/to/File.cs --oneline               # how it changed since
git diff <baseline>..HEAD -- path/to/dir/                  # everything that moved in an area
```

---

## Editing `review-items.md` safely

You will edit it on every item. Two traps, both of which have corrupted it in practice:

**Do not use `perl -i -pe` on it.** It double-encodes UTF-8: every em-dash on a rewritten line
becomes mojibake. Use a proper editor tool; `sed` is fine for byte-level surgery.

**Do not pattern-match numbered-bold lines across the whole file.** Numbered protocol steps and
numbered queue items have the identical shape, so a global match hits both. Anchor to the queue
section first. This exact mistake corrupted a protocol section while the integrity check — which only
looked at the queue — reported everything fine.

**Never delete a finding — strike it through.** IDs are referenced by other items and by any session
started before the fix landed. A deleted ID turns those into dangling references.

### Integrity check

`review-items.md` records its own invariants. They **never legitimately change** — striking a finding
through does not remove it — so any drift means an edit went wrong. Run after any scripted edit:

```sh
# adjust the section markers to match review-items.md
sed -n '/^# WORK QUEUE/,/^# FINDINGS/p' review-items.md > /tmp/q

echo "items=$(grep -cE '^\*\*[0-9]+\. ' /tmp/q)"
grep -E '^\*\*[0-9]+\. ' /tmp/q | grep -o '`[A-Z]\+[0-9]\+`' | tr -d '`' | sort > /tmp/f
echo "assigned=$(wc -l < /tmp/f) unique=$(sort -u /tmp/f | wc -l) dupes=$(uniq -d /tmp/f | wc -l)"

# encoding damage: both must be 0
grep -c $'\xc3\xa2\xc2\x80\|\xc3\xb0\xc2\x9f' review-items.md
```

Compare every number against the invariants block in `review-items.md`.

**Definition adequacy** — every report-backed finding assigned in the queue must have a full entry in
`review-items-detail.md` (not just an appearance in the index). Run this to catch a finding whose detail
is missing, or — the subtler failure — a whole area reconstructed under the wrong ID offset:

```sh
grep -oE '`[A-Z][0-9]+`' review-items.md | tr -d '`' | grep -E '^[ABCDEFHKLSW][0-9]+$' | sort -u > /tmp/ref
grep -oE '^[A-Z][0-9]+\.' review-items-detail.md | tr -d '.' | sort -u > /tmp/def
echo "orphan detail (defined but never referenced — MUST be empty):"; comm -13 /tmp/ref /tmp/def
echo "missing detail (referenced but not defined):";                  comm -23 /tmp/ref /tmp/def
```

The *orphan* list must be empty — a defined entry nothing references means a typo'd or off-by-N ID.
The *missing* list is expected to contain **only** Area `S` items and anything under "Found while
working the queue" (both are self-contained in `review-items.md` by design). Any `A`–`W` area finding
showing up there means its detail entry is missing or misnumbered.

---

## Recording results

The orchestrator maintains `review-results.md` — one entry per completed item:

```markdown
## Item N — title
**Findings:** `X1` `X2`
**Commits:** `abc1234` (X1) · `def5678` (X2)
**Verification:** integrity <numbers> ✓ · cursor → item N+1 ✓ · one commit per finding ✓ ·
build clean ✓ · tests <observed counts> ✓
**Notes:** <breaking changes · tests that are coverage not proof · judgment calls that could
reasonably have gone the other way · anything a future reader needs that a diff will not show>
```

**The worker writes nothing here; the orchestrator does.** Each entry pairs the worker's claim with
independent evidence, and keeping them separate is the point.

**The notes are the most valuable part.** A diff shows what changed, never that a change forces a
one-time full re-sync on upgrade, or that a passing test proves nothing, or that a judgment call went
one way and could reasonably have gone the other.

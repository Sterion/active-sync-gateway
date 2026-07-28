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
> Spawn every worker on **Sonnet** (`model: "sonnet"` on the Agent call) — do not let it inherit the
> orchestrator's model.
>
> When a subagent returns, **verify independently — do not trust its report**:
> - run the integrity check below against the invariants recorded in `review-items.md`
> - confirm `git log` shows one commit per finding with the ID in the subject
> - confirm the cursor advanced — resume must now return the *next* item
> - confirm the build is clean at the project's baseline, and that tests actually **ran**
>
> - read the diff of **every** finding the item landed against its `review-items-detail.md` entry —
>   a sample is not a verification
>
> Then append an entry to `review-results.md`. If a check fails, **stop and report** — do not continue.
>
> **Narrate as you go** — say what you are spawning, a SHORT summary of what came back, and each check
> with its result, as you run them. See "Narrate the run" below.
>
> Before you finish the run, do the **end-of-run sweep** over every item the run landed and record a
> run-summary entry.

### Verify, don't trust — this is load-bearing

The first item ever run under this protocol produced a completely truthful success report: three
findings fixed, tested, committed. It had marked them in the findings list rather than on the queue
line, so the cursor never advanced and the next session would have redone the whole item. Every check
the worker ran passed. The one it did not run was the one that mattered.

An orchestrator that reads the summary and moves on inherits exactly that class of failure.

### Read every finding's diff — a sample is not a verification

For each item, read the diff of **every** finding it landed, against that finding's entry in
`review-items-detail.md`. Not one or two per item, not "the interesting ones" — all of them. Three
questions per finding, and they are cheap because the detail entry already states the answer:

1. Does the change touch the **site the finding names** (the type and member, the quoted expression)?
2. Does it implement **the defect's remedy**, or something adjacent that merely compiles and passes?
3. Was the test **red first** on unmodified code — or is it labelled coverage, with a stated reason
   the symptom cannot be exhibited here?

A worker following a spec fails by *drifting off the spec*, not by writing code that fails to build.
The build and the suite catch nothing here: a fix aimed at the wrong expression compiles, passes its
own test, and gets struck through — and the strike is what stops anyone ever looking again. Sampling
finds this only in the findings you happen to sample.

Where the diff is uninspectable at that granularity — a tight-cluster commit covering several
findings — read the whole commit against **all** the IDs in its subject and say so in the results
entry, as the existing entries for items 2 and 5 do.

### Narrate the run — a silent orchestrator cannot be corrected

An orchestrated run emits almost nothing a human can read. The work happens in subagents and in tool
calls, and the harness collapses both to one-line chips — "Ran 7 commands, ran an agent". A run can
therefore proceed for hours, land dozens of commits, and give the human no place to step in. On this
programme's first long run the human had to interrupt at item 5 and ask for output, having watched
four items go by as a column of collapsed chips.

**So narrate, in the turn itself, as you go.** Four beats per item, none of them long:

1. **Before spawning** — the item number and title, its finding IDs, and anything you have decided
   about it: whether you are running the live suite and *why*, a restart you kicked off in parallel,
   an orientation document you had to read first.
2. **When the worker returns** — a **SHORT** summary. Two to four lines: what each finding actually
   changed, plus anything the worker disclosed that you intend to check (a rewritten test, a
   deviation, a skipped suite). Never paste the worker's report; it is written for you, not for the
   human.
3. **Each check, with its result** — integrity numbers, cursor, strike-with-the-fix, the red-first
   re-proof, build, unit counts, live counts. State the number you got, not "verified".
4. **The entry** — say it is recorded, and surface the one or two things a human would actually want
   to act on (a breaking change, a partial close, a new finding).

Keep it to what a reader can skim. No test logs, no full diffs, no restating the finding text — those
are what `review-results.md` and the detail file are for.

This is not decoration, and it is not for reassurance. It is the mechanism behind two rules this
document already relies on:

- **The human is the backstop for a degrading orchestrator** ("When to hand off", below) — and they
  can only catch what they can see. Degradation shows up first as *terse verification*: "looks good"
  instead of the counts. A narrated run makes that visible in the transcript, to the human and to you.
- **Stopping early costs nothing** (working protocol, step 7) — but only if the human knows where the
  run has got to. An interruption during a silent run leaves them reconstructing state from `git log`.

The results entry is written for the *next orchestrator*, after the fact. The narration is written for
the *human*, during. They are different audiences and neither substitutes for the other.

### The end-of-run sweep

Per-item verification is done with a worker's report in front of you and one item of context. Before
you finish a run — hand off, hit a phase boundary, or stop — do one pass over **everything the run
landed**, cold:

```sh
git log --oneline <commit before the run>..HEAD          # one commit per finding, ID in each subject
git diff --stat <commit before the run>..HEAD            # nothing touched outside the items' scope
```

Then, over that whole range:

- **Every finding claimed in the run is struck in Part 1** and its ID appears in exactly one commit
  subject — reconcile the two lists, do not eyeball them.
- **Nothing landed outside the items' declared file clusters.** A worker straying into a neighbouring
  file is in-scope-creep the per-item check reads as normal.
- **Run the integrity check and the full unit suite once more at HEAD**, plus the live suite if any
  item in the run was `[LIVE]`, landed a migration, or changed auth or the pipeline. Per-item runs
  each proved a tree that no longer exists; only this one proves the tree you are handing over.
- **Re-read the notes you wrote** for the run's items. A caveat that looked local at item 3 —
  a behaviour change, a coverage-not-proof test, a judgment call — often reads differently once
  items 4–8 have landed on top of it.

Record the result as a **run summary** entry in `review-results.md` (see Recording results). If the
sweep contradicts an entry you already wrote, the sweep wins: correct the entry and say what changed.

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

### The worker model is pinned, not inherited

Spawn workers with an explicit **`model: "sonnet"`**. This is a *spawn parameter*, not brief text —
the brief above stays a constant, byte-identical for every worker.

Without it a subagent inherits the orchestrator's model, so the run's cost and behaviour drift with
whatever the human happened to start the session on. Pinning splits the two roles the way the
protocol already assumes they differ:

- **The worker executes a spec.** The expensive thinking is already done — the finding names the
  defect, the file, the symbol and usually the remedy; the protocol dictates red-first, one commit
  per finding, where to mark the cursor. That is instruction-following against a written contract,
  which is what the queue was flattened into one-session items to make it.
- **The orchestrator judges.** It decides whether a diff matches the finding rather than merely
  compiling, whether a result is in scope, whether a repair brief is right, whether a finding
  contradicts the architecture. It is also the participant that accumulates context and degrades.
  Leave it on whatever model the human started; do not pin it here.

The protection against a weaker worker is the verification that already exists — integrity check,
one commit per finding, cursor advanced, build clean, tests actually ran, and the diff read against
the finding's detail entry. If a worker's output does not survive that, the answer is the documented
one: bisect, spawn a scoped repair subagent, or stop and report. **Never** relax a verification step
because of the model a worker ran on, and never quietly upgrade a single worker to get an item to
pass — an item that needs a bigger model to land is a signal the *item* is mis-sized or the finding
is under-specified, and that is a human decision.

The one standing exception is an item `review-items.md` marks **"Run alone"** — the big structural
operations (assembly moves, decompositions, wholesale anchor rewrites). Those are architecture
execution rather than spec execution, and the human may choose a stronger worker model for them;
say so explicitly when starting that run.

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

**The strike ships WITH the fix. One bookkeeping commit at the end is a protocol violation, even
though it ends up looking identical.** This is the most-repeated deviation in this programme — three
of four workers in one run did it, each disclosing it honestly afterwards, which means the
instruction (not the worker) is what keeps failing. So, concretely:

> ❌ `fix(a)` → `fix(b)` → `fix(c)` → `docs: mark item N complete (a, b, c)`
> ✅ `fix(a)` **+ strike a** → `fix(b)` **+ strike b** → `fix(c)` **+ strike c**

`git show <your commit> --stat` must list **both** the source file and `review-items.md`, every time.
If it doesn't, `git commit --amend` the strike in **before** starting the next finding — that costs
nothing, and it is the only cheap moment to fix it.

Why it matters even though the end state is the same: the strike is not paperwork, it is the
**cursor**. Between your last fix commit and a trailing bookkeeping commit, the tree holds N finished
findings that the document says are not done — so an interruption there (context exhaustion, a failed
build, a stopped run, a crash) sends the next session to redo the entire item on top of work that
already landed. The window is usually minutes and the loss is an entire item, which is a bad trade
for a commit you have to make anyway.

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

**8a. NEVER put a finding ID in source, tests or shipped docs — the ID is scaffolding, the explanation is
the deliverable.** `// F13: a backend created the folder but its listing does not reflect it yet` must be
written as `// A backend can create the folder while its own listing does not yet reflect it`. The same
goes for test names (`F14_VanishedItem_IsNotCountedAsSent` → `VanishedItem_IsNotCountedAsSent`), commit
*bodies*, and any doc outside the review folder.

Three reasons, and the first two bite even before the queue is finished:

- **The IDs are per-round and they collide.** Round 2's `F13` and round 3's `F13` are different findings.
  A reader who greps `F13` gets two answers and no way to tell which. This is not hypothetical — it is
  already true in this repository.
- **A bare marker carries no information at all.** `(A18)` next to a line of code tells a future reader
  nothing except that a document they may not have once said something.
- **The review folder is temporary.** It gets deleted when the round is finished, taking every referent
  with it and leaving dangling IDs behind in permanent code.

**The commit SUBJECT is the one exception** — `fix(imap): scope EXPUNGE to the deleted UID (D1)` is
correct and required by step 2, because the subject is how the orchestrator reconciles findings to commits
and git history is not deleted with the folder. Keep the ID out of the body prose.

Test this on yourself before writing the comment: **delete the review folder in your head, then re-read
the line.** If it stops making sense, the comment was leaning on the ID and needs the explanation written
into it instead.

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

**A green unit suite is not evidence that you do not need the live suite.** It is the single most
tempting wrong inference on this queue, and it has already cost a run: an item touching WebUi
endpoints reported 1085 unit tests green, concluded no live run was needed, and had broken **three**
integration tests — a portal response shape and a probe's semantics, neither of which any unit test
covers. The unit suites do not exercise the assembled HTTP surface, so they cannot speak to it.

So the decision is not "do I think this needs a live run?" but **"can I show it cannot?"** If your
item touches any endpoint, handler, DTO shape, migration, auth path, or anything reachable over HTTP,
run it. When you skip it, the report must say *why* in one specific sentence ("no file in this item is
reachable from a request path"), not "not marked [LIVE]". A worker's judgement that its own item is
low-risk is exactly the judgement the orchestrator's independent full-suite run exists to check —
and when the two disagree, it has so far been the worker.

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

And one **run summary** per run, from the end-of-run sweep:

```markdown
## Run summary — items N–M
**Swept:** `git log` range · every claimed finding struck in Part 1 and present in exactly one commit
subject ✓ · no changes outside the items' file clusters ✓
**At HEAD:** integrity <numbers> ✓ · build clean ✓ · unit <counts> ✓ · live <counts, or why not required> ✓
**Carried forward:** <caveats that read differently now that later items landed on top · anything
the next orchestrator must know · where the cursor rests>
```

This is the entry a fresh orchestrator reads first. The per-item entries say what each item did; the
run summary says what state the tree is actually in — verified at HEAD, not at eight intermediate
trees that no longer exist.

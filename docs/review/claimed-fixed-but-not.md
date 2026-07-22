# Claimed fixed in round 1, found again in round 2

A regression-integrity audit. Round 1 (items 1–38 done, 36 partial) struck a set of findings as
**fixed**. This file lists the cases where round 2 independently re-found **the same defect at the
same site** — i.e. the round-1 fix did not actually close the problem it claimed to.

## Method & bar for inclusion

Round-2 finding IDs mean nothing to round 1, so nothing here is matched by ID. Each entry was matched
by **described problem + code site**, then filtered hard:

1. The round-1 finding must have been **explicitly struck/COMPLETE** (marked fixed on its item line),
   in an item that was **actually done** (items 1–38). Anything in items 39–56 was never claimed fixed
   and is excluded, even where round 2 re-reports it.
2. The round-2 finding must be the **same defect**, not a near-neighbour the round-1 fix introduced or
   left adjacent. A defect that is "close but different" (a new interaction, the same rule in a
   different handler, a gap the fix explicitly declared out of scope) is **excluded**, with a note
   below on why, so the exclusions are auditable too.
3. The round-1 fix commit's own claim is quoted where it contradicts the surviving defect.

Two findings clear that bar. A third (**A6 ↔ A22**) is a **probable** — the same symptom at the same
method, where whether it counts as "the same defect" depends on how narrowly you read the round-1
finding; it is recorded in its own section below, kept separate from the two high-confidence ones.

---

## 1. Case-variant account/setting rows still collapse — round-1 `B2` → round-2 `B1`

**Round 1** — item 25 "Account resolution & storage casing" (**COMPLETE**, `~~B2~~` struck):

> `B2` **[High]** `AccountEntry.UserName` / `GlobalSetting.Key` matched case-SENSITIVELY in SQL,
> case-INsensitively in memory … `eas user set Phone1` when `phone1` exists inserts a SECOND row;
> `LoadAllAsync` collapses both, last-row-wins by unspecified order — which override is live flips
> between restarts.

**What the fix actually did** (commit `ca03f49`, *"fix(accounts): match store lookups
case-insensitively (B2)"*). It changed the three read predicates in `AccountStore` and
`GlobalSettingStore` from `a.UserName == login` to `a.UserName.ToLower() == login.ToLower()` (and the
same for `GlobalSetting.Key`), so `Get`/`Upsert`/`Delete` find an existing differently-cased row instead
of inserting a colliding one. Its regression test (`Upsert_IsCaseInsensitive_NoDuplicateRow`) upserts
`phone1` then `PHONE1` and asserts `ListAsync` returns a single row. That proves the **CLI/API upsert
path** no longer duplicates — and nothing more. The commit touched no migration, no index, and not
`LoadAllAsync`. Its own message even restates the surviving hazard verbatim: *"LoadAllAsync then
collapsed both with a last-row-wins winner that flipped across restarts"* — describing it as the past
tense of what B1 shows is still present.

**Round 2** — `B1` **[High]**, `Accounts/AccountStore.cs:43-46` (`LoadAllAsync`):

> Case-only-duplicate DB account rows silently collapse, last-write-wins. `AccountEntry.UserName` has a
> unique index on the **raw** column; under SQLite's default BINARY collation `phone1` and `Phone1` are
> two distinct index values, so both rows coexist. The OrdinalIgnoreCase load dictionary then keeps
> only one arbitrarily, silently discarding the other user's entire override set.

**Why it's the same problem, not a near-neighbour.** The round-1 fix changed only the **write-time
lookup** (`Get`/`Upsert`/`Delete` now `LOWER()`-compare), so a write *through the CLI/API upsert path*
now updates the existing row. It did **not**:

- change the **unique index**, which is still on the raw BINARY column — so two case-variant rows can
  still coexist (a pre-existing pair from before the fix, a row written by any path that bypasses the
  `LOWER()` upsert, a restored dump, or a direct DB write), and
- change **`LoadAllAsync`** (the line the original B2 named as the collapse site), which still builds an
  `OrdinalIgnoreCase` dictionary that collapses any such pair last-write-wins.

The High that B2 described — *"`LoadAllAsync` collapses both, last-row-wins … flips between restarts"* —
is exactly what B1 still observes at exactly that method. The fix closed the *duplication-on-CLI-write*
symptom but not the *collapse* the finding was about. (Round-2 `B8` separately notes the `ToLower()`
predicates are non-sargable and still can't see the pair; it is grouped with `B1` in round-2 item 5.)

**What actually closes it:** case-folded uniqueness at the DB layer (store `UserName`/`Key` lowercased,
or a computed lower column carrying the unique index) + a migration to collapse existing duplicates —
which is what round-1 B2's own FIX text proposed (*"normalize at the store boundary or use
`Collate("NOCASE")`/citext indexes; migration to collapse existing duplicates"*) and the commit did not
do.

---

## 2. Passphrase key-derivation salt is still fixed by default — round-1 `K45` → round-2 `K1`

**Round 1** — item 14 "Credential & key handling" (**COMPLETE**, `~~K45~~` struck, commit
`41a476d`):

> `K45` **[Medium]** Global fixed PBKDF2 salt — one rainbow table covers every deployment …
> `DerivationSalt = UTF8.GetBytes("ActiveSync.Encryption.KeyDerivation.v1")` — every gateway derives its
> passphrase key with the same salt … FIX: derive a per-deployment salt from a random 16-byte value
> generated at first boot and stored in the settings table (versioned, falling back to the fixed salt
> for existing installs); or switch to Argon2id.

**What the fix actually did** (commit `41a476d`, *"fix(crypto): add an **optional** per-deployment
PBKDF2 salt (K45)"*). It added a new `EncryptionOptions.KeyDerivationSalt` and a `ResolveSalt(options)`
helper, and routed `DeriveKey` through it: `Rfc2898DeriveBytes.Pbkdf2(material, ResolveSalt(options),
…)`. But `ResolveSalt` is:

```csharp
private static byte[] ResolveSalt(EncryptionOptions options)
{
    if (string.IsNullOrWhiteSpace(options.KeyDerivationSalt))
        return DerivationSalt;                          // ← the original global fixed salt, unchanged
    return SHA256.HashData(Encoding.UTF8.GetBytes(
        "ActiveSync.Encryption.KeyDerivation.v1:" + options.KeyDerivationSalt.Trim()));
}
```

So the per-deployment salt applies **only when the operator sets the new option**; the unset default
path returns the same `DerivationSalt` constant K45 named. The commit's own doc-comment says it plainly:
*"Unset keeps the historical fixed application salt for back-compat."* Its regression test
(`KeyDerivationSalt_ProducesDeploymentSpecificKeys`) asserts two *different explicit salts* yield
different keys — it never asserts anything about the default. The fix made the vulnerability *avoidable*;
it did not make the default *safe*, which is the half of K45's own FIX text (*"generated at first boot
and stored in the settings table"*) that was not implemented.

**Round 2** — `K1` **[High]**, `Crypto/EncryptionKeyLoader.cs:28,141-147` (`ResolveSalt`):

> Passphrases are the DOCUMENTED path (README quick-start `"Key": "<any passphrase>"`); unless an
> operator sets `KeyDerivationSalt` (unset by default, not in the quick-start), every deployment
> stretches its passphrase against one identical, publicly-known salt … a single precomputed table
> recovers the master key for every default-configured gateway that used a weak passphrase.

**Why it's the same problem, not a near-neighbour.** The round-1 fix implemented the *opt-in half* of
its own FIX text: it added `Encryption:KeyDerivationSalt` so an operator **who sets it** gets a
per-deployment salt. But `ResolveSalt` still returns the original global constant `DerivationSalt` when
the option is unset (the default), and the option is not part of the documented quick-start. So the
**default deployment** — a passphrase in `Encryption:Key` with no `KeyDerivationSalt` — has the exact
exposure K45 described: one publicly-known salt shared across every install, one precomputed table over
every stolen DB. The finding was struck on a fix that made the vulnerability *avoidable* but left it as
the *default*.

Round 2 raises the severity from Medium to High because it re-scoped the blast radius against the
documented passphrase path (the master key decrypts all local item content, sealed backend credentials,
the TLS private key and CLI envelopes) — but it is the **same defect at the same line**, not a new one.

**What actually closes it:** the *other* half of round-1's own FIX text — generate the random salt on
first boot with **no operator action** and persist it (the state DB already holds the sealed cert row),
or refuse passphrase mode without an explicit salt. Round-2 `K1` says the same. (This is a breaking
change — it re-keys passphrase deployments on upgrade — which is acceptable here; see Standing context.)

---

## Probable — same symptom at the same method, arguably a distinct race

This one is ~95% but not certain. It is kept separate from the two above because whether it counts as
"the same defect" depends on how narrowly you read the round-1 finding.

### `CompleteAccountWipeAsync` still 500s on a concurrent wipe ack — round-1 `A22` → round-2 `A6`

**Round 1** — item 22 "State layer correctness" (**COMPLETE**, `~~A22~~` struck):

> `A22` **[Low]** `CompleteAccountWipeAsync` is check-then-act against a unique index …
> `AnyAsync` then `AddAsync` on unique `(UserName, DeviceId)`. Concurrent wipe acks → unhandled
> `DbUpdateException` → 500. Every other insert path guards this.

**What the fix actually did** (commit `d59a730`, *"fix(state): treat a raced wipe-block insert as
success (A22)"*). It hoisted the `LoginBlock` into an `added` local and wrapped the save in:

```csharp
catch (DbUpdateException ex) when (added is not null && DbExceptions.IsUniqueViolation(ex))
{
    // A concurrent wipe ack already inserted the (user, device) block … this is success, not a 500.
    db.Entry(added).State = EntityState.Detached;
    await db.SaveChangesAsync(ct).ConfigureAwait(false);
}
```

Its regression test injects a `SqliteException("UNIQUE constraint failed", 19, 2067)` — i.e. it targets,
and only targets, the **`LoginBlock` unique-index insert race**.

**Round 2** — `A6` **[Low]**, `State/DeviceStore.cs:165-176`:

> `CompleteAccountWipeAsync` can surface a `DbUpdateConcurrencyException` as a 500 during a
> security-critical wipe ack. The catch filter is `when (added is not null && IsUniqueViolation)`, which
> does not cover a concurrent **`Device` write** raising `DbUpdateConcurrencyException` (Device is
> token-stamped).

**Why 95% and not 100%.** Same method, same user-visible symptom the finding's title names —
*"`CompleteAccountWipeAsync` … concurrent wipe acks → 500"* — and A22's evident intent was to make the
wipe ack concurrency-safe. But A22's fix, its comment and its test are all specifically about the
`LoginBlock` **unique-violation**, and that exact race **is** closed and stays closed. A6 is a
**different concurrency source at the same method**: the optimistic-concurrency token on the `Device`
row (added by a *sibling* item-22 finding, A6/A6-era stamping), which raises
`DbUpdateConcurrencyException`, not a unique violation, so the `IsUniqueViolation` filter never catches
it.

- Read A22 as *"the wipe ack 500s under concurrency — make it robust"* → A6 proves it **still does**,
  and this is the same error.
- Read A22 narrowly as *"the LoginBlock insert race"* → that race is fixed, and A6 is a genuinely new
  adjacent bug the concurrency-token work introduced.

Both readings are defensible, which is the residual 5%. Either way the wipe ack — a security-sensitive
path — is still not concurrency-safe: the fix should also reload+re-apply on
`DbUpdateConcurrencyException`, not only swallow the unique violation.

---


Recorded so the exclusions are auditable — each was a plausible match that failed the bar:

- **C2 (probe SSRF oracle) vs round-1 `C9`** — item 9 fixed C9 (raw exception text from the probe) and
  *explicitly declared the SSRF itself acceptable* ("an admin who sets the backend URL permanently can
  already make the gateway connect anywhere"). C2 disputes that scoping for arbitrary per-request hosts;
  it is a **different concern C9 ruled out of scope**, not a failed fix of C9.
- **E1 (`X-Forwarded-Proto` trusted) vs round-1 `E3`** — item 10 fixed E3 (`X-Forwarded-For` throttle
  keying) and deliberately left `Request.Scheme`/`UsePublicScheme` "handled separately." E1 is the
  **gap that fix explicitly left**, not a regression of E3's claim.
- **F1 (MeetingResponse post-reply failure → duplicate REPLY) vs round-1 `F10`/`F30`** — item 26 fixed
  the ComposeMail/SubmitDraft post-send-failure family. It never touched `MeetingResponseHandler`'s
  send path (that handler's round-1 finding, `F34`, was only "every failure reports Status 2"). F1 is
  the **same rule in a handler the idempotency sweep did not cover**, not a re-break of F10/F30.
- **H2 (CalDAV create full enumeration) vs round-1 `H13`** — same defect and site, but `H13` sits in
  item 36, which was **PARTIAL / left OPEN**, and `H13` is **unstruck** on its item line. Round 1 never
  claimed it fixed → not a regression.
- **D1 (no SMTP MaxSize preflight), D9 (Sieve SASL PLAIN), D18 (`<script>`/`<style>` leak)** — the
  matching round-1 findings (D-nits, `D11`, `D21`) all live in **item 49**, which was **not done**.
  Never claimed fixed.
- **A9/A10 (faulted Lazy session slots not swept) vs round-1 `A2`** — item 21 fixed A2 (idle eviction
  disposing an *active* session) via the refcounted lease, and round 2 re-verified that fix holds.
  A9/A10 are a **different defect the lease design left** (never-materialized/faulted slots), not a
  re-break of A2.
- **A24 (`LastUsedUtc` torn read)** — fixed in item 21 and re-verified correct in round 2 (now
  `Interlocked`). No survivor.

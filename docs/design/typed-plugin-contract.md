# Typed plugin contract — making `ActiveSync.Contracts` self-sufficient

> **Status: APPROVED FOR IMPLEMENTATION (owner approval 2026-07-29).** Phases 1–5 of § 9 are
> green-lit, to be executed per the § 9 execution model: branch `plugin-restructure`, one commit
> per phase, Phase 3 alone pushes and gates on a green Actions run. Every § 10 decision is
> settled (including versioning — minor bumps only, decision 14); § 11 is empty.
> *History: drafted 2026-07-28; revised 2026-07-29 against the built contract surface; second
> revision same day folding in an external design review — the mail store split in § 5.4, the
> GAL photo statuses in § 5.8, the interop load-context hazard in § 5.6.1, the conformance-kit
> scope in § 7, the per-unit converter-split specification in § 7.1, and a batch of signature
> fixes.*
>
> **Implementation progress: Phases 1–4 landed on `plugin-restructure` (contract version 1.7).**
> Phase 3 is two commits per the § 9 execution model: the 3a checkpoint (exemplars + host seam +
> the completed contract surface) and the 3b completion (every remaining provider converted —
> `dav`, `jmap`, `smtp`; `sieve` needed nothing — plus the test-suite port). Recorded deviations:
> `BodyPreference.Eas16` survived until Phase 3 (§ 5.1 — the whole record is now host-side); the
> read-only revert marker reaches `CollectionDiff` as a `forceChanged` set rather than through
> the snapshot entry type, because `ActiveSync.Protocol` cannot see a Contracts type (§ 5.2);
> plus the Phase 3a/3b notes in § 5.5, § 6.3 and the Phase 3 section; Phase 4's own notes are in
> its § 9 section (the class names on the backend side of the converter split, the interop
> project's early birth, and the calendar-attachment knob decision). Phase 5 is not started.
>
> **Authority rules.** `AGENTS.md` and `docs/plugins.md` describe the contract **as it exists
> today** — they are the authority on current behaviour only, and nothing more. The moment
> implementation of this design begins, THIS document is the authority on the *target* design:
> `docs/plugins.md` in particular describes the OLD contract and is stale until its Phase 5
> rewrite — an implementer must never "correct" the new surface back toward what that page says.
> `AGENTS.md`'s *invariants* (sync model, licensing, the contract-bump procedure, the 14.1
> byte-identical rule) remain binding throughout; where this document deliberately changes
> something `AGENTS.md` states (e.g. `BodyPreference.Eas16`, the attachment FileReference
> format), the change is called out here explicitly and this document wins, with the
> `AGENTS.md` update landing in the phase that makes it true.

**Who this is for:** an agent or contributor picking this up in a fresh session with no knowledge
of the conversation that produced it. The evidence behind every claim is reproducible from the
repository as it stands.

**Read first, in this order:** `AGENTS.md` (§ *Solution layout and dependency rule*,
§ *Licensing*, § *Backend layer notes*, § *Testing expectations*), `docs/plugins.md`,
`src/ActiveSync.Contracts/IContentStore.cs`, `src/ActiveSync.Contracts/Models.cs`.

---

## 1. The problem

`ActiveSync.Contracts` is published as *the* package a backend plugin references. It is enough to
**compile** a plugin. It is not enough to **write** one.

### 1.1 Evidence

**The proof-of-concept plugin never syncs anything.** `tests/ActiveSync.TestPlugin/TestPlugin.cs`
is 68 lines, implements no `IContentStore`, and its `CreateConnectionAsync` throws
`NotSupportedException`. The claim "Contracts alone is the whole plugin surface" has only ever
been exercised against a registration stub.

**Every real backend depends on the unpublished `ActiveSync.Backends.Common`** (3,802 lines,
~3,200 of them converters):

| Backend | Pulled from `Backends.Common` |
|---|---|
| `imap` | `MailConverter` ×4, `DraftMessageBuilder` ×2, `MailKitWireLogger`, `MailTransportSecurity` ×2, `BackendSchemaFields` |
| `jmap` | `MailConverter`, `DraftMessageBuilder`, `CalendarConverter` ×3, `AirSyncBodyWriter`, `BodyText` ×2, `BackendHttpClientFactory` ×2, `RedirectingHttpSender` ×4 (its `ContactConverter` mention is comment-only — contacts go through its own `JsContactConverter`) |
| `caldav`/`carddav` | `CalendarConverter` ×6, `ContactConverter` ×5, `TasksConverter` ×3, `CalendarAttachmentPolicy` |
| `local` | `CalendarConverter` ×6, `ContactConverter` ×3, `TasksConverter` ×2, `NotesConverter` ×2 |

The transitive closure for a mailstore is roughly 2,000 lines: `MailConverter` (508) pulls
`CalendarConverter` (900), `TimeZoneBlob` (203), `BodyText` (51), `AirSyncBodyWriter` (23);
`DraftMessageBuilder` (227) pulls `MailConverter`; `CalendarConverter` pulls `RecurrenceMapper`
(174).

**The documented reference implementation cannot be built from the published surface.**
`docs/plugins.md:10` names the `jmap` provider as the reference for a multi-role backend. It uses
nine `Backends.Common` types.

### 1.2 Root cause

`IContentStore` trades in `IReadOnlyList<XElement> ApplicationData`. The *actual* contract is
therefore the EAS item schema (MS-ASEMAIL, MS-ASCAL, MS-ASCNTC, MS-ASTASK, MS-ASNOTE) — untyped,
unversioned, and expressed in no published artifact.

This has a second consequence that matters more than the first:
`ContractSurfaceApprovalTests` snapshots C# signatures and **cannot see the XML shape**. The
contract-version gate protects the part that rarely breaks and not the part that does. A change to
what a store must emit inside `ApplicationData` breaks every plugin silently, with no version bump
and no failing test.

### 1.3 The `ActiveSync.Protocol` question

The immediate trigger was a wish to stop publishing `ActiveSync.Protocol`. Measured usage:

| Protocol type | Used by backends | Used only by host |
|---|---|---|
| `EasClass` | Dav 11, Jmap 9, Local 10, Imap 3 | Server 58, Core 15 |
| `EasFolderType` | Dav 9, Jmap 9, Local 4, Imap 5 | Server 7 |
| `EasNamespaces` | Common 18, Jmap 8, Imap 3 | Server 33 |
| `EasDateTime` | Common 37, Dav 3, Jmap 2 | Server 2 |
| `WireLog` | Common 7 | Server 7, Core 3 |
| `WbxmlEncoder`/`Decoder`/`CodePages`/`Exception` | **none** | Server |
| `EasRequestParameters` | **none** | Server 4, Core 1 |
| `CollectionDiff`/`CollectionChanges`/`ItemChange` | **none** | Server |
| `EasVersion` | **none** | Server 14, WebUi 2 |

**1,662 of Protocol's 2,044 lines (81%) are unusable by any plugin.** No backend touches WBXML,
the HTTP query parser, the diff engine, or even `EasVersion` — the last by design, since
`BodyPreference.Eas16` exists so store signatures stay version-free.

Two facts make the severance cheap:

- Only one file in Contracts imports Protocol — `Models.cs`, for `EasClass.Email` /
  `EasClass.Calendar` in `ContentFilter.ForClass`. `EasFolderType` appears in a `//` comment only.
- **The built `ActiveSync.Contracts.dll` carries no assembly reference to `ActiveSync.Protocol`.**
  Verified by reading the assembly's metadata table: its references are `System.*` and
  `Microsoft.Extensions.*` only. The `const string`s are inlined by the compiler.

No Protocol type appears anywhere in Contracts' public surface — in
`tests/ActiveSync.Core.Tests/ContractSurface.approved.txt`, the `assembly ActiveSync.Contracts`
section runs lines 19–312 (1–18 is the file header) and `assembly ActiveSync.Protocol` begins at
line 313, of 477 total.

**Under this design the question dissolves entirely:** once no EAS XML crosses the boundary,
Contracts needs neither `EasNamespaces` nor `EasDateTime`, and the two constant tables become
enums. Protocol becomes wholly host-only.

---

## 2. Goals and non-goals

### Goals

1. A plugin references **exactly one package** and needs nothing else to implement a fully
   functional backend for the roles it claims.
2. Every value crossing the boundary is **strongly typed and self-describing**.
3. The contract-version gate covers the **real** contract, so a breaking change cannot ship
   silently.
4. `ActiveSync.Protocol` stops being published.
5. A plugin author implementing one role never encounters types belonging to another role, and
   never inherits a third-party dependency they do not want.

### Non-goals

- Publishing `Backends.Common`, or any converter, in any form.
- Preserving the current contract shape. Breaking changes are explicitly acceptable at this stage
  (the project is not in production).
- Changing the sync engine's full-enumeration posture (`AGENTS.md` § *Sync model*, decision H16).
- Changing the role/provider model, `ProviderSettings`, or `BackendConfigField` config schema.

---

## 3. Principles

These are the rules the design is checked against. Each is intended to be testable by review.

1. **No out-of-band knowledge.** Nothing crossing the boundary may require a document to
   interpret. A dictionary of mail keywords is data; a dictionary whose *keys* the host
   understands is a private protocol wearing a type's clothes.
2. **Carry the format, never the parser's object model.** The contract may carry RFC822 bytes; it
   may never carry `MimeMessage`. It may carry an iCalendar string; it may never carry
   `Ical.Net.Calendar`. This single rule is what keeps MailKit, Ical.Net and FolkerKinzel.VCards
   out of every plugin's dependency graph.
3. **No EAS wire encoding in the contract.** No `XElement`, no wire integers (`int EasType`), no
   wire characters (`char Kind`), no delimited composite strings.
4. **No untyped `object` bags.** A collection of `object` that the host must interrogate is not a
   contract. **This does NOT outlaw interface capability-testing** — `store is IItemMoveOperations`
   is compile-time-checked, IntelliSense-discoverable, idiomatic .NET, and is the pattern the
   codebase already uses correctly (`provider is IPerUserResourceOwner` in `BackendSessionFactory`,
   `is IFreeBusySource` in `ResolveRecipientsHandler`, `is IReadinessSource` in `ReadinessProbe`).
   It stays.
5. **Role isolation is structural.** A `Notes` plugin implements `INotesStore` and sees no mail
   type. No shared base type may mention a class-specific format.
6. **One deliberate exception: `ProviderSettings` stays opaque.** Its keys mean something to the
   *plugin*, and the host must not know their shape — that is the design, not an accident.
   `DescribeConfiguration` already renders it safely.
7. **Additive evolution by construction.** Contract models use **init-only property records**,
   never positional records. Positional records cannot gain a field without breaking every
   caller; init-only ones can. (Every model in Contracts is positional today.) **One deliberate
   exception:** the single-value key wrappers of § 5.2 (`FolderKey`, `ItemKey`, `ItemRevision`)
   are positional `readonly record struct`s — a newtype over one string is *defined* by never
   gaining a second field, so the positional-record hazard cannot apply to it.
8. **One instant type.** Every date/time crossing the boundary is a `DateTimeOffset` (UTC by
   convention on the way in). Today's surface mixes UTC-named `DateTime`s (`BusyPeriod`,
   `OofReply`, `ContentFilter`, `SearchAsync`) with nothing else; a redesign that adds
   `DateTimeOffset` fields beside them would ship a permanently inconsistent API and re-import
   the `DateTime.Kind` ambiguity. The retype standardises every retained type in the same pass
   (Phase 1).

---

## 4. The currency decision

The governing choice: what does a store hand over per item?

**Decision: standard interchange formats, with typed metadata alongside — except where no
accepted standard exists.**

| Class | Currency | Rationale |
|---|---|---|
| Mail | **RFC822 bytes** + typed flags/categories | RFC822 *is* the precise domain type. A typed mail model would mean re-modelling MIME inside the contract. The host already owns MimeKit. |
| Calendar | **iCalendar `VEVENT`** (string) | Universally supported; CalDAV backends have it natively; the `jmap` provider already converts JSCalendar → iCalendar today, so it simply stops one step earlier. |
| Tasks | **iCalendar `VTODO`** (string) | Same, and CalDAV task collections are VTODO natively. |
| Contacts | **vCard** (string) | Same, CardDAV-native. |
| Notes | **Typed `NoteItem` record** | There is **no accepted notes standard.** The repo maps notes to `VJOURNAL`, which is a local convention, not interoperability. Notes is also the simplest possible plugin — subject, body, categories — and forcing VJOURNAL ceremony on it works directly against the goal of making simple plugins easy. |

### 4.1 Why this beats full typed models

- **Contracts stays dependency-free.** Payload records of `string`/`ReadOnlyMemory<byte>` plus
  enums need no domain library. A notes plugin never sees MailKit.
- **It solves lossy round-trip for free.** Today's converters merge into the *stored*
  `existingIcs`/`existingVcard` precisely so properties EAS cannot express survive an edit. A full
  typed model would drop every unmodelled property on every edit unless an escape hatch were
  added. Carrying the payload preserves them inherently — the plugin stores what it is given.
- **It shrinks the permanently-MIT surface.** Payload records and enums are trivial and carry no
  commercial value; the 3,200 lines of conversion logic stay host-side and PolyForm. This design
  *improves* the licensing posture rather than eroding it (see § 8).
- **It removes the ghosting problem from the contract entirely** (see § 6).

### 4.2 The honest cost

A plugin whose backend does not natively speak the format must emit it. Emitting iCalendar is a
documented standard with many libraries to choose from — or none. Emitting EAS `ApplicationData`
XML, which is what the contract requires **today**, has no public spec-to-code path at all. Strict
improvement in every case.

The residual ergonomic cost is real: `ReadOnlyMemory<byte>` of RFC822 is awkward to read in a
debugger and awkward to construct in a test. That is addressed by the optional interop package in
§ 5.6 — **without** putting third-party types in the contract, which § 5.6 explains would be
actively harmful.

A second cost needs an explicit rule: a plugin can hand over a **malformed payload**. The host must
parse defensively and degrade rather than throw. Proposal: an unparseable payload behaves exactly
as a fetch failure — `null` for that item, the snapshot is not advanced, the item is retried next
round. `IContentStore.GetItemsAsync` already specifies that behaviour for failed fetches, so this
is an extension of an existing rule rather than a new one.

The same rule covers an **oversized payload**. A buggy or hostile plugin can return a 2 GB "ICS
string" or a pathologically nested MIME tree that the host now feeds to MimeKit/Ical.Net (the
exposure is not new — plugins already hand over unbounded `XElement` lists — but the defensive
rule is the right place to make the bound explicit). The host applies a hard size cap before
parsing; over the cap is treated exactly as unparseable. The codebase already has the pattern:
`WireLog.Payload`'s 16 KB dump cap.

Third, **buffer ownership must be pinned**, because the host caches payloads (§ 6.3). A
`ReadOnlyMemory<byte>` says nothing about whether the underlying buffer is pooled or reused; a
store returning memory over a rented buffer would let the host's payload cache silently alias
bytes that mutate underneath it. Contract rule: memory handed across the boundary (in either
direction) must remain **valid and unchanged indefinitely** — in practice, a dedicated array. A
store that wants pooling copies before returning.

---

## 5. Contract surface sketch

**Implementation stance:** once this document is approved, these shapes are the reference —
implement them as written. They are sketches only in the sense that XML docs, parameter-name
polish and trivial member ordering are the implementer's to finish; the *decisions* they encode
(which types exist, which members they have, what is nullable, what is deleted) are settled in
§ 10 and are not up for silent revision. Where the code proves a sketch wrong, deviate — and
record the deviation here in the same change, so the document stays the authority it claims to be.

### 5.1 Primitives and enums

Replacing wire-typed primitives (`AGENTS.md` § *Backend layer notes* pins the wire values):

```csharp
// NOTE: no ContentClass enum in Contracts. Earlier sketches had one, but every consumer fell
// away under review: BackendFolder dropped its Class member (§ 5.3), the store base derives
// its class from the alias interface a store implements (§ 5.4), and ContentFilter's
// class-dispatching helper moves host-side with the FilterType maps (Phase 1). A host-side
// enum by the same name is fine; publishing a contract enum nothing in the contract uses
// would be permanent dead ABI.

public enum FolderType   // MS-ASCMD FolderSync Type; wire values pinned explicitly
{
    UserGeneric = 1, Inbox = 2, Drafts = 3, DeletedItems = 4, SentItems = 5,
    Outbox = 6, Tasks = 7, Calendar = 8, Contacts = 9, Notes = 10, Journal = 11,
    UserMail = 12, UserCalendar = 13, UserContacts = 14, UserTasks = 15,
    UserJournal = 16, UserNotes = 17
}

public enum BodyType { PlainText = 1, Html = 2, Rtf = 3, Mime = 4 }

// Replaces BusyPeriod's char '1'/'2'/'3'. Values deliberately UNPINNED and the enum order
// deliberately arbitrary: the MergedFreeBusy digit mapping ('0' free … '3' OOF, '4' no data)
// is HOST-side, applied where the digit string is built — the wire never sees this enum.
// Free is included for completeness but never appears as a busy *period*; "no data" is the
// null return of IFreeBusySource, not an enum member. Do not "helpfully" pin or reorder
// these to the digits — that would re-import the wire encoding principle 3 removes.
public enum BusyKind { Free, Tentative, Busy, OutOfOffice }
```

> **Implementation deviation, recorded in Phase 1 (per this section's own rule).** Phase 1's
> line item "drop `Eas16` from `BodyPreference`" was NOT carried out; the flag stays until
> Phase 3 deletes the whole record. Reason, found in the code: while stores still perform the
> EAS conversion (they do, until Phase 3/4 move it host-side), the store is handed nothing else
> per request from which 16.x-ness could be derived — `CalendarConverter.ToApplicationData` is
> the sole consumer and gates `airsyncbase:Location` and inline event attachments on it. Dropping
> the flag in Phase 1 would therefore have silently regressed shipped 16.1 behaviour for two
> phases and reddened Phase 2's own integration gate (`Eas16Tests` asserts both shapes), which
> contradicts § 9's "each phase leaves the tree green" and makes 3a the plan's only red commit.
> Everything else in the Phase 1 line landed as written, `BodyPreference.Type` included (now
> `BodyType`). Consequence for the authority rules: `AGENTS.md`'s `BodyPreference.Eas16`
> statement stays TRUE until Phase 3, and it is Phase 3 that must update it.

`BodyPreference` **leaves the contract entirely** (an earlier draft merely dropped its `Eas16`
flag). It is an AirSyncBase notion: under the payload currency no store can act on it — a
calendar/contact/task/notes store returns the full payload and the host truncates during EAS
conversion, and mail is always-full by decision 3. Keeping it in every store signature would
make every plugin author ask what to do with `TruncationSize` (answer: nothing). What remains
contract-side is a mail-only extension point:

```csharp
/// Fetch options for the mail store. EMPTY today by design (decision 3: fetches are
/// always-full, exactly today's behaviour). Exists so a future truncation hint
/// (e.g. MaxBodyBytes for IMAP BODYSTRUCTURE / JMAP bodyValues stores) can be added
/// additively — principle 7 — without touching a signature.
public sealed record MailFetchOptions
{
    public static readonly MailFetchOptions Full = new();
}
```

### 5.2 Keys and references

Backend keys carry a prefix convention today (`imap:`, `caldav:`, `caldav-tasks:`, `carddav:`,
`local:`) with dispatch via `OwnsBackendKey`. Composite references are delimited strings:
`UrlEncode("{imapBackendKey}|{uid}|{attachmentIndex}")`, `"calatt::<serverId>::<index>"`,
`SharedCollection`'s `"href|ro"`.

```csharp
public readonly record struct FolderKey(string Value);
public readonly record struct ItemKey(string Value);
public readonly record struct ItemRevision(string Value);   // opaque to the host, by design
```

These are the principle-7 exception: positional single-value newtypes. One hole to acknowledge:
`default(FolderKey)` exists with a null `Value` despite the non-nullable annotation (structs
always have a default). Convention over ceremony — the host never manufactures a default key,
and the conformance kit asserts a store never returns one; no throwing constructor guard.

An earlier draft also defined an `AttachmentReference` record here. It is **gone**: § 5.8
deletes `GetAttachmentAsync` from the mail side-operations (the host extracts attachments from
the raw message itself), and the calendar attachment fetch takes plain
`(FolderKey, ItemKey, int index)` parameters — so no composite reference type crosses the
boundary at all. The wire-facing composites (`FileReference`, Search `LongId`) become entirely
host-internal encodings.

Encoding these onto the wire becomes host-owned, and two existing Contracts types follow from that:

- **`DelimitedKey` (`Encode(string[])` / `Decode(string, int)`) moves OUT of Contracts** into the
  host. Its whole purpose is packing composite keys into delimited strings; once no delimited key
  crosses the boundary, a plugin has no use for it and its presence would invite one.
- **`SharedCollection` loses `Parse(string)` and `Validate(string, string)`.** The record itself is
  already properly typed (`Href`, `ReadOnly`) — the problem is the `"href|ro"` string format parsed
  *inside the contract*. Keep the record; move parsing and validation host-side, where the config
  string is read.

**Note on `ItemRevision`:** it is legitimately opaque — the diff engine only compares it. But the
host currently pokes the sentinel `"!ro"` into the revision *value space* for read-only silent
revert (`AGENTS.md` § *Sync model*), which is exactly the kind of hidden coupling this design
removes. Read-only poisoning should move to a host-side field beside the revision, never inside it.

> **Implementation deviation, recorded in Phase 2 (per § 5's own rule).** The snapshot entry IS
> `(ItemRevision Revision, bool PendingReadOnlyRevert)` as specified — `ActiveSync.Core.State
> .SnapshotEntry`, a `readonly record struct` — but **`CollectionDiff` does not see that type**.
> Reason, found in the code: the diff lives in `ActiveSync.Protocol`, which references no other
> project (Protocol and Contracts are two INDEPENDENT roots; `DependencyRuleTests
> .CollectionDiff_MovedFromCoreToProtocol` pins the location and its "nothing but BCL types"
> rationale), so it cannot name `ItemRevision`. Giving Protocol a Contracts reference, or minting a
> second entry type inside Protocol, both cost more than the seam is worth. Instead
> `CollectionDiff.Compute` gained an optional **`IReadOnlySet<string>? forceChanged`** — ids to
> report as Changes even when the revision matches — and `CollectionSnapshot.Diff` (Core) is the
> single place the two shapes meet: it projects the typed snapshot to id → revision plus the
> pending set, and re-marries the flags with the diff's result (the marker clears only for items
> actually charged to the window). The property this phase was for is intact and is now stronger
> than the design's own sketch: no sentinel exists in the revision value space at all, not even
> transiently, and the diff states the host's intent as its own parameter instead of inferring it
> from a magic value. Consequence for Phase 3: when `GetItemRevisionsAsync` starts returning
> `IReadOnlyDictionary<ItemKey, ItemRevision>`, that projection is where the unwrapping belongs —
> `CollectionDiff` stays BCL-only.
>
> Two smaller decisions the code forced, both recorded here rather than left implicit:
>
> - **The forced resync is made real, not left to chance.** The persisted snapshot is a VERSIONED
>   gzipped document (`v`, the flat `items` map — the same bulk as before — and a `pendingReverts`
>   sidecar written only when non-empty, so the typed entry costs nothing per item on disk). A blob
>   in any other shape reads as `null`, and the caller answers Sync **Status 3** (GetItemEstimate
>   Status 4), so the device restarts that collection from SyncKey 0. Deserializing an old blob
>   into the new shape would otherwise have yielded an EMPTY snapshot — silently re-Adding every
>   item the device already holds, which is a worse outcome than the announced resync.
> - **`DelimitedKey` landed in `ActiveSync.Protocol`, not Core.** § 5.2 says only "out of Contracts
>   into the host". Core is not reachable: `Backends.Common`'s `DraftMessageBuilder` decodes
>   FileReferences and `Backends.Common` must not reference Core (test-enforced). Protocol is the
>   assembly it already references explicitly, and FileReference/LongId are EAS wire values, so the
>   encoder sits with the other wire encodings. Likewise `SharedCollection.Parse`/`Validate` became
>   `ActiveSync.Backends.Dav.SharedCollectionEntry` — the "href|ro" string is a config syntax, and
>   the caldav provider is the only thing that reads it.

### 5.3 Folders

```csharp
public sealed record BackendFolder
{
    public required FolderKey Key { get; init; }
    public required string DisplayName { get; init; }
    public FolderKey? ParentKey { get; init; }
    public required FolderType Type { get; init; }
    // NOTE: no ContentClass member. A store serves exactly one class (see § 5.4), so every
    // folder it lists is that class — a per-folder Class field could only agree with the
    // owning store or be a bug. The host tags folders with the store's class itself.
}
```

### 5.4 Stores

Split so role isolation is structural (principle 5). Class-agnostic members on a non-generic base;
payload members on a generic one for the four payload classes — and a **hand-written mail store**,
because mail's write model does not fit the generic shape (see below, and § 6.2).

```csharp
public interface IContentStore
{
    bool OwnsKey(FolderKey key);
    Task<IReadOnlyList<BackendFolder>> ListFoldersAsync(CancellationToken ct);
    Task<IReadOnlyDictionary<ItemKey, ItemRevision>> GetItemRevisionsAsync(
        FolderKey folder, ContentFilter filter, CancellationToken ct);
    Task DeleteItemAsync(FolderKey folder, ItemKey item, bool permanent, CancellationToken ct);
    Task<IReadOnlyList<FolderKey>> WaitForChangesAsync(
        IReadOnlyList<FolderKey> folders, TimeSpan timeout, CancellationToken ct);
}

public interface IContentStore<TItem> : IContentStore where TItem : class
{
    Task<TItem?> GetItemAsync(FolderKey folder, ItemKey item, CancellationToken ct);
    Task<IReadOnlyDictionary<ItemKey, TItem?>> GetItemsAsync(
        FolderKey folder, IReadOnlyList<ItemKey> items, CancellationToken ct);
    Task<(ItemKey Key, ItemRevision Revision)> CreateItemAsync(
        FolderKey folder, TItem item, CancellationToken ct);
    Task<ItemRevision> UpdateItemAsync(
        FolderKey folder, ItemKey item, TItem value, ItemRevision? expected, CancellationToken ct);
}

public interface ICalendarStore : IContentStore<CalendarItem> { }
public interface ITaskStore     : IContentStore<TaskItem> { }
public interface IContactStore  : IContentStore<ContactItem> { }
public interface INotesStore    : IContentStore<NoteItem> { }

// Mail is deliberately NOT IContentStore<MailItem>. Its everyday write is a flags/categories
// PATCH (the RFC822 is never rewritten), and its only content write — an EAS 16.x draft
// rewrite — can CHANGE THE ITEM KEY (IMAP: delete + append). A generic UpdateItemAsync taking
// a full MailItem could express neither: it conflates "mark read" with "rewrite the message",
// forces the host to materialise RFC822 bytes to set a flag, and cannot report a moved key.
public interface IMailStore : IContentStore
{
    Task<MailItem?> GetItemAsync(
        FolderKey folder, ItemKey item, MailFetchOptions options, CancellationToken ct);
    Task<IReadOnlyDictionary<ItemKey, MailItem?>> GetItemsAsync(
        FolderKey folder, IReadOnlyList<ItemKey> items, MailFetchOptions options, CancellationToken ct);

    /// EAS 16.x drafts — the ONLY mail create a client can Sync; anywhere but Drafts is refused.
    Task<(ItemKey Key, ItemRevision Revision)> CreateDraftAsync(
        FolderKey folder, MailItem item, CancellationToken ct);

    /// The everyday mail Change: flags and categories, presence-carried (see MailFlagsPatch).
    Task<ItemRevision> UpdateFlagsAsync(
        FolderKey folder, ItemKey item, MailFlagsPatch patch, ItemRevision? expected, CancellationToken ct);

    /// EAS 16.x draft content rewrite (Drafts folder only). The returned key MAY differ from
    /// the input key — IMAP implements this as delete + append, so the UID moves. CAUTION for
    /// the host implementation: the returned key is informational (logging, the response to
    /// this command) and MUST NOT be echo-suppressed into the snapshot — the client still
    /// holds the OLD ServerId, and the next diff's Delete+Add against the unpatched snapshot
    /// IS the re-identification that teaches it the new one (today's flow, AGENTS.md § EAS
    /// 16.1: "a rewrite changes the IMAP UID and the snapshot diff re-identifies as
    /// Delete+Add"). Patching the snapshot under the new key would suppress exactly that.
    Task<(ItemKey Key, ItemRevision Revision)> ReplaceDraftAsync(
        FolderKey folder, ItemKey item, MailItem value, CancellationToken ct);
}
```

Notes on shape, each of which closes a hole a review found:

- **A store's content class is derived, not declared.** An earlier sketch had
  `ContentClass Class { get; }` on the base — redundant with the alias interfaces and able to
  contradict them (a store declaring `Calendar` while implementing `IMailStore`). Instead: a
  store implements **exactly one** of the five alias interfaces, the host derives the class from
  which one, and connection creation rejects a store implementing more than one (or none). The
  `where TItem : class` constraint is what makes `TItem?` in `GetItemsAsync` a real nullable
  reference — without it "null = not fetched" is not expressible in the type system.
- **Tuples vs. records, the rule:** a tuple is acceptable where the element *types* disambiguate
  the elements (`(ItemKey, ItemRevision)` cannot be confused), a record is required where they do
  not (`SearchHit` replaces `(string, string)` in § 5.8 precisely because two strings carry no
  meaning). That is why `CreateItemAsync` keeps its tuple and `SearchAsync` gets a record.
- The `GetItemsAsync` default-implementation contract (batch fetch, `null` = "not fetched, do not
  advance the snapshot") is preserved verbatim — it is load-bearing and well documented today.
- The `expected` precondition's failure semantics are defined in § 6.3 (mitigation 2), including
  the store that cannot check it.

### 5.5 Items

```csharp
public sealed record MailItem
{
    // Ownership rule (§ 4.2): must be a dedicated, never-mutated buffer — the host caches it.
    public required ReadOnlyMemory<byte> Rfc822 { get; init; }
    public required MailFlags Flags { get; init; }
    public IReadOnlyList<string> Categories { get; init; } = [];   // legitimate: opaque to host
    public DateTimeOffset? Received { get; init; }
}

public sealed record MailFlags
{
    public bool Seen { get; init; }
    public bool Flagged { get; init; }
    public bool Answered { get; init; }
    // $Forwarded. NOT optional: ImapMailBackend reads it into the converter today
    // (ImapMailBackend.cs:164 — it feeds EAS LastVerbExecuted) and writes it in
    // SetAnsweredAsync; dropping it would lose data the host consumes.
    public bool Forwarded { get; init; }
    public bool Draft { get; init; }
}

// The everyday mail Change (IMailStore.UpdateFlagsAsync). Presence is carried ON THE VALUE
// (§ 6.2): an unset Optional means "client did not send this", never "clear it". This is the
// typed equivalent of today's presence-guarded XElement handling in ImapMailBackend
// (Read / Flag / Categories each applied only when the element is present).
public readonly struct Optional<T>
{
    public bool HasValue { get; }
    public T Value { get; }                       // throws when !HasValue
    public static implicit operator Optional<T>(T value) => ...;
}

public sealed record MailFlagsPatch
{
    public Optional<bool> Read { get; init; }
    public Optional<bool> Flagged { get; init; }
    public Optional<IReadOnlyList<string>> Categories { get; init; }
}

public sealed record CalendarItem { public required string ICalendar { get; init; } }   // VEVENT
public sealed record TaskItem     { public required string ICalendar { get; init; } }   // VTODO
public sealed record ContactItem  { public required string VCard { get; init; } }

public sealed record NoteItem
{
    public required string Subject { get; init; }
    public required TextBody Body { get; init; }
    public IReadOnlyList<string> Categories { get; init; } = [];
    public DateTimeOffset? LastModified { get; init; }
}

public sealed record TextBody
{
    public required BodyType Type { get; init; }
    public required string Content { get; init; }
    // NOTE: no Truncated flag. A store always hands over the FULL body; truncation is an EAS
    // presentation concern applied host-side per the client's BodyPreference. A truncation
    // marker at this seam could only mean the store lost data it should not have.
}
```

`CalendarItem`/`TaskItem`/`ContactItem` are single-property records rather than bare strings on
purpose: they are the extension point if a class later needs metadata beside the payload (as mail
already does), and they make the generic store aliases type-distinct.

> **Implementation deviation, recorded in Phase 3a (per § 5's own rule).** `Optional<T>` gained a
> named factory, `public static Optional<T> Of(T value)`, beside the sketched implicit operator.
> Reason, found by the compiler: C# never applies a user-defined conversion whose operand type is
> an interface, so for `Optional<IReadOnlyList<string>>` — the `MailFlagsPatch.Categories` shape
> this section itself specifies — the implicit operator can never fire and there was no way to
> mark the field as sent. The operator stays for the concrete-typed fields (`Optional<bool>`);
> `Of` is the interface-typed escape hatch.

Two fidelity notes, stated so nobody "fixes" them later: EAS's mail `Flag` is really a follow-up
flag with `Status` 0/1/2, a type and dates — `bool Flagged` matches today's deliberately lossy
mapping (`Status == 2` → flagged, anything else → clear) and stays that way; richer follow-up
data would be an additive `MailFlagsPatch`/`MailFlags` extension later. And the instant
properties are `DateTimeOffset` per principle 8 — the `Utc` name suffix goes with the retype
(`Received`, `LastModified`), since an offset-carrying type makes the suffix a lie.

### 5.6 Interop helper packages (ergonomics without ABI coupling)

The payload currency is deliberately unopinionated about *how* a plugin produces its bytes. That is
correct for the contract and unpleasant for the author. The fix is **one small, optional** package
carrying nothing but extension methods:

| Package | References | Provides |
|---|---|---|
| `ActiveSync.Contracts.Interop` | MimeKit, Ical.Net, FolkerKinzel.VCards | `MailItem` ⇄ `MimeMessage`; `CalendarItem`/`TaskItem` ⇄ `Ical.Net.Calendar`; `ContactItem` ⇄ `VCard` |

```csharp
// ActiveSync.Contracts.Interop — NOT referenced by ActiveSync.Contracts
public static class MailItemMimeExtensions
{
    public static MimeMessage ToMimeMessage(this MailItem item);
    public static MailItem ToMailItem(this MimeMessage message, MailFlags flags,
                                      DateTimeOffset? receivedUtc = null);
}
```

Roughly 100 lines of `Load`/`WriteTo` in total, plus test builders — which is where the "payloads
are hard to test" objection is actually answered.

**One package rather than one per format — a deliberate trade.** Split packages would let a
contacts plugin take only the vCard helpers, whereas a single package means opting into ergonomics
pulls all three libraries. That cost is bounded and acceptable because the package is **optional**
and the contract itself still has no domain dependencies: the Notes plugin that motivated role
isolation (principle 5) references *nothing*, since `NoteItem` is already typed. What is lost is
granularity *within* the opt-in; what is gained is one artifact to version, publish, document and
test — and one version line instead of three that would drift apart. The host's own conversion
layer (§ 7) consumes the same helpers, so they are exercised by the in-repo suite rather than
shipped untested.

**Why not put the third-party types in the contract signature instead?** Three reasons, in
increasing order of severity:

1. **It breaks role isolation** (principle 5). A signature-level `MimeMessage` means the Notes
   plugin inherits MimeKit. This is the constraint that motivated the whole currency decision.
2. **It couples the contract ABI to three third-party release cadences.** The loader demands an
   exact contract `major.minor` match, so an Ical.Net major bump would force a contract bump that
   refuses every existing plugin for a reason unrelated to the contract. Not hypothetical:
   `AGENTS.md` records that Ical.Net 5.x already broke its surface (`CalDateTime` replacing
   `IDateTime`, `ExceptionDates.GetAllDates()`, `Duration.FromMinutes()`).
3. **It destroys per-plugin dependency isolation** — see § 5.6.1, which is the decisive argument.

Because these helpers are not loader ABI, they sit **outside** the contract-surface approval gate.
That is what preserves the decoupling that matters: a MimeKit or Ical.Net major bump changes the
interop package's dependencies, **the contract does not move, and no plugin is refused by the
loader** — which is the failure mode § 5.6.1 exists to prevent. Nothing about the interop package's
own version number affects that.

**Publishing rule: tagged releases only — never `main`, never a branch, never a PR.** This needs
**no workflow change**; the existing gate already covers it and will cover the new packages
automatically:

- `build.yaml`'s *Derive version from tag* step sets `version` only when `github.ref_name` matches
  `^v?[0-9]+\.[0-9]+\.[0-9]+([-.].*)?$`. A branch, `main`, a PR ref, the nightly schedule and a
  `workflow_dispatch` on a branch all yield an **empty** string.
- The *Pack and publish NuGet packages* step is gated `if: needs.test.outputs.version != ''`, so on
  every non-tag trigger it does not run at all — no pack, no push to GitHub Packages, no push to
  nuget.org.
- That step packs the whole solution (`dotnet pack ActiveSync.slnx`, i.e. every project with
  `IsPackable=true`) and pushes by glob (`dist/nupkg/*.nupkg`), so adding the interop project
  requires nothing beyond setting `IsPackable` on it.

**Versioning: the interop package takes the RELEASE TAG, unlike Contracts and Protocol.** It is
therefore the *simple* case — it sets no version properties at all and inherits the global
`-p:Version=<tag>` CI passes. Contracts and Protocol are the ones that opt **out**, by assigning
`PackageVersion` explicitly so the global property cannot reach them; as their csproj comment
records, "the SDK only falls back to `$(Version)` for a version property the project leaves empty."
So the interop csproj simply omits that block.

Three reasons this is right despite the package being conceptually independent:

- **There is no better version source.** The package tracks nothing of its own; inventing an
  `$(InteropVersion)` would mean hand-maintaining a number with no natural trigger to bump it.
- **It documents the host's own dependency pins.** Because package versions are centralised in
  `Directory.Packages.props`, interop `1.5.0` carries exactly the MimeKit / Ical.Net /
  FolkerKinzel.VCards versions the gateway `1.5.0` assemblies were built against. A plugin author
  referencing the matching interop version is automatically aligned with the host — visible in the
  nuspec, without reading the repository.
- **Staying in lockstep is easier to explain** than a second version line users must map to
  releases.

The accepted cost: every tagged release republishes the interop package even when nothing in it
changed, so `--skip-duplicate` (already on both `dotnet nuget push` invocations) stops being the
mechanism that suppresses redundant pushes. That is harmless — the version genuinely did change,
and it now means something.

Note the resulting dependency shape is deliberate, not a mistake: interop `1.5.0` will declare a
dependency on `ActiveSync.Contracts` at `$(ContractVersion)` — e.g. `2.3.0` — because that is the
version Contracts pins for itself. The two numbers differing is the point (§ 8).

**That dependency must be an EXACT pin — `[2.3.0]`, not NuGet's default floor.** Contract
*minor* is breaking by policy, so "any Contracts ≥ 2.3.0" is a false promise: a plugin could
resolve interop 1.5.0 beside Contracts 2.4.0, whose surface legitimately changed, and fail at
runtime *inside the helper* with a `MissingMethodException` the loader's gate never saw. An
exact range makes the mismatch a restore-time error instead. The compatibility story for
authors is then one sentence: *pick the interop release whose nuspec pins your contract
version* — and since interop tracks the release tag, that is simply "the release that shipped
your contract".

Two notes on shape: `CalendarItem` cannot map to `Ical.Net.CalendarEvent` — it has to be the
`Calendar` container, or VTIMEZONE and the surrounding properties MS-ASTZ needs are lost (which is
exactly what the converters do today: `Calendar.Load(ics)`, then `calendar?.Events`). And the
helpers are the natural home for **test builders**, which is where the "hard to test" objection is
actually answered.

#### 5.6.1 This is what makes plugin dependency isolation work

`PluginLoader` already gives each plugin its own `AssemblyLoadContext` with its own folder, and
`PluginLoadContext.IsHostOwned` (`PluginLoader.cs:456`) shares **only**:

```
ActiveSync.*   |   System.*   |   Microsoft.Extensions.*   |   System, mscorlib, netstandard
```

Everything else — MailKit, MimeKit, Ical.Net, FolkerKinzel.VCards — resolves from the plugin's own
folder. So a host on MailKit 1.0 and a plugin shipping MailKit 1.1 coexist today, as do three
plugins with mutually conflicting dependency versions. The `Load` override even carries the scar
tissue: host-first was once applied to everything and *"silently downgraded a plugin's private
dependency to whatever version the host happened to have loaded"* (`PluginLoader.cs:412-415`).

**That isolation holds only while third-party types do not cross the boundary.** If `MimeMessage`
appeared in a contract signature, MimeKit would have to be added to `IsHostOwned` — otherwise host
and plugin would hold two distinct `MimeMessage` types and the seam would fail with a cast or type
-load error. Adding it pins every plugin to the host's exact MailKit version, permanently.

The proof that this cost is real is already in the list: `Microsoft.Extensions.*` **is** host-owned,
precisely because `IServiceCollection`/`IConfiguration` cross the boundary in
`IGatewayPlugin.Register`. That is the one place plugins are version-pinned today, and it is
unavoidable given the signature. Every third-party type added to the contract buys another one.

**The interop package's own NAME is a trap here, and it must be defused explicitly.**
`IsHostOwned` is *prefix*-based — `name.StartsWith("ActiveSync.", ...)`
(`PluginLoader.cs:456-463`) — and the host ships its own copy of
`ActiveSync.Contracts.Interop` (§ 7: the host conversion layer consumes it). So under the
current rule — and equally under a naive "narrow the prefix to `ActiveSync.Contracts`", which
`ActiveSync.Contracts.Interop` *still* prefix-matches — a plugin's interop assembly resolves
**host-first**. That is exactly the "silently downgraded a plugin's private dependency" failure
this section quotes, with a sharper edge: the host's interop copy binds the HOST's
MimeKit/Ical.Net in the default context, while the plugin's own code binds its private copies in
its plugin context. Passing a plugin-context `MimeMessage` into a host-context extension method
is a type-identity failure at JIT/cast time. Two rules follow:

1. `IsHostOwned` narrows to an **exact simple-name match** on `ActiveSync.Contracts` — never a
   prefix. (`ActiveSync.Contracts.Conformance` has the same name shape; it runs in test
   projects, not plugin folders, but the exact-match rule covers it for free.)
2. The interop package is **plugin-local by requirement**: a plugin using it MUST ship it in its
   folder, beside its own MimeKit/Ical.Net/FolkerKinzel copies. `docs/plugins.md`'s current rule
   ("do not ship copies of `ActiveSync.*`") inverts for this one assembly and must say so
   (Phase 5).

Two follow-ups this opens, neither blocking:

- **Narrow `IsHostOwned` in Phase 5** — to the exact name per the rule above. `ActiveSync.*`
  currently shares *every* gateway assembly with plugins, including Core and Crypto.
  `ActiveSync.Contracts` itself **must** stay shared — a plugin loading its own copy would make
  its `IBackendProvider` a type the registry cannot see. The doc comment on the class already
  describes a narrower set than the code implements.
- **Plugins should `dotnet publish` with their dependencies.** `Load` falls back to the default
  context when the plugin ships nothing, so a plugin built against MailKit 1.1 that ships no copy
  silently binds to the host's 1.0 and can fail at runtime with a missing member.

### 5.7 Capabilities — mostly leave them alone

An earlier draft of this document claimed `BackendConnection`'s `IReadOnlyList<object>` was a
capability bag discovered by runtime type-testing, and proposed replacing it with a
`BackendCapabilities` record. **That was wrong on both counts**, and the correction matters because
it would have meant rebuilding something that is not broken.

**What `IReadOnlyList<object>` actually is.** It is the `ownedResources` constructor parameter of
`BackendConnection` (`BackendProviders.cs:130-134`) — a **disposal list**. Its own doc comment
explains the `object`: it holds things like `WebDavClient` / `JmapClient`, "which no single
disposable interface covers", because the set spans `IDisposable` and `IAsyncDisposable`.
`IBackendConnection` exposes only three typed properties: `Stores`, `MailSubmit`, `Oof`.

**How capabilities are really discovered.** By `is`-testing the provider or the store:
`provider is IPerUserResourceOwner` (`BackendSessionFactory.cs:332,401`),
`is IReadOnlyCollectionSource` (`CompositeBackendSession.cs:134`), `is IFreeBusySource`
(`ResolveRecipientsHandler.cs:155`), `is IReadinessSource` (`ReadinessProbe.cs:51`),
`is IWatcherDiagnostics` (`StateEndpoints.cs:28`). That is typed, compile-time-checked and
idiomatic — see principle 4. **It stays as it is.**

A single `BackendCapabilities` record could not have worked regardless: the capabilities live on
**two different objects**. Store-level — `IItemMoveOperations`, `IFolderOperations`,
`IFreeBusySource`, `ICalendarAttachmentSource`, `IReadOnlyCollectionSource`. Provider-level —
`ICredentialVerifier`, `IPerUserResourceOwner`, `IReadinessSource`, `IWatcherDiagnostics`. One
record cannot hold both.

So the only genuine fix here is the disposal list, which should stop being `object`:

```csharp
// BackendConnection ctor — replaces IReadOnlyList<object>? ownedResources.
// A CLASS, not a record: it has no value semantics (a record with no printable members would
// make all instances Equals-equal and ToString-empty — both wrong for a resource handle).
public sealed class OwnedResource
{
    // Two NAMED factories, not an Of() overload pair: a type implementing BOTH IDisposable and
    // IAsyncDisposable (common for connection types) would make Of(x) an ambiguous call.
    // Prefer OfAsync for such a type — matching BackendConnection's existing dispose switch,
    // which tries IAsyncDisposable first.
    public static OwnedResource OfAsync(IAsyncDisposable resource);
    public static OwnedResource OfSync(IDisposable resource);
    internal ValueTask DisposeAsync();   // same assembly as BackendConnection, which calls it
}
```

`IWatcherDiagnostics` / `WatcherInfo`, `BackendConnectionContext` and `ResolvedRole` are reviewed
and **unchanged** by this design; they are listed here so their absence elsewhere is not read as an
oversight.

### 5.8 Side operations — the other half of the surface

`IContentStore` is not the whole plugin contract. The sibling side-operation interfaces AND the
optional store capabilities carry the *same* untyped problems and must be converted in the same
phase, or the exercise is half-done. An earlier draft enumerated "five sibling interfaces" and
promptly missed several — so the Phase 3 checklist is **mechanical, not enumerated**: *every
`string` parameter or return anywhere in Contracts that names a folder, an item, or a revision
becomes `FolderKey`/`ItemKey`/`ItemRevision`; every untyped tuple whose elements share a type
becomes a record* (§ 5.4's tuple rule). That sweep catches what a list forgets:
`IItemMoveOperations.MoveItemAsync` (string keys and an untyped `(string, string)` return →
`(ItemKey, ItemRevision)`), `IFolderOperations` (string keys throughout; `CreateFolderAsync`
returns `FolderKey`), `IReadOnlyCollectionSource.IsReadOnlyCollection(FolderKey)`,
`IFreeBusySource` (`BusyKind`, `DateTimeOffset` per principle 8), and
`ICalendarAttachmentSource.GetEventAttachmentAsync(FolderKey, ItemKey, int index, ct)` — where
`index` is normatively **the Nth `ATTACH` property of the event in the payload the store itself
handed over**, so a plugin can always resolve it from its own data (principle 1).

**Naming, resolved** (was open question 2): the side-operation interfaces are renamed —
`IMailStoreOperations` → **`IMailboxOperations`**, `ICalendarOperations` →
**`IMeetingOperations`**, `IContactOperations` → **`IDirectoryOperations`** — rather than
bending the store names, since `IMailStore` vs `IMailStoreOperations` was a genuine collision
and the store aliases are what every plugin author meets first. `IMailSubmitOperations` and
`IOofBackend` collide with nothing and keep their names.

**`IDirectoryOperations.SearchGalAsync` is a second `XElement` seam** — today it returns
`IReadOnlyList<IReadOnlyList<XElement>>`, i.e. raw EAS ApplicationData, which nothing outside this
document's § 4 decision would let a plugin produce. GAL entries are **not** contacts: they are
`gal:`-namespace shaped, and ResolveRecipients re-projects them into its own RR namespace
(`AGENTS.md` § *GAL photos*). So they need their own record rather than reuse of `ContactItem`:

```csharp
public sealed record GalEntry
{
    public required string DisplayName { get; init; }
    public string? EmailAddress { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? Company { get; init; }
    public string? Title { get; init; }
    public string? Office { get; init; }
    public string? Phone { get; init; }
    public string? MobilePhone { get; init; }
    public string? HomePhone { get; init; }
    public string? Alias { get; init; }
    public GalPictureResult? Picture { get; init; }   // null when photos were not requested
}

// Photo limits are enforced by the STORE (it received GalPhotoRequest and can stop fetching
// photo data once MaxCount is spent / skip one over MaxSizeBytes — today's bandwidth
// behaviour, "the stores count granted photos across the whole result set"). The HOST maps
// the result to the MS-ASCMD wire statuses: Available → 1+Data, None → 173,
// OverSizeLimit → 174, OverCountLimit → 175. An earlier sketch had the host enforce the
// limits but gave it only `GalPicture?` — a null that cannot distinguish 173 from 174/175,
// so the statuses were unimplementable. Status enum + data, never a bare nullable.
public enum GalPictureStatus { None, Available, OverSizeLimit, OverCountLimit }

public sealed record GalPictureResult
{
    public required GalPictureStatus Status { get; init; }
    public GalPicture? Picture { get; init; }         // set exactly when Status == Available
}

public sealed record GalPicture
{
    public required ReadOnlyMemory<byte> Data { get; init; }
    public required string ContentType { get; init; }
}

// photos stays NULLABLE — null means the client did not request pictures at all, which is
// distinct from "requested with no limits" (an earlier sketch made it non-nullable and lost
// that state).
Task<IReadOnlyList<GalEntry>> SearchGalAsync(
    string query, int maxResults, GalPhotoRequest? photos, CancellationToken ct);
```

**`IMeetingOperations`** (was `ICalendarOperations`):

```csharp
public enum MeetingResponseKind { Accepted = 1, Tentative = 2, Declined = 3 }  // was `int userResponse`

Task<bool> ShouldSendInvitationsAsync(CancellationToken ct);
// Returns the calendar item key holding the event — so ItemKey?, not string? (an earlier
// sketch returned string? under this very design's own key types). eventUid stays a string:
// an iCalendar UID is domain data, not a backend key.
Task<ItemKey?> RespondToMeetingAsync(
    FolderKey calendar, string eventUid, MeetingResponseKind response, CancellationToken ct);
```

`GetRawEventAsync` is **deleted**. It exists only because `GetItemAsync` used to return EAS XML
rather than the stored ICS; once `CalendarItem` carries the iCalendar, the ordinary fetch is the raw
read, and a second method returning the same thing is a trap (see § 6.2).

**`IMailboxOperations`** (was `IMailStoreOperations`):

```csharp
Task EmptyFolderAsync(FolderKey folder, CancellationToken ct);
Task SaveToSentAsync(ReadOnlyMemory<byte> rfc822, CancellationToken ct);
Task SetAnsweredAsync(FolderKey folder, ItemKey item, bool forwarded, CancellationToken ct);
// NULLABLE, as today: null = the message vanished, which SmartReply/SmartForward relies on
// (an earlier sketch dropped the nullability).
Task<ReadOnlyMemory<byte>?> GetRawMessageAsync(FolderKey folder, ItemKey item, CancellationToken ct);
// folder is NULLABLE, as today: null = search the whole mailbox, not one folder (an earlier
// sketch made it required and lost mailbox-wide Search). Hits come newest first — the prose
// contracts (ordering, null semantics) move onto the new XML docs verbatim, they are not
// implied by the types.
Task<IReadOnlyList<SearchHit>> SearchAsync(
    FolderKey? folder, string freeText, DateTimeOffset? since, int maxResults, CancellationToken ct);

// replaces IReadOnlyList<ValueTuple<string, string>> — an untyped pair whose meaning
// (which string is the folder? which the item?) lived only in the callers.
public sealed record SearchHit
{
    public required FolderKey Folder { get; init; }
    public required ItemKey Item { get; init; }
}
```

**`GetAttachmentAsync` is deleted, not retyped.** Verified against both in-repo
implementations: `ImapMailBackend.GetAttachmentAsync` fetches the **full message**
(`GetMessageAsync`) and then does `message.Attachments.Skip(index)` (`ImapMailBackend.cs:495-511`),
and `JmapMailStore` does exactly the same over the raw blob (`JmapMailStore.Attachments.cs:24-29`).
So the store-side method saves nothing today — and its `index` was defined as "position in
`MimeMessage.Attachments`", i.e. **MimeKit's enumeration order**, out-of-band knowledge no
MimeKit-free plugin could reproduce (a direct principle-1 violation). Instead the host fetches
via `GetRawMessageAsync` and extracts the attachment itself with its own MimeKit; the index
becomes purely host-internal. A store that can someday fetch a single MIME part efficiently
(IMAP `BODYSTRUCTURE` partial fetch, a JMAP blob) would be a new *optional* capability with a
store-chosen opaque part token — additive, not blocking. (`ICalendarAttachmentSource` is
different and stays: inline ICS attachments genuinely live in the payload the store owns, and
its index is payload-defined — see the sweep note above.)

**`IMailSubmitOperations`** needs only `byte[] mime` → `ReadOnlyMemory<byte> rfc822`, for
consistency with `MailItem`. Its currency was already right — RFC822 is the domain type, and
SmartReply/SmartForward composition stays host-side.

**`IOofBackend` / `OofReply` are reviewed and correct as they stand** — modulo `OofReply`'s
instants joining the principle-8 `DateTimeOffset` retype in Phase 1. `OofReply(BodyText,
BodyIsHtml, Start, End)` is properly typed, and `EnableAsync`'s `string` return is a
**legitimately opaque restore token** — the backend's own previous-script name, which the host
stores and hands back verbatim (`AGENTS.md` § *Out-of-office*). It is named here explicitly so a
future reader does not "fix" it into something typed.

---

## 6. Ghosting, merging and the read-before-write question

### 6.1 The problem being solved

EAS 16.x sends **partial** updates: an absent element means "leave unchanged", not "clear".
`XElement` distinguishes those for free; a record with nullable properties does not — `null`
conflates "unsent" and "erase". Load-bearing invariants depend on it (`AGENTS.md`: exception dates
merge rather than clear; an omitted `Recurrence` preserves the stored RRULE).

### 6.2 Why no changed-field mask is needed

Under a payload currency, **the merge never crosses the boundary.** The host holds the client's
partial `ApplicationData`, where presence information exists natively in the XML. It reads the
current payload, merges, and hands the store a **complete** payload. The plugin never receives a
patch, so there is nothing to mask.

The architecture is already most of the way there: `ICalendarOperations.GetRawEventAsync` exists
today precisely so the stored merged ICS can be read back before a 16.x partial update, and the
converters already take `existingIcs` / `existingVcard` parameters. **That method does not survive
this design** (§ 5.8) — it becomes redundant the moment `GetItemAsync` returns
`CalendarItem.ICalendar`, which *is* the raw read. It is cited here as evidence that the read
already happens, not as the mechanism that continues to perform it.

One class does still need presence semantics, and it is not a residue — it is the dominant write
in the whole system: **the mail `Change`.** Flags and categories live *outside* the RFC822, the
message is never rewritten, and `ImapMailBackend.UpdateItemAsync` is presence-guarded today for
exactly this reason (`Read`, `Flag` and `Categories` are each applied only when the element is
present — `ImapMailBackend.cs:245-298`; "client did not send categories" must stay
distinguishable from "client cleared categories"). An earlier draft filed this under "if that
residue needs expressing" while sketching `IMailStore : IContentStore<MailItem>` — an interface
that could not express it at all. The resolution is structural and is now the design (§ 5.4):
mail gets its own store interface whose everyday write is `UpdateFlagsAsync(…, MailFlagsPatch, …)`,
with presence carried **on the value** (`readonly struct Optional<T>` with `HasValue`/`Value`,
patch types distinct from item types) — never a parallel field-set, which can drift when a field
is set but not flagged with nothing to catch it. The only mail *content* write, the EAS 16.x
draft rewrite, is its own method (`ReplaceDraftAsync`) because it may change the item key.

### 6.3 The round trip — four mitigations

The concern: moving the merge host-side appears to add a fetch before every write.

**Mitigation 0 — it is mostly relocation, not addition.** The read already happens today. The DAV
path fetches the stored ICS and passes it to `CalendarConverter.FromApplicationData(..., existingIcs, ...)`.
Moving the merge up one level relocates that read; it does not invent it. Net cost on the DAV path
is close to zero.

**Mitigation 1 (primary) — revision-keyed payload cache.** A client can only edit an item the
gateway has already sent it, and the sync engine knows the revision it sent. Cache the raw payload
keyed by `(FolderKey, ItemKey, ItemRevision)` on the way out, bounded LRU per session. A client
`Change` arriving against that revision merges with **no fetch at all**. A miss, or a revision
mismatch, falls back to a fetch — which is exactly what correctness demands anyway, since a moved
revision means the item changed underneath.

**Mitigation 2 — conditional update.** `UpdateItemAsync(..., ItemRevision? expected, ...)` lets
the store apply an `If-Match` / `UIDVALIDITY`-style precondition. This does not remove the fetch;
it closes the lost-update window that read-merge-write opens. Complementary, not alternative.
Its failure semantics must be pinned on both sides of the seam, or every store invents its own:

- A store that detects a mismatch throws **`BackendPreconditionFailedException :
  BackendException`** (new, in Contracts) — a typed signal, so the host can distinguish "the
  item moved underneath the merge" from any other backend error.
- The host's response: drop the cached payload, re-fetch, re-merge the client's partial data
  onto the fresh payload, retry **once** with the new revision; a second failure surfaces as
  the ordinary per-item conflict status. Bounded by construction — no retry loop.
- A store that **cannot** check the precondition (IMAP has no If-Match for flags) ignores
  `expected` and applies the write; that is conforming. `expected` is an *upgrade* for stores
  that can honour it (DAV `If-Match`, JMAP `ifInState`), never an obligation.

**Mitigation 3 — batch the read with the diff.** A Sync round already enumerates the collection.
Where a store can return payloads cheaply for the items being changed in the same round, the fetch
folds into work already scheduled.

**Explicitly rejected:** an optional `IPartialUpdate` capability letting a store apply the client's
partial data itself. It would push EAS ghosting semantics back into plugins, split the semantics
across two code paths, and reintroduce exactly the untyped surface this design removes.

> **Implementation deviation, recorded in Phase 3a (per § 5's own rule).** Mitigation 2's retry
> runs with **no precondition** rather than "the new revision": the contract has no
> revision-returning single-item fetch, so after a failed precondition the host re-fetches the
> payload, re-merges, and writes unconditionally — merging onto the freshest payload IS the
> conflict resolution, and the write is still bounded to one retry by construction. (A second
> `BackendPreconditionFailedException` can therefore only come from a store that races again on
> its own concurrency check — e.g. the local store's row-version guard — and the handler answers
> it as the ordinary per-item conflict, Status 7 + pending revert, exactly as specified.) The
> conditional first attempt fires only on a payload-cache hit, where the cached revision IS the
> merge basis; a cache miss merges onto a fresh fetch, where an `expected` pin from an older
> generation would only produce false conflicts.

---

## 7. What moves where

```
ActiveSync.Contracts     enums, keys, item records, store interfaces, capabilities.
      (published, MIT)   Dependencies: BCL + Microsoft.Extensions.{Configuration,DI}.Abstractions.
                         NO domain libraries. NO ActiveSync.Protocol reference.

ActiveSync.Contracts     OPTIONAL ergonomics (§ 5.6). One package: MimeKit + Ical.Net +
  .Interop               FolkerKinzel.VCards. Extension methods only, ~100 lines plus test builders.
      (published, MIT)   Takes the RELEASE TAG version (sets no version properties, so it inherits
                         the global -p:Version) — so its dependency pins document exactly what the
                         gateway of the same version was built against. Outside the surface-approval
                         gate because it is not loader ABI. Consumed by the host's own conversion
                         layer too, so it is exercised by the in-repo suite.

ActiveSync.Contracts     OPTIONAL conformance suite (Phase 5): STORE obligations only — see the
  .Conformance           scope correction in Phase 5; engine-level invariants (SyncKey replay,
      (published, MIT)    windowing, echo suppression) cannot ship MIT because exercising them
                         requires the PolyForm engine. Release-versioned like .Interop. Run by
                         the replaced fixture plugin in CI, so the "one package is enough" claim
                         is tested rather than asserted.

ActiveSync.Protocol      Wbxml* (980 ln), EasRequestParameters (406), CollectionDiff (163),
      (HOST-ONLY)        EasVersion (113), EasNamespaces (97), EasDateTime (85), WireLog (110).
                         IsPackable=false. Referenced explicitly by Core/Server/Backends.Common.

ActiveSync.Eas           NEW, host-only (§ 10, decision 2). The format -> EAS-XML conversion layer:
  .Conversion            iCalendar/vCard/RFC822 <-> ApplicationData, plus the ghosting merge.
                         References Contracts + Protocol + MimeKit/Ical.Net/FolkerKinzel, and is
                         referenced by Server. MUST NOT reference Core — Core carries no domain
                         library today and ActiveSync.WebUi references Core ONLY, so putting the
                         converters in Core would hand MimeKit to the admin UI. Test-enforced by a
                         new DependencyRuleTests case, mirroring BackendsCommon_DoesNotReferenceCore.

ActiveSync.Core          Gains the per-session payload cache (§ 6.3) on CompositeBackendSession.
                         Gains NO domain library: its package list stays EF Core / Npgsql / SQLite
                         / Microsoft.Extensions.*.

Backends.Common          Keeps the backend-facing half: TLS/wire helpers, HTTP factory, schema
      (HOST-ONLY)        fields, and the R2 store-need helpers of § 7.1 (SetPartStat,
                         ExtractAttachment, BusyPeriodsFromEvents, ParseFreeBusy, ExtractUid,
                         CategoryKeywords) — so it RETAINS Ical.Net + FolkerKinzel.VCards.
                         Provider-private format bridges (JSCalendar/JSContact) stay in
                         Backends.Jmap. Gains an explicit ProjectReference to Protocol (it has
                         one transitively today via Contracts and is the heaviest consumer:
                         EasDateTime 37, EasNamespaces 18, WireLog 7) — expected to drop to
                         near zero after the split.
```

**The converter split is the substantial engineering.** It is specified per unit in § 7.1 —
derived from the actual call sites, not asserted.

### 7.1 The converter split, specified

Four rules generate every disposition below; when a future helper is added, apply the rules
rather than extending the table by feel:

- **R1 — EAS in, host-side out.** Anything that reads or writes EAS XML or an EAS wire encoding
  (`XElement` ApplicationData, AirSyncBase bodies, the MS-ASTZ blob, `EncodeGlobalObjId`, the
  `"calatt::"` reference format) moves to `ActiveSync.Eas.Conversion`.
- **R2 — store-need, backend-side.** Anything a store needs to fulfil the *new* contract from
  its own payload or backend data (extract a UID to name an href, pull the Nth `ATTACH`, set a
  PARTSTAT, parse a free-busy REPORT, classify IMAP keywords into categories) stays backend-side:
  in `Backends.Common` when ≥2 backend assemblies use it, in the single consumer's assembly
  otherwise.
- **R3 — load/serialize quirks, interop.** Bare parse/serialize with quirk handling
  (`IcalHelpers.Load/Serialize`) becomes the published `ActiveSync.Contracts.Interop` surface —
  this is § 5.6's "the host's own conversion layer consumes the same helpers" made concrete.
- **R4 — host-only consumer, host-side.** A helper only the host calls goes host-side even when
  it contains no EAS (e.g. iMIP mail composition — its sole consumers are
  `MeetingInvitationService` and `MeetingResponseHandler` in Server).

| Unit (today, in `Backends.Common`) | Disposition | Rule / evidence |
|---|---|---|
| `MailConverter.ToApplicationData`, `BuildBody`, `EncodeGlobalObjId` + their private helpers | `Eas.Conversion` | R1 |
| `MailConverter.CategoryKeywords` / `SanitizeKeyword` | stays `Backends.Common` | R2 — the imap store's revision string is built from them (`ImapMailBackend.RevisionOf`, `:646-652`) and its category writes use them (`:282`); jmap keywords use the same classification |
| `MailConverter.MessageFlags` record | deleted | replaced by the contract's `MailFlags` (§ 5.5) |
| `DraftMessageBuilder` (whole, 227 ln) | `Eas.Conversion` | R1 — merges EAS draft XML into MIME. The host already calls it today (`SyncHandler.ClientCommands.cs:393`); after the move the host merges and hands the store a finished `MailItem` via `CreateDraftAsync`/`ReplaceDraftAsync` |
| `CalendarConverter.ToApplicationData` / `FromApplicationData` | `Eas.Conversion` | R1 — this pair IS the ghosting merge |
| `CalendarConverter.ReadSchedulingInfo` / `SchedulingSignificantlyDiffers` / `SchedulingInfo` | `Eas.Conversion` | R4 — sole consumer is `MeetingInvitationService` |
| `CalendarConverter.AttachmentReferencePrefix` / `ParseAttachmentIndex` | `Eas.Conversion` | R1 — the `"calatt::"` wire format; `ItemOperationsHandlers.cs:87` already reads the prefix host-side |
| `CalendarConverter.ExtractUid`, `SetPartStat`, `ExtractAttachment`, `ParseFreeBusy`, `BusyPeriodsFromEvents` | stay `Backends.Common` | R2 — Dav *and* Local stores need them for `IMeetingOperations`, `ICalendarAttachmentSource`, `IFreeBusySource` and href naming. `ParseFreeBusy` in particular is the hand-parsed FREEBUSY workaround (`AGENTS.md`: Ical.Net 5.x returns null) — it must not be "simplified" during the move |
| `ContactConverter.ToApplicationData` / `FromApplicationData` | `Eas.Conversion` | R1. The hand-rolled vCard 3.0 *writer* travels with `FromApplicationData` — the `AGENTS.md` invariant (write by hand, read via FolkerKinzel) survives, relocated |
| `ContactConverter.ExtractUid` | stays `Backends.Common` | R2 — Dav and Local |
| `ContactConverter.ToGalEntry` / `BuildGalEntry` / `AppendGalPicture` | split | the vCard→**typed `GalEntry`** projection (query match, field extraction, photo with store-enforced limits per § 5.8) stays in `Backends.Common` — R2's shared clause: CardDAV **and** the local contact store both serve GAL (`LocalStores.cs:58` calls `BuildGalEntry`); jmap has its own JSContact-based GAL projection in `Backends.Jmap` and touches `ContactConverter` in a comment only. The `GalEntry`→`gal:`-XML and RR-namespace shaping is R1 → `Eas.Conversion` |
| `TasksConverter.ToApplicationData` / `FromApplicationData` | `Eas.Conversion` | R1 (keeps the Regenerate/DeadOccur holes and the omitted-Recurrence presence guard) |
| `TasksConverter.ExtractUid` | stays `Backends.Common` | R2 — Dav and Local |
| `NotesConverter` (83 ln) | moves **into `Backends.Local`**, private | NOT deleted, correcting an earlier § 7 line. `LocalItem.Content` rows are AES-sealed **VJOURNAL text**; keeping VJOURNAL at rest means zero data migration for existing notes. The mapper becomes the local store's private `NoteItem` ⇄ VJOURNAL storage convention — no other backend will ever see it, which is exactly what decision 1 says VJOURNAL is |
| `RecurrenceMapper`, `TimeZoneBlob`, `BodyText`, `AirSyncBodyWriter` | `Eas.Conversion` | R1 — EAS Recurrence XML, the MS-ASTZ blob, AirSyncBase body shaping |
| `CalendarAttachmentPolicy` | `Eas.Conversion` | R1-adjacent: `CapBytes` gates what the host-side merge writes into the ICS — this is the Phase 4 knob-inventory poster child (`Backends:Calendar:CalendarAttachments` becomes a host option) |
| `ImipMailBuilder` | `Eas.Conversion` | R4 — Server-only consumers |
| `IcalHelpers.Load/Serialize` | `Contracts.Interop` | R3 — the quirk-handling load/serialize both halves need |
| `MailKitWireLogger`, `MailTransportSecurity`, `ServerCertificateValidator`, `BackendHttpClientFactory`, `RedirectingHttpSender`, `BackendSchemaFields`, `BackendDescription`, `BackendConnectionOptions` | stay `Backends.Common` | not converters — the TLS/wire/HTTP/schema half was never in question |

Two consequences the table forces, both corrections to earlier expectations:

- **`Backends.Common` KEEPS Ical.Net and FolkerKinzel.VCards.** An earlier Phase 4 line expected
  it to shed both "entirely" — the R2 rows above disprove that: `SetPartStat`,
  `ExtractAttachment`, `BusyPeriodsFromEvents` and the `ExtractUid` family are Ical.Net
  consumers shared by Dav and Local, and `ContactConverter.ExtractUid` needs FolkerKinzel. What
  `Backends.Common` sheds is the **EAS half**: its `EasNamespaces`/`EasDateTime` usage
  (18 + 37 sites, the § 1.3 table's heaviest consumers) drops to near zero, which is what
  actually matters for goal 4.
- **The jmap provider's converters retarget, not relocate.** `JsContactConverter` today maps
  JSContact ⇄ EAS XML directly (`JmapContactStore.cs:84,91,111`); under the payload currency it
  must produce/consume **vCard** instead — a rewrite of its EAS half, staying in
  `Backends.Jmap`. Likewise `JmapCalendarStore` already builds iCalendar mid-pipeline (JSCalendar
  → iCal → EAS) and simply stops at the iCal step; `JmapMailStore` already fetches the raw
  RFC822 blob and drops its EAS body-shaping tail. This is the § 4 table's "stops one step
  earlier" claim made concrete per store.

The converter unit tests currently hosted in `Core.Tests` (there is no per-provider test
project) follow their code: tests of the EAS half move with it and gain nothing; tests of the
R2 helpers stay pointed at `Backends.Common`.

---

## 8. Licensing and versioning consequences

**Licensing (`AGENTS.md` § *Licensing*).** Anything in Contracts is permanently MIT and
irrevocable once published. This design puts *more* types there (enums, keys, item records, the
`OwnedResource` handle) but they are trivial and carry no commercial value, while moving the 3,200
lines of conversion logic — the part with real value — decisively host-side and PolyForm. Net
posture improves.

`ActiveSync.Protocol`'s already-published versions stay MIT forever; that cannot be undone. Going
forward, once host-only, it can be relicensed PolyForm like Core/Crypto/Backends.Common were.

**Versioning.** `Directory.Build.props` pins `$(ContractVersion)` onto both Contracts' and
Protocol's `AssemblyVersion` specifically to keep the release tag away from them. Once Protocol is
host-only it should rejoin the normal `-p:Version` flow. `ContractVersion.cs`'s doc-comment
("the surface formed by `ActiveSync.Contracts` and `ActiveSync.Protocol`, which version together")
needs rewriting.

**This is a wholesale contract redesign — versioned with minor bumps throughout (owner
decision, 2026-07-29; § 10 decision 14).** Under policy, minor absorbs breaking changes and
**`ContractVersionMajor` is a human decision that must never be taken without an explicit
request** (`AGENTS.md`). The owner has decided: every phase raises `ContractVersionMinor` only;
the owner will raise the major to **2.0 manually, themselves, once satisfied with the result** —
no phase of this plan touches `ContractVersionMajor`, and an implementing agent must not raise
it "on completion" or for any other reason.

**Mechanical touch points, all verified:**

- `ContractSurfaceApprovalTests.cs:179` —
  `Assembly[] assemblies = [typeof(IGatewayPlugin).Assembly, typeof(EasVersion).Assembly];`
  Drop the second entry; the Protocol section (approved file lines 313–477) leaves the snapshot.
- `ContractSurfaceTests.ContractVersion_IsTheExpectedSurfaceVersion` — literal tripwire to update.
- Regenerate with `EAS_APPROVE_CONTRACT_SURFACE=1 dotnet test --filter FullyQualifiedName~ContractSurfaceApprovalTests`.
- `PluginLoader` needs **no change**: it already keys the gate off
  `typeof(IGatewayPlugin).Assembly` (`PluginLoader.cs:310`), which is Contracts.
- `Directory.Build.targets` — the `IsPackable` gate already handles package metadata correctly;
  flipping Protocol's flag is sufficient to stop packing it.
- `docs/plugins.md:16-29` and `:184`, plus several `AGENTS.md` sections, describe Protocol as part
  of the published surface.

---

## 9. Phased plan

Each phase is independently landable and leaves the tree green. Phases 1–2 are low-risk and can be
reviewed before committing to the rest.

**Every phase changes the published surface**, so every phase raises `ContractVersionMinor` and
regenerates the approved snapshot — not just Phase 1. `ContractSurfaceApprovalTests` enforces
this mechanically (and its history block is append-only), so phases landing between two release
tags still each record their own bump; only the tag publishes.

**Execution model (owner's choice, 2026-07-29).** All five phases land on one long-lived
branch, **`plugin-restructure`** (created from `main` if absent), as **one commit per phase —
except Phase 3, which is two** (the 3a checkpoint and the 3b completion; see the phase's own
orchestration below). Six phase commits total (small operator process edits to this document
may add their own commits between phases; they are not phase work). Each *phase* ends with the tree green per its
verification line; the 3a checkpoint is the plan's ONE deliberately-red intermediate commit
and is never pushed on its own. Phases are sequential: Phase N assumes the prior phases'
commits are already on the branch.

**Phase 3 — and only Phase 3 — pushes and waits for CI.** The GitHub pipeline's integration
matrix is the ONLY place the full backend barrage runs (five active stacks; locally
`scripts/test-fast` covers stalwart + axigen alone), and Phase 3 is the one that changes the
data path for every class on every backend — so Phase 3 is **done** only when its branch push's
Actions run is green (the push carries everything up through the 3b commit, so the matrix
validates the accumulated branch). A red run is fixed by **amending the 3b commit and
force-pushing** (the branch is worked sequentially by one session at a time, so force-push is
safe). Every other phase commits locally and does **not** push — its own verification line is
sufficient, and gating each phase on a full CI round-trip would add significant wall-clock
waiting for no coverage gain. Branch pushes are publish-safe: the pack/NuGet steps are
tag-gated (§ 5.6), so nothing is released by pushing. Merging to `main` and raising 2.0
afterwards remain the owner's own acts (decision 14).

**Model assignment (owner's choice):** Phase 1 → Sonnet · Phase 2 → Opus · Phase 3 → **Fable
as the orchestrating session, one Opus subagent as the 3b worker** · Phase 4 → Opus ·
Phase 5 → Opus. All at high effort. The rationale: Fable is spent only on the judgment-dense
core of Phase 3 (3a + the review), never on the token-heavy bulk conversion.

**Operator note — the prompt for a clean agent. Phases 1, 2, 4 and 5, one line, substitute
the phase number:**

> Read `AGENTS.md` (coding conventions, invariants, testing expectations) and `docs/design/typed-plugin-contract.md` in full and follow the design document's authority rules, then implement Phase N of its § 9 plan exactly as specified, working on the `plugin-restructure` branch (create it from `main` if it does not exist), and finish with that phase's verification gates green and the phase's work as exactly one commit on that branch — do not push, do not touch `ContractVersionMajor`, and do not start any other phase.

**Phase 3 has its own prompt — run it in a Fable session:**

> Read `AGENTS.md` (coding conventions, invariants, testing expectations) and `docs/design/typed-plugin-contract.md` in full and follow the design document's authority rules, then execute Phase 3 of its § 9 plan per that phase's 3a/3b orchestration: implement Phase 3a yourself, make the 3a checkpoint commit on the `plugin-restructure` branch, then STOP and report what landed — do not begin 3b until I tell you to continue. When I say continue: spawn exactly one Opus subagent to implement Phase 3b per its work list, adversarially review the worker's complete diff against § 5, § 7.1 and your 3a exemplars and fix what the review finds, make the 3b commit, then push the branch and confirm the GitHub Actions run for that push completes green (fix failures by amending the 3b commit and force-pushing) — do not touch `ContractVersionMajor`, and do not start any other phase.

### Phase 1 — typed primitives, and sever Protocol

- Introduce `FolderType`, `BodyType`, `BusyKind` in Contracts (`ContentClass` is deliberately
  NOT among them — it ends up host-side, § 5.1).
- Retype `BackendFolder` and `BusyPeriod`; drop `Eas16` from `BodyPreference` (the record itself
  is deleted in Phase 3 with the store retype — until then stores still take it, minus the leak).
  **Landed with a deviation: `Eas16` was KEPT** — see the recorded note in § 5.1; dropping it
  while stores still convert would have regressed 16.x shapes for two phases. `BodyPreference`
  is otherwise retyped (`BodyType Type`) and init-only like every other model.
- **Slim `ContentFilter`** — easy to miss, but it is the actual severance work: `Models.cs`'s
  `ForClass(string easClass, …)` is the ONLY use of Protocol in Contracts (`EasClass.Email` /
  `EasClass.Calendar` consts), so the ProjectReference cannot be deleted while it stands. The
  whole helper family — `ForClass` and the `FromMailFilterType`/`FromCalendarFilterType`
  EAS-wire-int mappings — moves host-side (principle 3: FilterType is wire encoding);
  Contracts keeps only `ContentFilter(DateTimeOffset? Since)` + `All`. *Landed as
  `ActiveSync.Core.Backend.ContentFilters` — Core beside `MergedFreeBusy`, the existing
  precedent for a host-side wire mapping over a contract value; the three call sites are all in
  Server. The AirSyncBase body-Type wire integer gets the same treatment on the way in
  (`Server/Eas/EasBodyTypes.FromWire`), so a client-supplied integer is never cast blindly onto
  the `BodyType` enum.*
- Standardise every instant on `DateTimeOffset` (principle 8): `BusyPeriod`, `OofReply`,
  `ContentFilter`, `SearchAsync` — one pass, so the surface never ships mixed.
- Convert every Contracts model from positional to init-only property records (principle 7).
  **Explicit line item: `BackendCredentials` keeps its hand-written `PrintMembers` password
  redaction through the conversion** (`Models.cs:24-28`) — a mechanical record rewrite is
  exactly how such an override gets dropped — and gains a test asserting `ToString()` contains
  `***` and never the password, making the property permanent.
- Delete the `ActiveSync.Protocol` `ProjectReference` from `ActiveSync.Contracts.csproj`.
- Add explicit Protocol references to `Backends.Common` and any assembly that relied on the
  transitive one.
- **Verification:** solution builds at 0 warnings; unit suite green; contract surface regenerated.

### Phase 2 — structural cleanups

- `FolderKey` / `ItemKey` / `ItemRevision`. (No `AttachmentReference` — the composite wire
  references `FileReference`/`LongId` stay host-internal encodings, § 5.2/§ 5.8.) *Introduced as
  the three positional newtypes; `ItemRevision` gets its first consumer here (the snapshot entry
  below), `FolderKey`/`ItemKey` land with the store retype in Phase 3.*
- Type `BackendConnection`'s `ownedResources` disposal list (§ 5.7). **Leave capability discovery
  alone** — `is`-testing providers and stores is correct as it stands. *Landed as
  `OwnedResource.OfAsync`/`OfSync`; the disposal-identity check compares the wrapped resource, so
  a store listed as its own owned resource is still disposed exactly once.*
- Move `DelimitedKey` out of Contracts; strip `Parse`/`Validate` from `SharedCollection` (§ 5.2).
  *Landed in `ActiveSync.Protocol` and `ActiveSync.Backends.Dav.SharedCollectionEntry`
  respectively — see the deviation note in § 5.2 for why neither could go to Core.*
- Move the read-only `"!ro"` sentinel out of the revision value space: the snapshot entry becomes
  `(ItemRevision Revision, bool PendingReadOnlyRevert)`. Five call sites, all in `SyncHandler`.
  *Landed as `ActiveSync.Core.State.SnapshotEntry` + `CollectionSnapshot`, with the marker
  reaching the (BCL-only, Protocol-resident) diff as a `forceChanged` set — see the recorded
  deviation in § 5.2. The conflict/read-only sites in `SyncHandler.ClientCommands`, the
  render-failure restore in `SyncHandler.Collection`, `MoveItemsHandler` and
  `PendingChangeDetector` all move with it.*
- **A forced resync is a CHOICE here, not a consequence — make it deliberately.** Snapshots
  persist as JSON in `CollectionState`; a shape change invalidates every stored snapshot and each
  device restarts from SyncKey 0. But it is avoidable if wanted: `ItemRevision` can serialize
  identically to today's bare string via a JSON converter, and `PendingReadOnlyRevert` can be a
  sidecar key-set serialized only when non-empty — old snapshots stay readable. Pre-production,
  the clean break is the simpler code and is precedented (the schema reinit that removed
  `LegacyAccountJson`); this phase takes the break, but as an announced decision line, with the
  compatible-serialization alternative recorded so the choice is visible. *Taken as written, and
  made explicit rather than incidental: the stored blob is version-stamped, and an older one is
  refused (Sync Status 3) instead of read as empty — § 5.2's deviation note has the reasoning.
  Note the sidecar idea was adopted for the ON-DISK shape anyway, purely so the snapshot's bulk
  stays a flat id → revision map; it does not make the old blobs readable.*
- **Verification:** unit suite; one integration stack (`stalwart`) via `scripts/test-fast`.

### Phase 3 — item currency (the substantial one)

**Orchestration (owner's choice, 2026-07-29): one Fable session runs this phase, with a
mandatory pause after 3a.** Fable implements 3a (the judgment-dense core), makes the checkpoint
commit, then **STOPS and reports — it does not proceed to 3b until the operator explicitly says
to continue** (the pause is a deliberate operator checkpoint: it lets the owner review the 3a
surface and it is the clean place to absorb a usage-limit break, since the checkpoint commit is
the designed handoff state). On the operator's go-ahead, the same session spawns **exactly one
Opus subagent** to implement 3b (the token-heavy bulk, following 3a's exemplars), adversarially
reviews the worker's complete diff, fixes what the review finds, makes the 3b commit, pushes,
and gates on the CI matrix. The split exists because the expensive part and the hard part of
this phase are different code: the semantics concentrate in 3a, the tokens in 3b.

**Phase 3a — the orchestrator (Fable) implements:**

- Introduce `MailItem`, `CalendarItem`, `TaskItem`, `ContactItem`, `NoteItem`, `MailFlagsPatch`
  + `Optional<T>`, `MailFetchOptions`; delete `BodyPreference` from the contract (host-side now).
- Split `IContentStore` into the generic form and per-class aliases — and the **separate
  `IMailStore`** (§ 5.4): `UpdateFlagsAsync` (the everyday patch), `CreateDraftAsync` /
  `ReplaceDraftAsync` (the 16.x draft paths, key-change explicit). Mail is the class most
  likely to be quietly wedged into the generic shape it does not fit; it is a named line item
  so it cannot be.
- **Convert the side operations and capabilities too (§ 5.8)** — the contract surface in full.
  The checklist is the mechanical sweep, not a list: every `string` naming a
  folder/item/revision, every same-typed tuple. Highlights: rename to
  `IMailboxOperations`/`IMeetingOperations`/`IDirectoryOperations`; `SearchGalAsync` →
  `GalEntry` with `GalPictureResult` (the typed photo statuses — a bare nullable cannot carry
  173 vs 174/175); `MeetingResponseKind`; `RespondToMeetingAsync` → `ItemKey?`; `SearchHit`
  replacing the untyped `(string, string)` pair, with `SearchAsync`'s whole-mailbox
  `FolderKey?` and `GetRawMessageAsync`'s nullability preserved; **deletion** of both
  `ICalendarOperations.GetRawEventAsync` and `IMailStoreOperations.GetAttachmentAsync` (host
  extracts from the raw message — verified equivalent, § 5.8); typed keys through
  `IItemMoveOperations` / `IFolderOperations` / `IReadOnlyCollectionSource` /
  `ICalendarAttachmentSource` / `IFreeBusySource`.
- Add `BackendPreconditionFailedException` and the § 6.3 conditional-update semantics
  (host re-fetch + one retry; stores may ignore `expected`).
- Move ghosting/merge host-side; add the payload cache and `expected` revision — including the
  `SyncHandler` interplay (echo suppression, the draft Delete+Add path, read-only revert).
- Raise `ContractVersionMinor` and regenerate the surface snapshot **here** — the contract
  surface is complete after 3a; 3b touches providers only.
- **Exemplar conversions, in full: `imap` and `local`.** `imap` because every mail subtlety
  lives there (the flags patch, both draft paths, `CategoryKeywords`); `local` because it is
  the smallest complete payload-class store (calendar/contacts/tasks/notes incl. the
  `NoteItem` ⇄ VJOURNAL mapping, § 7.1). These are the worker's reference implementations.
- **The 3a checkpoint commit — the plan's one exception to the green rule.** The unconverted
  providers (`dav`, `jmap`, `sieve`, `smtp`) will not compile against the new seam; that is
  expected, and their compile errors ARE 3b's work list. This commit is never pushed alone.

**Phase 3a implementation notes (recorded at the checkpoint; § 5's deviation rule):**

- **The host conversion seam landed as `ActiveSync.Server/Eas/Content/`** — `ContentAdapter`
  (typed fetch/render/merge/cache/precondition retry, wrapping the typed keys at the string-keyed
  handler boundary), `NotesXml` (the XML half of the old notes converter), `GalXml` (typed
  `GalEntry` → gal:-namespace shape + the wire photo statuses; ResolveRecipients projects the RR
  shape from the same record), and `MailFileReference` (the "{folder}|{item}|{index}" encoding,
  now entirely host-internal — ItemOperations/GetAttachment fetch the raw message and extract the
  part with the host's MimeKit). This is the Phase-3 home; Phase 4 relocates the converters
  themselves into `ActiveSync.Eas.Conversion` and this seam shrinks to calling them.
- **`BodyPreference` (with `Eas16`) moved to `Backends.Common.Converters`,** where the converters
  it parameterizes live until Phase 4 moves both together. Stores never see it.
- **Two § 7.1 rows were pulled forward from Phase 4 because the seam change forced them:** the
  notes converter split (`NoteItem` ⇄ VJOURNAL became `Backends.Local.NoteJournalMapper`, private;
  the XML half became Server's `NotesXml`; `Backends.Common/Converters/NotesConverter.cs` is
  deleted), and `MailConverter.MessageFlags` (deleted — `ToApplicationData` now takes the
  contract's `MailFlags` plus the store-classified category list). `ContactConverter`'s GAL
  projection likewise now returns the typed `GalEntry` (the § 7.1 split, host shaping separated).
  `IcalHelpers` became `public` (its R3 destiny is the published interop surface; the local notes
  mapper needs it today).
- **`BackendAttachment.Content` is `ReadOnlyMemory<byte>`** (was `byte[]`) — the § 4.2 ownership
  rule applied uniformly to every byte payload on the surface.
- **The `Backends:Calendar:CalendarAttachments` knob is pinned to Auto semantics (1 MiB cap) for
  every backend while conversion is host-side.** The knob is provider-owned config the host must
  not read; § 9's Phase 4 knob inventory (which names this exact knob) restores operator control
  as a host option.
- **`ImapMailBackend` no longer takes `mailAddress`** — draft composition (its only consumer)
  is host-side.
- **Draft-rewrite echo suppression:** the host records the store's returned revision under the
  OLD item key (never the returned key), preserving the Delete+Add re-identification exactly as
  § 5.4's caution specifies.
- **The 3a surface snapshot was regenerated by a standalone copy of the approval test's
  generator** (byte-identical output), because `Core.Tests` references the deliberately-red
  provider assemblies and cannot run at the checkpoint; the 3b run's own
  `ContractSurfaceApprovalTests` execution verifies it.

**Phase 3b implementation notes (recorded at completion; § 5's deviation rule):**

- **DAV honours the `expected` precondition as `If-Match`** (§ 6.3's encouraged upgrade): a 412
  surfaces as the typed `BackendPreconditionFailedException` from `WebDavClient`. The create-PUT
  replay reinterpretation (If-None-Match:\*) runs BEFORE that mapping, so a replayed create is
  still absorbed; a replayed update-PUT's 412 now takes the host's re-fetch → re-merge →
  unconditional-retry path, which converges (an improvement over the old hard failure).
- **DAV updates no longer GET before the PUT** — the pre-read existed only to feed the
  converter's merge, which is host-side now. One fewer round trip per update; a vanished item
  surfaces via the host's own pre-fetch or the 412.
- **A new DAV resource's href is named from the payload's own UID** (host-embedded) rather than
  a store-minted guid, keeping the resource name and document in agreement for the
  canonical-href verification. `DavStoreBase` became generic (`DavStoreBase<TItem>`), with the
  payload as the identity in both directions.
- **JMAP calendar/contact honour `expected`** by comparing the revision of the card they already
  fetch for member preservation — deliberately non-atomic (no per-item JMAP precondition
  exists); it catches the common race and the host's retry covers the rest. **JMAP mail
  ignores** `expected`: the only available state token is account-wide and would false-conflict
  on any busy mailbox (conforming per § 6.3).
- **`JmapMailStore.ReplaceDraftAsync` reports the real new email id** (the pre-contract code
  discarded the import result); the host still keys the snapshot on the OLD id per § 5.4.
- **`JsContactConverter` retargeted to JSContact ⇄ vCard** with the Managed/ClearedOnUpdate
  patch semantics, anniversary preservation and photo non-touching intact; vCard is written by
  hand (folded at 75 octets on code-point boundaries, control characters escaped) and read via
  FolkerKinzel — the same split the shared contact converter keeps.
- **JMAP GAL** reports the typed `GalPictureStatus.None` when photos are requested (the bridge
  reads no `media` member), preserving the explicit wire status 173 the old projection emitted.
- **Process correction: a bare branch push does NOT trigger the pipeline** — `build.yaml` fires
  on `main` pushes, tags and pull requests only, so § 9's "the branch push's Actions run" was
  executed as a `workflow_dispatch` of `build.yaml` against the `plugin-restructure` ref (the
  same mechanism release.yaml uses). Run 30439195463: `test` + all SIX integration legs
  (stalwart, mailserver, baikal, james, axigen, cyrus) green. Publish-safety caveat sharpened:
  the NuGet pack and release steps skipped as designed, but the multi-arch image step pushes on
  any non-PR event, so the run published a branch-tagged container
  (`ghcr.io/…:plugin-restructure`) — a Phase 5 candidate if branch dispatches should stop doing
  that.

**Phase 3b — one Opus subagent (spawned by the session) implements, and does NOT commit:**

- Convert `dav`, `jmap`, `sieve`/`smtp` and every remaining side-operation implementation to
  the new seam, following the exemplars (`imap` for mail semantics, `local` for payload
  classes).
- This includes the **jmap retargets** (§ 7.1's jmap consequence lands HERE, not Phase 4,
  because the new seam demands payloads): `JsContactConverter` produces/consumes vCard,
  `JmapCalendarStore` stops at the iCalendar it already builds, `JmapMailStore` hands over the
  raw RFC822 blob and drops its EAS body-shaping tail.
- GAL search and ResolveRecipients paths wired through `GalEntry`/`GalPictureResult`.
- Solution builds at 0 warnings; unit suite + `scripts/test-fast` green. Work is left
  **uncommitted** so the orchestrator reviews a clean working-tree diff.

**Review and close — the orchestrator (Fable), same session:**

- Adversarially review the worker's complete diff against § 5, § 7.1 and the 3a exemplars.
  Priority order: GAL/ResolveRecipients (the least-covered paths), the DAV merge paths, the
  jmap retargets, every echo-suppression call site. Fix what the review finds.
- Make the 3b commit, push the branch, and gate on the Actions run (amend + force-push on red).
- **Verification:** full integration suite across every enabled stack — this phase changes the
  data path for every class on every backend. The full matrix runs via this phase's branch push
  and its green Actions run (the § 9 execution model — Phase 3 is the only phase that pushes);
  `scripts/test-fast` is the local pre-push check. Two named gates on top of "suite green": the
  **`Eas16Tests` 14.1 observer device asserting byte-identical responses** is the single
  sharpest detector of conversion-relocation regressions and must be called out, not buried in
  "the suite"; and GAL search / ResolveRecipients need explicit attention — they are the paths
  least covered by the item-focused tests.

### Phase 4 — converter relocation

- Split the converters exactly as § 7.1's disposition table specifies — the table is the work
  order for this phase; apply its four rules (R1–R4) to anything it does not name.
- Confirm `Backends.Common` no longer needs `EasNamespaces`/`EasDateTime` on the paths that
  moved. It **keeps Ical.Net and FolkerKinzel.VCards** for the R2 backend-facing helpers
  (`SetPartStat`, `ExtractAttachment`, `BusyPeriodsFromEvents`, `ParseFreeBusy`, the
  `ExtractUid` family) and MailKit for `MailKitWireLogger`/`MailTransportSecurity` — an earlier
  draft expected it to shed the domain libraries entirely, which § 7.1's evidence disproves.
  What it sheds is the EAS half: `EasNamespaces`/`EasDateTime` usage drops to near zero.
- Confirm the jmap retargets already landed in Phase 3b (`JsContactConverter` ⇄ vCard,
  `JmapCalendarStore` stopping at iCalendar, `JmapMailStore` raw RFC822 — they belong to the
  seam change, not the relocation): no EAS XML production remains anywhere in `Backends.Jmap`.
- **Inventory the converter-behaviour knobs that live in provider config sections.** Example:
  `Backends:Calendar:CalendarAttachments` (Auto/On/Off) is bound by the caldav provider but
  governs what the *converter* emits — once conversion is host-side, each such knob either
  moves to a host option (a config-key break, to be listed) or the host would have to read a
  provider-owned setting, which violates "the host never knows a provider's option shape".
  Decide per knob, deliberately.
- **XML-doc cref hygiene**: the packages ship XML documentation and the repo just invested in
  a no-dangling-crefs guard. Moving types across assemblies (`DelimitedKey` out, Protocol
  severed, members deleted) orphans crefs; run the guard and fix in the same change, both here
  and in Phase 5.
- **Verification:** unit suite + `scripts/test-fast` (stalwart + axigen — sufficient here, per
  the owner: Phase 3's CI run already validated every stack on the new seam, and this phase
  relocates code along it); `DependencyRuleTests` extended with
  `EasConversion_DoesNotReferenceCore` and a check that Core still carries no domain library.

**Phase 4 implementation notes (recorded at completion; § 5's deviation rule):**

- **The backend side of each split converter was RENAMED, not left sharing its old name.**
  `CalendarConverter`/`ContactConverter`/`TasksConverter` keep their names in
  `ActiveSync.Eas.Conversion` (they are the converters); what stays in `Backends.Common` became
  `CalendarPayload` (ExtractUid, SetPartStat, ExtractAttachment, ParseFreeBusy,
  BusyPeriodsFromEvents), `ContactPayload` (ExtractUid, BuildGalEntry) and `TaskPayload`
  (ExtractUid), with `MailConverter.CategoryKeywords` becoming `MailKeywords.CategoryKeywords`.
  Reason, found in the code: `ContentAdapter` calls BOTH halves for calendar, tasks and contacts
  (`…Payload.ExtractUid` to name the merge's UID, `…Converter.FromApplicationData` to build it),
  so two same-named types in two namespaces would have forced alias usings at the one seam that
  most needs to be readable. The names also stopped being true — nothing left on the backend side
  converts to EAS. `DependencyRuleTests.ConverterTypes_UseTheCommonAssemblyRootNamespace` names a
  payload type as its example now, and a new `EasConversion_OwnsTheEasHalfOfTheConverters` pins
  the whole split by type name in both assemblies.
- **`ActiveSync.Contracts.Interop` is created HERE, not in Phase 5**, because § 7.1's `IcalHelpers`
  row targets it and both halves of the split need those helpers immediately (the host's
  conversion, `CalendarPayload.SetPartStat`, and `Backends.Local`'s VJOURNAL mapper). It ships
  nothing yet: no `IsPackable`, no version properties, no Contracts pin — Phase 5's line item is
  unchanged and now means "publish it, and add the MimeKit/Ical.Net/FolkerKinzel extension
  methods", not "create it".
- **The knob inventory came out at exactly one knob.** Of the four settings the caldav provider
  opts into, `CalendarAttachments` alone governs CONVERTER behaviour and moved to the host as
  **`ActiveSync:Eas:CalendarAttachments`** (Auto/On/Off, live, validated in
  `ActiveSyncOptionsValidator` and catalogued in `SettingKeys`); `SendInvitations` (a property of
  the DAV server's own scheduling), `SharedCollections` and `TaskFolder` are store behaviour and
  stay provider-owned. No other provider has one. Two consequences stated plainly: the config key
  `Backends:Calendar:CalendarAttachments` is a **break** (it is gone, not aliased — pre-production,
  and leaving a dead key that silently does nothing is worse), and the setting is now **global
  where it used to be per-user overridable**, which is the price of the host not reading
  provider-owned config. The `Eas16Tests` attachment round-trip covers the new path; the WebUi
  portal tests that used this key as their example of a self-service provider field now use
  `SendInvitations`, which has the same shape.
- **`Backends.Common` shed the EAS half as predicted and kept the domain libraries.**
  `EasNamespaces` usage went 18 → **0** and `EasDateTime` 37 → **1 call site**: the free-busy
  parser, kept deliberately because an iCalendar UTC date-time is byte-identical to the EAS
  compact form and swapping in a second parser during a relocation would risk a behaviour change
  for no gain. The explicit Protocol reference therefore stays (and its test with it), now for
  `WireLog` plus that one parse.
- **The contract surface did NOT move this phase.** Nothing public in `ActiveSync.Contracts` or
  `ActiveSync.Protocol` changed; the minor still went 1.6 → **1.7** because § 9's preamble makes
  the bump a per-phase rule, and the approval snapshot simply records another version line (its
  only textual delta is the assembly version embedded in one `Deconstruct` signature). Phase 5
  bumps again as its own line item says.
- **XML-doc cref guard:** the relocation orphaned **no** cref (`GenerateDocumentationFile=true`
  over the solution reports zero new CS1574). One stale cref inside the moved
  `ContactConverter` was fixed in passing. Thirteen pre-existing dangling crefs survive in
  files this phase does not touch (Core, Crypto, Server, tests) — deliberately left, since
  fixing them is a separate cleanup rather than phase work.

### Phase 5 — packaging, licensing, documentation

- `IsPackable=false` on Protocol; relicense it PolyForm; rejoin the release-version flow.
- Ship `ActiveSync.Contracts.Interop` (§ 5.6): `IsPackable=true`, **no** version properties (so it
  inherits the release tag via the global `-p:Version`, unlike Contracts/Protocol), an **exact**
  `[X.Y.Z]` dependency pin on `ActiveSync.Contracts` (§ 5.6 — minor is breaking, so a floor range
  is a false promise), and excluded from the contract-surface snapshot. **No `build.yaml` change
  is required** — the existing `if: needs.test.outputs.version != ''` gate already restricts
  publishing to tagged builds, and the pack/push steps are solution-wide and glob-based. Verify
  this rather than assume it: confirm a branch build produces no `dist/nupkg` push.
- Narrow `PluginLoadContext.IsHostOwned` from the `ActiveSync.*` prefix to an **exact
  simple-name match** on `ActiveSync.Contracts` (§ 5.6.1 — a narrowed *prefix* still captures
  `ActiveSync.Contracts.Interop` and silently host-resolves the plugin's copy, which is the
  type-identity failure that section exists to prevent) and align the class doc comment with
  what the code actually shares. Add a loader test: a plugin folder shipping
  `ActiveSync.Contracts.Interop` gets ITS copy, not the host's.
- Contract version: this phase's changes ride another **minor** bump like every phase
  (decision 14 — the major stays untouched; 2.0 is the owner's own manual act later); surface
  snapshot regenerated.
- Rewrite `docs/plugins.md` around the new surface; update `AGENTS.md`; regenerate
  `THIRD-PARTY-NOTICES.md` if package references moved. Two specific corrections the rewrite
  must carry: the ship-nothing rule inverts for interop (a plugin using
  `ActiveSync.Contracts.Interop` MUST ship it in its folder, § 5.6.1); and the current page's
  "the gateway login is the identity" key-rules bullet contradicts the hard invariant
  (`User.UserId` is the identity, the login is mutable; `BackendConnectionContext.GatewayUserId`
  is what durable keys derive from) — fix it rather than propagate it.
- **Replace the fixture plugin.** `ActiveSync.TestPlugin` must implement a *real* store — the
  natural choice is a working `INotesStore`, which under this design needs no third-party
  dependency. Without this the "one package is enough" claim stays untested, which is how the
  current gap arose.
- **Ship a conformance test kit** (`ActiveSync.Contracts.Conformance`, published, MIT) — scoped
  to what a published package CAN test. An earlier draft listed SyncKey N−1 replay, windowing
  and echo suppression among its invariants; those are **engine** behaviours (owned by
  `SyncStateService`/`CollectionDiff`/`SyncHandler`), which a store can neither pass nor fail —
  and exercising them would require shipping the PolyForm sync engine inside an MIT package,
  which the licensing posture forbids. The kit therefore covers **store obligations**: revision
  stability across unchanged fetches, `null` = "not fetched, do not advance the snapshot",
  create-then-list visibility, delete semantics, `WaitForChangesAsync` timeout behaviour,
  key-space disjointness / no default-valued keys, precondition semantics where implemented, and
  payload round-trip fidelity (what the store is given is what it returns). Engine-level
  conformance runs only in-repo, against the replaced fixture plugin — stated openly rather than
  promised and unbuildable. Two reasons the kit earns its place over more documentation: the
  invariants it covers are exactly the ones prose has failed to convey (they are why
  `Backends.Common` became load-bearing), and it gives the replaced fixture plugin something
  real to be validated against rather than merely compiling. Versioned with the release, like
  the interop package, with the same exact Contracts pin.

---

## 10. Resolved decisions

Each of these was an open question in the first draft. They are recorded here with the evidence
that settled them, so the reasoning survives without the conversation.

**1. Notes stay a strongly-typed `NoteItem`.** There is no accepted notes interchange standard;
`VJOURNAL` is a local convention, not interoperability. Notes is also the simplest possible plugin,
and it is the case that motivated role isolation. Tasks was the borderline call and **stays a
payload** — VTODO is genuinely standard and CalDAV-native. Deciding otherwise would be symmetry for
its own sake.

**2. The EAS conversion layer gets its own host-only assembly** (`ActiveSync.Eas.Conversion`),
**not** `ActiveSync.Core`. Decisive evidence: Core carries **no domain library today** — its
package list is EF Core, Npgsql, SQLite and `Microsoft.Extensions.*` — and `ActiveSync.WebUi`
references **Core only**. Putting the converters in Core would hand MimeKit, Ical.Net and
FolkerKinzel.VCards to the admin UI and the CLI's graph, destroying the exact boundary
`Backends.Common` exists to defend. See § 7 for the reference direction and the enforcing test.

**3. `MailItem` carries full RFC822 on every fetch.** This needed measurement in the first draft;
the measurement was already in the code. `ImapMailBackend.GetItemAsync` calls
`folder.GetMessageAsync(uid, ct)` (`ImapMailBackend.cs:152`) — a **full message download** — and
`MailConverter` truncates afterwards per `BodyPreference`. Always-full is therefore *exactly
today's behaviour*, not a regression, and it keeps the riskiest phase behaviour-neutral.

Truncation stays available as a **later additive change**, which principle 7 makes free: adding
`bool BodyTruncated` and `long? FullSizeBytes` to an init-only record breaks nobody, and the
mail fetch signature carries `MailFetchOptions` (§ 5.1 — empty today) precisely so a truncation
hint can be added without touching a signature. A store that can do IMAP `BODYSTRUCTURE` +
partial fetch, or JMAP `bodyValues`, opts in; a simple store returns full and leaves the flags
defaulted. The genuinely wasteful case — large attachments — is already wasteful today, and EAS
fetches attachments separately via ItemOperations/GetAttachment regardless.

**4. `ItemRevision` is fully opaque; the read-only sentinel moves out of the value space.**
Verified: nothing in the host parses revision structure. The only host semantics attached to the
value is `ReadOnlyRevertRevision = "!ro"`, used at five sites, all in `SyncHandler` — written into
the snapshot and compared, never decomposed. Mail's `"101|kw1,kw2"` encoding is constructed and
consumed entirely within the mail store. Consequence: a forced resync when the snapshot shape
changes — taken deliberately in Phase 2, where the avoidable-alternative serialization is also
recorded.

**5. The payload cache is per-session, count-bounded, and small.** The sizing concern largely
dissolves on inspection: the cache is only needed where a partial update merges **into a payload**,
and mail `Change`s are flags and categories, which live *outside* the RFC822 — so mail never needs
its cached payload to merge. What the cache actually holds is iCalendar and vCard strings:
kilobytes, not megabytes. Therefore an LRU bounded by entry count (~256 is generous) hung off
`CompositeBackendSession`.

- **Per session, never global.** These are decrypted user payloads — for local stores, literally
  the plaintext of the AES-GCM-sealed `LocalItem.Content` rows. A global cache would be a
  cross-user disclosure surface for no benefit.
- **Per-(user, device), which the session already is** — correct, because the merge is driven by
  what *that device* was sent and at which revision.
- It inherits `BackendSessionFactory`'s existing idle eviction, so there is no new lifetime
  machinery to get wrong.

`NoteItem` needs no cache: it is typed and backed by a local DB row, so a partial note update reads
the current note and merges directly. That is cheaper than introducing `Optional<T>` for one class.

**6. One interop package, not one per format** (§ 5.6). Optionality is what preserves role
isolation, so the granularity within the opt-in is worth trading for a single artifact to version,
publish, document and test.

**7. The interop package takes the release-tag version**, unlike Contracts and Protocol, which pin
their own. There is no independent version source worth hand-maintaining, and tying it to the
release makes the package *self-documenting*: because package versions are centralised in
`Directory.Packages.props`, interop `X.Y.Z` carries precisely the MimeKit / Ical.Net /
FolkerKinzel.VCards versions gateway `X.Y.Z` was built against. Accepted cost: a republish on every
tag even when nothing changed. Note this does **not** weaken the § 5.6.1 argument — what must stay
decoupled from third-party cadence is `$(ContractVersion)`, which the loader gates on; the interop
package is not loader ABI, so its version is free to track anything. Its *dependency* on
Contracts, however, is an exact `[X.Y.Z]` pin (§ 5.6), because contract minor is breaking.

The following were settled by the second review pass (2026-07-29); evidence inline where it
decided the call.

**8. Mail gets its own store interface — it is NOT `IContentStore<MailItem>`** (§ 5.4, § 6.2).
Decisive evidence: `ImapMailBackend.UpdateItemAsync` (`ImapMailBackend.cs:245-298`) shows the
everyday mail Change is a presence-guarded flags/categories patch that never touches the RFC822,
and the only content write — the 16.x draft rewrite (`:210-242`) — is delete+append, which
**changes the item key**, something a generic `UpdateItemAsync` returning one revision cannot
report. `IMailStore` therefore carries `UpdateFlagsAsync(MailFlagsPatch)` (presence via
`Optional<T>`) and `CreateDraftAsync`/`ReplaceDraftAsync` (key-change explicit). A full-`MailItem`
update method would have forced the host to materialise RFC822 bytes to mark a message read.

**9. GAL photo limits are enforced by the store, reported with a typed status** (§ 5.8).
Host-side enforcement would either waste backend photo fetches past `MaxCount` or make statuses
174/175 unimplementable behind a bare `GalPicture?` — a null cannot say *why* there is no
picture. `GalPictureResult { Status, Picture? }` keeps today's bandwidth behaviour and gives the
host exactly what the MS-ASCMD statuses need.

**10. `IMailStoreOperations.GetAttachmentAsync` is deleted, not retyped** (§ 5.8). Both in-repo
implementations already fetch the full raw message and extract the part with MimeKit
(`ImapMailBackend.cs:495-511`, `JmapMailStore.Attachments.cs:24-29`), so host-side extraction
over `GetRawMessageAsync` is behaviour- and cost-identical — and it removes an attachment index
whose meaning was "MimeKit's enumeration order", out-of-band knowledge a MimeKit-free plugin
could not reproduce.

**11. Instants are `DateTimeOffset`, contract-wide** (principle 8, Phase 1). The alternative —
new fields on the new convention beside retained UTC `DateTime`s — would ship a permanently
mixed API. Standardised in one pass while everything is being retyped anyway; the `Utc` name
suffix goes with it.

**12. The side-operation interfaces are renamed** (resolves former open question 2):
`IMailboxOperations`, `IMeetingOperations`, `IDirectoryOperations` (§ 5.8). `IMailStore` vs
`IMailStoreOperations` was a genuine collision; the store aliases keep the obvious names because
they are what every plugin author meets first.

**13. `TransientRetry` stays in Contracts** (resolves former open question 3). Moving it to the
interop package would force an HTTP-backend author to swallow MimeKit + Ical.Net +
FolkerKinzel.VCards for a retry helper — the exact coupling § 5.6 exists to avoid. It is
BCL-only, small, already published, and already maintained as contract surface (the
`ImmutableArray` hardening of `DelaysMs` was a deliberate contract fix). Kept as a deliberate
convenience, documented as such.

**14. Version with minor bumps only; the owner raises 2.0 manually at the end** (decided by the
owner, 2026-07-29 — resolves former open question 1, the last one). Every phase raises
`ContractVersionMinor` per the § 9 preamble; **no part of this plan touches
`ContractVersionMajor`**, whatever an implementing agent's instincts say about "a redesign this
large deserves a major". The owner will raise it to 2.0 themselves, once satisfied with the
implemented result — which also preserves the "not ABI-stable pre-2.0 / stable from 2.0" story:
2.0 is declared over the *finished, verified* surface, not over the plan.

## 11. Still open

Nothing. Every open question of the earlier drafts is resolved and recorded in § 10
(decisions 12–14 cover the last three). The one act reserved for the owner — raising the
contract major to 2.0 — is not an open design question but a deliberate final step, specified in
decision 14.

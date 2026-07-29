# Writing a backend plugin

The gateway's backends are **providers**: named implementations that fill one or more
**roles** (`MailStore`, `MailSubmit`, `Calendar`, `Tasks`, `Contacts`, `Notes`, `Oof`).
The in-repo providers (`imap`, `jmap`, `smtp`, `caldav`, `carddav`, `sieve`, `local`) are
ordinary providers registered at startup; an **out-of-repo plugin** is the same thing
shipped as a separate assembly the gateway loads from a directory. Nothing about a plugin
provider is second-class — config assigns it to a role by name exactly like a built-in.

Two references worth opening beside this page:

- `tests/ActiveSync.TestPlugin` — the smallest COMPLETE plugin: an entry point, a provider and
  a working `INotesStore`, built against `ActiveSync.Contracts` and nothing else.
- `src/ActiveSync.Backends.Jmap` — a multi-role HTTP backend (mail, calendar, contacts,
  submission and out-of-office over one session), for the shape of a real one.

## The contract

Reference **one** NuGet package (published to GitHub Packages, and nuget.org when
configured) — its version is the *contract* version, which moves independently of the
gateway's release version (see *Versioning* below):

- **`ActiveSync.Contracts`** — the whole plugin contract: `IBackendProvider`, the content
  stores, `IGatewayPlugin`, the roles, provider settings and the config schema. It depends on
  the Microsoft.Extensions configuration/DI abstractions and nothing else — no Core, no
  Crypto, no EF Core, and no MIME/iCalendar/vCard library.

That is the entire required surface. `ActiveSync.Protocol` used to be published beside it and
is **not** any more: no EAS wire encoding crosses the store boundary, so there is nothing in it
for a plugin to reference. The gateway's other assemblies (`ActiveSync.Core`,
`ActiveSync.Crypto`, `ActiveSync.Backends.Common`, `ActiveSync.Eas.Conversion`) are host
implementation detail and are not published either.

Two **optional** packages sit beside the contract. Neither is needed to write a plugin:

| Package | What it gives you |
|---|---|
| `ActiveSync.Contracts.Interop` | Converts the payload records to and from MimeKit / Ical.Net / FolkerKinzel.VCards, plus sample payload builders for tests. Opting in pulls those three libraries. |
| `ActiveSync.Contracts.Conformance` | Runs your store against the obligations this page states in prose and returns a report. References only `ActiveSync.Contracts` — no test framework, no domain library. |

Both track the **gateway release version** rather than the contract version, and each pins the
contract it was built against as an exact dependency (`[1.8.0]`). Pick the release that shipped
your contract version and the versions line up by construction. Neither is part of the
loader's compatibility gate, so a MimeKit or Ical.Net major bump can move them without refusing
a single plugin.

A plugin assembly contains:

1. One or more **`IBackendProvider`** implementations — the actual backend.
2. One **`IGatewayPlugin`** implementation — the entry point that registers them.

```csharp
using ActiveSync.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

[assembly: SupportedGatewayContract(1, 8)]   // see Versioning; mandatory

public sealed class MyPlugin : IGatewayPlugin
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        // Register like an in-repo provider; anything it depends on can be registered too.
        services.AddSingleton<IBackendProvider, MyNotesProvider>();
    }
}

public sealed class MyNotesProvider : IBackendProvider
{
    public string Name => "my-notes";                       // what config's Provider names
    public IReadOnlySet<BackendRole> SupportedRoles { get; } =
        new HashSet<BackendRole> { BackendRole.Notes };

    public void ValidateConfiguration(BackendRole role, ProviderSettings settings, IList<string> failures)
    {
        // Bind your OWN options type — the host never knows its shape.
        MyOptions options = settings.Bind<MyOptions>();
        if (string.IsNullOrWhiteSpace(options.Endpoint))
            failures.Add($"my-notes ({role}): Endpoint is required.");
    }

    public string DescribeRole(BackendRole role, ProviderSettings settings) =>
        $"my-notes {settings.Bind<MyOptions>().Endpoint}";   // one redacted banner line, no secrets

    public Task<IBackendConnection> CreateConnectionAsync(BackendConnectionContext context, CancellationToken ct)
    {
        // Build one content store per content role assigned to you, over one connection.
        // context.GatewayUserId is the IDENTITY (DB scoping, durable keys); each role's
        // Credentials are what to present to the backend. Async so you can open a transport
        // (a TCP/TLS connect, an auth round-trip) here; return Task.FromResult if you don't need to.
        MyOptions options = context.Roles[0].Settings.Bind<MyOptions>();
        return Task.FromResult<IBackendConnection>(
            new BackendConnection([new MyNotesStore(options, context.Roles[0].Credentials)]));
    }
}
```

## What a store trades in

**No EAS XML crosses the store boundary.** A store hands over the payload in a standard
interchange format, or a typed record where no standard exists, and the host does every EAS
conversion itself:

| Class | Store interface | Payload |
|---|---|---|
| Mail | `IMailStore` | `MailItem` — raw RFC822 bytes plus `MailFlags`, categories, the backend's received timestamp |
| Calendar | `ICalendarStore` | `CalendarItem` — an iCalendar `VEVENT` document |
| Tasks | `ITaskStore` | `TaskItem` — an iCalendar `VTODO` document |
| Contacts | `IContactStore` | `ContactItem` — a vCard document |
| Notes | `INotesStore` | `NoteItem` — typed (subject, body, categories): there is no accepted notes standard, and notes should be the easiest plugin to write |

A store implements **exactly one** of those five. The host derives the store's content class
from which one it implements — implement two, or none, and the session build rejects it.

Consequences worth stating plainly, because they remove work rather than add it:

- **You never see a partial update.** EAS 16.x sends only changed elements, but the host reads
  the current payload, merges the client's partial data onto it, and hands you a COMPLETE
  payload. There is no ghosting to model, no "was this field sent or cleared?" question.
- **You never truncate.** Body preferences are an EAS presentation concern applied host-side.
  Return the whole thing.
- **What you store is what you return.** The contract carries the payload precisely so
  properties EAS cannot express survive an edit. Store the document you are given.
- **Buffer ownership**: memory crossing the boundary in either direction must stay valid and
  unchanged indefinitely — the host caches payloads. If you pool buffers, copy before returning.

The four payload classes share one generic shape:

```csharp
public interface IContentStore<TItem> : IContentStore where TItem : class
{
    Task<TItem?> GetItemAsync(FolderKey folder, ItemKey item, CancellationToken ct);
    Task<IReadOnlyDictionary<ItemKey, TItem?>> GetItemsAsync(               // default impl loops the above
        FolderKey folder, IReadOnlyList<ItemKey> items, CancellationToken ct);
    Task<(ItemKey Key, ItemRevision Revision)> CreateItemAsync(
        FolderKey folder, TItem item, CancellationToken ct);
    Task<ItemRevision> UpdateItemAsync(
        FolderKey folder, ItemKey item, TItem value, ItemRevision? expected, CancellationToken ct);
}
```

**Mail is deliberately not that shape.** Its everyday write is a flags/categories patch that
never rewrites the message, and its only content write — the 16.x draft rewrite — can change
the item key (IMAP does it as delete + append). So `IMailStore` carries `UpdateFlagsAsync`
(taking a `MailFlagsPatch`, whose `Optional<T>` fields carry "the client sent this" on the
value), plus `CreateDraftAsync` and `ReplaceDraftAsync`, whose return value may report a moved
key.

### Keys, revisions and the diff

`FolderKey`, `ItemKey` and `ItemRevision` are single-value wrappers over a string, opaque to
the host. The rules behind them are what the sync engine depends on:

- **`OwnsKey(FolderKey)`** must claim a key space disjoint from every other store in a session
  (the built-ins use `imap:`, `caldav:`, `caldav-tasks:`, `carddav:`, `local:` — pick your own
  prefix). The session dispatches to the first store that claims a key.
- **`GetItemRevisionsAsync` enumerates the WHOLE collection.** The engine diffs that map
  against its snapshot, so an item missing from the map reads as a deletion. There is no
  incremental-delta path in the contract; a state token is only ever a change *sentinel* for
  the wait.
- **A revision must be stable while the item is unchanged**, and must change when it changes.
  Anything else re-sends every item to every device on every sync round. It is otherwise
  entirely yours: an ETag, a flags hash, a row version.
- **`null` from a fetch means "not fetched", never "gone"**: the engine skips the item and
  does NOT advance its snapshot, so it retries next round. That is what makes a transient
  backend failure cost one item and one round rather than a lost message.
- **`expected` on an update is an upgrade, not an obligation.** A store that can check it
  (DAV `If-Match`, JMAP `ifInState`, a row version) throws
  `BackendPreconditionFailedException` on a mismatch and the host re-fetches, re-merges and
  retries once. A store that cannot check it ignores the parameter and writes. Both conform.

### Optional capabilities

Capabilities are extra interfaces your store or provider may also implement; the host
`is`-tests for them. Implement only what applies — the command answers gracefully when you do
not.

On a **store**: `IItemMoveOperations` (MoveItems), `IFolderOperations` (client folder
create/rename/delete), `IFreeBusySource` (availability — `null` means "no data", an empty list
means "free"), `ICalendarAttachmentSource` (inline event attachments, indexed by position among
the payload's own `ATTACH` properties), `IReadOnlyCollectionSource` (shared collections the
engine silently reverts writes to).

On a **provider**: `ICredentialVerifier` (the auth probe — required if you serve `MailStore`
for pass-through logins), `IPerUserResourceOwner` (trim per-user caches on session eviction),
`IReadinessSource` (a `/readyz` probe), `IWatcherDiagnostics` (live push watchers for the admin
dashboard).

Beside the stores, a connection may expose the side operations: `IMailboxOperations` (empty
folder, save to Sent, mark answered, fetch a raw message, search), `IMailSubmitOperations`
(send RFC822), `IMeetingOperations` (respond to a meeting; whether the backend already sends
invitations itself), `IDirectoryOperations` (GAL search, returning typed `GalEntry` records
whose photo result carries a status rather than a bare nullable) and `IOofBackend`.

### Key rules

- **`Name`** is the discriminator config uses (`"Provider": "my-notes"`). It must be unique
  across all providers — a collision fails startup.
- **Bind your own options** from `ProviderSettings` inside your provider. The host passes the
  raw role section through; it deliberately cannot see your option type. That is what lets a
  plugin carry configuration the host was never compiled against.
- **The identity is `context.GatewayUserId`** — an immutable, never-reused integer. Every
  durable per-user thing the gateway keys on derives from it. The gateway *login* is a mutable
  attribute (renaming it leaves sync state attached), so never use it, or a per-backend user
  name, as a durable key. It is the right key only for ephemeral credential-bearing caches.
- **Throw `BackendException`** (or your own subclass of it) for backend failures, and
  `BackendItemNotFoundException` when the object is gone. The host funnels those; anything else
  reads as a bug.

## Testing your store

`ActiveSync.Contracts.Conformance` exercises the obligations above — the ones the type system
cannot state — and returns a report you can assert on from any test framework:

```csharp
ConformanceReport report = await StoreConformance.RunAsync(store, new ConformanceOptions
{
    Folder = new FolderKey("my-notes:default"),   // null = the first folder the store lists
    AllowMutation = true                          // it creates, updates and deletes one item
});

Assert.True(report.Passed, report.ToString());
```

It checks folder listing and key-space disjointness, revision stability across unchanged
enumerations, create-then-list visibility, the batch fetch's `null` semantics, update and
delete semantics, the wait timeout, payload round-trip fidelity, and the update precondition
where you implement it. A check that does not apply is reported as **Skipped**, which is not a
failure: the contract has genuine "a store that cannot do this still conforms" clauses.

What it deliberately does not cover is engine behaviour — SyncKey replay, windowing, echo
suppression. Those belong to the gateway, not to your store; you can neither pass nor fail
them.

## Configuration

A plugin provider is assigned to a role like any other, by name:

```json
"ActiveSync": {
  "Backends": {
    "Notes": { "Provider": "my-notes", "Endpoint": "https://notes.example.com" }
  }
}
```

`Endpoint` (and anything else in the section) is bound by your provider, not the host.

### Describing your settings

Override `DescribeConfiguration(role)` and the web UI renders a real form for your provider —
labelled inputs, dropdowns for enums, defaults as placeholders, per-field validation — in the
admin Backends page, the per-user override editor and the user portal alike. The host still
binds nothing: it only knows the shapes you declare.

```csharp
public IReadOnlyList<BackendConfigField> DescribeConfiguration(BackendRole role) =>
[
    new BackendConfigField("Endpoint", "Endpoint", BackendFieldType.Url, Required: true,
        Help: "Absolute https URL of the notes service."),
    new BackendConfigField("Mode", "Sync mode", BackendFieldType.Enum, Default: "Auto",
        EnumValues: ["Auto", "Push", "Poll"], SelfServiceEditable: true)
];
```

Field types: `String`, `Int` (with optional `Min`/`Max`), `Bool`, `Enum` (with `EnumValues`),
`Url`, `Secret` (masked, never echoed back) and `StringList` (repeated `Name:0`, `Name:1` keys —
give the list root as `Name`). `Default` must be the string form of your options class's own
default, since it is what the UI shows as the dimmed placeholder.

**What `Secret` does and does not do.** A `Secret` field is masked in every editor, never echoed
back in an API response, and redacted from logs and the startup banner — that is the whole of what
the type governs. It is **not** at-rest field encryption. The one secret the gateway seals in its
state database is the role's own credential: you declare it through the host-reserved `Password`
per-user key (not a `Secret` field of your own), and the host seals it on write and **unseals it
before your provider sees it**, handing it to you in plaintext as
`context.Roles[…].Credentials.Password`. Any other `Secret` field you declare is stored as entered
and bound to your options in plaintext. If your provider needs to seal an *additional* secret of its
own at rest, bring your own primitive (e.g. `System.Security.Cryptography.AesGcm` under a key from
your own settings) — Contracts carries no crypto on purpose, and the gateway's internal sealing
assembly is not published.

`SelfServiceEditable` decides whether a **non-admin** account holder may set the field for their
own account in the user portal. It defaults to `false`, so your provider is administration-only
until you opt a field in — the safe default, because the gateway presents the role's stored
credential to whatever the connection settings point at, and a portal user is the lowest
privilege level in the system. Opt in preferences; never a host, URL, port, path template or
certificate-trust knob. The portal's form is built from the opted-in fields alone, and a save
carrying anything else is refused with 400 (settings an administrator set on the account are
preserved untouched across such a save).

The method has a default implementation returning nothing, so an older plugin keeps compiling
and working — its settings simply stay raw key/value rows in the UI. Describing only part of
your surface is fine too: undescribed keys remain editable **in the admin editor** and are never
dropped on save. They are never editable from the user portal, which accepts described,
self-service fields only.
`ValidateConfiguration` is still where semantic checks belong; the schema covers shape only.

## Packaging and deployment

Build your plugin as a normal class library targeting the same framework as the gateway
(`net10.0`). The gateway loads plugins from a **plugins directory**
(`ActiveSync:Plugins:Directory`, default `/app/plugins` in the container image): one
**subdirectory per plugin**, whose entry assembly is named after the subdirectory, with
any private dependencies beside it.

```
/app/plugins/
  my-notes/
    my-notes.dll                        <- entry assembly (matches the directory name)
    ActiveSync.Contracts.Interop.dll    <- SHIP this one if you use it (see below)
    MimeKit.dll                         <- and the libraries it needs
    SomePrivateDep.dll                  <- your other private dependencies
```

Do **not** ship a copy of `ActiveSync.Contracts` or the framework — the loader resolves those
from the host so your types unify with the gateway's (a private copy would make
`IBackendProvider` a different type and the provider would be ignored). Mark that package
reference `<Private>false</Private>` (or `ExcludeAssets="runtime"`).

**`ActiveSync.Contracts.Interop` inverts that rule: ship it.** It is an ordinary private
dependency despite the name — the loader shares the contract assembly by exact name, not by
prefix, precisely so your copy of the interop package wins. If the host's copy were used
instead, it would bind the *host's* MimeKit and Ical.Net while your code binds yours, and
passing a `MimeMessage` across that line fails on type identity. The same goes for
`ActiveSync.Contracts.Conformance`, though that one normally lives in your test project rather
than your plugin folder.

Prefer `dotnet publish` for the plugin folder, so your dependencies actually travel with it. A
plugin built against MimeKit 4.17 that ships no copy silently binds to whatever the host has.

Two ways to get the directory populated:

- **Derived image** (immutable, the documented default):

  ```dockerfile
  FROM ghcr.io/sterion/active-sync-gateway:latest
  COPY my-notes/ /app/plugins/my-notes/
  ```

- **Volume mount** (update without rebuilding): mount your plugin into `/app/plugins`
  (a k8s `volume` / initContainer that drops the DLLs, or `-v ./plugins:/app/plugins`).

The image is **multi-arch** (`linux/amd64` + `linux/arm64`). A pure-managed plugin runs on
both as-is; a plugin with a native dependency must ship both RIDs.

## Loading behavior

- Each plugin loads in its own `AssemblyLoadContext`. Exactly one gateway assembly is shared
  with the host — **`ActiveSync.Contracts`**, matched by exact name — along with the framework
  and `Microsoft.Extensions.*`, because those types appear in the contract's own signatures.
  **Everything else resolves from your plugin folder first**, so a library you pinned is the one
  you get even when the gateway ships a different version of it; if the folder does not have it,
  the host's copy is the fallback. Both a `dotnet publish` layout (with `.deps.json`) and a
  hand-assembled drop of DLLs work.
- Loading is **fail-fast**: a corrupt/incompatible plugin, a subdirectory whose entry
  assembly is missing, or an assembly with no public `IGatewayPlugin` aborts startup rather
  than silently degrading a role that config assigned to it. An absent or empty plugins
  directory is a no-op, and directories whose name starts with `.` are ignored (so a
  Kubernetes projected volume's `..data` is not mistaken for a half-copied plugin).
- Every assembly in the folder is checked against the host's contract version, not just the
  entry one — so a bundled helper built against a different contract is refused at startup
  instead of failing deep inside a sync.
- Each loaded provider appears on the startup banner via its role line.

### The load context is not a sandbox

The per-plugin `AssemblyLoadContext` is **dependency isolation, not a security boundary**.
A plugin runs in the gateway process with exactly the gateway's rights: it can read the
master key, open sockets, touch the database and replace host service registrations through
the `IServiceCollection` it is handed. Installing a plugin is equivalent to installing a
different build of the gateway — treat it as code you own or code you have reviewed.

What the loader can do is refuse to load bytes you did not review, which is why plugin
settings are **host-controlled** (file or environment only — never the database or the admin
UI; see [configuration.md](configuration.md)) and why a plugin can be pinned:

| Option | Default | Description |
|--------|---------|-------------|
| `Plugins:Pins:<dirname>` | *(none)* | Expected SHA-256 digest of the plugin directory — every regular file beneath it, path and contents (not just `*.dll`: native `.so`/`.dylib` payloads and `.deps.json` are covered too). A mismatch aborts startup. |
| `Plugins:RequirePinned` | `false` | Refuse to load any plugin that has no pin. |

To pin a plugin, set the pin to any placeholder and start the gateway once: the failure
message reports the digest it actually computed, so you can review the directory and paste
the value in.

```json
"ActiveSync": {
  "Plugins": {
    "RequirePinned": true,
    "Pins": { "my-notes": "9f2c…" }
  }
}
```

A pin proves the directory is byte-for-byte what you reviewed. It is not a signature — it
carries no identity and no revocation — and it says nothing about what the code does once
loaded.

## Versioning

The backend contract is **not ABI-stable before 2.0** — the stores and their neighbours still
evolve with new EAS features.

**Declare the contract you support.** Every plugin entry assembly must carry:

```csharp
[assembly: SupportedGatewayContract(1, 8)] // must equal ActiveSync.Contracts.ContractVersion.Major/Minor
```

The loader reads that declaration from metadata *before loading anything* and refuses the
plugin unless it matches the host exactly. It is a declaration rather than an inference on
purpose: your plugin's own version is your business (a plugin at 3.7.2 may support contract
1.0), and the package version you happened to compile against says nothing about which
contract you actually verified against. Only you know that.

**Both components are breaking.** Major *and* minor must match — a plugin declaring 1.7 will
not load on a 1.8 host. That is deliberate while the contract is pre-2.0: it lets an
incompatible change ship as a minor bump instead of inflating the major into a meaningless
counter. The patch component is not part of the declaration and never gates anything.

**The contract version is not the gateway version.** It is the version of
`ActiveSync.Contracts` alone, and it moves only when that surface changes, so it stays put
across ordinary gateway releases — a gateway released as 1.5.0, or even 2.0.0, still runs a
plugin declaring contract 1.0, as long as the surface itself did not change. Track the
contract, not the release. You can read the host's value at runtime as
`ActiveSync.Contracts.ContractVersion.Current` (`Major.Minor`).

The optional packages are the other way round: they carry the release version and pin their
contract exactly, so `ActiveSync.Contracts.Interop 1.6.0` depends on `[1.8.0]` of the contract
and simply will not restore beside a different one. That is intentional — a floor range would
promise a compatibility that contract minors do not have.

When the contract does move, rebuild against the new package and update your declaration; the
loader's error message names both versions.

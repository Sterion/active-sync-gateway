# AGENTS.md — ActiveSync Gateway

Guidance for AI agents (and new contributors) working in this repository. Read this before
changing code; it captures the architecture, invariants, and gotchas that are not obvious
from the file tree.

## What this project is

A **.NET 10** service that speaks **Microsoft Exchange ActiveSync (EAS) 14.1** to mail
clients (iOS Mail, Android, Outlook) and translates every operation to standard backends
hosted elsewhere: **IMAP** (mail), **SMTP** (send), **CalDAV** (calendar), **CardDAV**
(contacts). Functionally equivalent to Z-Push (PHP), implemented from Microsoft's open
specifications.

**Deliberately out of scope** (do not add without explicit request): full-device
RemoteWipe (never add a code path that can factory-reset a phone — the 16.1 account-only
wipe and `eas block` are the levers), S/MIME (`ValidateCert`), the SMS class. (Notes
**is** implemented — a local-only `LocalNotesStore` — see the Backend layer notes.)

**EAS 16.1 is implemented** (see docs/eas16-checklist.md for the audited token diff and
per-delta status). Invariants: version gating rides `BodyPreference.Eas16` (set from
`context.Version >= EasVersion.V160`) so store signatures stay version-free; 14.1
responses must stay byte-identical (a 14.1 observer device asserts this in Eas16Tests).
calendar:Location is ≤14.1-only — 16.x emits/reads airsyncbase:Location(DisplayName);
exception dates MERGE (never clear) because 16.x sends only the new exception; Sync
Delete + airsyncbase:InstanceId becomes a synthesized deleted exception through the
normal partial-merge path. Drafts: Sync Add/Change of Email is allowed ONLY in the
Drafts folder (`DraftMessageBuilder` merges fields; a rewrite changes the IMAP UID and
the snapshot diff re-identifies as Delete+Add); email2:Send submits instead of storing.
Event attachments live INLINE in the iCal (base64 ATTACH; `Dav:CalendarAttachments`
Auto/On/Off caps them) with FileReferences "calatt::<serverId>::<index>" — the converter
emits the index and SyncHandler stamps the ServerId; ItemOperations resolves them via
`ICalendarAttachmentSource`. Account-only wipe: `Device.PendingAccountWipe` → 449 herds
into Provision → directive → ack auto-blocks the partnership (`CompleteAccountWipeAsync`);
MeetingResponse tolerates 16.x SendResponse/proposals but always sends the iTIP reply
(pre-16 behavior).

**Device security policies** (MS-ASPROV) are implemented via `ActiveSync:Policy`
(`PolicyOptions`, EAS element names 1:1). Off by default — `Provision` then hands out an
empty policy so iOS/Outlook proceed (Z-Push `LOOSE_PROVISIONING` equivalent). When
enabled: `PolicyDocument.Build/Hash` produce the `EASProvisionDoc` and its SHA-256; the
endpoint gate in `EasEndpoint` answers **HTTP 449** to any non-Provision command unless
the presented policy key (base64 query field or `X-MS-PolicyKey` header) matches
`Device.PolicyKey` AND `Device.PolicyDocHash` matches the current doc hash — stamped only
in the acknowledging Provision phase 2, so any config change re-provisions the fleet
automatically. Compliance is device-asserted (like Exchange), not attestation.
Settings→DevicePassword escrows the recovery password (sealed via `LocalContentProtector`,
AAD `user + "recovery:" + deviceId`) only when `PasswordRecoveryEnabled`; read it back
with `eas device password`.

**Out-of-office** (Settings→Oof) is backed by **ManageSieve** when `ActiveSync:Sieve` is
enabled (off = the historical accept-and-ignore stub). The handler is backend-agnostic:
`IOofBackend.EnableAsync(OofReply)` returns an opaque restore token (null = re-arm, keep
the stored token) and the backend renders its own rule — `SieveVacationScript` (script
name "eas-gateway") lives entirely inside `SieveOofBackend`. Invariants: the
`OofSettings` DB row is the source of truth for Get — the script is derived output and
never parsed back; Set hits the backend FIRST, then the DB (a failed arm must not leave
the phone believing Oof is armed; BackendException → Oof Status 4); the restore token
(previously active script name) is stored on the row and restored on disable; one reply
body for all three audiences. `ManageSieveClient` is minimal RFC 5804
(STARTTLS + AUTH PLAIN, no pooling — one connection per operation), wire-logged at Trace
as `ActiveSync.Backends.Sieve` with the AUTHENTICATE line always masked.

**Free/busy** (ResolveRecipients Availability): calendar stores implement
`IFreeBusySource.GetBusyPeriodsAsync` — null means "no data for that target" (per-recipient
Availability Status 163), an EMPTY list means "completely free"; keep that distinction.
`MergedFreeBusy.Build` (Core) turns busy periods into the spec digit string (30-minute
intervals, overlap marks, higher digit wins). CalDAV free/busy is a hand-parsed
free-busy-query — **Ical.Net 5.x cannot deserialize the FREEBUSY property** (comes back
null), so `CalendarConverter.ParseFreeBusy` parses the unfolded lines itself; don't
"simplify" it back to Ical.Net without checking that bug is fixed. A free/busy failure
must never fail the whole ResolveRecipients.

**GAL photos**: `SearchGalAsync` takes an optional `GalPhotoRequest(MaxSizeBytes, MaxCount)`;
`ContactConverter.AppendGalPicture` implements the MS-ASCMD statuses (1+Data / 173 no
photo / 174 over MaxSize / 175 count limit) and the stores count granted photos across
the whole result set. ResolveRecipients translates the gal:Picture element into its own
RR-namespace Picture shape — keep the two in sync.

**Autodiscover** is implemented (`AutodiscoverEndpoint`): `POST /autodiscover/autodiscover.xml`
(and `.json`) returns the EAS URL in the Outlook MobileSync schema. It shares Basic-auth
handling with the EAS endpoint via `HttpBasicAuth` — reuse that helper for any new
authenticated endpoint rather than re-parsing the header.

## Commands

```powershell
dotnet build ActiveSync.slnx                 # build everything (note: .slnx, not .sln)
dotnet test  ActiveSync.slnx                 # run all unit tests (must stay green)
dotnet run --project src/ActiveSync.Server   # run the service (listens on :5080)
docker build -f docker/Dockerfile .          # container build
docker compose -f docker/backends/stalwart/docker-compose.yml up -d  # default test backend
docker compose -f docker/docker-compose.ci.yml run --rm tests        # full suite, CI-style
```

- The solution file is **`ActiveSync.slnx`** (new SDK format). `dotnet build` without an
  argument works from the repo root; `ActiveSync.sln` does not exist.
- **`nuget.config` pins nuget.org as the only source.** Developer machines may have
  private feeds configured globally that 401 on public packages — do not remove this file.
- **NuGet package versions are centralized in `Directory.Packages.props`**
  (`ManagePackageVersionsCentrally`). Every csproj's `<PackageReference Include="...">` must
  have **no** `Version` attribute — add/bump versions only in `Directory.Packages.props`.
  The one exception is `Microsoft.VisualStudio.Threading.Analyzers`, which is a
  `<GlobalPackageReference>` there (applies to every project automatically) rather than a
  per-project `PackageReference` — don't re-add it to individual csproj files.
- Quick smoke test after server changes:
  `curl -i -X OPTIONS http://localhost:5080/Microsoft-Server-ActiveSync`
  must return `MS-ASProtocolVersions: 2.5,12.0,12.1,14.0,14.1`.

## Solution layout and dependency rule

```
src/ActiveSync.Protocol/    WBXML codec, code pages, MS-ASHTTP query parser, EAS constants.
                            Depends on NOTHING project-wise. No ASP.NET, no MailKit.
src/ActiveSync.Core/        Backend interfaces, EF Core state store, diff engine, options.
                            Depends on Protocol only (+ EF Core packages).
src/ActiveSync.Backends/    IMAP/SMTP (MailKit), CalDAV/CardDAV (in-house WebDAV client),
                            MIME/iCal/vCard ⇄ EAS converters. Depends on Core.
src/ActiveSync.Server/      Kestrel host, /Microsoft-Server-ActiveSync endpoint, Basic auth,
                            one IEasCommandHandler per EAS command. Depends on Backends.
tests/ActiveSync.Protocol.Tests/   WBXML round-trip + query parser tests
tests/ActiveSync.Core.Tests/       diff engine, sync-key state machine, options validator,
                                   AND the Backends unit tests (converters, cert validator,
                                   WebDAV redirect safety) — there is no Backends.Tests project,
                                   so Core.Tests references Backends and hosts them.
tests/ActiveSync.Server.Tests/     handler-level tests (has InternalsVisibleTo into Server)
tests/ActiveSync.Integration.Tests/  real-backend E2E tests (see "Integration tests" below)
```

Keep the dependency direction strict: `Protocol ← Core ← Backends ← Server`. Converters
live in Backends (they need MimeKit/Ical.Net/FolkerKinzel), never in Protocol.

## Coding conventions

- **Async end-to-end is a hard rule.** Every I/O path is `async`/`await` with a
  `CancellationToken` parameter, flowing from `HttpContext.RequestAborted`. The
  `.editorconfig` sets **VSTHRD002 and VSTHRD103 to `error`** — `.Result`, `.Wait()`,
  and sync-over-async will fail the build. Library code uses `.ConfigureAwait(false)`;
  ASP.NET handler code does not need it.
- **House style is tabs + CRLF** (`.editorconfig` + `.gitattributes`). New files follow it.
  Historically most `.cs` files were written 4-space/LF; those are **not** bulk-reformatted
  (blame preservation), so you will see both — match the file you are editing, and use
  tabs+CRLF for brand-new files.
- Nullable reference types are enabled everywhere; the build must stay at **0 warnings**.
- Long-poll code (Ping, Sync with Wait) must never block a thread: use `Task.WhenAny`
  over pollers plus `Task.Delay`, and always cancel losers via a linked
  `CancellationTokenSource`.
- MailKit's `ImapClient` is **not thread-safe**: all IMAP access goes through
  `ImapSession.RunAsync`, which serializes with a `SemaphoreSlim` and reconnects dropped
  connections. Never touch a client outside `RunAsync`. Hold the semaphore only around
  each protocol exchange — a Ping poll loop must release it between iterations so a
  concurrent Sync on the same session can interleave.

## Protocol layer invariants (read before touching Protocol/)

- **WBXML token tables** in `WbxmlCodePages.cs` are transcribed from **MS-ASWBXML**. If a
  decode fails with "unknown tag token", the table is wrong or incomplete — verify against
  the spec (or Z-Push's `wbxmldefs.php`), never guess. A historical bug here: AirSyncBase
  was off by one because `FileReference` (0x11) was missing. Every table change needs a
  round-trip test.
- XML representation: element **namespaces are the EAS code page names** ("AirSync",
  "FolderHierarchy", …) exactly as in the MS-AS* spec examples.
- **OPAQUE data** (raw bytes, e.g. SendMail MIME, ConversationId): the element's text is
  base64 and it carries the marker attribute `EasNamespaces.OpaqueAttribute` = "1". The
  encoder emits OPAQUE for marked elements; the decoder sets the marker. Anything reading
  `Mime` or writing binary values must honor this convention.
- EAS 12.1+ clients send the query string as a **packed base64 blob**, not `?Cmd=...`.
  Parsing lives in `EasRequestParameters.FromBase64` (MS-ASHTTP 2.2.1.1.1.1). Binary
  DeviceIds (GUID bytes) are hex-encoded to strings.
- Relevant specs: MS-ASHTTP (transport), MS-ASWBXML (encoding), MS-ASCMD (commands),
  MS-ASAIRS (Body/Attachments), MS-ASEMAIL, MS-ASCAL, MS-ASCNTC, MS-ASDTYPE (dates),
  MS-ASTZ (timezone blob), MS-ASPROV (provisioning). All on Microsoft Learn open specs.
  Z-Push (https://github.com/Z-Hub/Z-Push) is the behavioral reference — consult it for
  semantics, but **do not port code** (AGPLv3).

## Sync model (the heart of the service)

- **Differential sync, Z-Push style.** No CONDSTORE/QRESYNC/sync-collection dependency.
  Each collection stores a snapshot `itemKey → revision`; each Sync round fetches the
  backend's current revision map and diffs (`CollectionDiff.Compute`):
  - Mail: itemKey = IMAP UID, revision = flags string (`seen|flagged|answered` as "101"),
    plus `|kw1,kw2` (sorted category keywords) ONLY when the message carries any — the
    non-empty-only rule keeps unkeyworded messages byte-identical across the upgrade.
  - DAV: itemKey = href, revision = ETag.
- **Windowing:** items beyond WindowSize are left OUT of the persisted snapshot so they
  surface on the next round; `MoreAvailable` is emitted. Deletes are never windowed.
- **SyncKey lifecycle** (`SyncStateService.ValidateSyncKeyAsync`): keys are per-device,
  per-collection integers. The store keeps snapshot N **and N−1**; a client resending
  key N−1 (lost response) rolls back one generation and gets a recomputed batch. Unknown
  key → EAS status 3 → client restarts from 0. Initial sync (key 0) returns an empty
  response with key 1; items flow on the next round. **Never break the N−1 replay** —
  clients lose or duplicate items without it.
- **Echo suppression:** when a client change is applied (add/change/delete/move), patch
  the snapshot in place so the same change is not sent back to the client on the next
  diff. MoveItems patches both source and destination snapshots.
- **Item ServerIds** are `"{collectionId}:{sub}"` — sub is the IMAP UID for mail, or a
  short numeric id from the `DavItems` href-map table for DAV items. Folder ServerIds are
  the `UserFolder.Id` primary key. The folder registry (`UserFolder`) is **per-user**
  (shared across devices); `DeviceFolder` tracks what each device has acknowledged, and
  the FolderSync diff compares the two.
- "No changes" on Sync without payload = **HTTP 200 with empty body** (canonical EAS
  answer, clients expect it).
- **Empty Sync request = replay the cache.** Every full Sync request stores its replayable
  shape (Wait/HeartbeatInterval, window sizes, collection list + GetChanges flags) in
  `Device.LastSyncRequestJson`; per-collection body/filter options live in
  `CollectionState.OptionsJson`. Client `Commands` are one-shot and are **never** cached.
  An empty request rebuilds synthetic `<Collection>` elements using each collection's
  **current stored SyncKey**; a missing/never-synced collection in the cache falls back to
  Sync Status 13 (client resends the full request).
- **Read-only mode** (`ActiveSync.ReadOnly`) uses **silent revert** at the handler level
  (never inside backend stores — the revert needs snapshot access): a suppressed `Change`
  poisons the snapshot revision with `"!ro"` so the next diff re-sends the server version;
  a suppressed `Delete` removes the item from the snapshot so it is re-Added; `Add` gets
  per-item Status 6; MoveItems reports Status 5 *and* drops the item from the source
  snapshot so the client converges; sends return ComposeMail Status 120; folder ops
  Status 3; EmptyFolderContents/MeetingResponse Status 2. Settings/Provision stay writable
  (they only touch our own state DB). If you add a new write path, wire it into this
  scheme.
- **Metrics** (`GatewayMetrics`, static Meter "ActiveSync.Gateway" in Core/Observability):
  instruments are incremented from handlers/backends directly (no DI); the OTel Prometheus
  exporter in Server subscribes by meter name only when `Metrics:Enabled`. eas_requests
  ride a tiny middleware reading the (command, user) tuple EasEndpoint stashes in
  HttpContext.Items — NOT the Serilog middleware. Per-user labels collapse to "-" when
  `Metrics:PerUser=false` (set once into `GatewayMetrics.PerUserLabels` at startup —
  ALWAYS emit the user tag so Prometheus series shapes stay consistent). With
  `Metrics:Port` set, /metrics is gated on `Connection.LocalPort` (not Host headers).
  /readyz = cached ReadinessProbe (DB SELECT 1, IMAP TCP, DAV OPTIONS where any HTTP
  status counts); /healthz stays trivial liveness — the container healthcheck depends on
  that. Test-fixture gotcha: `Metrics:Enabled` gates EAGER service registrations, so
  isolated-factory overrides must travel via UseSetting (GatewayFixture does this for all
  overrides now).
- **Outbound iMIP** (`MeetingInvitationService`, hooks in `SyncHandler.ApplyClientCommandAsync`
  for the Calendar class): REQUEST on create / significant change / attendee add, CANCEL
  on delete, occurrence delete (RECURRENCE-ID) and attendee removal. Three duplicate-mail
  guards, all load-bearing: (1) `CalDavStore.ShouldSendInvitationsAsync` — Auto probes the
  home set's DAV header for **calendar-auto-schedule** (Stalwart and Axigen both schedule
  implicitly; Stalwart exposes NO schedule-outbox-URL, so the outbox is only the fallback
  signal); (2) `CalendarConverter.SchedulingSignificantlyDiffers` — only time/recurrence/
  location/summary changes re-invite (reminder edits, PARTSTAT echoes and ghosted Changes
  stay silent), EXDATE excluded because occurrence cancels send their own targeted CANCEL;
  (3) hooks run strictly AFTER successful writes and never throw (mail failure = warning).
  Groundwork lives in the converter: client Attendees parse with per-attendee PARTSTAT
  preservation, ORGANIZER injection (clients never send one), SEQUENCE bump on significant
  merges. The stored (merged) ICS is read via `ICalendarOperations.GetRawEventAsync` so
  16.x ghosting can't hide attendees. Caveat: a client RETRYING a Sync Add re-executes it
  (snapshot rollback design) and duplicates the event AND its invitation — pre-existing
  behavior, tracked separately.
- **Shared calendars** ride the same silent-revert path per folder:
  `IBackendSession.IsReadOnlyFolder` (CalDavStore matches the folder href against
  read-only grants) ORs into SyncHandler's `readOnly` flag. Grants = config
  `CalDav:SharedCollections` ("href|ro" entries) ∪ DB `SharedCalendarGrants` (`eas
  share`, DB wins per href), loaded once per session build in BackendSessionFactory —
  changes apply on session recycle, not immediately. Href comparison against grants is
  deliberately lenient (`SharedHrefEquals`: unescape + case-insensitive) because servers
  canonicalize hrefs; `ListFoldersAsync` dedupes shared entries against the home set
  BOTH before and after the depth-0 probe for the same reason. A granted collection
  that fails its probe is skipped with a warning — never break folder sync over a share.
- **Default-calendar pick is deterministic**: DAV multistatus order is server whim (a
  CI Stalwart once listed a freshly MKCALENDARed collection first), so `ListFoldersAsync`
  sorts the home set by href before crowning the first VEVENT collection Type 8 — and a
  collection matching a share grant NEVER claims the default slot (it's a share, not the
  user's primary calendar).

## State store

EF Core with two providers selected by config (`Database.Provider`: `Sqlite` | `Postgres`;
a `postgresql://` URI connection string implies Postgres and is converted to Npgsql keyword
form by `Core/Options/PostgresConnectionUri` — CNPG secret `uri` values work verbatim).
Schema is managed by **EF Core migrations**, applied at startup via `MigrateAsync()`.
Because SQLite and PostgreSQL emit different DDL, migrations are **per-provider**: the
abstract `SyncDbContext` holds the model, and `SqliteSyncDbContext` / `NpgsqlSyncDbContext`
each own a migration set under `src/ActiveSync.Core/Migrations/{Sqlite,Npgsql}`. DI registers
the matching subclass but exposes it as `SyncDbContext` (`AddDbContext<SyncDbContext,
TProvider>`), and the connection string is resolved **lazily** from `IOptions` inside the
`AddDbContext` callback — reading it eagerly from `builder.Configuration` misses
`WebApplicationFactory` overrides and silently shares one DB across tests. When you change an
entity, add a migration for **both** contexts (see README) — never hand-edit the model
snapshot. Entities: `Device`, `UserFolder`, `DeviceFolder`, `CollectionState`, `DavItem`,
`LocalItem` (the local contacts/calendar/tasks/notes store; added by the `AddLocalItems`
migration). `LocalItem.Content` is **AES-256-GCM ciphertext at rest** (`"v1:" + base64`,
sealed by `LocalContentProtector` with user+collection as AAD) — never read or write the
column except through the local stores, which decrypt/encrypt at their seams.
JSON blobs (snapshots, cached options, ping params, cached sync requests) use
`System.Text.Json`. Use `SyncStateService.PersistAsync` to save mutations on tracked
entities — do not repurpose `SaveDeviceInfoAsync` as a generic save.

Design-time factories (`SqliteSyncDbContextFactory` / `NpgsqlSyncDbContextFactory`) exist
only so `dotnet ef` can instantiate the contexts; their connection strings are placeholders.

Options are validated at startup (`ActiveSyncOptionsValidator` + `ValidateOnStart`):
IMAP/SMTP hosts are **mandatory** in PassThrough mode; `CalDav`/`CardDav` sections are
optional but must have a valid absolute http(s) `BaseUrl` when present; an `Encryption`
key (`Key` or `KeyFile`, ANY string — base64 of exactly 32 bytes is used as the raw key,
anything else is PBKDF2-stretched by `EncryptionKeyLoader`) is **mandatory** unless
`Encryption:AllowPlaintext=true` is set explicitly (test fixtures use
`GatewayFixture.TestEncryptionKey`). Options classes deliberately avoid the `required`
modifier so the validator (not the config binder) produces the error messages.

**Auth model**: pass-through is the baseline (EAS Basic credentials forwarded to every
backend, validated by an IMAP login probe). `ActiveSync:Users` (or a mounted `UsersFile`)
is an optional per-user OVERLAY resolved by `Core/Accounts/AccountResolver` — override
only what differs per backend (user name / password / endpoint fields); unset passwords
inherit the presented EAS password, SMTP/DAV credentials default to the effective IMAP
pair, and the merge/unseal rules live ONCE (shared with the validator via
`ValidateUsers`). Auth precedence per login: explicit gateway `Password`
(`GatewayPasswordHasher`, pbkdf2$/plaintext, local verify) → configured `Imap:Password`
(presented must equal it, timing-safe) → IMAP probe against the user's EFFECTIVE
endpoint+username → undeclared = global probe. `RequireDeclaredUsers=true` turns `Users`
into an allowlist (undeclared logins get a local 401; an empty entry is a grant). Backend
passwords may be `enc:v1:` values sealed by `SecretValue` under the Encryption master key
— CLI commands `protect` / `hash-password` (Spectre.Console.Cli app in
`src/ActiveSync.Server/Cli/`; Program.cs is a thin dispatcher — bare invocation shows the
banner and exits, the web host requires `serve`, and admin commands
users/devices/folders/items/show/block/unblock/purge/user query the state DB via
`CliServices`). Operator login blocks (`LoginBlocks` table) are enforced post-auth with
403 in `EasEndpoint`/`AutodiscoverEndpoint`.
**Database-declared accounts**: `AccountEntry` rows (serialized `AccountOptions` JSON,
managed by `AccountStore` / the `eas user` branch) REPLACE the whole config entry for the
same login. Every store mutation bumps the single `AccountsStamp` row in the same
SaveChanges; `AccountResolver` point-reads it at most every `Auth:UsersRefreshSeconds`
(lazily, on the request path via `EnsureFreshAsync`) and swaps an immutable snapshot;
`BackendSessionFactory` subscribes to `SnapshotChanged` and clears both auth caches.
Invalid/malformed DB rows are skipped with a warning — never let one row break auth. The
resolver's `MergedUsers` view feeds the startup sub-banner (origins + masked secrets).
**Self-signed HTTPS**: `serve` also listens on `:5443` (`SelfSignedTls` options, default
on) with a certificate from `GatewayCertificateStore` — generated on first serve (RSA
2048, 20 years, CN/SAN from `PublicUrl`'s host, no renewal logic) and persisted as the
single `ServerCertificates` row (Id=1, PKCS#12 base64 sealed by `LocalContentProtector`
with `_gateway`/`tls` AAD), so restarts and replicas share one fingerprint; the PK
conflict settles first-boot races, an unsealable row is regenerated. Kestrel reads it via
`ServerCertificateSelector` from a closure populated after migrations. Any configured
`Kestrel:Endpoints` https URL disables the whole path — mounted certs are served as-is.
**Verbose wire logging** (Serilog `Verbose` / MEL `Trace`): components' own categories,
no artificial namespace — `ActiveSync.Server.Eas.*` dumps decoded request/response XML in
`EasContext.ReadRequestAsync`/`WriteResponseAsync` (+ Autodiscover bodies; binary/raw
side-channels log sizes only); `ActiveSync.Backends.Imap`/`.Smtp` attach
`MailKitWireLogger` (an `IProtocolLogger`→`ILogger` adapter; SMTP has its own category
string so it can be traced alone) in `ImapConnectionFactory` and the inline `SmtpClient`;
`ActiveSync.Backends.Dav` logs method/URI/bodies in `WebDavClient.SendAsync`. Invariants:
mail-wire credentials are masked via MailKit's `AuthenticationSecretDetector` contract,
DAV wire logging must NEVER log headers (Authorization), payloads go through
`WireLog.Payload` (16 KB cap + control-character neutralization), and every tap is
`IsEnabled`-guarded so the tier is free when off.
**Console log shaping**: `LoggingSetup.ConfigureConsole` (Server/Setup) maps
`ActiveSync:Log:Mode` (Simple = stock template, Standard = date + full level name +
SourceContext pipe-delimited [the default], Extended = + `{Properties:j}` + thread/machine
enrichers) × `:Format` (Text, or Json = CLEF `RenderedCompactJsonFormatter`). Rule: when
`Serilog:WriteTo` is configured the gateway adds NO console sink (operator sinks rule; the
CLI banner passes `alwaysConsole: true` so bare `eas` still prints). Values validated in
ActiveSyncOptionsValidator; keep Simple byte-identical to the historical output.
**Hard identity invariant**: DB row scoping (`Device`/`UserFolder`/`LocalItem.UserName`),
`LocalChangeNotifier` keys, the `LocalContentProtector` AAD and session/watcher cache keys
are all the GATEWAY login — never a per-backend user name. Changing that orphans sync
state and makes encrypted local rows undecryptable. The user's mail address is
`IBackendSession.MailAddress` (explicit override, else login-if-it-contains-'@') — never
derive an address from `UserName` with `Contains('@')`.

## Backend layer notes

- **Provider engine**: every backend is an `IBackendProvider` ("imap", "smtp", "caldav",
  "carddav", "sieve", "local") registered in DI and indexed by `BackendProviderRegistry`.
  `CompositeBackendSession` groups an account's `ResolvedRole`s by provider, opens ONE
  `IBackendConnection` per provider (a provider serves all its assigned roles over one
  connection — the JMAP shape), and aggregates stores/side-ops. `RoleMapper` is a
  TRANSITIONAL shim translating the typed `ResolvedAccount` slots into role assignments
  via the historical presence rules; it dissolves when config grows role sections with
  provider discriminators. `ResolvedRole.Settings` is the provider's own options object
  (typed handoff, same transition). Optional provider capabilities: `ICredentialVerifier`
  (auth probe — the MailStore role's provider verifies pass-through logins; a provider
  without it means declared-users-only), `IPerUserResourceOwner` (per-user cache trim on
  the eviction sweep).
- One `CompositeBackendSession` per (user, deviceId), cached in `BackendSessionFactory`
  with idle eviction; auth verdicts are cached ~5 minutes. DAV stores are optional —
  when a `CalDav`/`CardDav` section is omitted, that class is served by a **local store**
  instead (below), so `Session.Contacts` / `Session.Calendar` are now always non-null.
- **Local stores** (`Backends/Local/`): `IContentStore` over the `LocalItems` table when
  no DAV backend is configured, plus `LocalNotesStore` which is **always** present (no
  DAV backend carries notes) and `LocalTaskStore` when no CalDAV tasks collection is
  configured. One fixed folder per class; content is vCard / iCalendar VEVENT / VTODO /
  VJOURNAL text (same converters as the DAV stores — `NotesConverter` maps Notes to
  VJOURNAL, `TasksConverter` maps Tasks to VTODO); item key = row id, revision = a
  per-row version counter.
  They cannot hold the request-scoped DbContext (sessions outlive requests) — they open
  short-lived contexts via `ISyncDbContextFactory`. `WaitForChangesAsync` awaits the
  in-process `LocalChangeNotifier` (instant cross-device push, single-instance only —
  multi-instance deployments rely on the watchdog re-check). Local data is visible only
  to ActiveSync clients; the state DB therefore holds real user data.
- Push/Ping: the priority folder (INBOX when pinged, else the first watched mail folder)
  is watched by a **persistent per-(user, folder) IDLE watcher** (`ImapIdleWatcher`,
  shared by all the user's devices, owned by `ImapBackendProvider` and resolved lazily
  per folder via a provider closure): lazy-started on first wait, dedicated connection
  (never the shared session client — IDLE occupies the whole connection; 9-min slices per
  MailKit guidance), reconnect with capped backoff, evicted when the user's last session
  goes.
  Events are **latched** (`LastChangeUtc`) so changes between requests reach the next
  wait instantly; `WaitForChangeAsync(sinceUtc, …)` returns `null` when IDLE is
  unavailable (no capability / stale credentials) and the wait degrades to pure STATUS
  polling — keep that fallback intact. The watcher is a latency optimization only: the
  Ping entry check and the watchdog re-check remain the correctness guarantees. Mail
  folders also STATUS-poll every ~30 s; DAV collections poll ctag/sync-token every
  `DavPollSeconds`. `Eas.UseImapIdle=false` disables IDLE entirely.
- Attachment `FileReference` format: `UrlEncode("{imapBackendKey}|{uid}|{attachmentIndex}")`
  where index is the position in `MimeMessage.Attachments`. Search `LongId` format:
  `UrlEncode("{folderBackendKey}|{itemKey}")`. Both round-trip through ItemOperations.
- Folder backend keys are prefixed: `imap:`, `caldav:`, `caldav-tasks:`, `carddav:`,
  `local:` — each store claims its keys via `IContentStore.OwnsBackendKey` and the
  session dispatches on the first claimant, so key spaces must stay disjoint (the colon
  keeps `caldav:` from matching `caldav-tasks:` keys; local stores match their single
  folder key exactly). Read-only folders route the same way: the owning store opts in
  via `IReadOnlyCollectionSource`.
- **Tasks over CalDAV** (`CalDavTaskStore`): VTODOs in the home-set collection named by
  `CalDav.TaskFolder` (default "Tasks", Axigen's layout); matched by displayname or
  trailing path segment among VTODO-capable collections. `CalDavStore` conversely skips
  collections whose `supported-calendar-component-set` lacks VEVENT. Task recurrence maps
  via `RecurrenceMapper` (shared with the calendar converter) with two deliberate holes:
  Regenerate/DeadOccur are skipped (no RRULE equivalent) and an omitted Recurrence element
  preserves the stored RRULE (presence-guard over MS-ASTASK's nominal full-replace).
- **Axigen DAV quirks** (verified live 2026-07-17 against Axigen X6): its `calendar-query`
  **omits events carrying an RRULE when the VEVENT comp-filter has no `time-range`** —
  which is why `CalDavStore.BuildEventFilter` always sends a time-range (epoch start when
  the sync is unfiltered); don't "simplify" that away. The quirk is **VEVENT-only**:
  VTODO queries return recurring and dateless todos fine without a time-range (probed
  2026-07-18), so `CalDavTaskStore` deliberately stays time-range-free. New DAV items are **indexed
  asynchronously** (PROPFIND/REPORT listings can lag a PUT by up to ~a minute) — polling
  belongs in tests, not the gateway. Its `addressbook-query` REPORT returns hrefs under a
  wrong path (`/Contacts/Contacts/...`), so CardDAV listing must stay PROPFIND-based.
  ManageSieve is not offered (no port 4190) — Oof needs a Sieve-capable backend.
- **WebDavClient follows redirects manually** (auto-redirect strips Authorization and
  downgrades methods — Stalwart's `/.well-known/caldav` 307 would land on an
  unauthenticated HTML page). Same-host only, 5 hops max; never revert to
  `AllowAutoRedirect = true`. Non-XML multistatus responses surface as
  `BackendException`, not `XmlException`.
- Third-party API surfaces that changed recently and were verified against the shipped
  packages (don't trust training data, check `~/.nuget/packages/**/*.xml` or reflection):
  - **Ical.Net 5.x**: `CalDateTime` (no `IDateTime`), `ExceptionDates.GetAllDates()/Add()`,
    `Duration.FromMinutes()`, `RecurrencePattern.Count` is `int?`. `RecurrenceRules`/
    `RecurrenceId` are obsolete-but-correct here (EAS carries one rule); the pragma in
    `CalendarConverter.cs` explains why.
  - **FolkerKinzel.VCards 8.x**: `Vcf.Parse`, `vCard.ContactID` (not `ID`),
    `Organization.Name/Units`, enum extensions like `PCl?.IsSet(...)` take the *nullable*
    receiver, `DataProperty.Value.Bytes`, `DateAndOrTime.DateOnly/DateTimeOffset`.
- vCards are **written** by hand-rolled vCard 3.0 serialization (`ContactConverter
  .FromApplicationData`) and **read** via FolkerKinzel — keep that split; writing via the
  builder API was deemed riskier than emitting the simple format directly.
- The MS-ASTZ timezone blob (`TimeZoneBlob`) is a 172-byte little-endian struct in base64.
  All-zeros = UTC. Only the bias is read back from clients.

## Server layer notes

- One class per EAS command implementing `IEasCommandHandler`, registered in `Program.cs`,
  dispatched by `Cmd` name in `EasEndpoint`. To add a command: handler class in
  `src/ActiveSync.Server/Eas/Handlers/`, DI registration, and append the name to
  `ProtocolCommands` in `EasEndpoint.cs`.
- Kestrel keep-alive is 65 minutes because Ping can legally hold a request for 59 minutes.
  Do not "fix" the long timeout.
- SendMail has **two wire forms**: WBXML ComposeMail (14.x, MIME in an OPAQUE element)
  and raw `message/rfc822` body with query options (12.x). `ComposeMailHandlerBase`
  handles both; keep it that way.
- Success for SendMail/SmartReply/SmartForward is an **empty 200**, not a WBXML body.
- MoveItems status quirk: **3 = success** (1 and 2 are the error codes).

## Integration tests

`tests/ActiveSync.Integration.Tests` hosts the gateway **in-process**
(`WebApplicationFactory<Program>`; `Program` has a `public partial class Program;` marker)
and drives it with `EasTestClient` — a mini EAS 14.1 client built on the production WBXML
codec and `EasRequestParameters.ToBase64()`. The factory invokes the entry point without a
`serve` arg, so the assembly's module initializer (`Infrastructure/TestBootstrap.cs`) sets
`AS_TEST_FORCE_SERVE=1` to route empty invocations to the web host instead of the CLI
banner. Rules:

- Every test class: `[Collection("gateway")]`, `[Trait("Category","Integration")]`, and
  `[BackendFact]` on each test. `BackendFact` skips when no IMAP backend answers —
  **never** make integration tests hard-fail on a docker-less machine.
- Backends resolve via `TestBackend` env vars (`AS_TEST_IMAP_HOST`, `AS_TEST_SMTP_HOST`,
  `AS_TEST_DAV_URL`, `AS_TEST_STACK`, …) with localhost defaults matching both compose
  stacks under `docker/backends/` (same ports 143/587/5232, same users user1/user2 @
  example.com, password `pass`).
- `AS_TEST_PG` (CI-only; leave unset locally) is a throwaway Postgres admin
  `postgresql://` URI: when set, each gateway factory creates its own fresh Postgres
  database instead of a SQLite temp file — never dropped, the CI container is discarded —
  so Npgsql migrations, URI conversion and provider inference all run in CI.
- Two stacks, one suite: `stalwart` (default; pinned **v0.13.4** — 0.16+ moved to a
  JSON/web-setup config that cannot be provisioned declaratively; users are created by the
  compose `provision` one-shot via the management API) and `mailserver`
  (docker-mailserver + Radicale; set `AS_TEST_STACK=mailserver` so the DAV `HomeSetPath`
  preset switches to `/{user}/`).
- Tests use **GUID subjects/markers** and poll with `WaitUntil` — never assume an empty
  mailbox and never assert on absolute counts. `MailboxJanitor` exists for best-effort
  purges when needed.
- Read-only revert semantics: the reverting Add/Change usually arrives **in the same Sync
  response** as the suppressed command (the diff runs after client commands) — check the
  command's own response before polling.
- Test backends speak **plaintext**; the gateway fixture sets
  `Imap/Smtp Security="None"` because Stalwart advertises STARTTLS with a self-signed
  cert and MailKit would otherwise fail the upgrade. `MailTransportSecurity` in Backends
  is the single mapping point for these options.
- TLS certificate validation callbacks come **only** from
  `ServerCertificateValidator` (Backends root) — MailKit and SocketsHttpHandler share the
  delegate type. Never inline a validation callback. Knobs per backend section:
  `AllowInvalidCertificates` (accept everything, lab use) and `CaCertificatePath`
  (PEM CAs trusted on top of the system store via CustomRootTrust).
- CI: `docker compose -f docker/docker-compose.ci.yml run --rm tests` (copies the repo
  inside the container before building — never build directly on the bind mount, it would
  poison host bin/obj with Linux paths). Image builds run unit tests only via the
  Dockerfile `test` stage (forced by a marker-file COPY into `runtime`).
- GitHub workflow (`.github/workflows/build.yaml`, single pipeline): steps deliberately
  stream everything through the docker daemon API (`docker cp` for the Stalwart config —
  as a *directory* copy, because the image entrypoint hardwires
  `--config /opt/stalwart/etc/config.toml` and ignores command args; stdin for
  provision.sh) instead of bind mounts, a pattern inherited from a docker-out-of-docker
  runner and kept for robustness — don't add bind mounts to workflow steps. A warm-up
  canary mail runs before the suite (a cold Stalwart intermittently delays its first
  delivery, which would flake one test). The runtime image pushes to ghcr.io with the
  built-in GITHUB_TOKEN; no repository secrets are needed.
- Release flow (`release.yaml`, workflow_dispatch): validate version → generate notes
  from commit subjects since the previous RELEASE (not tag) → push the tag → create the
  release object → **explicitly dispatch build.yaml against the tag ref** (a tag pushed
  with GITHUB_TOKEN never fires other workflows — loop prevention — so the dispatch is
  load-bearing, don't remove it). The tag run of build.yaml then pushes the image and
  attaches the zips to that release. Order matters: release-with-notes first, files
  arrive when the build is green.
- Download zips: after the integration tests, a container from the test image publishes
  framework-dependent linux-x64/win-x64 outputs (WITH apphost, unlike the image) plus a
  README.txt, zips them as `eas-gateway-<platform>-x64-<tag|branch>.zip` and `docker cp`s
  them out; the publish DIRECTORIES are uploaded via `actions/upload-artifact@v4`, and on
  version tags the .zip files are attached to the tag's release via `gh release upload
  --clobber` (existing releases are reused, assets replaced — re-runs are idempotent).

## Testing expectations

- WBXML/codec changes → round-trip tests in `tests/ActiveSync.Protocol.Tests` (include a
  hand-crafted byte fixture when adding decode paths).
- Sync-state changes → tests in `tests/ActiveSync.Core.Tests` (in-memory SQLite via
  `Data Source=:memory:` with an open connection).
- Handlers currently have no test harness; if you add one, drive the endpoint with
  WBXML-encoded requests (encode fixtures with `WbxmlEncoder`) rather than mocking the
  codec.
- End-to-end: the integration suite (see "Integration tests" above) covers the full
  pipeline against real backends, including a 2-client mail exchange. Real-client
  validation (iOS especially — it is the strictest about status codes) has **not** been
  done yet; treat client-visible behavior changes with corresponding care.

## Security posture

- Credentials arrive as HTTP Basic and are passed through to backends. TLS termination is
  assumed (reverse proxy or Kestrel HTTPS) — never log passwords; `BackendSessionFactory`
  caches only a SHA-256 hash for auth-cache comparison.
- No provisioning enforcement, no wipe: this is by design and confirmed by the owner.

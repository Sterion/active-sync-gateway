# AGENTS.md — ActiveSync Gateway

Guidance for AI agents (and new contributors) working in this repository. Read this before
changing code; it captures the architecture, invariants, and gotchas that are not obvious
from the file tree.

## What this project is

A **.NET 10** service that speaks **Microsoft Exchange ActiveSync (EAS) 16.1** to mail
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
Event attachments live INLINE in the iCal (base64 ATTACH; `Backends:Calendar:CalendarAttachments`
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

**Out-of-office** (Settings→Oof) is backed by **ManageSieve** when the `Oof` role is
assigned to the `sieve` provider (no Oof role = the historical accept-and-ignore stub). The handler is backend-agnostic:
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
  must return `MS-ASProtocolVersions: 12.1,14.0,14.1,16.0,16.1`.

## Solution layout and dependency rule

```
src/ActiveSync.Protocol/    WBXML codec, code pages, MS-ASHTTP query parser, EAS constants.
                            Depends on NOTHING project-wise. No ASP.NET, no MailKit.
src/ActiveSync.Core/        Backend interfaces + provider engine (BackendProviderRegistry,
                            CompositeBackendSession, BackendSessionFactory), EF Core state
                            store, diff engine, options. Depends on Protocol only
                            (+ EF Core / config-binder packages). Provider-agnostic.
src/ActiveSync.Backends.Common/  Shared building blocks: MIME/iCal/vCard ⇄ EAS converters
                            + TLS/wire-logging helpers + the shared backend-options bases
                            (NetworkBackendOptions = the TLS knobs; MailConnectionOptions =
                            Host/Port/UseSsl/Security). Depends on Core (+ MailKit, Ical.Net,
                            FolkerKinzel.VCards) so those deps stay OUT of Core.
                            OPTIONS CONVENTION: a provider's own options class lives in ITS
                            assembly (e.g. ImapOptions, JmapOptions), deriving from the Common
                            bases and adding only its specifics; bound via ProviderSettings.
                            Bind<T>(). Core holds only host/protocol options (Eas/Auth/Database/
                            Encryption/Log/Policy/…), never backend-specific ones.
src/ActiveSync.Backends.Imap/    "imap" provider (MailKit). Depends on Core + Common.
src/ActiveSync.Backends.Jmap/    "jmap" provider (Stalwart JMAP over HttpClient +
                            System.Text.Json). Serves MailStore + MailSubmit + Oof
                            (VacationResponse) + Contacts (JSContact) + Calendar (JSCalendar)
                            over one HTTP session. JSContact maps direct to EAS; JSCalendar
                            bridges via iCalendar and reuses CalendarConverter (iCal↔EAS).
                            Listing uses */get ids:null, not the FTS-backed /query
                            (eventually-consistent). Calendar update strips read-only members
                            (isDraft/isOrigin/…) to avoid invalidProperties. Mail Ping/Sync
                            waits accelerate off a shared per-user EventSource (SSE) watcher
                            (JmapEventSourceWatcher; poll stays the backstop), trimmed via
                            IPerUserResourceOwner like the IMAP IDLE watcher. Tasks/Notes are
                            NOT JMAP (no standard) — leave them on caldav/local. Core+Common.
src/ActiveSync.Backends.Smtp/    "smtp" provider (MailKit). Depends on Core + Common.
src/ActiveSync.Backends.Dav/     "caldav" + "carddav" providers (one assembly — shared
                            WebDavClient/DavStoreBase/DavDiscovery). Depends on Core + Common.
src/ActiveSync.Backends.Sieve/   "sieve" provider (ManageSieve). Depends on Core + Common.
src/ActiveSync.Backends.Local/   always-shipped "local" fallback stores (gateway DB) +
                            LocalChangeNotifier. Depends on Core + Common.
src/ActiveSync.WebUi/       Web admin interface (/admin) + user portal (/user): cookie/OIDC
                            auth, the JSON API, and the embedded no-build SPA (wwwroot).
                            Depends on Core ONLY (see "Web UI layer notes").
src/ActiveSync.Server/      Kestrel host, /Microsoft-Server-ActiveSync endpoint, Basic auth,
                            one IEasCommandHandler per EAS command, provider DI registration
                            (AddBackendProviders). References Core + all seven backend
                            assemblies + WebUi.
tests/ActiveSync.Protocol.Tests/   WBXML round-trip + query parser tests
tests/ActiveSync.Core.Tests/       diff engine, sync-key state machine, options validator,
                                   provider engine + resolver, AND the backend unit tests
                                   (converters, cert validator, WebDAV redirect safety) —
                                   there is no per-provider test project, so Core.Tests
                                   references the provider assemblies and hosts them.
tests/ActiveSync.Server.Tests/     handler-level tests (has InternalsVisibleTo into Server)
tests/ActiveSync.WebUi.Tests/      web UI unit tests (key repository, OIDC decision matrix)
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
  status counts); the `configured` component is REPORTED but never gates the verdict.
  /healthz stays trivial liveness — the container healthcheck depends on
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
  `IBackendSession.IsReadOnlyFolder` (the owning store opts in via
  `IReadOnlyCollectionSource`; CalDavStore matches the folder href against read-only
  grants) ORs into SyncHandler's `readOnly` flag. Grants = config Calendar-role
  `SharedCollections` ("href|ro" entries) ∪ DB `SharedCalendarGrants` (`eas share`, DB
  wins per href); the factory loads the DB grants once per session build and the caldav
  provider unions them with its own configured list —
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

Host options are validated at startup by `ActiveSyncOptionsValidator` + `ValidateOnStart`
(database, EAS, auth, encryption, policy, ...); an `Encryption` key (`Key` or `KeyFile`,
ANY string — base64 of exactly 32 bytes is used as the raw key, anything else is
PBKDF2-stretched by `EncryptionKeyLoader`) is **mandatory** unless
`Encryption:AllowPlaintext=true` is set explicitly (test fixtures use
`GatewayFixture.TestEncryptionKey`). The BACKEND role sections + declared users are
validated separately by `BackendConfigurationValidator`, which runs AFTER the service
provider is built (it needs the registry): every named provider must exist and support
its role, and each provider validates its own settings (`ValidateConfiguration`).
MailStore + MailSubmit are mandatory; content roles fall back to `local`. Options classes
deliberately avoid the `required` modifier so the validators produce the error messages.

**Auth model**: pass-through is the baseline (EAS Basic credentials forwarded to every
backend, validated by the MailStore provider's login probe via `ICredentialVerifier`).
`ActiveSync:Users` (or a mounted `UsersFile`) is an optional per-user OVERLAY resolved by
`Core/Accounts/AccountResolver` — role-keyed overrides (Provider / Enabled / UserName /
Password / free-form Settings per role); unset passwords inherit the presented EAS
password, every non-MailStore role's credentials default to the effective MailStore pair,
and the merge/unseal rules live ONCE (shared with the validator via `ValidateUsers`). Auth
precedence per login: explicit gateway `Password` (`GatewayPasswordHasher`,
pbkdf2$/plaintext, local verify) → configured MailStore `Password` (presented must equal
it, timing-safe) → MailStore provider probe against the user's EFFECTIVE endpoint+username
→ undeclared = global probe. `RequireDeclaredUsers=true` turns `Users`
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
string so it can be traced alone) in `ImapConnectionFactory` and `SmtpSubmitBackend`;
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
  connection — the JMAP shape), and aggregates stores/side-ops. Config assigns roles
  directly: `ActiveSync:Backends:<Role>` sections carry a `Provider` discriminator, parsed by
  `BackendRolesConfig` and held live by `BackendRolesProvider` (rebuilt when a settings change
  moves the `Backends` subtree — the session cache recycles, no restart; absent mail roles =
  UNCONFIGURED, so EAS/Autodiscover answer 503 until set — but `/readyz` stays READY and only
  reports `"configured": false`, since configuring the gateway is what the admin UI is for);
  `AccountResolver` produces role→provider resolutions
  (`ResolvedRole`), per-user overrides are role-keyed with subtree-replace list merges, and
  each provider binds its OWN options from its raw `ProviderSettings` (the host never knows
  a plugin provider's option shape — that is the whole point). Providers validate their
  sections via `ValidateConfiguration` and describe themselves for the banner via
  `DescribeRole`. **`DescribeConfiguration(role)`** (a DEFAULT interface member, so older
  plugins still compile and just fall back to raw key/value editing) returns
  `BackendConfigField`s — name, type, default, enum values, help — which is the ONLY thing
  the web UI and CLI know about a provider's settings; they render forms from it and never
  hard-code a field. In-repo providers compose the shared bases from
  `Backends.Common/BackendSchemaFields`; a declared `Default` MUST equal the options class's
  own (BackendSchemaTests binds an empty section and compares — a drift renders a wrong
  "(default: X)"). `BackendConfigValidation` holds the generic shape checks + the
  effective-section pass, `ProviderSettings.FromFlat` materializes entered values, and
  `BackendKeyValidator` applies the schema to `eas config set ActiveSync:Backends:...`. Pre-role-model DB account rows are upgraded at startup by
  `AccountStore.UpgradeLegacyRowsAsync` (`LegacyAccountJson`) — unconvertible rows are
  logged as errors, never silently dropped. Optional provider capabilities:
  `ICredentialVerifier` (auth probe — the MailStore role's provider verifies pass-through
  logins; a provider without it means declared-users-only), `IPerUserResourceOwner`
  (per-user cache trim on the eviction sweep), `IReadinessSource` (/readyz probe).
- **DB-backed global settings**: every setting is CLI-settable (`eas config set`) and stored in
  the state DB (`GlobalSetting` rows + a single-row `SettingsStamp`, mirroring the accounts store).
  A `DbSettingsConfigurationProvider` is layered LAST in configuration so the DB wins over
  appsettings/env, which win over code (POCO) defaults; `SettingsRefresher` polls the stamp
  (`Auth:UsersRefreshSeconds`, ~1s) and swaps the provider snapshot, firing the config reload token
  so `IOptionsMonitor`/`IOptionsSnapshot` recompute. Consumers therefore read options live (NOT
  `IOptions<>` captured once). Bootstrap `Database`/`Encryption` are env/file-only (they open and
  decrypt that DB). `SettingKeys` (`Cli/`) is the settable-key catalogue (type/range/tier). Options
  are validated once at startup (fail-fast) — NOT via a registered `IValidateOptions`, so a bad live
  value never throws on read. Multi-pod: each replica polls its own stamp; no cross-process bus.
- **Out-of-repo plugins**: `Core/Plugins/PluginLoader` loads assemblies from
  `ActiveSync:Plugins:Directory` (default `/app/plugins`, one subdir per plugin, entry dll
  = dir name) in a per-plugin non-collectible `AssemblyLoadContext` that resolves the
  contract (Core/Protocol/Backends.*/framework) from the HOST — so a plugin's
  `IBackendProvider` IS the host type the registry indexes. Each `IGatewayPlugin.Register`
  adds its providers. Fail-fast (corrupt/incompatible/no-entry aborts startup; empty dir =
  no-op), major-version-gated against `ActiveSync.Core`. Wired in ProgramServer AND
  CliServices before the container is built. Protocol/Core/Backends.Common are packed to
  NuGet on tagged CI so plugin authors compile against the contract; see docs/plugins.md
  (contract NOT ABI-stable pre-2.0).
- One `CompositeBackendSession` per (user, deviceId), cached in `BackendSessionFactory`
  with idle eviction; auth verdicts are cached ~5 minutes. Content roles are optional —
  when a role has no configured provider it falls back to the **local store** (below), so
  `Session.Contacts` / `Session.Calendar` are always non-null.
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
- **Tasks over CalDAV** (`CalDavTaskStore`, the `Tasks` role on the `caldav` provider):
  VTODOs in the home-set collection named by the Tasks role's `TaskFolder` setting
  (default "Tasks", Axigen's layout; the Tasks section inherits the Calendar section's
  server settings as its base); matched by displayname or
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

## Web UI layer notes (`src/ActiveSync.WebUi`)

- A class library (plain `Microsoft.NET.Sdk` + `FrameworkReference Microsoft.AspNetCore.App`)
  referenced by the Server; it references **Core only** — never the Server (that would be
  circular) and never backend assemblies (cross-provider state goes through Core capability
  interfaces like `IWatcherDiagnostics`). `ProgramServer.cs` touches it in exactly two
  lines: `builder.AddWebUi()` + `app.MapWebUi()`.
- Everything is mapped unconditionally and gated at runtime by the LIVE
  `ActiveSync:WebUi:{Admin,UserPortal}:Enabled` flags (404 when off) — same pattern as the
  `/metrics` port filter. The admin UI must keep working in unconfigured mode (bootstrap).
- Auth: one passive cookie scheme (`WebUi`), policies `WebUiAdmin` (claim `eas:admin`) /
  `WebUiUser`; CSRF = SameSite=Strict + the `X-EAS-WebUi` header filter on non-GET. The
  cookie scheme never challenges — the EAS Basic-auth path must stay byte-identical.
  DataProtection keys persist in the `DataProtectionKeys` table via `DbXmlRepository`
  (sealed with the master key) — do NOT swap in the official EF package; it would drag
  ASP.NET into Core's packed plugin contract.
- **Backends page** (`BackendsEndpoints` + `admin/views/backends.js`): role→provider assignment
  and settings as `GlobalSetting` rows over the file config — the same write path as settings,
  so `BackendRolesProvider` applies it live (~1 s, no restart). Two invariants: (1) the DB
  settings layer is INSIDE `IConfiguration`, so "what the config file says" must be read by
  walking the providers and skipping `DbSettingsConfigurationProvider` (`FileValue`) — reading
  it naively makes a save look redundant and delete its own row; (2) saves ELIDE values equal
  to the layer below (config file, else the provider's declared default) and delete any
  existing row for them, so an override always means a real deviation and typing the default
  is the reset. `shared/schema-form.js` renders the fields for all three editors (Backends
  page, admin user editor, portal); keys no schema claims must survive the full-replacement
  PUTs — Advanced section in the admin views, invisible carry-through in the portal.
- API endpoints reuse the exact CLI pipelines from `ActiveSync.Core.Administration`
  (`SettingKeys`, `AccountFieldPaths`, `AccountSecretPolicy`, `AccountEditing`) — the web
  must never accept what `eas` would reject, and stored secrets never leave the server
  (DTOs carry set/unset flags; there is a leak-guard test asserting no `pbkdf2$`/`enc:v1:`
  in responses).
- `wwwroot/` is a **no-build SPA**: plain HTML/CSS/native ES modules, zero npm/bundler —
  keep it that way. `shared/theme.css` owns all visual tokens (restyle there);
  `shared/app.css` consumes only variables; no inline SCRIPTS ever (the CSP forbids them —
  inline `style=` attributes are allowed for layout).
  Set `EAS_WEBUI_ASSETS=<path to wwwroot>` to serve live files from disk while designing.
  The default-as-placeholder convention: unset fields render empty with the default as a
  dimmed placeholder + badge; clearing reverts to the default.
- OIDC (`WebUiServiceCollectionExtensions`): handler registered only when
  `Oidc:Authority` is set at startup (restart tier); the ticket→account decision matrix
  lives in `Auth/OidcLogin.cs` (unit-tested in isolation); AdminClaim/AutoProvision are
  read live per sign-in. The callback is `/oidc/callback` — outside the gated prefixes on
  purpose.

## Integration tests

`tests/ActiveSync.Integration.Tests` hosts the gateway **in-process**
(`WebApplicationFactory<Program>`; `Program` has a `public partial class Program;` marker)
and drives it with `EasTestClient` — a mini EAS 14.1 client built on the production WBXML
codec and `EasRequestParameters.ToBase64()`. The factory invokes the entry point without a
`serve` arg, so the assembly's module initializer (`Infrastructure/TestBootstrap.cs`) sets
`AS_TEST_FORCE_SERVE=1` to route empty invocations to the web host instead of the CLI
banner. Rules:

- Every test class: `[Collection("gateway")]`, `[Trait("Category","Integration")]`, and a
  capability-gated fact attribute on each test. `[BackendFact]` skips when no IMAP backend
  answers — **never** make integration tests hard-fail on a docker-less machine. The
  narrower gates skip a test cleanly on a backend that lacks the feature (so the *same*
  suite runs against every stack): `[SieveBackendFact]` (ManageSieve 4190 **offering STARTTLS**
  — Cyrus timsieved is plaintext-only), `[JmapMailFact]` / `[JmapGroupwareFact]` /
  `[JmapVacationFact]` (fetch the session via `/.well-known/jmap` discovery — works for both
  Stalwart's `/jmap/session` and Cyrus's `/jmap/` — and check the mail / calendars+contacts /
  vacation capabilities), `[CalDavFreeBusyFact]` (probe `supported-report-set` for
  `free-busy-query`; Radicale omits it), `[BackendEnforcesAuthFact]` (probe that the IMAP
  backend rejects a bad password — the Cyrus test image accepts any), `[SmtpSubmissionFact]`
  (a submission MSA on 587 — Cyrus is LMTP-only), and `[SkipOnStackFact(stack, reason)]` for
  genuine per-server behavior differences with no cheap probe. Add a gate rather than a hard
  `[BackendFact]` when a test needs a capability not every backend has.
- Backends resolve via `TestBackend` env vars (`AS_TEST_IMAP_HOST`, `AS_TEST_SMTP_HOST`,
  `AS_TEST_DAV_URL`, `AS_TEST_STACK`, …) with localhost defaults matching both compose
  stacks under `docker/backends/` (same ports 143/587/5232, same users user1/user2 @
  example.com, password `pass`).
- `AS_TEST_PG` (CI-only; leave unset locally) is a throwaway Postgres admin
  `postgresql://` URI: when set, each gateway factory creates its own fresh Postgres
  database instead of a SQLite temp file — never dropped, the CI container is discarded —
  so Npgsql migrations, URI conversion and provider inference all run in CI.
- Six stacks, one suite: five gate every push (`stalwart`, `mailserver`, `baikal`, `james`,
  `axigen`); `cyrus` is **temporarily disabled** (failing in CI, under investigation — the
  `RUN_LEG` gate never runs it, though its compose/config stay in place). `stalwart`
  (default; **v0.16.13**, self-provisioning), `mailserver` (docker-mailserver + Radicale; set
  `AS_TEST_STACK=mailserver` so the DAV `HomeSetPath` preset switches to `/{user}/`), `cyrus`
  (`cyrus-docker-test-server`: an independent C implementation of IMAP + CalDAV/CardDAV + JMAP +
  ManageSieve, auto-provisioning user1..5 + default collections; `docker/backends/cyrus`), and
  `baikal` (`docker/backends/baikal`). Cyrus is driven purely by
  `matrix.include` env — `AS_TEST_DAV_HOMESET=/dav/calendars/user/{user}/`,
  `AS_TEST_DAV_CONTACTS_HOMESET=/dav/addressbooks/user/{user}/`, `AS_TEST_MAILSUBMIT=jmap`
  (LMTP-only, so it submits over JMAP), `AS_TEST_SIEVE_TLS=false`. Its permissive test-image
  auth, no-SMTP-submission, plaintext-sieve, internal iMIP auto-scheduling and non-INBOX-IDLE
  behaviors are handled by the capability gates above (see the `SkipOnStackFact("cyrus", …)`
  reasons; `SkipOnStackFact` also accepts a comma-separated stack list for one shared reason,
  e.g. `"cyrus,baikal"`).
- The **companion pattern** (Baikal): a DAV-only server paired with a mail server so the
  mandatory mail roles still have a backend. `baikal` = **Baikal** (sabre/dav — the most-deployed
  CalDAV/CardDAV codebase, absent from the other stacks) for calendar/contacts + a
  **docker-mailserver** service (reusing `../mailserver/config`, incl. `dovecot.cf`) for IMAP 143
  / submission 587. `matrix.include` carries `AS_TEST_DAV_HOMESET=/dav.php/calendars/{user}/`
  and `AS_TEST_DAV_CONTACTS_HOMESET=/dav.php/addressbooks/{user}/` (sabre/dav's `/dav.php/…`
  roots, keyed by the full `user@example.com`). Baikal is a **self-provisioning custom image**
  (`docker/backends/baikal/Dockerfile` + `seed.php`): it bakes a pre-installed `baikal.yaml`
  (Basic auth, realm `BaikalDAV`) and a pre-seeded SQLite DB (users user1/user2, one default
  calendar + address book each — `digesta1 = md5("user@example.com:BaikalDAV:pass")`), so DAV
  works on first boot with no web installer and no one-shot provisioner (the base image's
  `config`/`Specific` VOLUMEs initialise from the baked layers each `up`). JMAP/ManageSieve/
  free-busy tests skip via the capability gates; the iMIP-auto-schedule test skips because
  Baikal's DAV server and the mail companion are separate systems (`SkipOnStackFact("cyrus,baikal", …)`).
  The mailserver compose runs a one-shot
  `radicale-provision` (`docker/backends/mailserver/radicale/provision.sh`) that MKCALENDARs
  each user's default calendar + address book — Radicale auto-creates none, so without it DAV
  discovery finds nothing and every DAV test silently no-ops. Radicale lacks JMAP, ManageSieve
  and (advertised) CalDAV free-busy, so those tests skip via the capability gates above. 0.16 dropped the mounted-TOML + REST
  provisioning the old 0.13 stack used — config now lives in the data store and is written
  through Stalwart's own management API (schema-driven, `urn:stalwart:jmap`), only in a
  bootstrap mode that needs a restart to take effect. The `stalwart` backend is therefore a
  small **custom image** — `docker/backends/stalwart/Dockerfile` bakes **stalwart-cli** (from
  `ghcr.io/stalwartlabs/cli`) onto the pinned server. Its `entrypoint.sh` drives the dance
  in-process (bootstrap → restart → settings → restart → users) using the CLI **declaratively**:
  `stalwart-cli update Bootstrap` then `stalwart-cli apply <plan>.ndjson` (`bootstrap.json`,
  `provision-settings.ndjson`, `provision-users.ndjson` — idempotent `upsert matchOn` /
  singleton `update`, schema-driven so payloads adapt on upgrade instead of being hand-rolled).
  It creates the users, plaintext IMAP 143 + submission 587 listeners (matching the retired
  0.13 ports) and a relaxed password/auth/ban policy so the trivial `pass` password keeps
  working, then serves mail + CalDAV/CardDAV + ManageSieve + the full JMAP surface (incl.
  calendars/contacts/vacation) from one container. Two gotchas baked into the entrypoint:
  the CLI caches the downloaded schema under `$HOME`, so it runs with `HOME=/tmp` (uid 2000's
  home is not writable); and the domain id for the users plan is resolved at runtime via
  `stalwart-cli query Domain`. It writes a `.provisioned` marker when done (the compose
  healthcheck gates on it); re-runs are idempotent. This one stack also backs the JMAP
  groupware tests (formerly a separate `stalwart-jmap` 0.16 stack, now removed) —
  `JmapGroupware*` in `TestBackend` default to it with a real user. Compose/CI/devcontainer
  use `build:` (the custom image); the GitHub workflow `docker build`s it then runs it.
- **Axigen** (`docker/backends/axigen`, `AS_TEST_STACK=axigen`) — full groupware
  (IMAP/SMTP + CalDAV/CardDAV, no JMAP/ManageSieve) from one container via Axigen's built-in
  **3-day trial/demo mode** (no license key). DAV home sets are `/Calendar/` and `/Contacts/`
  (`matrix.include`; Axigen reports each from the mailbox root, and `/Calendar/Tasks/` ships
  out of the box). It is a **self-provisioning custom image** (`Dockerfile` + `ax-entrypoint.sh`
  + `provision.py`): a fresh demo starts with only the log/processing/cli/webadmin services and
  no domain, so the entrypoint boots Axigen once, drives the CLI (port 7000) to create domain
  `example.com` + users user1/user2 and open the canonical listeners (IMAP 143, submission 587,
  HTTP/DAV 80), `SAVE CONFIG`, then **edits the top-level `services` list** in the saved
  `axigen.cfg` to enable `imap smtpIncoming smtpOutgoing webmail` (there is no CLI verb for that
  list) and execs the final server. A `.provisioned` marker makes it idempotent; `down -v`
  gives each run a fresh trial. **Licensing:** trial mode is evaluation-only; running it on every
  push is an accepted trade-off, so the leg runs on every trigger like the others and gates
  `publish` (the `RUN_LEG` gate only excludes the disabled `cyrus`). Axigen enforces auth and
  emails iMIP invitations from the same system, so the auth-rejection and CalDav-auto-schedule
  tests run (not skipped); JMAP/ManageSieve tests self-skip by probe.
- **Apache James** (`docker/backends/james`, `AS_TEST_STACK=james`) — a second, independent
  (Java) implementation of **IMAP + SMTP submission**, run with **no CalDAV/CardDAV**
  (`AS_TEST_DAV_URL=none`). The `none` sentinel makes `TestBackend.DavUrl` null, so
  `GatewayFixture` leaves calendar/contacts/notes on the **local stores** and the DAV-only tests
  skip via the new **`[DavBackendFact]`** gate (skips when `DavUrl is null`); the retagged DAV
  tests are `DavRoundTripTests` (both) — `DavTaskTests`/`SharedCalendarTests` already self-guard,
  and the CalDav-auto-schedule test adds `james` to its `SkipOnStackFact` list. Stock JMAP serves
  no usable routes for our client (JMAP probe skips), and **ManageSieve is left disabled** — James
  advertises STARTTLS in its ManageSieve greeting even with startTLS off, but the actual STARTTLS
  negotiation hangs the gateway's Sieve client, so with no 4190 listener the Oof/Sieve tests skip
  cleanly (as on Cyrus). It is a **self-provisioning custom image** (`Dockerfile` + `entrypoint.sh`):
  the Dockerfile flips config knobs (`imapserver.xml` `plainAuthDisallowed` → false for the
  fixture's plaintext LOGIN; `smtpserver.xml` announce=always + requireSSL=false so the 587
  submission listener advertises AUTH over plaintext to any source — stock James only announces it
  to unauthorized addresses after STARTTLS, so CI's 127.0.0.1 client would never see it), and —
  because the memory server keeps users in RAM — the entrypoint starts James in the background,
  waits for WebAdmin (port 8000, no auth), `PUT`s domain example.com + users user1/user2 **plus
  their Trash/Sent/Drafts/Outbox mailboxes** (James creates only INBOX; MoveItems-to-Trash needs
  them), drops a `.provisioned` marker, and hands the foreground back. The healthcheck gates on
  that marker + IMAP 143, so `up --wait` blocks until provisioning is done (no separate one-shot,
  which a single-service backend cannot depend on without a cycle).
- Tests use **GUID subjects/markers** and poll with `WaitUntil` — never assume an empty
  mailbox and never assert on absolute counts. `MailboxJanitor` exists for best-effort
  purges when needed.
- Read-only revert semantics: the reverting Add/Change usually arrives **in the same Sync
  response** as the suppressed command (the diff runs after client commands) — check the
  command's own response before polling.
- Test backends speak **plaintext**; the gateway fixture sets the MailStore/MailSubmit
  role `Security="None"` because Stalwart advertises STARTTLS with a self-signed cert and
  MailKit would otherwise fail the upgrade. `MailTransportSecurity` in Backends is the
  single mapping point for these options.
- TLS certificate validation callbacks come **only** from
  `ServerCertificateValidator` (Backends root) — MailKit and SocketsHttpHandler share the
  delegate type. Never inline a validation callback. Knobs per backend section:
  `AllowInvalidCertificates` (accept everything, lab use) and `CaCertificatePath`
  (PEM CAs trusted on top of the system store via CustomRootTrust).
- Fast per-change check: `scripts/test-fast.ps1` (or `.sh`) runs **stalwart + axigen in parallel**
  and **leaves both stacks running** (start only if not already healthy; reused when warm). To
  coexist, these two use **dedicated host ports** — stalwart `10143/10587/10190/10232`, axigen
  `20143/20587/20232` — passed through the compose `${STALWART_*}`/`${AXIGEN_*}` port vars (which
  default to canonical, so CI/devcontainer are unaffected). That frees the canonical set
  (`143/587/5232/4190`) so an on-demand `test-backends` leg (e.g. baikal) can run alongside them
  without a port clash. `-f`/`-Filter` sets the filter; `-d`/`-Down` tears both down (default:
  leave running). Two isolated `dotnet test --no-build` processes (own env, own SQLite temp DB,
  in-proc TestServer). Don't drive stalwart/axigen through both runners at once (dedicated vs
  canonical ports → compose recreates the container).
- Local all-stacks run: `scripts/test-backends.ps1` (or `.sh`) brings each backend compose
  up `--wait`, runs `Category=Integration` with the right `AS_TEST_STACK`, tears it down, and
  prints a per-backend summary. Sequential (the stacks share host ports 143/587/5232). `-p`
  adds a throwaway Postgres for CI parity; default is SQLite temp DBs.
- GitHub workflow (`.github/workflows/build.yaml`): a **three-job** pipeline, one compile.
  - `test` — builds the Dockerfile `test` stage (compiles + runs unit tests once) and exports
    it to a `type=gha` build cache. Uses the default docker-container buildx driver (the old
    single-daemon containerd-store step is gone; the job split replaces daemon layer reuse
    with the shared cache).
  - `integration` — `strategy.matrix.backend: [stalwart, mailserver, cyrus, baikal, james, axigen]`
    (via `RUN_LEG`: cyrus is temporarily disabled — never runs; every other leg, incl. axigen,
    runs on every trigger), `fail-fast: false`,
    `needs: test`. Each leg loads the cached test image (`cache-from: type=gha`, no
    recompile), `docker compose ... up --wait`s its backend + a Postgres sidecar, warms mail,
    and runs the suite `--no-build` with `--network host` (so `localhost` reaches the
    published backend/Postgres ports). Legs push nothing. Adding a backend = one matrix entry
    + a `docker/backends/<name>/docker-compose.yml`.
  - `publish` — `needs: [test, integration]`, so it runs only when **every** backend leg is
    green. Loads the cached test image for the NuGet-pack and zip steps, then builds+pushes
    the multi-arch runtime image (`cache-from: type=gha`), publishes NuGet, uploads the zips
    and creates/attaches the release. This is the only pushing job; nothing is published until
    every backend leg passes. Pushes to ghcr.io with the built-in GITHUB_TOKEN; no repository
    secrets are needed.
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

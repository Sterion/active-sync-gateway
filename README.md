# ActiveSync Gateway

> ## 🤖 This project is 100% AI-generated
>
> Every line of code, every test and this README were written by an AI (Anthropic's
> Claude, driven through Claude Code). A human chose the goals, reviewed the results and
> runs the deployments — but hand-wrote none of it. Judge, audit and trust it accordingly.

A .NET 10 service that speaks **Microsoft Exchange ActiveSync (EAS) 16.1** to mail clients
(iOS Mail, Android mail apps, Outlook) and translates everything to standard backends hosted
elsewhere:

| Data class | Backend | Library |
|-----------|---------|---------|
| Mail      | IMAP    | MailKit |
| Sending   | SMTP    | MailKit |
| Calendar  | CalDAV *(or gateway DB)* | in-house WebDAV client + Ical.Net |
| Contacts  | CardDAV *(or gateway DB)* | in-house WebDAV client + FolkerKinzel.VCards |
| Tasks     | CalDAV VTODO collection *(or gateway DB)* | Ical.Net (VTODO) |
| Notes     | gateway DB (always) | Ical.Net (VJOURNAL) |

Same role as [Z-Push](https://github.com/Z-Hub/Z-Push), but implemented from the Microsoft
open specifications (MS-ASHTTP, MS-ASWBXML, MS-ASCMD, MS-ASAIRS, MS-ASEMAIL, MS-ASCAL,
MS-ASCNTC, MS-ASTZ) in modern async C#.

## Documentation

This README is the overview and getting-started guide. The reference material lives in
[`docs/`](docs):

- **[Backend capability matrix](docs/backends.md)** — what each provider (`imap`, `jmap`,
  `caldav`, `carddav`, `sieve`, `local`) can and can't express, per role.
- **[Configuration & option reference](docs/configuration.md)** — every `ActiveSync:*`
  setting: default, meaning and live-vs-restart tier.
- **[Operator CLI (`eas`)](docs/cli.md)** — inspection, user & device management, global
  settings, secret helpers.
- **[Web interfaces (`/admin` + `/user`)](docs/webui.md)** — the browser admin console and
  self-service portal: bootstrap, OIDC, and the security model.
- **[Writing a backend plugin](docs/plugins.md)** — ship a provider out-of-repo.
- **[Testing & backend stacks](docs/testing.md)** — the integration suite, the CI pipeline
  and how to run each backend.

## Features

**Protocol**

- The full command set through **EAS 16.1**: FolderSync and folder create/rename/delete,
  two-way Sync for mail/contacts/calendar/tasks/notes (including recurring tasks and
  mail **categories** ↔ IMAP keywords), Ping (long-poll push),
  GetItemEstimate, ItemOperations (item + attachment fetch, EmptyFolderContents),
  GetAttachment, SendMail, SmartReply, SmartForward, MoveItems, Search and the 16.1
  **Find** command (mailbox + GAL via CardDAV, **contact photos** included on request),
  Settings, MeetingResponse, ResolveRecipients (photos and **free/busy** too).
  Advertises versions 12.1–16.1.
- **Free/busy** (ResolveRecipients Availability → MergedFreeBusy): the requesting user's
  own availability is computed from their calendar (CalDAV `free-busy-query`, or the
  local store); other recipients work only where the DAV server lets the requester read
  their calendars (e.g. an explicit `HomeSetPath` template plus server-side grants) and
  degrade to a clean per-recipient "no data" status otherwise — RFC 6638 scheduling is
  not implemented because no supported backend offers it.
- **Shared calendars**: extra CalDAV collections sync as additional calendar folders —
  statically via `Backends:Calendar:SharedCollections` or per user at runtime via `eas share`
  grants, each read-write or **gateway-enforced read-only**.
- **Meeting invitations (iMIP)**: create/update/cancel a meeting on the phone and the
  gateway mails the attendees (`METHOD:REQUEST`/`CANCEL`) — unless the CalDAV server
  schedules on its own (`calendar-auto-schedule`/schedule outbox, probed automatically),
  in which case the gateway stays silent to avoid double invites. Reminder-only edits
  never mail; attendee replies flow back via the existing MeetingResponse iTIP handling.
- **EAS 16.x features**: server-synced **Drafts** (compose on one device, finish on
  another; `Send` submits a draft), calendar **event attachments** (stored inline in the
  event, works on any CalDAV server and the local store), 16.x calendar semantics
  (partial updates, single-occurrence deletes, structured locations), body-less
  SmartForward with Forwardees, and **account-only remote wipe** (removes the account
  from a device — never a factory reset).
- **Autodiscover** (`POST /autodiscover/autodiscover.xml`, Outlook MobileSync schema) —
  clients can be added with just email + password instead of a manually typed server.
- **Out-of-office from the phone** (Settings→Oof) backed by **ManageSieve**: enabling the
  auto-reply in the mail app uploads a gateway-owned sieve `vacation` script (scheduled
  windows supported) and disabling it restores whatever sieve script was active before.
- **Device security policies** (MS-ASPROV): optionally require a device PIN (length,
  complexity, auto-lock, wipe-after-N-failures), storage encryption and an attachment size
  cap — enforced with the standard 449 re-provision handshake, including automatic fleet
  re-provisioning after a policy change. Off by default (then Provision is a no-op stub,
  equivalent to Z-Push `LOOSE_PROVISIONING`). No remote wipe, by design.
- **Differential sync engine** (Z-Push style): each round diffs a stored snapshot against
  the backend's current revision map (IMAP UID+flags, DAV ETags), so it works against any
  server — no CONDSTORE/QRESYNC/sync-collection support required.
- One generation of **SyncKey replay history** per collection plus a cached last-request
  shape for empty Sync requests, so client retries and resends never desync a mailbox.

**Push**

- Persistent per-(user, folder) **IMAP IDLE** watchers, shared by all the user's devices
  and surviving across requests, give sub-second mail push; events are latched, so a
  change that fires between two Pings reaches the next one instantly. Degrades to STATUS
  polling automatically when the server lacks IDLE.
- DAV collections poll ctag/sync-token; local stores signal in-process.
- A **watchdog** re-checks exact revision maps at a fixed cadence during every wait, so a
  change can never sit undetected behind a silently broken IDLE connection.
- **Fast, clean shutdown**: parked long-polls are answered normally (Ping status 1, empty
  Sync) the moment the host stops — restarts don't strand devices mid-heartbeat, and the
  process exits in milliseconds instead of waiting out heartbeats.

**Backends**

- IMAP/SMTP via MailKit; CalDAV/CardDAV via an in-house WebDAV client with **RFC 6764
  discovery** or explicit home-set templates (`/{user}/`); Tasks ride a CalDAV VTODO
  collection when the server has one.
- Per-backend **TLS knobs**: explicit security mode (implicit TLS / STARTTLS / none), an
  extra CA bundle for private PKIs, accept-any-certificate for labs.
- **Local fallback stores**: omit the CalDAV/CardDAV config and contacts, calendar and
  tasks are served from the gateway's own database (Notes always are) — encrypted at rest
  with **AES-256-GCM** and pushed to the user's other devices near-instantly.

**Users & authentication**

- The baseline needs **zero user administration**: HTTP Basic credentials are validated by
  an IMAP login probe and passed through to all backends.
- **Per-user overrides** from three sources — appsettings, a mounted users file, or the
  **state database via `eas user ...`** (no config edit, no redeploy; a running gateway
  picks database edits up within ~1 s). Override only what differs: another SMTP login, a
  personal DAV server, a fully decoupled phone password (PBKDF2 hash), sealed backend
  secrets (`enc:v1:`, AES-GCM under the master key).
- `RequireDeclaredUsers` **allowlist mode**, per-user/per-device **login blocking** (403),
  brute-force throttling per (client address, user), and negative/positive auth caches
  that shield the IMAP server from password floods.
- The startup banner lists every declared user with origin (`[config]` / `[db]`) and all
  overridden fields — passwords only ever render as masked markers.

**Operations**

- Single ~100 MB (compressed) Docker image: non-root, container `HEALTHCHECK`, release
  builds stamped with the git tag they were built from.
- **HTTPS out of the box**: alongside HTTP on `:5080`, the gateway serves HTTPS on `:5443`
  with a self-signed certificate generated on first start and persisted in the state
  database — unique per deployment, identical across restarts and replicas, fingerprint in
  the startup banner. Point `ActiveSync:Tls:CertificatePath` at a mounted certificate
  (PEM/PFX, e.g. cert-manager/ACME) to serve that instead; inspect either on the admin
  **TLS** page or with `eas tls`.
- The entire admin surface ships inside the image as the **`eas` CLI** — inspection, user
  management, blocking, purging, secret sealing (see [Operator CLI](docs/cli.md)).
- **SQLite** (zero-config default) or **PostgreSQL** (accepts `postgresql://` URIs, e.g.
  straight from a CloudNativePG secret); EF Core migrations apply automatically at startup.
- Stateless apart from the shared database: **multiple replicas behind any load balancer
  work without sticky sessions**.
- **Read-only mode**: a pure mirror that accepts every client write on the wire and
  silently reverts it.
- Serilog structured logging; the startup banner prints the full effective configuration
  with secrets redacted.

**Quality**

- ~500 automated tests, including an integration suite that hosts the gateway in-process
  and drives it with a real WBXML-speaking mini-EAS client against **live backends**
  (Stalwart; docker-mailserver + Radicale; Cyrus IMAP; Baikal + docker-mailserver; Apache James;
  and Axigen in trial mode).
- CI compiles the solution exactly once and only pushes an image after both the unit and
  integration suites pass.
- Async end-to-end: all I/O uses async/await with `CancellationToken` propagation;
  sync-over-async is blocked by analyzer rules (VSTHRD002/VSTHRD103 as errors).

## Out of scope (by design)

Full-device RemoteWipe (a gateway config file should never be able to factory-reset a
phone — use the 16.1 account-only wipe or block the device instead), S/MIME
(ValidateCert), SMS class.

## Quick start

The server starts with the explicit `serve` command (`dotnet run` passes it via
launchSettings; the Docker image's default command is `serve`). Bare `eas` does **not**
start anything — it prints the effective-configuration banner (or the validation errors
serve would die with) and exits, and `eas help` lists all commands.

```bash
dotnet run --project src/ActiveSync.Server          # serve; listens on :5080 by default
# or
docker build -f docker/Dockerfile -t activesync-gateway .
docker run -p 443:5443 -p 5080:5080 -v activesync-data:/data \
  -e ActiveSync__Backends__MailStore__Provider=imap \
  -e ActiveSync__Backends__MailStore__Host=imap.example.com \
  -e ActiveSync__Backends__MailSubmit__Provider=smtp \
  -e ActiveSync__Backends__MailSubmit__Host=smtp.example.com \
  -e ActiveSync__Encryption__Key='any passphrase - or a key from: openssl rand -base64 32' \
  activesync-gateway
```

The published container image is **multi-arch** (`linux/amd64` + `linux/arm64`) — it runs
as-is on a Raspberry Pi or any ARM box.

Prefer running without docker? Every release on the releases page also ships
`eas-gateway-linux-x64-<tag>.zip`, `eas-gateway-linux-arm64-<tag>.zip` and
`eas-gateway-windows-x64-<tag>.zip` — install the
[ASP.NET Core Runtime 10](https://dotnet.microsoft.com/download/dotnet/10.0), unzip, and
run `./ActiveSync.Server serve` (`ActiveSync.Server.exe serve` on Windows). Branch builds
of the same zips hang off each CI run as artifacts.

On the phone, add an **Exchange** account: email + password is enough when the client
supports Autodiscover; otherwise enter the server (your host) manually, username = full
email address, no domain. Phones insist on HTTPS — `:5443` serves it immediately with an
auto-generated self-signed certificate (the device asks once to trust it; fingerprint is
in the startup banner). For real certificates or a reverse proxy, see
[HTTPS](#https-self-signed-by-default-or-bring-your-own-certificate).

Quick protocol smoke test:

```bash
curl -i -X OPTIONS http://localhost:5080/Microsoft-Server-ActiveSync
# → MS-ASProtocolVersions: 12.1,14.0,14.1,16.0,16.1
```

## Backend capability matrix

Each role is filled by a **provider** (`imap`, `jmap`, `smtp`, `caldav`, `carddav`, `sieve`
or `local`, plus any plugin). Providers differ in what they can express — a protocol either
carries a feature or it doesn't, and some mappings are lossy. The full per-provider
comparison for every role (mail, contacts, calendar, tasks, notes, out-of-office), including
push-vs-poll latency and every partial/unsupported cell, is in
**[docs/backends.md](docs/backends.md)**.

## Configuration

**Settings live in layers — the database wins, and it's all CLI-settable.** Every global
setting can be set with [`eas config set <key> <value>`](docs/cli.md#global-settings) and is
stored in the state database. From highest precedence to lowest:

1. **Database** — set via `eas config set` or the web admin. Applies to every running replica
   within ~1 s, no restart — except a few listener settings (HTTP/HTTPS ports, self-signed-TLS,
   metrics enable/port), which apply on the next restart.
2. **`appsettings.json` / environment variables** — file and env defaults.
3. **Built-in code defaults** — everything shown below is a code default or example.

The gateway starts with **no mail configuration at all**: with only the bootstrap Encryption
key set it runs **unconfigured** (EAS/Autodiscover answer 503; `/readyz` stays ready and
reports `"configured": false`) until you point it at a backend — from the web admin's
**Backends** page or with `eas config set`. The shipped `appsettings.json` therefore carries
only the two **bootstrap** sections, `Database` and `Encryption`: they open and decrypt the
database that stores everything else, so they are the only settings that cannot live in it.

One backend set serves all users; each user authenticates with their own credentials
(HTTP Basic, validated by the MailStore provider's login probe and passed through to all
backends). Backends are configured per **role** under `ActiveSync:Backends`, each naming
the **provider** that serves it. The web admin's **Backends** page does this in a browser —
pick a provider per role and fill the fields it asks for, rendered from the provider's own
description of them — and stores the result as database overrides. To set it in the config
file instead, edit `src/ActiveSync.Server/appsettings.json`:

```jsonc
"ActiveSync": {
  "Backends": {
    "MailStore":  { "Provider": "imap",   "Host": "imap.example.com", "Port": 993, "UseSsl": true },
    "MailSubmit": { "Provider": "smtp",   "Host": "smtp.example.com", "Port": 465, "UseSsl": true },
    "Calendar":   { "Provider": "caldav", "BaseUrl": "https://dav.example.com", "HomeSetPath": "" },  // "" → RFC 6764 discovery
    "Tasks":      { "Provider": "caldav" },                                    // VTODOs in the calendar home set
    "Contacts":   { "Provider": "carddav", "BaseUrl": "https://dav.example.com", "HomeSetPath": "" }, // or e.g. "/{user}/"
    "Oof":        { "Provider": "sieve",  "Host": "mail.example.com", "Port": 4190 }  // optional; out-of-office
  },
  "Database": { "Provider": "Sqlite", "ConnectionString": "Data Source=activesync.db" },
  "Encryption": { "Key": "<any passphrase — or a raw key from: openssl rand -base64 32>" },
  "Eas": { "MaxHeartbeatSeconds": 1770, "DavPollSeconds": 60, "WatchdogSeconds": 60 }
}
```

- **Roles**: `MailStore`, `MailSubmit`, `Calendar`, `Tasks`, `Contacts`, `Notes`, `Oof`.
  Each role section has one host-reserved key — `Provider` (the backend implementation:
  `imap`, `jmap`, `smtp`, `caldav`, `carddav`, `sieve`, `local`, plus any third-party
  plugin providers) — plus provider-owned settings the host never interprets. One
  provider can fill several roles: `caldav` serves both Calendar and Tasks over one
  connection, and `jmap` serves MailStore + MailSubmit over one HTTP session.
- **JMAP** (e.g. Stalwart): assign the mail roles to the `jmap` provider with a single
  `BaseUrl` — `"MailStore": { "Provider": "jmap", "BaseUrl": "https://mail.example.com" }`
  and the same for `MailSubmit`. The gateway discovers the session resource at
  `{BaseUrl}/.well-known/jmap`, reuses the raw-RFC822 blob for message bodies, and submits
  via `EmailSubmission`. OOF (`Oof` role) uses JMAP `VacationResponse`; contacts (`Contacts`
  role) map JSContact ↔ EAS; calendar (`Calendar` role) bridges JSCalendar ↔ iCalendar ↔ EAS
  (common event fields — advanced recurrence overrides are not yet round-tripped). Tasks and
  Notes have no JMAP standard — keep them on `caldav`/`local`. Contacts and calendar also
  stay available over CalDAV/CardDAV (same data), and JMAP calendars/contacts need a current
  Stalwart (0.16+) — the 0.13 line advertises only mail/OOF over JMAP.
- **MailStore and MailSubmit are mandatory** — startup fails with a clear validation error
  when either role is missing (a mail gateway without mail access makes no sense).
- **Calendar / Tasks / Contacts / Notes are optional**: omit the role (or don't assign a
  provider) and it falls back to the **`local` provider** — served from the **gateway's
  own database**, stored as vCard/iCalendar rows visible to all of the user's ActiveSync
  devices and nowhere else (no webmail/DAV client will see them). Changes push to other
  devices near-instantly via an in-process notifier. **Notes are always local** (no DAV
  backend carries them). With local stores in play the state database holds real user
  data, not just sync state — back it up accordingly.
- **Tasks over CalDAV requires the explicit `Tasks` role** — `"Tasks": { "Provider":
  "caldav" }`. It inherits the `Calendar` section's settings as its base (share the
  server), so it usually only needs `Provider` and optionally `TaskFolder`. Without the
  role, tasks are stored locally.
- `HomeSetPath` supports `{user}` and `{localpart}` placeholders (Radicale/Baikal style is
  `/{user}/`). Leave empty for `.well-known` + `current-user-principal` discovery.
- `Database.Provider` may be `Sqlite` or `Postgres`
  (`"Host=db;Database=activesync;Username=...;Password=..."`, or a
  `postgresql://user:password@host:5432/database` URI — e.g. straight from a
  CloudNativePG secret — which implies `Postgres` by itself).
- **An `Encryption` key is mandatory** — locally-stored content is encrypted at rest (see
  the `Encryption` section of [docs/configuration.md](docs/configuration.md)). Startup fails without a key unless
  `Encryption.AllowPlaintext=true` is set explicitly (dev/test only).
- **Backend TLS**: every network provider (`imap`, `jmap`, `smtp`, `caldav`, `carddav`,
  `sieve`) supports two certificate knobs in its role section. `"CaCertificatePath": "/path/to/ca.pem"`
  trusts the CAs in that PEM file *in addition to* the system store — the right choice for a
  private PKI / self-signed setup. `"AllowInvalidCertificates": true` disables certificate
  validation entirely (lab use only; it wins over `CaCertificatePath` and is called out as
  `certs=ACCEPT-ANY`-style in the startup banner). A configured CA file that is missing or
  not valid PEM fails startup validation.

### Full option reference

Every option lives under the `ActiveSync` section and can be set via `appsettings.json`,
environment variables (`__` separator, e.g. `ActiveSync__Backends__MailStore__Host=...`) or
the database (`eas config set`, which wins). The **complete reference — every key, its
default, and whether it applies live or on restart — is in
[docs/configuration.md](docs/configuration.md)**, grouped by section (Root, the per-provider
Backends settings, Database, Encryption, Eas, Auth, Log, Tls, Policy, Metrics,
WebUi, Cli, Plugins).

### Per-user overrides

Pass-through is always the baseline: the EAS credentials are forwarded to every backend. A
`Users` entry is a pure **overlay** — declare only the users who need something different
(another SMTP login, a personal DAV supplier, a different mail host or user name) and
override only the fields that differ. Everyone without an entry keeps working untouched.
Unset passwords inherit the presented EAS password, per user *and* per role.

Each entry is `{ Password?, MailAddress?, Backends: { "<Role>": {...} } }`, and each role
override has host-reserved `Enabled` / `Provider` / `UserName` / `Password` plus a free-form
`Settings` map that overlays the global role section. Setting `Provider` **switches** the
provider for that user (its `Settings` then start fresh, since a different provider's keys
mean something else); leaving `Provider` unset overlays `Settings` onto the global section
(a user list like `Settings:SharedCollections:0` **replaces** the whole global list).
`Enabled: false` sends a content role to the `local` provider (and turns Oof off); it is
invalid on the two mail roles.

```jsonc
"ActiveSync": {
  "Backends": {
    "MailStore":  { "Provider": "imap", "Host": "imap.example.com" },  // global defaults, per-user overridable
    "MailSubmit": { "Provider": "smtp", "Host": "smtp.example.com" }
  },
  "Users": {
    "anna@example.com": {
      // No passwords: anna's phone password stays her mail password (probe validates it);
      // only her contacts live elsewhere, with their own credentials.
      "Backends": {
        "Contacts": { "Provider": "carddav", "UserName": "anna", "Password": "enc:v1:...",
                      "Settings": { "BaseUrl": "https://cloud.example.com" } }
      }
    },
    "ben@example.com": {
      // Only the SMTP login differs; everything else is plain pass-through.
      "Backends": { "MailSubmit": { "UserName": "relay-ben", "Password": "relay-pw" } }
    },
    "phone-carla": {
      // Fully decoupled: own phone password, mapped to a real mailbox.
      "Password": "pbkdf2$200000$...$...",         // hash-password verb
      "MailAddress": "carla@example.com",
      "Backends": { "MailStore": { "UserName": "carla@example.com", "Password": "enc:v1:..." } }
    }
  }
}
```

**How a login is authenticated** (per user, first rule that applies):

| # | Entry has | The phone's password is |
|---|-----------|-------------------------|
| 1 | `Password` (pbkdf2$ or plaintext) | verified against it locally — fully decoupled from the mail backend |
| 2 | `Backends:MailStore:Password` (no `Password`) | pinned: it must equal the configured MailStore password (timing-safe compare, no probe) |
| 3 | neither | the mail password — validated by the MailStore provider's login probe against the user's *effective* host/user name (overrides apply) |
| 4 | *(no entry)* | same as 3 with the global MailStore section — classic pass-through |

#### Database-declared users (`eas user ...`)

The exact same entries can live in the **state database**, managed entirely by the CLI —
no config edit, no redeploy. Three sources, per-login precedence:

1. `ActiveSync:Users` in appsettings / environment variables,
2. the `UsersFile` (merged over 1 at startup),
3. **database entries** — a database row **replaces the whole config entry** for that
   login; `eas user remove` falls back to the config entry.

A running gateway notices database edits within `Auth:UsersRefreshSeconds` (default **1 s**;
one primary-key point-read on the next request — `0` checks every request, negative
disables live pickup). An edit also resets the auth verdict caches, so a rotated gateway
password applies to the very next request. Database entries count as declared for
`RequireDeclaredUsers`, and the startup banner lists every user with its origin
(`[config]` / `[db]` / `[db, shadows config]`) and all overridden fields — passwords render
only as `***(pbkdf2)` / `***(sealed)` / `***(PLAINTEXT)` markers.

```bash
kubectl exec <pod> -- eas user add phone-dana                         # allowlist grant
echo -n 'phone-pw' | kubectl exec -i <pod> -- eas user password phone-dana
kubectl exec <pod> -- eas user set phone-dana MailAddress dana@example.com
kubectl exec <pod> -- eas user set phone-dana Backends:MailStore:UserName dana@example.com
echo -n 'imap-pw' | kubectl exec -i <pod> -- eas user secret phone-dana Backends:MailStore:Password
kubectl exec <pod> -- eas user show phone-dana
```

`user set` addresses every field by its path — `MailAddress`, `Password`, and per-role keys
`Backends:<Role>:Provider|Enabled|UserName|Password` plus free-form provider settings under
`Backends:<Role>:Settings:<Key>` (e.g. `Backends:MailStore:Settings:Host`). It accepts
password keys too: plaintext values are hashed (`Password` → pbkdf2$) or sealed
(`Backends:<Role>:Password` → `enc:v1:`) on the spot, already-prepared values are stored
verbatim — but plaintext on the command line lands in shell history, so the stdin forms
above are preferred (the CLI warns).

Rules worth knowing:

- **Merging**: any field a user sets wins; anything unset inherits the global section.
  SMTP/DAV credentials default to the effective IMAP credentials; the IMAP user name
  defaults to the gateway login. A per-user DAV section with its own `BaseUrl` works even
  without a global one; `"Enabled": false` disables a globally-configured DAV side for
  that user.
- **Secrets**: backend passwords may be plaintext (put the file in a Secret) or
  `enc:v1:...` values sealed with the `Encryption` master key (file can then live in a
  ConfigMap): `echo -n 'imap-password' | ActiveSync.Server protect`. Gateway password
  hashes: `echo -n 'phone-password' | ActiveSync.Server hash-password`. In a running
  container the same verbs are available as the `eas` command — see
  [Operator CLI](docs/cli.md).
- **Allowlist**: `"RequireDeclaredUsers": true` rejects any login without a `Users` entry
  before touching a backend. An empty entry (`"dora@example.com": {}`) grants access with
  zero overrides.
- **Auto-provisioning** (`AutoProvisionUsers`, **on by default**): the first time a plain
  pass-through login clears its backend probe over EAS, the gateway writes an empty database
  entry for it (marked `[db, auto-provisioned]`). The user then shows up in `eas users`/the
  admin UI, can be blocked, and can sign in to the self-service portal (verified against the
  same backend); auth itself is unchanged (the row has no gateway password, so it still
  probes). This is what ties the "who has synced" view to the "declared accounts" view — every
  real user becomes a first-class account. Set it to `false` to keep pass-through logins
  ephemeral (nothing persisted). Inert under the allowlist (which rejects the login before it
  can authenticate). Deleting an auto-created row is not permanent while the flag is on: the
  user's next sync re-creates it — `eas block` them, or turn the flag off, to make removal stick.
- **Identity is the gateway login.** Sync state, locally-stored items and their encryption
  are all keyed by it — keep logins stable (e.g. when introducing an entry for an existing
  user, keep the login they already sync with) or devices re-sync from scratch and locally
  stored items become unreadable.
- **Pickup semantics**: config and `UsersFile` entries are loaded once at startup (edits
  need a restart); database entries are picked up live within `Auth:UsersRefreshSeconds`.
- Rules 1/2 verify locally, so the per-address `Auth` throttle is the primary brute-force
  defense there — keep it enabled.

Kubernetes shape: mount a Secret (or ConfigMap when using `enc:` values) containing
`users.json` and point at it with one variable —

```yaml
env:
  - name: ActiveSync__UsersFile
    value: /etc/activesync/users.json
volumeMounts:
  - name: users
    mountPath: /etc/activesync
    readOnly: true
```

**Logging** uses standard [Serilog configuration](https://github.com/serilog/serilog-settings-configuration)
under the `Serilog` section — note that the stock ASP.NET Core `Logging` section is
**ignored** (Serilog replaces that pipeline). To see Debug output locally, set
`"Serilog": { "MinimumLevel": { "Default": "Debug" } }`, not `Logging:LogLevel`.

**Log output shape** is controlled by two gateway knobs (`ActiveSync:Log:Mode` ×
`ActiveSync:Log:Format`):

| Mode | `Format=Text` line looks like |
|------|-------------------------------|
| `Simple` | `[17:42:23 INF] Ping: changes in INBOX for anna@example.com` |
| `Standard` *(default)* | `2026-07-17 17:42:23.123 +02:00 \| Information \| ActiveSync.Server.Eas.Handlers.PingHandler \| Ping: changes in INBOX for anna@example.com` |
| `Extended` | Standard + ` \| {"Folders":"INBOX","User":"anna@example.com","ThreadId":14,"MachineName":"active-sync-…"}` — every structured property of the event, plus thread id and machine (pod) name |

`Format=Json` emits **CLEF** (compact JSON, one object per event with `@t`, `@l`, `@m`,
`SourceContext` and all properties) — the right feed for Grafana Loki or Elastic: fields
are queryable (`{app="active-sync"} | json | SourceContext="ActiveSync.Backends.Smtp"`)
and multi-line payload dumps from the Verbose tier become single events. In Json,
`Simple`/`Standard` are identical (CLEF always carries everything); `Extended` adds the
thread/machine enrichment. **The default changed in 1.0.7** from the Simple look to
Standard — set `ActiveSync__Log__Mode=Simple` for the old output. Escape hatch: declare
your own sinks under `Serilog:WriteTo` and the gateway adds no console sink at all — your
Serilog configuration has full control (templates, files, whatever).

**Verbose wire logging**: at the `Verbose` level the gateway dumps every interaction —
the decoded EAS request/response XML per command, Autodiscover bodies, the raw IMAP/SMTP
protocol exchange (`C:`/`S:` lines, tagged per connection), and every DAV request/response
with bodies. Enable it globally (`Serilog__MinimumLevel__Default=Verbose`) or per surface
via the ordinary override hierarchy:

```bash
Serilog__MinimumLevel__Override__ActiveSync.Server.Eas=Verbose     # client <-> gateway
Serilog__MinimumLevel__Override__ActiveSync.Backends.Imap=Verbose  # gateway <-> IMAP
Serilog__MinimumLevel__Override__ActiveSync.Backends.Smtp=Verbose  # gateway <-> SMTP only
Serilog__MinimumLevel__Override__ActiveSync.Backends.Dav=Verbose   # gateway <-> Cal/CardDAV
```

Credentials never appear — IMAP/SMTP authentication exchanges are masked (`********`) via
MailKit's secret detector, and DAV logging omits headers entirely (no `Authorization`).
Dumps are capped at 16 KB per event. **Everything else IS logged verbatim: mail bodies,
contacts, calendars — full personal data.** Treat wire logs as sensitive and enable them
only while debugging.
The listening address is the Kestrel endpoint in `appsettings.json` (`:5080`); override
it with the `Kestrel__Endpoints__Http__Url` environment variable. (Kestrel endpoint
configuration takes precedence over `ASPNETCORE_URLS`/`ASPNETCORE_HTTP_PORTS`, so use
the endpoint variable — the others would be ignored with a startup warning.)

### HTTPS: self-signed by default, or bring your own certificate

The gateway serves HTTP on `:5080` (for a TLS-terminating reverse proxy / ingress) **and its
own HTTPS listener on `:5443`** (`ActiveSync:Tls`) — publish only the port that matches your
setup. The HTTPS certificate is either self-signed (the default) or a file you mount; both are
viewable on the admin **TLS** page and with `eas tls`. Everything under `ActiveSync:Tls` is
**restart-tier** — the certificate is read once at startup (so a rotated mount, e.g. from
cert-manager/ACME, takes effect on the next restart, matching how Kubernetes mounts behave).

**Self-signed (zero config).** With no certificate configured, the first `serve` generates one
(RSA 2048, 20-year validity, CN/SAN from `PublicUrl` when set) and stores it **in the state
database**, private key sealed with the `Encryption` master key. Every restart and every
replica serves the same certificate, so a device only has to trust it once — the SHA-256
fingerprint is in the startup banner (and `eas tls`) for comparison against what the phone
shows. Nine and iOS Mail offer a one-tap "trust anyway"; **some clients — notably Gmail —
refuse untrusted certificates entirely**, so a mounted certificate (below) or a reverse proxy
remain the production path. Should the self-signed certificate ever need replacing, delete the
`ServerCertificates` row and restart.

**Mounted certificate.** Point `ActiveSync:Tls:CertificatePath` at a mounted certificate and
the gateway serves it instead of the self-signed one. A PEM pair (Let's Encrypt / certbot /
cert-manager layout) — a full chain plus its key:

```bash
docker run -p 443:5443 -v /path/to/certs:/certs:ro \
  -e ActiveSync__Tls__CertificatePath=/certs/fullchain.pem \
  -e ActiveSync__Tls__CertificateKeyPath=/certs/privkey.pem \
  -e ActiveSync__Backends__MailStore__Provider=imap \
  -e ActiveSync__Backends__MailStore__Host=imap.example.com \
  -e ActiveSync__Backends__MailSubmit__Provider=smtp \
  -e ActiveSync__Backends__MailSubmit__Host=smtp.example.com \
  -e ActiveSync__Encryption__Key='...' \
  -v activesync-data:/data activesync-gateway
```

The same paths can be set from the database instead — `eas config set
ActiveSync:Tls:CertificatePath /certs/fullchain.pem` (and `:CertificateKeyPath`) — then restart.

- Point `CertificatePath` at the **full chain** (leaf + intermediates, certbot's
  `fullchain.pem`), not just the leaf — phones are strict about incomplete chains.
- Have a PFX/PKCS#12 file instead? Set `ActiveSync:Tls:CertificatePath=/certs/cert.pfx`, leave
  `CertificateKeyPath` unset, and use `ActiveSync:Tls:CertificatePassword` if it is protected
  (stored sealed with the Encryption key).
- A configured certificate that cannot be loaded **fails startup** with a clear error rather
  than silently falling back to self-signed — so a bad mount surfaces immediately.
- **Keep the default HTTP endpoint** — the container `HEALTHCHECK` probes `/healthz` over
  it on the loopback. Simply don't publish port 5080; only the HTTPS port needs `-p`.
- The container runs as a non-root user, so keep the *inside* port above 1024 (`5443`
  here) and map `443` on the outside — EAS clients default to 443.
- Restart the container after renewing/rotating the certificate; it is read at startup.
- Turn the gateway's HTTPS off entirely (terminate TLS in front) with
  `ActiveSync:Tls:Enabled=false`.

### Metrics and readiness

Prometheus metrics are off by default; enable and (recommended) pin them to a dedicated
scrape port that stays inside the cluster:

```bash
docker run ... \
  -e ActiveSync__Metrics__Enabled=true \
  -e ActiveSync__Metrics__Port=9090 ...
```

`/metrics` then answers **only** on that port (gated on the connection's local port, not
spoofable headers); without `Port` it shares the main listeners — protect it via ingress
or network policy then. Everything is labeled **per account** by default (`user=...` on
request counts, synced-item counts by class/direction/operation, sent-mail counts by
kind, live session/IDLE-watcher/long-poll gauges); set `Metrics:PerUser=false` on large
multi-tenant fleets where that label cardinality would hurt. Backend errors count by
protocol, throttle rejections globally.

`/readyz` is a real readiness probe: cached (~10 s) checks of the state database plus each
configured backend role whose provider can probe itself — `mailstore` (TCP, no
credentials) and `calendar`/`contacts` (any HTTP answer counts, including 401). Component
names in the JSON are the role names; the probe returns 503 with per-component detail when
something is down. `/healthz` stays a trivial liveness 200 on purpose: a dead mail server
should drain traffic, not restart gateway pods.

A gateway that has no mail backend yet reports `"configured": false` but stays **ready** — it
is working, it just answers 503 on EAS until you configure it, and an orchestrator that never
routes traffic to it is an orchestrator you can never reach the admin UI through. (Before
1.1 this component failed the probe; deployments that gated on it will now see the pod go
healthy earlier.)

### Device security policies

Off by default. Enable `ActiveSync:Policy` to require a device PIN and more before mail
is handed out:

```bash
docker run ... \
  -e ActiveSync__Policy__Enabled=true \
  -e ActiveSync__Policy__DevicePasswordEnabled=true \
  -e ActiveSync__Policy__MinDevicePasswordLength=6 \
  -e ActiveSync__Policy__MaxInactivityTimeDeviceLock=300 \
  activesync-gateway
```

How it works (the standard MS-ASPROV handshake every EAS client speaks):

- Any request from a device that has not acknowledged the **current** policy is answered
  **HTTP 449** ("retry after Provision"). The client then runs the two-phase Provision
  handshake, receives the policy document, applies it (prompting the user to set a PIN if
  needed) and retries — from the phone this looks like a normal "your organization
  requires..." setup step.
- Every policy acknowledgment records a hash of the acknowledged document, so **changing
  any policy option automatically re-provisions the whole fleet** on their next request.
  No restart choreography, no manual resets.
- With `PasswordRecoveryEnabled`, compliant devices escrow a recovery password readable
  via `eas device password <user> <deviceId>` (stored sealed with the Encryption key).

Two honest caveats: compliance is **asserted by the device** — a well-behaved client
(iOS, Nine, Outlook, Gmail) genuinely enforces the PIN, but a malicious client can simply
lie in the handshake, exactly as with Exchange itself. And a client that cannot satisfy
the policy (or does not implement provisioning at all) is locked out of everything except
Provision — that is the point, but remember it when a device suddenly gets 449 loops
after you tighten the policy. There is deliberately **no remote wipe**: a gateway config
should never be able to factory-reset a phone. To cut a device off, use `eas block`.

### Read-only mode

Set `"ActiveSync": { "ReadOnly": true }` to make the gateway a pure mirror. All client
writes are suppressed:

- Item edits, flag changes, and deletes are **silently reverted** — the change is accepted
  on the wire, never applied to the backend, and the server's version is pushed back on
  the next sync round (no error banners on the phone).
- New items are rejected with a per-item status; moves fail and the item is re-pushed.
- SendMail/SmartReply/SmartForward return "submission failed" (the client shows an honest
  send error); folder create/rename/delete and meeting responses return error statuses.

### Upgrading

The state database schema is managed with **EF Core migrations**, applied automatically at
startup (`MigrateAsync`). Schema upgrades roll forward in place — no manual step, no data
loss. Each provider (SQLite / PostgreSQL) ships its own migration set. To add a migration
after changing an entity:

```bash
dotnet ef migrations add <Name> --context SqliteSyncDbContext \
  --project src/ActiveSync.Core --startup-project src/ActiveSync.Server --output-dir Migrations/Sqlite
dotnet ef migrations add <Name> --context NpgsqlSyncDbContext \
  --project src/ActiveSync.Core --startup-project src/ActiveSync.Server --output-dir Migrations/Npgsql
```

### Security notes

- **TLS**: run behind a reverse proxy, or let the gateway terminate TLS itself (see
  [HTTPS](#https-self-signed-by-default-or-bring-your-own-certificate)). EAS clients
  require HTTPS in practice, and credentials travel as Basic auth — the default
  self-signed endpoint encrypts the wire, but only real certificates (or a trusted proxy)
  protect against an impersonating middlebox. The DAV `BaseUrl` should be
  https too — the gateway forwards the user's credentials there on every request (it
  refuses redirects that would downgrade an https DAV base URL to http, or leave the host).
- **Brute force**: see the `Auth` options above; defaults throttle per client address and
  shield the mail server from repeated bad-password attempts. Users with a local password
  rule (gateway `Password` or pinned `Backends:MailStore:Password`) are verified without a
  backend round-trip, so the throttle is the primary brute-force defense for them.
- **Data at rest**: locally-stored item content (Notes always; Contacts/Calendar/Tasks
  without a DAV backend) is encrypted with AES-256-GCM under the `Encryption` key, bound
  to the owning user + collection so rows cannot be swapped between users. What stays
  plaintext by design: user names, item UIDs, per-item event dates (`ItemDateUtc`, needed
  for EAS filter windows — reveals *when* events exist, not their content), revision
  counters/timestamps, folder names, and sync-state metadata. Mail is never stored in the
  gateway database. Still protect the `/data` volume (permissions, backups) — and back up
  the key separately from the database.
- **Outgoing mail** is submitted over authenticated SMTP with the client's MIME as-is;
  sender-alignment policy is the SMTP server's job (or set `Backends:MailSubmit:ForceFrom`).

## Operator CLI (`eas`)

The Docker image puts an `eas` command on the PATH, so the whole admin surface is one line
inside any container (`kubectl exec <pod> -- eas users`, `docker exec <c> eas block <user>`).
It is a **slim forwarding client**: it POSTs the command to the running gateway's `/cli`
endpoint (sealed with the `ActiveSync:Encryption` master key) so it returns in a fraction of
a second, falling back to in-process execution when no gateway is running. Outside a
container the same verbs run as `dotnet ActiveSync.Server.dll <command>`.

The full command reference — inspection, user & device management, global settings, access
control, secret helpers, and how forwarding works — is in **[docs/cli.md](docs/cli.md)**.

## Web interfaces (`/admin` and `/user`)

The gateway can serve a web **admin interface** under `/admin` (full CLI parity: backends
editor, settings editor, user management, devices with block/wipe/purge, shared calendars,
live logs, state dashboard) and a **user self-service portal** under `/user` (own password
+ backend credentials only). Both are **off by default** and toggle **live** (~1 s, no
restart):

```bash
# Bootstrap on a fresh gateway (works even before any mail backend is configured):
echo -n 'admin-password' | eas user password admin
eas user set admin Admin true
eas config set ActiveSync:WebUi:Admin:Enabled true          # then open http://host:5080/admin
eas config set ActiveSync:WebUi:UserPortal:Enabled true     # optional, also on the admin Settings page
```

From there the **Backends** page is where a fresh gateway is pointed at its mail, calendar
and contacts servers: a provider dropdown per role, then the fields that provider says it
needs. Assigning MailStore and MailSubmit is what brings ActiveSync online.

Local logins check the same account machinery as the phones (declared accounts only; the
admin UI additionally requires the account's `Admin` flag). With
`ActiveSync:WebUi:Oidc:Authority` + `ClientId` set, all web logins go through your **OIDC**
identity provider instead and the local login form is disabled — see
[docs/webui.md](docs/webui.md) for the full endpoint reference, the OIDC claim mapping
(`LoginClaim`, `AdminClaim`, `AutoProvision` JIT accounts) and the security model (cookie
auth is passive and never touches the EAS Basic-auth path; sessions survive restarts via a
DataProtection key-ring in the state database, sealed with the Encryption master key).

## Testing

~500 automated tests in two layers: **unit tests** (run anywhere, no docker) and an
**integration suite** that hosts the gateway in-process and drives it with a real
WBXML-speaking mini-EAS client against live backends (Stalwart, docker-mailserver + Radicale,
Cyrus, Baikal, Apache James, Axigen) — skipping automatically when no stack is reachable.
`scripts/test-fast` is the recommended per-change check (stalwart + axigen in parallel).

The backend stacks, the fast/all-stacks runners, the CI pipeline and the release process are
documented in **[docs/testing.md](docs/testing.md)**.

## Architecture

```
src/
  ActiveSync.Protocol/   WBXML codec (all MS-ASWBXML code pages), MS-ASHTTP base64 query
                         parser, EAS constants. No ASP.NET dependencies.
  ActiveSync.Contracts/  The backend plugin contract — the interfaces/records a provider
                         implements (IBackendProvider, IContentStore, IGatewayPlugin, roles,
                         provider settings, config schema). Tiny; the one package a plugin
                         references. Depends only on Protocol + MS config/DI abstractions.
  ActiveSync.Core/       The provider engine (registry, composite session, session factory),
                         EF Core state store (devices, folder registry, sync keys +
                         snapshots, DAV href map), differential sync engine (CollectionDiff),
                         options/account model. References Contracts.
  ActiveSync.Backends.Common/   MIME/iCalendar/vCard ↔ EAS converters, MS-ASTZ timezone
                         blob, TLS/wire-logging helpers — shared by the providers.
  ActiveSync.Backends.{Imap,Smtp,Dav,Sieve,Jmap,Local}/   one assembly per provider (Dav
                         serves both caldav + carddav); Local is the gateway-DB fallback.
                         New backends drop in as another such assembly.
  ActiveSync.Server/     Kestrel host, /Microsoft-Server-ActiveSync endpoint, Basic auth,
                         one handler class per EAS command, provider DI wiring, the `eas` CLI.
```

**Backend plugins.** Backends are named *providers* that fill *roles*; a new backend (e.g.
a Microsoft Graph bridge, or your own) is just another provider assembly. Out-of-repo plugins
drop into `/app/plugins` and register themselves — no fork required. A plugin references the
one small **`ActiveSync.Contracts`** package (published to NuGet per release; optionally
`ActiveSync.Backends.Common` for the MIME/iCal/vCard converters). See
**[docs/plugins.md](docs/plugins.md)**.

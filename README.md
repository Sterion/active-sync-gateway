# ActiveSync Gateway

> ## 🤖 This project is 100% AI-generated
>
> Every line of code, every test and this README were written by an AI (Anthropic's
> Claude, driven through Claude Code). A human chose the goals, reviewed the results and
> runs the deployments — but hand-wrote none of it. Judge, audit and trust it accordingly.

A .NET 10 service that speaks **Microsoft Exchange ActiveSync (EAS) 14.1** to mail clients
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

## Features

**Protocol**

- The full command set through **EAS 16.1**: FolderSync and folder create/rename/delete,
  two-way Sync for mail/contacts/calendar/tasks/notes, Ping (long-poll push),
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
  the startup banner. Mounting real certificates switches it off automatically.
- The entire admin surface ships inside the image as the **`eas` CLI** — inspection, user
  management, blocking, purging, secret sealing (see [Operator CLI](#operator-cli-eas)).
- **SQLite** (zero-config default) or **PostgreSQL** (accepts `postgresql://` URIs, e.g.
  straight from a CloudNativePG secret); EF Core migrations apply automatically at startup.
- Stateless apart from the shared database: **multiple replicas behind any load balancer
  work without sticky sessions**.
- **Read-only mode**: a pure mirror that accepts every client write on the wire and
  silently reverts it.
- Serilog structured logging; the startup banner prints the full effective configuration
  with secrets redacted.

**Quality**

- ~330 automated tests, including an integration suite that hosts the gateway in-process
  and drives it with a real WBXML-speaking mini-EAS client against **live backends**
  (Stalwart, or docker-mailserver + Radicale).
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
  -e ActiveSync__Imap__Host=imap.example.com \
  -e ActiveSync__Smtp__Host=smtp.example.com \
  -e ActiveSync__Encryption__Key='any passphrase - or a key from: openssl rand -base64 32' \
  activesync-gateway
```

Prefer running without docker? Every release on the releases page also ships
`eas-gateway-linux-x64-<tag>.zip` and `eas-gateway-windows-x64-<tag>.zip` — install the
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
# → MS-ASProtocolVersions: 2.5,12.0,12.1,14.0,14.1
```

## Configuration

One backend set serves all users; each user authenticates with their own credentials
(HTTP Basic, validated by an IMAP login probe and passed through to all backends).
Edit `src/ActiveSync.Server/appsettings.json`:

```jsonc
"ActiveSync": {
  "Imap":  { "Host": "imap.example.com", "Port": 993, "UseSsl": true },
  "Smtp":  { "Host": "smtp.example.com", "Port": 465, "UseSsl": true },
  "CalDav":  { "BaseUrl": "https://dav.example.com", "HomeSetPath": "" },   // "" → RFC 6764 discovery
  "CardDav": { "BaseUrl": "https://dav.example.com", "HomeSetPath": "" },   // or e.g. "/{user}/"
  "Database": { "Provider": "Sqlite", "ConnectionString": "Data Source=activesync.db" },
  "Encryption": { "Key": "<any passphrase — or a raw key from: openssl rand -base64 32>" },
  "Eas": { "MaxHeartbeatSeconds": 1770, "DavPollSeconds": 60, "WatchdogSeconds": 60 }
}
```

- **IMAP and SMTP are mandatory** — startup fails with a clear validation error when
  either host is missing (a mail gateway without mail access makes no sense).
- **CalDAV/CardDAV are optional**: omit the `CalDav`/`CardDav` sections and that content
  class is served from the **gateway's own database** instead — contacts and calendar
  still sync, stored as vCard/iCalendar rows, visible to all of the user's ActiveSync
  devices and nowhere else (no webmail/DAV client will see them). Changes push to other
  devices near-instantly via an in-process notifier. **Notes are always stored locally**
  (no DAV backend carries them). With local stores in play the state database holds real
  user data, not just sync state — back it up accordingly. A `CalDav`/`CardDav` section
  that is present but has an invalid `BaseUrl` fails startup validation.
- `HomeSetPath` supports `{user}` and `{localpart}` placeholders (Radicale/Baikal style is
  `/{user}/`). Leave empty for `.well-known` + `current-user-principal` discovery.
- `Database.Provider` may be `Sqlite` or `Postgres`
  (`"Host=db;Database=activesync;Username=...;Password=..."`, or a
  `postgresql://user:password@host:5432/database` URI — e.g. straight from a
  CloudNativePG secret — which implies `Postgres` by itself).
- **An `Encryption` key is mandatory** — locally-stored content is encrypted at rest (see
  the `Encryption` option table below). Startup fails without a key unless
  `Encryption.AllowPlaintext=true` is set explicitly (dev/test only).
- **Backend TLS**: every backend section (`Imap`, `Smtp`, `CalDav`, `CardDav`) supports
  two certificate knobs. `"CaCertificatePath": "/path/to/ca.pem"` trusts the CAs in that
  PEM file *in addition to* the system store — the right choice for a private PKI /
  self-signed setup. `"AllowInvalidCertificates": true` disables certificate validation
  entirely (lab use only; it wins over `CaCertificatePath` and is called out as
  `certs=ACCEPT-ANY` in the startup banner). A configured CA file that is missing or not
  valid PEM fails startup validation.

### Full option reference

Everything lives under the `ActiveSync` configuration section. Any option can also be set
via environment variables using `__` as the separator, e.g.
`ActiveSync__Imap__Host=imap.example.com` (that is what the Docker examples use).

**Root**

| Option | Default | Description |
|--------|---------|-------------|
| `ReadOnly` | `false` | Pure-mirror mode: every client write is suppressed (see [Read-only mode](#read-only-mode)). |
| `PublicUrl` | `null` | Public base URL of the gateway (e.g. `https://eas.example.com`), advertised by Autodiscover. When unset, the advertised URL is derived from the request's `Host` / `X-Forwarded-Proto` / `X-Forwarded-Host` headers — set this behind a reverse proxy so it never depends on client-supplied headers. |
| `Users` | `null` | Optional per-user overrides keyed by gateway login (see [Per-user overrides](#per-user-overrides)). Undeclared logins are plain pass-through. |
| `RequireDeclaredUsers` | `false` | Allowlist switch: only logins with a `Users` entry (config or database) may authenticate — anyone else gets 401 without a backend probe. An empty entry (`{}`) is a valid grant. |
| `UsersFile` | `null` | Path to a JSON file merged into configuration at startup (full shape: `{ "ActiveSync": { "Users": { ... } } }`) — the natural fit for a mounted Kubernetes Secret/ConfigMap. Changes require a restart. |

**`Imap` (required)**

| Option | Default | Description |
|--------|---------|-------------|
| `Host` | — | IMAP server host. **Required** — startup fails without it. |
| `Port` | `993` | IMAP port. |
| `UseSsl` | `true` | Implicit TLS on connect (used when `Security` is unset). |
| `Security` | `null` | Explicit transport security: `None` \| `SslOnConnect` \| `StartTls` \| `StartTlsWhenAvailable` \| `Auto`. When unset, derived from `UseSsl`/`Port`. `None` also skips opportunistic STARTTLS (needed for plaintext test servers advertising STARTTLS with self-signed certs). |
| `AllowInvalidCertificates` | `false` | Accept any TLS certificate (lab use; wins over `CaCertificatePath`). |
| `CaCertificatePath` | `null` | PEM file with CA certificates trusted in addition to the system store (private PKI). Validated at startup. |
| `PathSeparator` | `null` | IMAP folder path separator override; autodetected when unset. |

**`Smtp` (required)**

| Option | Default | Description |
|--------|---------|-------------|
| `Host` | — | SMTP server host. **Required.** |
| `Port` | `465` | SMTP port. |
| `UseSsl` | `true` | Implicit TLS on connect (used when `Security` is unset). |
| `Security` | `null` | Same values/semantics as `Imap.Security`. |
| `AllowInvalidCertificates` | `false` | As above. |
| `CaCertificatePath` | `null` | As above. |
| `ForceFrom` | `false` | Rewrite the `From` header of outgoing mail to the authenticated user before submission (display name is kept). Off by default because most SMTP servers already enforce sender alignment for authenticated submissions — enable it when yours does not. |

**`CalDav` / `CardDav` (optional sections)**

Omit a section entirely and that content class is served from the gateway database
(local storage) instead.

| Option | Default | Description |
|--------|---------|-------------|
| `BaseUrl` | — | Absolute http(s) URL of the DAV server. Required when the section is present. |
| `HomeSetPath` | `null` | Home-set path template; `{user}` and `{localpart}` are substituted (Radicale/Baikal style: `"/{user}/"`). Unset → RFC 6764 discovery via `.well-known` + `current-user-principal`. |
| `TaskFolder` | `"Tasks"` | *(CalDav only)* Name of the VTODO (tasks) collection in the calendar home set; when a collection with this display name or path segment exists, it becomes the ActiveSync Tasks folder (Axigen ships one named "Tasks"). Empty → tasks are stored in the gateway database instead. Recurring tasks are not mapped yet. |
| `CalendarAttachments` | `"Auto"` | *(CalDav only)* Event attachments for EAS 16.x clients: `Auto` (enabled, 1 MiB per attachment), `On` (enabled, 16 MiB) or `Off`. Attachments are stored **inline** in the event (base64 `ATTACH` property), so they work against any CalDAV server — the cap protects the DAV server from bloated items. Per-user overridable. |
| `AllowInvalidCertificates` | `false` | As above. |
| `CaCertificatePath` | `null` | As above. |

**`Sieve`** (out-of-office via ManageSieve)

When enabled, the phone's out-of-office settings page works: Settings→Oof Set uploads a
gateway-owned sieve script named `eas-gateway` (RFC 5230 `vacation`, wrapped in a
`currentdate` window for scheduled Oof) and makes it the active script; disabling
restores the previously active script. The state database is the source of truth for
what the phone sees — the script is derived output, never parsed back. One reply body is
used for all three EAS audiences (internal/external-known/external-unknown), and the
auto-reply is sent as plain text. Note that sieve has a single active script: while Oof
is enabled, the user's own filter script (if any) is inactive until Oof is disabled
again. Credentials default to the user's effective IMAP login, and a per-user
`Users:<login>:Sieve` override section exists with the same fields.

| Option | Default | Description |
|--------|---------|-------------|
| `Enabled` | `false` | Master switch. Off: Settings→Oof behaves as the historical stub (accepted, ignored). |
| `Host` | IMAP host | ManageSieve server; defaults to the (effective) IMAP host. |
| `Port` | `4190` | The standard ManageSieve port. |
| `UseTls` | `true` | Require STARTTLS before authenticating (ManageSieve has no implicit-TLS port). `false` = plaintext, test stacks only. |
| `AllowInvalidCertificates` | `false` | As above. |
| `CaCertificatePath` | `null` | As above. |

**`Database`**

| Option | Default | Description |
|--------|---------|-------------|
| `Provider` | `"Sqlite"` | `Sqlite` or `Postgres`. A `postgresql://` connection string implies `Postgres` automatically, so the provider can be left unset in that case. |
| `ConnectionString` | `"Data Source=activesync.db"` | Provider connection string. Postgres accepts the Npgsql keyword form (`"Host=db;Database=activesync;Username=...;Password=..."`) **or** a libpq-style URI (`postgresql://user:password@host:5432/database?sslmode=require`) — a [CloudNativePG](https://cloudnative-pg.io/) app secret's `uri`/`fqdn-uri` value works verbatim (the `jdbc-uri` variants work too; the `jdbc:` prefix is stripped). Passwords are redacted in the startup banner. |

**`Encryption` (local content at rest)**

Content stored in the gateway database (Notes always; Contacts/Calendar/Tasks when no DAV
backend is configured) is encrypted with AES-256-GCM. A key is **mandatory** — startup
fails without one unless `AllowPlaintext` is set explicitly. **The key can be any string**:
a base64 value decoding to exactly 32 bytes is used as the raw 256-bit key, anything else
is treated as a passphrase and stretched to 256 bits with PBKDF2-SHA256. For maximum
entropy generate a raw key with `openssl rand -base64 32` (PowerShell:
`[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))`);
passphrases shorter than 12 characters work but are called out in the startup banner.

| Option | Default | Description |
|--------|---------|-------------|
| `Key` | `null` | Master key, inline — a raw base64 32-byte key or any passphrase. Mutually exclusive with `KeyFile`. |
| `KeyFile` | `null` | Path to a file containing the key (same raw-or-passphrase interpretation) — the right choice with docker secrets (`/run/secrets/...`). |
| `AllowPlaintext` | `false` | Explicitly store local content unencrypted (dev/test only; shouted in the startup banner). Ignored when a key is configured. |

Losing the key makes the stored local content unrecoverable (drop the database and let
devices re-upload); key rotation is not supported yet.

**`Eas` (protocol tuning)**

| Option | Default | Description |
|--------|---------|-------------|
| `MinHeartbeatSeconds` | `60` | Lower bound for Ping/Sync heartbeats (must be ≥ 1). |
| `MaxHeartbeatSeconds` | `1770` | Upper bound for Ping/Sync heartbeats (EAS allows up to 3540). |
| `DavPollSeconds` | `60` | ctag/sync-token poll interval for DAV collections during waits. |
| `DefaultWindowSize` | `100` | Items per Sync response when the client sends no `WindowSize`. |
| `MaxWindowSize` | `512` | Hard cap on the client-requested window size. |
| `SessionIdleMinutes` | `15` | Idle backend sessions (and with them the user's shared IDLE watchers) are evicted after this long. |
| `UseImapIdle` | `true` | Persistent per-(user, folder) IMAP IDLE watcher for the priority pinged folder (sub-second push). Degrades to STATUS polling when disabled or unsupported by the server. |
| `WatchdogSeconds` | `60` | Interval of the exact pending-change re-check that backstops the IDLE/STATUS watchers during waits. `0` disables the periodic ticks (the check at Ping start always runs); minimum otherwise 15. |

**`Auth` (authentication hardening)**

| Option | Default | Description |
|--------|---------|-------------|
| `MaxFailures` | `20` | Failed Basic-auth attempts allowed per (client address, username) within `FailureWindowSeconds`; past the limit those requests get `429` (with `Retry-After`) until the window expires, without touching the IMAP backend. A successful login clears only that account's counter — never another user's on the same address. A looser per-address ceiling (5× this value) also bounds username-rotation floods from one address. `0` disables the throttle. |
| `FailureWindowSeconds` | `300` | Length of the failure-counting window. |
| `NegativeCacheSeconds` | `15` | A rejected (user, password) pair is remembered this long, so repeats are refused without an IMAP login round-trip (the gateway cannot be used to hammer the mail server). `0` disables. |
| `SuccessCacheMinutes` | `5` | A successful (user, password) pair is trusted this long without re-verifying against IMAP. Note the flip side: a password revoked on the mail server keeps working against the gateway for at most this long. `0` disables (every request performs an IMAP login). |
| `UsersRefreshSeconds` | `1` | How often the merged user set is re-checked against the database (one primary-key point-read, only when a request arrives). `0` checks on every request; negative disables live pickup (database edits then need a restart). |

Failed logins are logged at Warning with the client-supplied username, so fail2ban-style
tooling can match them; when the gateway runs behind a reverse proxy, all clients share the
proxy's address from the throttle's point of view — keep `MaxFailures` generous (a valid
login resets the counter, so legitimate users recover immediately).

**`SelfSignedTls`** (built-in HTTPS endpoint)

| Option | Default | Description |
|--------|---------|-------------|
| `Enabled` | `true` | Serve HTTPS on `Port` with a self-signed certificate generated on first serve and persisted in the state database (see [HTTPS](#https-self-signed-by-default-or-bring-your-own-certificate)). Skipped automatically when configuration declares a Kestrel HTTPS endpoint. |
| `Port` | `5443` | Listen port of the self-signed HTTPS endpoint. |

**`Policy`** (device security policies — see [Device security policies](#device-security-policies))

Option names match the MS-ASPROV `EASProvisionDoc` elements 1:1, so Microsoft's
documentation applies directly. Unset numeric options are omitted from the policy
document (the device default applies).

| Option | Default | Description |
|--------|---------|-------------|
| `Enabled` | `false` | Master switch. Off: Provision hands out an empty policy and nothing is enforced. On: the policy below is issued and required on every request. |
| `DevicePasswordEnabled` | `false` | Require a device lock PIN/password. |
| `AlphanumericDevicePasswordRequired` | `false` | Require letters *and* digits/symbols in the device password. |
| `AllowSimpleDevicePassword` | `true` | Allow simple PINs like `1234`. |
| `MinDevicePasswordLength` | unset | Minimum password length (1–16). |
| `MinDevicePasswordComplexCharacters` | unset | Minimum character classes — upper/lower/digit/symbol (1–4). |
| `MaxInactivityTimeDeviceLock` | unset | Seconds of inactivity before the device locks (1–9999). |
| `MaxDevicePasswordFailedAttempts` | unset | Wrong-password attempts before the device wipes **itself** locally (4–16). |
| `DevicePasswordExpiration` | unset | Days before the password must be changed (`0` = never). |
| `DevicePasswordHistory` | unset | Number of previous passwords the device must refuse to reuse. |
| `RequireDeviceEncryption` | `false` | Require device storage encryption. |
| `MaxAttachmentSize` | unset | Maximum attachment size in bytes the client may download. |
| `PasswordRecoveryEnabled` | `false` | Let the device escrow its recovery password with the gateway — readable via `eas device password <user> <deviceId>`, stored sealed with the Encryption master key. |

### Per-user overrides

Pass-through is always the baseline: the EAS credentials are forwarded to every backend. A
`Users` entry is a pure **overlay** — declare only the users who need something different
(another SMTP login, a personal DAV supplier, a different IMAP host or user name) and
override only the fields that differ. Everyone without an entry keeps working untouched.
Unset passwords inherit the presented EAS password, per user *and* per backend.

```jsonc
"ActiveSync": {
  "Imap": { "Host": "imap.example.com" },          // global defaults, per-user overridable
  "Smtp": { "Host": "smtp.example.com" },
  "Users": {
    "anna@example.com": {
      // No passwords: anna's phone password stays her IMAP password (probe validates it);
      // only her CalDAV lives elsewhere, with its own credentials.
      "CalDav": { "BaseUrl": "https://cloud.example.com", "UserName": "anna", "Password": "enc:v1:..." }
    },
    "ben@example.com": {
      // Only the SMTP login differs; everything else is plain pass-through.
      "Smtp": { "UserName": "relay-ben", "Password": "relay-pw" }
    },
    "phone-carla": {
      // Fully decoupled: own phone password, mapped to a real mailbox.
      "Password": "pbkdf2$200000$...$...",         // hash-password verb
      "MailAddress": "carla@example.com",
      "Imap": { "UserName": "carla@example.com", "Password": "enc:v1:..." }
    }
  }
}
```

**How a login is authenticated** (per user, first rule that applies):

| # | Entry has | The phone's password is |
|---|-----------|-------------------------|
| 1 | `Password` (pbkdf2$ or plaintext) | verified against it locally — fully decoupled from IMAP |
| 2 | `Imap:Password` (no `Password`) | pinned: it must equal the configured IMAP password (timing-safe compare, no probe) |
| 3 | neither | the IMAP password — validated by a login probe against the user's *effective* IMAP host/user name (overrides apply) |
| 4 | *(no entry)* | same as 3 with the global IMAP section — classic pass-through |

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
kubectl exec <pod> -- eas user set phone-dana Imap:UserName dana@example.com
echo -n 'imap-pw' | kubectl exec -i <pod> -- eas user secret phone-dana Imap:Password
kubectl exec <pod> -- eas user show phone-dana
```

`user set` addresses every field by its config path (`Imap:Host`, `CalDav:Enabled`, ...)
and accepts password keys too: plaintext values are hashed (`Password` → pbkdf2$) or
sealed (`*:Password` → `enc:v1:`) on the spot, already-prepared values are stored verbatim
— but plaintext on the command line lands in shell history, so the stdin forms above are
preferred (the CLI warns).

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
  [Operator CLI](#operator-cli-eas).
- **Allowlist**: `"RequireDeclaredUsers": true` rejects any login without a `Users` entry
  before touching a backend. An empty entry (`"dora@example.com": {}`) grants access with
  zero overrides.
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

The gateway serves HTTP on `:5080` (for a TLS-terminating reverse proxy / ingress) **and
HTTPS on `:5443` with a self-signed certificate** — publish only the port that matches
your setup.

**Self-signed (zero config).** On first `serve` the gateway generates a certificate (RSA
2048, 20-year validity, CN/SAN from `PublicUrl` when set) and stores it **in the state
database**, private key sealed with the `Encryption` master key. Every restart and every
replica serves the same certificate, so a device only has to trust it once — the SHA-256
fingerprint is printed in the startup banner for comparison against what the phone shows.
Nine and iOS Mail offer a one-tap "trust anyway"; **some clients — notably Gmail — refuse
untrusted certificates entirely**, so real certificates (below) or a reverse proxy remain
the production path. Tune with `SelfSignedTls:Enabled` / `SelfSignedTls:Port` (see the
option reference); should the certificate ever need replacing, delete the
`ServerCertificates` row and restart.

**Real certificates.** Add a Kestrel HTTPS endpoint via environment variables — the
self-signed endpoint then switches off automatically and the mounted files are served
as-is (the database is not involved). A PEM pair (Let's Encrypt / certbot layout) works
directly:

```bash
docker run -p 443:5443 -v /path/to/certs:/certs:ro \
  -e Kestrel__Endpoints__Https__Url=https://0.0.0.0:5443 \
  -e Kestrel__Endpoints__Https__Certificate__Path=/certs/fullchain.pem \
  -e Kestrel__Endpoints__Https__Certificate__KeyPath=/certs/privkey.pem \
  -e ActiveSync__Imap__Host=imap.example.com \
  -e ActiveSync__Smtp__Host=smtp.example.com \
  -e ActiveSync__Encryption__Key='...' \
  -v activesync-data:/data activesync-gateway
```

- Point `Certificate__Path` at the **full chain** (leaf + intermediates, certbot's
  `fullchain.pem`), not just the leaf — phones are strict about incomplete chains.
- Have a PFX/PKCS#12 file instead? Use `Kestrel__Endpoints__Https__Certificate__Path=/certs/cert.pfx`
  plus `Kestrel__Endpoints__Https__Certificate__Password=...` (and no `KeyPath`).
- **Keep the default HTTP endpoint** — the container `HEALTHCHECK` probes `/healthz` over
  it on the loopback. Simply don't publish port 5080; only the HTTPS port needs `-p`.
- The container runs as a non-root user, so keep the *inside* port above 1024 (`5443`
  here) and map `443` on the outside — EAS clients default to 443.
- Restart the container after renewing the certificate; the files are read at startup.

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
  shield the IMAP server from repeated bad-password attempts. Users with a local password
  rule (gateway `Password` or pinned `Imap:Password`) are verified without a backend
  round-trip, so the throttle is the primary brute-force defense for them.
- **Data at rest**: locally-stored item content (Notes always; Contacts/Calendar/Tasks
  without a DAV backend) is encrypted with AES-256-GCM under the `Encryption` key, bound
  to the owning user + collection so rows cannot be swapped between users. What stays
  plaintext by design: user names, item UIDs, per-item event dates (`ItemDateUtc`, needed
  for EAS filter windows — reveals *when* events exist, not their content), revision
  counters/timestamps, folder names, and sync-state metadata. Mail is never stored in the
  gateway database. Still protect the `/data` volume (permissions, backups) — and back up
  the key separately from the database.
- **Outgoing mail** is submitted over authenticated SMTP with the client's MIME as-is;
  sender-alignment policy is the SMTP server's job (or set `Smtp.ForceFrom`).

## Operator CLI (`eas`)

The Docker image puts an `eas` command on the PATH, so every command runs with a single
line inside any deployed container (`kubectl exec <pod> -- eas users`, `docker exec
<container> eas devices`). Outside a container the same commands run as
`dotnet ActiveSync.Server.dll <command>` with the configuration available via env vars or
appsettings.json in the working directory.

**Server**

| Command | What it does |
| --- | --- |
| *(none)* | Show the banner for the config that WOULD run — including the declared users — and exit; nothing starts. |
| `serve` | Start the gateway (accepts `--ActiveSync:Section:Key=value` overrides). |
| `healthcheck` | Probe the running gateway's `/healthz`; exit 0 when healthy (backs the container `HEALTHCHECK`). |
| `help` | List all commands (`eas <command> --help` shows per-command arguments). |

**Inspection**

| Command | What it does |
| --- | --- |
| `users` | List users that have data: devices, last-seen, folder/item counts, blocks. |
| `devices [user]` | List device registrations with last-seen and block state. |
| `folders <user>` | List a user's folder registry. |
| `items <user> [collection]` | List local item metadata (never decrypts). |
| `show <user> <collection> <uid>` | Decrypt and print one local item (needs the Encryption key). |
| `device password <user> <deviceId>` | Print a device's escrowed recovery password (see [Device security policies](#device-security-policies)). |
| `device wipe <user> <deviceId> [--cancel]` | Arm a **16.1 account-only wipe**: the device removes this account (never a factory reset) and the partnership is auto-blocked after the acknowledgment. Warns when the device last spoke <16.1 — use `block` for those. |

**User management** (see [Database-declared users](#database-declared-users-eas-user-))

| Command | What it does |
| --- | --- |
| `user list` | All declared users (config + database) with origin, mail address and overrides. |
| `user show <login>` | The effective entry for one login, secrets masked. |
| `user add <login>` | Declare a user in the database (an empty entry is an allowlist grant; copies a same-login config entry as the starting point). |
| `user remove <login>` | Delete the database entry — a same-login config entry becomes active again. |
| `user set <login> <key> <value>` | Set one field by config path (`Imap:Host`, `CalDav:Enabled`, ...); password keys are hashed/sealed automatically. |
| `user unset <login> <key>` | Clear one field (an emptied entry remains an allowlist grant). |
| `user password <login>` | Set the gateway password from stdin (stored as a pbkdf2$ hash). |
| `user secret <login> <key>` | Set a backend password (`Imap:Password`, ...) from stdin (stored sealed, enc:v1:). |

**Access control & cleanup**

| Command | What it does |
| --- | --- |
| `block <user> [deviceId]` | Refuse logins with **403** — the whole user, or one device. |
| `unblock <user> [deviceId]` | Remove a login block. |
| `purge user <user>` | Delete ALL of a user's state (asks for confirmation, or `--yes`). |
| `purge device <user> <deviceId>` | Delete one device registration (it re-syncs from scratch). |

**Secret helpers**

| Command | What it does |
| --- | --- |
| `protect` | Seal a secret from stdin with the Encryption key (`enc:v1:...`), e.g. for a users file. |
| `hash-password` | Hash a gateway password from stdin (`pbkdf2$...`). |

```bash
# Seal a backend password with the Encryption master key (-> enc:v1:... for the users file).
# The running pod already has the key via its env/config, so no extra flags are needed.
echo -n 'imap-password' | kubectl exec -i <pod> -- eas protect

# Hash a gateway password (-> pbkdf2$... for a Users entry's Password field).
echo -n 'phone-password' | docker exec -i <container> eas hash-password

# Who is syncing, and when were they last seen?
kubectl exec <pod> -- eas users

# Lock out a lost phone (or a whole account) without touching the mail server.
kubectl exec <pod> -- eas block user@example.com ABCDEF123456
```

Secrets travel via **stdin** (never argv, so they stay out of shell history and `ps`).
The database commands read the same configuration as `serve`, so inside a running pod they
just work; blocking answers the device's next request with HTTP 403 (no credential
re-prompt loop), and `purge` is the reset lever when a device should re-sync from scratch.

## Testing

Two layers:

- **Unit tests** (`Category!=Integration`) run anywhere, no docker needed.
- **Integration tests** (`tests/ActiveSync.Integration.Tests`) host the gateway in-process
  and drive it with a real mini-EAS-14.1 client (WBXML, base64 query, Provision handshake)
  against **real backends** — including the flagship scenario where user1 sends a mail
  through the gateway and user2 receives it on a second EAS client. They **skip
  automatically** when no backend stack is reachable, so `dotnet test` is always safe.

An opt-in third layer (`--filter Category=AxigenLive`) runs the 16.x/GAL/free-busy
scenarios against a real Axigen server. It skips unless `AS_TEST_AXIGEN_HOST`,
`AS_TEST_AXIGEN_USER` and `AS_TEST_AXIGEN_PASSWORD` are all set; point it only at a
dedicated throwaway mailbox — the tests create and delete real items.

### Backend stacks

Both stacks publish the same ports (IMAP 143, SMTP 587, DAV 5232) and users
(`user1@example.com` / `user2@example.com`, password `pass`):

```bash
# Default: Stalwart all-in-one (mail with real delivery + CalDAV/CardDAV)
docker compose -f docker/backends/stalwart/docker-compose.yml up -d

# Alternative (manual runs): docker-mailserver (Postfix+Dovecot) + Radicale
docker compose -f docker/backends/mailserver/docker-compose.yml up -d
AS_TEST_STACK=mailserver dotnet test --filter Category=Integration
```

### Where tests run

- **Visual Studio (Windows host)**: bring a stack up, run tests from Test Explorer —
  localhost defaults just work. Breakpoints hit gateway code (it runs in-process).
- **VS Code devcontainer**: "Reopen in Container" — the `.devcontainer` compose brings the
  Stalwart stack up alongside the workspace with env preset.
- **Gitea CI**: one workflow, `.gitea/workflows/build.yaml` (push + dispatch), compiles the
  solution exactly once: the Dockerfile `test` stage builds everything and runs the unit
  tests, the integration tests then run **from that same image** (`dotnet test --no-build`,
  joined to a network with throwaway Stalwart + Postgres containers), and only when the
  full suite is green is the runtime image assembled from the warm build cache and pushed
  to the project's container registry. The workflow deliberately avoids bind
  mounts (the runner is docker-out-of-docker: mount paths would resolve on the runner
  host) and streams the backend config and provision script through the docker API
  instead.

  Local reproduction of the CI suite:

  ```bash
  docker compose -f docker/docker-compose.ci.yml run --rm tests
  docker compose -f docker/docker-compose.ci.yml down -v
  ```

- **Image builds**: the Dockerfile has a `test` stage, so every `docker build` runs the
  unit test suite (integration tests need sibling containers and are CI-compose only).

### Cutting a release

Actions → **Create Release** → enter the version (`1.0.7` or `v1.0.7`) and an optional
headline. The workflow validates the version, generates release notes from the commit
subjects since the previous tag, pushes the tag (which starts the build pipeline) and
creates the release. The tag build then pushes the versioned docker image and attaches
the download zips to the release once the full test suite is green — so the release page
is complete a few minutes after dispatch. Manual tagging still works: a tag pushed by
hand gets a minimal auto-created release with the same assets.

## Architecture

```
src/
  ActiveSync.Protocol/   WBXML codec (all MS-ASWBXML code pages), MS-ASHTTP base64 query
                         parser, EAS constants. No ASP.NET dependencies.
  ActiveSync.Core/       Backend abstractions (IContentStore, IBackendSession), EF Core
                         state store (devices, folder registry, sync keys + snapshots,
                         DAV href map), differential sync engine (CollectionDiff).
  ActiveSync.Backends/   ImapMailBackend (MailKit), CalDavStore/CardDavStore over a thin
                         WebDAV client, MIME/iCalendar/vCard ↔ EAS converters, MS-ASTZ
                         timezone blob, per-(user,device) session cache.
  ActiveSync.Server/     Kestrel host, /Microsoft-Server-ActiveSync endpoint, Basic auth,
                         one handler class per EAS command, the `eas` CLI.
```

Design notes:

- **Differential sync** (Z-Push style): each Sync round compares a stored snapshot
  (item → revision) against the backend's current revision map (IMAP UID+flags, DAV ETags).
  Works on any server; no CONDSTORE/sync-collection requirement.
- **Item ServerIds** are `collectionId:sub` where `sub` is the IMAP UID or a short numeric
  id mapped to the DAV href in the database.
- **Ping/Sync waits** use `Task.WhenAny` over per-store watchers: a **persistent
  per-(user, folder) IMAP IDLE connection** (shared by all the user's devices, surviving
  across requests) watches the priority folder — INBOX when pinged, otherwise the first
  watched mail folder — for sub-second push; events are **latched**, so a change that
  fires between two Pings is delivered to the next one instantly (`Eas.UseImapIdle`, on
  by default, degrades to STATUS polling when the server lacks IDLE). Mail folders poll
  STATUS every ~30 s and DAV collections poll ctag/sync-token every `DavPollSeconds`. Because IDLE/STATUS notifications are
  best-effort in practice (some servers never broadcast flag changes; STATUS counters can
  be coincidentally identical), an exact **watchdog** re-check runs alongside every wait:
  at watch entry and every `Eas.WatchdogSeconds` (default 60, `0` disables the periodic
  ticks) it diffs the backend's revision map against the device's synced snapshot — the
  same ground truth Sync uses — so a change can never sit undetected for a whole
  heartbeat. Quiet ticks are silent; an entry-check hit (change arrived between requests,
  a normal path) logs Info, and a mid-wait hit (the watchers were live and stayed silent)
  logs a Warning.
- **Empty Sync requests** replay the cached shape of the device's last full Sync request
  (MS-ASCMD semantics); only when no usable cache exists does the server answer status 13
  to request a full resend.

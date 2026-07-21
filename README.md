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

Each role is filled by a **provider** (chosen in [Configuration](#configuration) below). The
providers differ in what they can express — a protocol either carries a feature or it
doesn't, and some mappings are lossy. These tables are the at-a-glance strengths/weaknesses
of each backend technology, so you can pick per role with eyes open.

Legend: ✅ full · ⚠️ partial (see note) · ❌ not supported · — not applicable (handled
elsewhere by design).

**Which providers fill which role:**

| Role | Providers | Falls back to |
|------|-----------|---------------|
| Mail store | `imap`, `jmap` | — (required) |
| Mail submit | `smtp`, `jmap` | — (required) |
| Calendar | `caldav`, `jmap`, `local` | `local` |
| Tasks | `caldav`, `local` | `local` |
| Contacts | `carddav`, `jmap`, `local` | `local` |
| Notes | `local` | `local` (only option) |
| Out-of-office | `sieve`, `jmap` | — (off if unset) |

**Push vs poll** (how fast a Ping/Sync sees a server-side change): mail has real push —
IMAP **IDLE** or JMAP **EventSource** (both with a polling backstop). CalDAV/CardDAV and
JMAP calendar/contacts are **poll-only** at `Eas:DavPollSeconds`. `local` stores wake
instantly via an in-process notifier. Below, ✅ push = near-instant, ⚠️ poll = poll-latency.

### Mail — `imap`+`smtp` vs `jmap`

Both are feature-complete for mail; the differences are mechanism, not coverage.

| Feature | IMAP + SMTP | JMAP | Note |
|---------|:-----------:|:----:|------|
| Sync messages & folders | ✅ | ✅ | |
| Full body + attachments | ✅ | ✅ | JMAP re-downloads the whole message to extract one attachment |
| Send / SmartReply / SmartForward | ✅ | ✅ | |
| Save to Sent · Drafts sync | ✅ | ✅ | |
| Read / answered flags | ✅ | ✅ | |
| Categories | ⚠️ | ✅ | IMAP needs server custom-keyword support (`PERMANENTFLAGS \*`), else silently dropped |
| Move between folders | ✅ | ✅ | IMAP needs UIDPLUS (re-IDs the item); JMAP keeps a stable id |
| Folder create / rename / delete | ✅ | ✅ | |
| Soft (Trash) + permanent delete | ✅ | ✅ | |
| Server-side search | ✅ | ✅ | |
| Empty folder | ✅ | ✅ | |
| Push for Ping/Sync | ✅ IDLE | ✅ EventSource | both fall back to polling |

- **IMAP+SMTP** — broadest server compatibility, sub-second IDLE push, fine-grained flag
  revisions. Two protocols/connections with separate auth; one SMTP connect per send.
- **JMAP** — one session/auth serves store + submit + push + OOF; stable ids (moves keep
  their key); batched requests. Push signal is coarse (any state change) and the attachment
  fetch pulls the full message rather than the part's own blob.

### Contacts — `carddav` vs `jmap` vs `local`

| Feature | CardDAV | JMAP | Local | Note |
|---------|:-------:|:----:|:-----:|------|
| CRUD | ✅ | ✅ | ✅ | |
| Move between address books | ❌ | ✅ | ❌ | only JMAP has stable cross-book ids |
| Contact photo | ✅ | ❌ | ✅ | JMAP maps no photo and **drops an existing one on edit** |
| Multiple address books | ⚠️ | ⚠️ | ❌ | DAV/JMAP list many but can't create/rename/delete; `local` is one fixed folder |
| GAL search (Search / ResolveRecipients) | ✅ | ✅ | ✅ | CardDAV does an N+1 GET per query |
| GAL photos | ✅ | ❌ | ✅ | |
| Web page / URL, car phone | ✅ | ❌ | ✅ | JMAP JSContact bridge doesn't map these |
| Anniversary | ❌ | ❌ | ❌ | only birthday is mapped, on all three |
| Preserve fields EAS can't express, on edit | ✅ | ⚠️ | ✅ | JMAP preserves unknowns except the photo |
| Push for Ping/Sync | ⚠️ poll | ⚠️ poll | ✅ instant | |

- **CardDAV** — real multi-address-book server interop, full photo + GAL-photo round-trip,
  robust against href-rewriting servers. Poll-latency change detection; GAL is N+1.
- **JMAP** — clean CRUD with cross-book move; **but no contact photos at all**, and loses
  web-page/URL + car-phone. Poll-only (the EventSource watcher is wired to mail, not
  contacts).
- **Local** — no external server, encrypted at rest, instant push, full field + photo
  coverage. Single address book only; data lives solely in the gateway DB.

### Calendar — `caldav` vs `jmap` vs `local`

| Feature | CalDAV | JMAP | Local | Note |
|---------|:------:|:----:|:-----:|------|
| Event CRUD | ✅ | ✅ | ✅ | |
| Move between calendars | ❌ | ✅ | ❌ | |
| Recurrence (RRULE) | ✅ | ⚠️ | ✅ | JMAP bridge maps only basic freq/interval/count/until/byDay |
| Recurrence exceptions / overrides | ⚠️ | ❌ | ⚠️ | CalDAV/local persist deletions; modified occurrences are read-only. JMAP drops all overrides |
| Inbound meeting requests | ✅ | ✅ | ✅ | |
| Meeting response (accept/tentative/decline) | ✅ | ✅ | ✅ | iTIP REPLY is sent by the gateway regardless of backend |
| Outbound iMIP invitations | ✅ | — | ✅ | CalDAV auto-probes RFC 6638 (server may schedule); JMAP server always schedules itself; `local` gateway always sends |
| Free/busy (Availability) | ✅ | ❌ | ⚠️ | CalDAV self + other principals; `local` self only |
| Reminders / alarms | ✅ | ❌ | ✅ | |
| Timezones | ✅ | ✅ | ✅ | |
| Inline event attachments (16.x) | ✅ | ❌ | ✅ | |
| Shared read-only calendars | ✅ | ❌ | ❌ | JMAP declares the capability but doesn't enforce it (writes not reverted) |
| Multiple calendars | ✅ | ✅ | ❌ | `local` is one fixed calendar |
| Push for Ping/Sync | ⚠️ poll | ⚠️ poll | ✅ instant | |

- **CalDAV** — the most complete calendar backend: full recurrence, reminders, attachments,
  free/busy (self + others), genuine read-only shared calendars, RFC 6638 double-invite
  avoidance. Client-sent *modified* occurrence overrides are dropped (deletions persist).
- **JMAP** — clean CRUD with event move and native server scheduling, but a **lossy
  JSCalendar bridge**: no recurrence overrides, reminders, attachments, or free/busy; partial
  RRULE; read-only-shared is a non-enforced stub. Best paired with CalDAV for calendar if you
  need those.
- **Local** — full recurrence/reminders/attachments (shares CalDAV's converter), encrypted,
  instant push, always sends invitations. One calendar only; free/busy limited to self.

### Tasks — `caldav` (VTODO) vs `local`

Field coverage is identical (both use the same converter); they differ only in storage and
push.

| Feature | CalDAV | Local |
|---------|:------:|:-----:|
| CRUD · subject / body / importance / completion | ✅ | ✅ |
| Start & due dates | ✅ | ✅ |
| Recurrence | ✅ | ✅ |
| Reminders | ✅ | ✅ |
| Multiple task folders | ❌ | ❌ |
| Push for Ping/Sync | ⚠️ poll | ✅ instant |

Tasks over CalDAV need the explicit `Tasks` role (a VTODO collection on the calendar
server); otherwise they're local.

### Notes — `local` only

Notes have no standard mail/DAV/JMAP representation, so they are **always** served by the
`local` store: CRUD, body, categories and last-modified, encrypted at rest, one fixed
folder, visible only to the user's own ActiveSync devices.

### Out-of-office — `sieve` vs `jmap`

| Feature | ManageSieve | JMAP VacationResponse |
|---------|:-----------:|:---------------------:|
| Enable / disable | ✅ | ✅ |
| Time-bounded window | ⚠️ day-granularity | ✅ exact instant |
| HTML body | ❌ (plain; markup sent as-is) | ✅ |
| Distinct reply per audience | ❌ | ❌ |
| Restore prior server state on disable | ✅ | — (native singleton) |

EAS's three audiences (internal / external-known / external-unknown) are collapsed to one
reply by the **gateway** before either backend is called — a protocol-model limitation, not
a backend shortfall. `sieve` composes with a user's pre-existing Sieve rules (it restores
whatever was active); `jmap` gives real HTML and precise start/end times.

## Configuration

> **Settings live in layers — the database wins, and it's all CLI-settable.** Every global setting
> can be set with `eas config set <key> <value>` and is stored in the state **database**, which
> overrides `appsettings.json` / environment variables, which override the built-in **code
> defaults**. A database change is applied by every running replica within ~1s (no restart), except
> a few listener settings — HTTP/HTTPS ports, self-signed-TLS and metrics enable/port — that apply
> on the next restart. The gateway also starts with **no mail configuration at all**: with only the
> bootstrap Encryption key set it runs **unconfigured** (EAS/Autodiscover answer 503; `/readyz`
> stays ready and reports `"configured": false`) until you point it at a backend — from the web
> admin's **Backends** page or with `eas config set`. So the shipped `appsettings.json`
> carries only the two **bootstrap** sections — `Database` and `Encryption`, which are needed to
> open and decrypt the database itself and are the only settings that cannot live in it. Everything
> shown below is a **code default or example**; set the real values with the
> [`eas config`](#operator-cli-eas) commands (or `appsettings.json`/env as defaults).

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
  the `Encryption` option table below). Startup fails without a key unless
  `Encryption.AllowPlaintext=true` is set explicitly (dev/test only).
- **Backend TLS**: every network provider (`imap`, `jmap`, `smtp`, `caldav`, `carddav`,
  `sieve`) supports two certificate knobs in its role section. `"CaCertificatePath": "/path/to/ca.pem"`
  trusts the CAs in that PEM file *in addition to* the system store — the right choice for a
  private PKI / self-signed setup. `"AllowInvalidCertificates": true` disables certificate
  validation entirely (lab use only; it wins over `CaCertificatePath` and is called out as
  `certs=ACCEPT-ANY`-style in the startup banner). A configured CA file that is missing or
  not valid PEM fails startup validation.

#### Migrating from the pre-1.x flat sections

The old fixed `ActiveSync:Imap` / `Smtp` / `CalDav` / `CardDav` / `Sieve` sections are
gone. Move each into its role section and add the matching `Provider`:

| Old key | New key |
|---------|---------|
| `ActiveSync:Imap:*` | `ActiveSync:Backends:MailStore:*` + `"Provider": "imap"` |
| `ActiveSync:Smtp:*` | `ActiveSync:Backends:MailSubmit:*` + `"Provider": "smtp"` |
| `ActiveSync:CalDav:*` | `ActiveSync:Backends:Calendar:*` + `"Provider": "caldav"` — and, to keep CalDAV tasks, also add `"Tasks": { "Provider": "caldav" }` |
| `ActiveSync:CardDav:*` | `ActiveSync:Backends:Contacts:*` + `"Provider": "carddav"` |
| `ActiveSync:Sieve:*` (with `Enabled: true`) | `ActiveSync:Backends:Oof:*` + `"Provider": "sieve"` — **`Host` is now required** (see below) |
| `Users:<login>:Imap:UserName` | `Users:<login>:Backends:MailStore:UserName` |
| `Users:<login>:Imap:Host` | `Users:<login>:Backends:MailStore:Settings:Host` |
| `Users:<login>:CalDav:Enabled=false` | `Users:<login>:Backends:Calendar:Enabled=false` |

Two behavior changes to note: the old `TaskFolder`-drives-tasks rule is replaced by the
explicit `Tasks` role assignment above, and the Oof/sieve **`Host` no longer defaults to
the IMAP host** — providers can't see each other's sections, so name it explicitly.
Database-declared users (`eas user ...`) written before the change are **upgraded
automatically at startup**; any row that cannot be converted is logged as an error and
skipped (never silently degraded), so watch the startup log on first boot after upgrading.

### Full option reference

Everything lives under the `ActiveSync` configuration section. Any option can also be set
via environment variables using `__` as the separator, e.g.
`ActiveSync__Backends__MailStore__Host=imap.example.com` (that is what the Docker examples
use).

**Root**

| Option | Default | Description |
|--------|---------|-------------|
| `ReadOnly` | `false` | Pure-mirror mode: every client write is suppressed (see [Read-only mode](#read-only-mode)). |
| `PublicUrl` | `null` | Public base URL of the gateway (e.g. `https://eas.example.com`), advertised by Autodiscover. When unset, the advertised URL is derived from the request's `Host` / `X-Forwarded-Proto` / `X-Forwarded-Host` headers — set this behind a reverse proxy so it never depends on client-supplied headers. |
| `Users` | `null` | Optional per-user overrides keyed by gateway login (see [Per-user overrides](#per-user-overrides)). Undeclared logins are plain pass-through. |
| `RequireDeclaredUsers` | `false` | Allowlist switch: only logins with a `Users` entry (config or database) may authenticate — anyone else gets 401 without a backend probe. An empty entry (`{}`) is a valid grant. |
| `UsersFile` | `null` | Path to a JSON file merged into configuration at startup (full shape: `{ "ActiveSync": { "Users": { ... } } }`) — the natural fit for a mounted Kubernetes Secret/ConfigMap. Changes require a restart. |

**`Backends:MailStore` (required, provider `imap` or `jmap`)**

The `imap` settings are below; for `jmap` see [the JMAP subsection](#backends-provider-jmap).

| Option | Default | Description |
|--------|---------|-------------|
| `Provider` | — | `imap` (settings below) or `jmap`. **Required.** |
| `Host` | — | IMAP server host. **Required** — startup fails without it. |
| `Port` | `993` | IMAP port. |
| `UseSsl` | `true` | Implicit TLS on connect (used when `Security` is unset). |
| `Security` | `null` | Explicit transport security: `None` \| `SslOnConnect` \| `StartTls` \| `StartTlsWhenAvailable` \| `Auto`. When unset, derived from `UseSsl`/`Port`. `None` also skips opportunistic STARTTLS (needed for plaintext test servers advertising STARTTLS with self-signed certs). |
| `AllowInvalidCertificates` | `false` | Accept any TLS certificate (lab use; wins over `CaCertificatePath`). |
| `CaCertificatePath` | `null` | PEM file with CA certificates trusted in addition to the system store (private PKI). Validated at startup. |
| `PathSeparator` | `null` | IMAP folder path separator override; autodetected when unset. |

**`Backends:MailSubmit` (required, provider `smtp` or `jmap`)**

The `smtp` settings are below; for `jmap` see [the JMAP subsection](#backends-provider-jmap).

| Option | Default | Description |
|--------|---------|-------------|
| `Provider` | — | `smtp` (settings below) or `jmap`. **Required.** |
| `Host` | — | SMTP server host. **Required.** |
| `Port` | `465` | SMTP port. |
| `UseSsl` | `true` | Implicit TLS on connect (used when `Security` is unset). |
| `Security` | `null` | Same values/semantics as MailStore's `Security`. |
| `AllowInvalidCertificates` | `false` | As above. |
| `CaCertificatePath` | `null` | As above. |
| `ForceFrom` | `false` | Rewrite the `From` header of outgoing mail to the authenticated user before submission (display name is kept). Off by default because most SMTP servers already enforce sender alignment for authenticated submissions — enable it when yours does not. |

<a id="backends-provider-jmap"></a>
**`Backends:*` (provider `jmap`)** — one HTTP session for mail, OOF, contacts and calendar

The `jmap` provider (RFC 8620/8621, e.g. Stalwart) can fill **MailStore**, **MailSubmit**,
**Oof**, **Contacts** and **Calendar** — assign it to each role you want it to serve and
repeat the same `BaseUrl`; a single JMAP session then backs them all (one auth, EventSource
push for mail). Tasks and Notes have no JMAP standard — keep them on `caldav`/`local`. The
option set is the same in every role section:

| Option | Default | Description |
|--------|---------|-------------|
| `Provider` | — | `jmap`. **Required.** |
| `BaseUrl` | — | Absolute http(s) base URL of the JMAP server (e.g. `https://mail.example.com`). **Required.** The session resource is discovered at `{BaseUrl}/.well-known/jmap`; the server's advertised api/download/upload URLs are re-anchored onto this authority (scheme/host/port), so a reverse proxy or container-network address still works. |
| `AllowInvalidCertificates` | `false` | Accept any TLS certificate (lab use; wins over `CaCertificatePath`). |
| `CaCertificatePath` | `null` | PEM file with CA certificates trusted in addition to the system store (private PKI). Validated at startup. |

```jsonc
"MailStore":  { "Provider": "jmap", "BaseUrl": "https://mail.example.com" },
"MailSubmit": { "Provider": "jmap", "BaseUrl": "https://mail.example.com" },
"Oof":        { "Provider": "jmap", "BaseUrl": "https://mail.example.com" },
"Contacts":   { "Provider": "jmap", "BaseUrl": "https://mail.example.com" },
"Calendar":   { "Provider": "jmap", "BaseUrl": "https://mail.example.com" }
```

What each role covers (and where it's lossy vs CalDAV/CardDAV — no photos, no recurrence
overrides, etc.) is in the [Backend capability matrix](#backend-capability-matrix). JMAP
calendars/contacts need a current server (Stalwart 0.16+); the 0.13 line advertised only
mail/OOF over JMAP. Mixing is fine — e.g. mail on `jmap`, calendar/contacts on `caldav`/`carddav`.

**`Backends:Calendar` / `Backends:Tasks` / `Backends:Contacts` (provider `caldav`/`carddav`, or `jmap` for Calendar/Contacts)**

Omit a role (or leave it on the `local` provider) and that content class is served from the
gateway database (local storage) instead. `Calendar`/`Tasks` use `caldav`, `Contacts` uses
`carddav` — or `Calendar`/`Contacts` can use `jmap` ([above](#backends-provider-jmap); `Tasks`
cannot). When `Tasks` shares the `caldav` provider with `Calendar` it inherits the Calendar
section as its base, so it usually sets only `Provider` (and maybe `TaskFolder`). The settings
below are the `caldav`/`carddav` ones.

| Option | Default | Description |
|--------|---------|-------------|
| `Provider` | — | `caldav` (Calendar/Tasks) or `carddav` (Contacts); Calendar/Contacts may also be `jmap`. |
| `BaseUrl` | — | Absolute http(s) URL of the DAV server. Required (Tasks may inherit it from Calendar). |
| `HomeSetPath` | `null` | Home-set path template; `{user}` and `{localpart}` are substituted (Radicale/Baikal style: `"/{user}/"`). Unset → RFC 6764 discovery via `.well-known` + `current-user-principal`. |
| `TaskFolder` | `"Tasks"` | *(caldav only)* Name of the VTODO (tasks) collection in the calendar home set; the collection with this display name or path segment becomes the ActiveSync Tasks folder (Axigen ships one named "Tasks"). Recurring tasks sync (regenerating "n days after completion" tasks have no iCalendar equivalent and keep their fixed schedule). |
| `CalendarAttachments` | `"Auto"` | *(caldav only)* Event attachments for EAS 16.x clients: `Auto` (enabled, 1 MiB per attachment), `On` (enabled, 16 MiB) or `Off`. Attachments are stored **inline** in the event (base64 `ATTACH` property), so they work against any CalDAV server — the cap protects the DAV server from bloated items. Per-user overridable. |
| `SharedCollections` | unset | *(caldav only)* Extra collection hrefs synced as additional calendar folders for every user: absolute paths (`"/dav/cal/team/"`) or same-host URLs, suffix `\|ro` for gateway-enforced read-only (client edits are silently reverted). Collections the server refuses (403/404) are skipped with a warning. Per-user overridable (a user's list **replaces** the global one); per-user runtime grants via `eas share`. |
| `SendInvitations` | `"Auto"` | *(caldav only)* iMIP invitation mails when the user organizes a meeting: `Auto` (send unless the server advertises `calendar-auto-schedule` or a schedule outbox — a scheduling server invites on its own, and double invites are worse than none), `On` (always) or `Off` (never). The local calendar store always sends (nothing else can). Per-user overridable. |
| `AllowInvalidCertificates` | `false` | As above. |
| `CaCertificatePath` | `null` | As above. |

**`Backends:Oof` (optional, provider `sieve` or `jmap`)** (out-of-office)

Out-of-office can be served two ways. With `jmap` ([above](#backends-provider-jmap)) it maps
to JMAP `VacationResponse` (HTML body + exact start/end times) — just add the `Oof` role with
the same `BaseUrl`. The `sieve` provider (settings below) manages a ManageSieve vacation
script instead. Assign the `Oof` role to the `sieve` provider and the phone's out-of-office settings page
works: Settings→Oof Set uploads a gateway-owned sieve script named `eas-gateway` (RFC 5230
`vacation`, wrapped in a `currentdate` window for scheduled Oof) and makes it the active
script; disabling restores the previously active script. The state database is the source
of truth for what the phone sees — the script is derived output, never parsed back. One
reply body is used for all three EAS audiences (internal/external-known/external-unknown),
and the auto-reply is sent as plain text. Note that sieve has a single active script: while
Oof is enabled, the user's own filter script (if any) is inactive until Oof is disabled
again. Omit the role and Settings→Oof behaves as the historical stub (accepted, ignored).
Credentials default to the user's effective MailStore login; a per-user
`Users:<login>:Backends:Oof` override exists with the same fields.

| Option | Default | Description |
|--------|---------|-------------|
| `Provider` | — | `sieve`. Presence of the role is the on switch (no separate `Enabled` flag). |
| `Host` | — | ManageSieve server. **Required** — unlike the old `Sieve` section, there is no "defaults to the IMAP host" fallback (providers cannot see each other's sections). |
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
  [Operator CLI](#operator-cli-eas).
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
  -e ActiveSync__Backends__MailStore__Provider=imap \
  -e ActiveSync__Backends__MailStore__Host=imap.example.com \
  -e ActiveSync__Backends__MailSubmit__Provider=smtp \
  -e ActiveSync__Backends__MailSubmit__Host=smtp.example.com \
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

The Docker image puts an `eas` command on the PATH, so every command runs with a single
line inside any deployed container (`kubectl exec <pod> -- eas users`, `docker exec
<container> eas devices`). Outside a container the same commands run as
`dotnet ActiveSync.Server.dll <command>` with the configuration available via env vars or
appsettings.json in the working directory.

Inside the image, `eas` is a **slim forwarding client**: instead of cold-starting the whole
gateway for each command, it POSTs the command line to the already-running gateway's `/cli`
endpoint and prints the result, so a command returns in a fraction of a second (handy when
firing several in a row). The request is **sealed with the `ActiveSync:Encryption` master key**,
so only a caller that holds the key (i.e. is really inside the gateway) is served. If no gateway
is running (e.g. repairing an unconfigured one with `config set`) it transparently falls back to
running the command in process. `serve` and `protect` always run locally; `EAS_NO_FORWARD=1`
forces every command local. See [How `eas` forwards](#how-eas-forwards) below.

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
| `users` | List every user — declared accounts (origin, mail, admin, gateway pw, overrides) joined with state usage (devices, last-seen, folder/item counts, blocks). |
| `devices [user]` | List device registrations with last-seen and block state. |
| `folders <user>` | List a user's folder registry. |
| `items <user> [collection]` | List local item metadata (never decrypts). |
| `show <user> <collection> <uid>` | Decrypt and print one local item (needs the Encryption key). |
| `logs [--since 1h] [--level Warning] [--user <u>] [-n 100]` | Show recent logs persisted to the database (Information+; every replica on a shared database). |
| `device password <user> <deviceId>` | Print a device's escrowed recovery password (see [Device security policies](#device-security-policies)). |
| `device wipe <user> <deviceId> [--cancel]` | Arm a **16.1 account-only wipe**: the device removes this account (never a factory reset) and the partnership is auto-blocked after the acknowledgment. Warns when the device last spoke <16.1 — use `block` for those. |

**User management** (see [Database-declared users](#database-declared-users-eas-user-))

| Command | What it does |
| --- | --- |
| `user show <login>` | The effective entry for one login, secrets masked. (`eas users` lists them all.) |
| `user add <login>` | Declare a user in the database (an empty entry is an allowlist grant; copies a same-login config entry as the starting point). |
| `user remove <login>` | Delete the database entry — a same-login config entry becomes active again. |
| `user set <login> <key> <value>` | Set one field by path (`MailAddress`, `Admin` — grants the web admin UI, `Backends:Calendar:Enabled`, `Backends:MailStore:Settings:Host`, ...); password keys are hashed/sealed automatically. |
| `user unset <login> <key>` | Clear one field (an emptied entry remains an allowlist grant). |
| `user password <login>` | Set the gateway password from stdin (stored as a pbkdf2$ hash). |
| `user secret <login> <key>` | Set a backend password (`Backends:MailStore:Password`, ...) from stdin (stored sealed, enc:v1:). |

**Global settings** — stored in the database; the database wins over appsettings/env, which win
over the built-in defaults. Applies live within ~1s (a background change-stamp poll), except a few
listener settings that apply on restart. The two bootstrap sections (`Database`, `Encryption`) are
env/file only — they are needed to open and decrypt the database that stores everything else.

| Command | What it does |
| --- | --- |
| `config list` | Every setting with its effective value and source (default / config / db). |
| `config get <key>` | One setting's effective value and source (e.g. `config get ActiveSync:ReadOnly`). |
| `config set <key> <value>` | Store a setting (e.g. `config set ActiveSync:Eas:MaxHeartbeatSeconds 1800`); validated by type/range; live in ~1s (listener settings say "restart"). |
| `config unset <key>` | Clear a database setting — falls back to the config file, then the code default. |

**Access control & cleanup**

| Command | What it does |
| --- | --- |
| `block <user> [deviceId]` | Refuse logins with **403** — the whole user, or one device. |
| `unblock <user> [deviceId]` | Remove a login block. |
| `share add <user> <collectionHref> [--read-only]` | Grant an extra CalDAV collection to a user as a shared calendar folder (`--read-only` = client edits silently reverted by the gateway). Applies at the next backend-session build (idle recycle or restart). |
| `share remove <user> <collectionHref>` | Remove a grant — the folder disappears at the next session build. |
| `share list [user]` | List shared-calendar grants. |
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

### How `eas` forwards

The `eas` binary in the image is a tiny client that forwards each command over HTTP to the
running gateway on its normal port and prints back stdout/stderr and the exit code. This
avoids a cold start of the full app per command; secret-setting verbs (`user password`,
`user secret`, …) forward too.

- **Auth is proof of the master key.** The client AES-256-GCM **seals** the request (args, stdin
  and a timestamp) with the `ActiveSync:Encryption` key it reads from the same config the server
  uses; the gateway opens it with the same key. That key is injected only into the gateway
  container — **not** a co-located Kubernetes sidecar or a `--network host` peer, which share the
  loopback interface but not the container's environment/secrets. So a valid envelope proves the
  caller is a real key holder (the trust set that can already decrypt everything at rest), which a
  bare loopback check can't: in a shared network namespace a sidecar's `127.0.0.1` reaches the
  gateway. The timestamp bounds replay of a sniffed ciphertext, and the payload is encrypted on
  the wire. Loopback is kept as a cheap pre-filter (and no forwarded-headers middleware exists, so
  the peer address is the real one); requests that fail either check get a 404.
- **Dev/test without a key** (`ActiveSync:Encryption:AllowPlaintext=true`): there is no key to
  seal with, so `/cli` falls back to loopback-only — acceptable for the same reason that mode runs
  content unencrypted.
- **Disable it** with `eas config set ActiveSync:Cli:Enabled false` (live) — `eas` then falls
  back to running every command in process.
- **`serve` and `protect` never forward** (they take arbitrary `--Section:Key=value` overrides);
  `EAS_NO_FORWARD=1` forces all commands to run in process. If the gateway isn't reachable, any
  command falls back to in-process execution automatically.
- The forwarding client ships **only in the container image**; the download zips run the full
  app directly as before.

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

Two layers:

- **Unit tests** (`Category!=Integration`) run anywhere, no docker needed.
- **Integration tests** (`tests/ActiveSync.Integration.Tests`) host the gateway in-process
  and drive it with a real mini EAS client speaking 14.1 or 16.1 (WBXML, base64 query,
  Provision handshake)
  against **real backends** — including the flagship scenario where user1 sends a mail
  through the gateway and user2 receives it on a second EAS client. They **skip
  automatically** when no backend stack is reachable, so `dotnet test` is always safe.

An opt-in third layer (`--filter Category=AxigenLive`) runs the 16.x/GAL/free-busy
scenarios against a real Axigen server. It skips unless `AS_TEST_AXIGEN_HOST`,
`AS_TEST_AXIGEN_USER` and `AS_TEST_AXIGEN_PASSWORD` are all set; point it only at a
dedicated throwaway mailbox — the tests create and delete real items.

### Backend stacks

All stacks publish the same ports (IMAP 143, SMTP 587, DAV 5232) and users
(`user1@example.com` / `user2@example.com`, password `pass`); the Stalwart stack also
publishes ManageSieve 4190 and serves the full JMAP surface (mail + calendars + contacts +
vacation) on the DAV port:

```bash
# Default: Stalwart 0.16 all-in-one — mail with real delivery, CalDAV/CardDAV, ManageSieve,
# and JMAP. A small custom image (pinned server + stalwart-cli, see docker/backends/stalwart/
# Dockerfile) self-provisions on first boot: its entrypoint uses stalwart-cli to bootstrap and
# then declaratively `apply` the users, plaintext listeners and relaxed test policy against the
# server's own management API. No mounted config, no separate provisioner — just build + up.
docker compose -f docker/backends/stalwart/docker-compose.yml up -d --build --wait

# Second backend (also runs in CI): docker-mailserver (Postfix+Dovecot) + Radicale. A
# one-shot in the compose provisions each user's default calendar + address book (Radicale
# ships none). --wait blocks until healthy AND provisioned.
docker compose -f docker/backends/mailserver/docker-compose.yml up -d --wait
AS_TEST_STACK=mailserver dotnet test --filter Category=Integration

# Cyrus IMAP — an independent C server: IMAP + CalDAV/CardDAV + JMAP + ManageSieve (mail
# submits over JMAP; LMTP-only for delivery). Home-sets live under /dav/…/user/{user}/.
docker compose -f docker/backends/cyrus/docker-compose.yml up -d --build --wait

# Baikal (sabre/dav) for CalDAV/CardDAV + docker-mailserver as the mail companion. A
# self-provisioning custom image bakes the config + a seeded SQLite DB, so DAV works on first
# boot with no installer. Home-sets live under /dav.php/…/{user}/.
docker compose -f docker/backends/baikal/docker-compose.yml up -d --build --wait

# Axigen full groupware (IMAP/SMTP + CalDAV/CardDAV) via its built-in 3-day trial/demo mode.
# A self-provisioning custom image creates the domain/users and opens the listeners on first
# boot. Trial mode is EVALUATION ONLY (running it in CI is an accepted trade-off).
# Home-sets live under /Calendar/ and /Contacts/.
docker compose -f docker/backends/axigen/docker-compose.yml up -d --build --wait

# Apache James (memory) — a second, independent Java IMAP + SMTP submission implementation
# (no CalDAV/CardDAV/JMAP/Sieve). Run this leg with AS_TEST_DAV_URL=none so calendar/contacts
# fall back to the gateway's local stores and the DAV tests skip.
docker compose -f docker/backends/james/docker-compose.yml up -d --build --wait
# AS_TEST_STACK=james AS_TEST_DAV_URL=none dotnet test --filter Category=Integration
```

**Fast per-change check (recommended).** `scripts/test-fast` runs the suite against **stalwart +
axigen in parallel** and leaves both stacks running for the next change (they start only if not
already healthy, and are reused when warm). To coexist, these two stacks use **dedicated host
ports** (stalwart `10143/10587/10190/10232`, axigen `20143/20587/20232`) via the compose
`${STALWART_*}` / `${AXIGEN_*}` overrides — the canonical set (`143/587/5232/4190`) stays free, so
you can leave them up and still run an on-demand backend (e.g. baikal) without a port clash:

```powershell
./scripts/test-fast.ps1                 # Windows  (-Filter <expr>, -Down to tear down)
scripts/test-fast.sh                    # Linux / devcontainer  (-f <expr>, -d to tear down)
```

Or run the suite against **every** stack in turn with one command (brings each up, tests,
tears down, prints a per-backend summary — sequential, since these use the canonical ports):

```powershell
./scripts/test-backends.ps1            # Windows
scripts/test-backends.sh               # Linux / devcontainer
```

Don't drive stalwart/axigen through both runners at the same time — `test-fast` uses dedicated
ports and `test-backends` uses canonical ones, so compose would recreate the container on switch.

### Where tests run

- **Visual Studio (Windows host)**: bring a stack up, run tests from Test Explorer —
  localhost defaults just work. Breakpoints hit gateway code (it runs in-process).
- **VS Code devcontainer**: "Reopen in Container" — the `.devcontainer` compose brings the
  Stalwart stack up alongside the workspace with env preset.
- **GitHub Actions**: `.github/workflows/build.yaml` (push + dispatch) is a three-stage
  pipeline that compiles the solution exactly once:
  - **`test`** — the Dockerfile `test` stage builds everything and runs the unit tests,
    exporting every layer to a `type=gha` build cache.
  - **`integration`** — a matrix leg per backend (`stalwart`, `mailserver`, `cyrus`, `baikal`,
    `james`, `axigen`) runs in parallel. Each loads the cached test image, brings its backend + a
    throwaway Postgres up, and runs the integration suite **from that image**
    (`dotnet test --no-build`). Tests for capabilities a backend lacks (JMAP/Sieve on
    docker-mailserver, CalDAV free-busy on Radicale, SMTP submission / password-enforcement on
    the Cyrus test image, JMAP/Sieve on the Baikal DAV stack, all of CalDAV/CardDAV + JMAP + Sieve
    on the mail-only James stack) skip cleanly. Legs push nothing. (The `cyrus` leg is currently
    disabled — under investigation — so it never runs; its compose/config remain in the tree.)
    The **`axigen`** leg runs Axigen's evaluation-only trial mode on every trigger (an accepted
    trade-off) and gates `publish` like the other legs.
  - **`publish`** — only when **every** backend leg is green: the multi-arch runtime image,
    the NuGet packages and the release zips are built from the warm cache and pushed.

  Adding a backend = one more matrix entry + its `docker/backends/<name>/docker-compose.yml`.

  Local reproduction — run the whole matrix sequentially:

  ```powershell
  ./scripts/test-backends.ps1        # or scripts/test-backends.sh
  ```

- **Image builds**: the Dockerfile has a `test` stage, so every `docker build` runs the
  unit test suite (integration tests need sibling containers and are CI-compose only).

### Cutting a release

Actions → **Create Release** → enter the version (`1.0.7` or `v1.0.7`) and an optional
headline. The workflow validates the version, generates release notes from the commit
subjects since the previous release, pushes the tag, creates the release and dispatches
the tag build. The tag build then pushes the versioned docker image and attaches
the download zips to the release once the full test suite is green — so the release page
is complete a few minutes after dispatch. Manual tagging still works: a tag pushed by
hand gets a minimal auto-created release with the same assets.

## Architecture

```
src/
  ActiveSync.Protocol/   WBXML codec (all MS-ASWBXML code pages), MS-ASHTTP base64 query
                         parser, EAS constants. No ASP.NET dependencies.
  ActiveSync.Core/       Backend abstractions (IContentStore, IBackendSession) + the
                         provider engine (registry, composite session, session factory),
                         EF Core state store (devices, folder registry, sync keys +
                         snapshots, DAV href map), differential sync engine (CollectionDiff).
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
drop into `/app/plugins` and register themselves — no fork required. The contract
(`ActiveSync.Protocol`, `ActiveSync.Core`, `ActiveSync.Backends.Common`) is published to
NuGet per release. See **[docs/plugins.md](docs/plugins.md)**.

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

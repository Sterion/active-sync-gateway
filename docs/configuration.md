# Full configuration & option reference

Everything lives under the `ActiveSync` configuration section. Any option can be set three
ways, in ascending precedence:

1. **Code defaults** (shown in the tables below).
2. **`appsettings.json` / environment variables** — env uses `__` as the separator, e.g.
   `ActiveSync__Backends__MailStore__Host=imap.example.com` (that is what the Docker
   examples use).
3. **The state database**, via [`eas config set <key> <value>`](cli.md#global-settings) or
   the web admin's Settings page — this **wins** over the file/env layer.

A database change is applied by every running replica within ~1 s (no restart), **except** a
few listener settings — HTTP/HTTPS ports, self-signed-TLS and metrics enable/port, OIDC
authority/client/scopes — that apply on the next restart (noted per option below). The two
**bootstrap** sections (`Database`, `Encryption`) are the exception to the whole model: they
are needed to open and decrypt the database that stores everything else, so they can only be
set via file/env, never `eas config`.

For how the config layers interact, unconfigured mode, and per-user overrides, see the
[Configuration](../README.md#configuration) section of the README.

## Root

| Option | Default | Description |
|--------|---------|-------------|
| `ReadOnly` | `false` | Pure-mirror mode: every client write is suppressed (see [Read-only mode](../README.md#read-only-mode)). |
| `PublicUrl` | `null` | Public base URL of the gateway (e.g. `https://eas.example.com`), advertised by Autodiscover. When unset, the advertised URL is derived from the request's `Host` / `X-Forwarded-Proto` / `X-Forwarded-Host` headers — set this behind a reverse proxy so it never depends on client-supplied headers. |
| `Users` | `null` | Optional per-user overrides keyed by gateway login (see [Per-user overrides](../README.md#per-user-overrides)). Undeclared logins are plain pass-through. |
| `RequireDeclaredUsers` | `false` | Allowlist switch: only logins with a `Users` entry (config or database) may authenticate — anyone else gets 401 without a backend probe. An empty entry (`{}`) is a valid grant. |
| `AutoProvisionUsers` | `true` | Create a database account row for a pass-through login the first time it clears its MailStore probe over EAS, so the user becomes visible/manageable (`eas users`, admin UI), blockable, and able to use the self-service portal. The row carries no gateway password, so auth is unchanged. Set `false` to keep pass-through logins ephemeral. Inert under `RequireDeclaredUsers`. See [Database-declared users](../README.md#database-declared-users-eas-user-). |
| `UsersFile` | `null` | Path to a JSON file merged into configuration at startup (full shape: `{ "ActiveSync": { "Users": { ... } } }`) — the natural fit for a mounted Kubernetes Secret/ConfigMap. Changes require a restart. |

## `Backends:MailStore` (required, provider `imap` or `jmap`)

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

## `Backends:MailSubmit` (required, provider `smtp` or `jmap`)

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
| `ForceFrom` | `false` | Rewrite the `From` header of outgoing mail to the authenticated user before submission (display name is kept; only applies when the login is a mail address). Off by default because most SMTP servers already enforce sender alignment for authenticated submissions — enable it when yours does not. |

<a id="backends-provider-jmap"></a>
## `Backends:*` (provider `jmap`) — one HTTP session for mail, OOF, contacts and calendar

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
overrides, etc.) is in the [Backend capability matrix](backends.md). JMAP
calendars/contacts need a current server (Stalwart 0.16+); the 0.13 line advertised only
mail/OOF over JMAP. Mixing is fine — e.g. mail on `jmap`, calendar/contacts on `caldav`/`carddav`.

## `Backends:Calendar` / `Backends:Tasks` / `Backends:Contacts` (provider `caldav`/`carddav`, or `jmap` for Calendar/Contacts)

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

## `Backends:Oof` (optional, provider `sieve` or `jmap`) — out-of-office

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

## `Database` (bootstrap — file/env only)

| Option | Default | Description |
|--------|---------|-------------|
| `Provider` | `"Sqlite"` | `Sqlite` or `Postgres`. A `postgresql://` connection string implies `Postgres` automatically, so the provider can be left unset in that case. |
| `ConnectionString` | `"Data Source=activesync.db"` | Provider connection string. Postgres accepts the Npgsql keyword form (`"Host=db;Database=activesync;Username=...;Password=..."`) **or** a libpq-style URI (`postgresql://user:password@host:5432/database?sslmode=require`) — a [CloudNativePG](https://cloudnative-pg.io/) app secret's `uri`/`fqdn-uri` value works verbatim (the `jdbc-uri` variants work too; the `jdbc:` prefix is stripped). Passwords are redacted in the startup banner. |

## `Encryption` (bootstrap — file/env only; local content at rest)

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

## `Eas` (protocol tuning)

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

## `Auth` (authentication hardening)

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

## `Log` (logging output & retention)

Controls the console line shape/format and the database log buffer that backs
[`eas logs`](cli.md#inspection) and the admin logs view. `Mode` and `Format` are
**restart-tier**; the rest apply live. The console-shape prose (examples, CLEF, Verbose
wire logging) is in the [Logging](../README.md#configuration) narrative in the README.

| Option | Default | Description |
|--------|---------|-------------|
| `Mode` | `Standard` | Console line shape: `Simple` (time + 3-letter level, the pre-1.0.7 look), `Standard` (date, full level, logger category, pipe-delimited) or `Extended` (Standard plus every structured property, thread id and machine name). In `Json` format `Simple`/`Standard` are identical. |
| `Format` | `Text` | `Text` (human-readable) or `Json` (CLEF — one JSON object per event, for Loki/Elastic). |
| `Database` | `true` | Also persist rendered events to the state database (rolling buffer for `eas logs` and the admin logs view). Trace/Debug are never persisted regardless. |
| `DbMinimumLevel` | `Information` | Minimum level persisted to the database: `Information`, `Warning`, `Error` or `Fatal`. |
| `RetentionDays` | `7` | Days of database log history to keep; a background sweep deletes older. `0` disables the sweep. |

## `Tls` (the gateway's HTTPS listener)

All keys are **restart-tier** — the listener binds and the certificate is read once at startup
(a rotated mount takes effect on the next restart). The certificate is either operator-supplied
(`CertificatePath` — a mounted PEM/PFX, e.g. cert-manager/ACME) or, when none is set, a
self-signed one generated on first serve and persisted in the state database (sealed with the
Encryption master key). A configured-but-unloadable certificate fails startup. Inspect the
active certificate on the admin **TLS** page or with [`eas tls`](cli.md#inspection). See
[HTTPS](../README.md#https-self-signed-by-default-or-bring-your-own-certificate).

| Option | Default | Description |
|--------|---------|-------------|
| `Enabled` | `true` | Serve the gateway's own HTTPS listener on `Port`. `false` = no HTTPS (terminate TLS in front of the gateway). |
| `Port` | `5443` | HTTPS listen port. |
| `CertificatePath` | `null` | Path to a mounted certificate to serve instead of the self-signed one: a PEM full chain (pair it with `CertificateKeyPath`) or a PKCS#12/PFX bundle. Unset = self-signed. |
| `CertificateKeyPath` | `null` | Path to the PEM private key that pairs with a PEM `CertificatePath`. Leave unset for a PFX bundle. |
| `CertificatePassword` | `null` | Password for the PFX bundle, or an encrypted PEM key. Stored sealed (`enc:v1:`) and unsealed only when the certificate is loaded. |

## `Policy` (device security policies)

Option names match the MS-ASPROV `EASProvisionDoc` elements 1:1, so Microsoft's
documentation applies directly. Unset numeric options are omitted from the policy
document (the device default applies). See [Device security policies](../README.md#device-security-policies).

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

## `Metrics` (Prometheus exporter)

`Enabled` and `Port` are **restart-tier**; `PerUser` applies live. See
[Metrics and readiness](../README.md#metrics-and-readiness).

| Option | Default | Description |
|--------|---------|-------------|
| `Enabled` | `false` | Enable the OpenTelemetry Prometheus exporter. |
| `Port` | `null` | Dedicated plain-HTTP port for `/metrics` (gated on the connection's local port, not spoofable headers). Unset → `/metrics` shares the main listeners (protect it via ingress/network policy then). |
| `PerUser` | `true` | Per-account (`user=…`) labels on request/item/mail/session metrics. Disable on large multi-tenant fleets where the label cardinality would hurt (the label collapses to `-`). |

## `WebUi` (admin + user-portal web interfaces)

Enable/disable flags apply **live**. The full endpoint reference, OIDC claim-mapping details
and security model are in **[docs/webui.md](webui.md)**; this table is just the settable keys.
The OIDC issuer/client/scopes/login-claim knobs are **restart-tier** (the handler registers
at startup); `AdminClaim`/`AdminClaimValue`/`AutoProvision` apply live.

| Option | Default | Description |
|--------|---------|-------------|
| `Admin:Enabled` | `false` | Serve the web admin interface under `/admin`. |
| `UserPortal:Enabled` | `false` | Serve the user self-service portal under `/user`. |
| `AllowInsecureCookies` | `false` | Emit the session (and OIDC correlation/nonce) cookies without `Secure` when the request resolves to http. Off by default — the gateway cannot tell a plain-http request apart from one that came through a TLS-terminating proxy forwarding neither `PublicUrl` nor `X-Forwarded-Proto`, so the flag would silently disappear on exactly the deployment that needs it. Turn on only to run the portals over plain http locally; logged as a warning at startup. Restart-tier. |
| `Oidc:Enabled` | `true` | Master switch for OIDC. When `false`, the identity-provider settings are kept but ignored (web login falls back to local passwords) — turn OIDC off without deleting its config. Restart-tier. |
| `Oidc:Authority` | `null` | Issuer URL of the identity provider. When set (and enabled), **all** web logins go through the IdP and the local login form is disabled. Restart-tier. |
| `Oidc:ClientId` | `null` | OIDC client id of this gateway. Restart-tier. |
| `Oidc:ClientSecret` | `null` | OIDC client secret, plaintext or `enc:v1:` sealed with the Encryption master key. Rendered masked. Restart-tier. |
| `Oidc:Scopes` | `openid profile email` | Space-separated scopes requested from the IdP. Restart-tier. |
| `Oidc:LoginClaim` | `preferred_username` | Token claim mapped to the gateway login (set to `email` for email-keyed accounts). Restart-tier. |
| `Oidc:AdminClaim` | `null` | Token claim granting web admin as an alternative to the account `Admin` flag (e.g. `roles`). Unset: only the account flag grants admin. Setting it **requires** `AdminClaimValue`. |
| `Oidc:AdminClaimValue` | `null` | Value of `AdminClaim` that grants admin — required whenever `AdminClaim` is set (startup fails otherwise), because "any value" would hand admin to every user carrying the claim. Use `*` to deliberately accept any value. |
| `Oidc:AutoProvision` | `false` | Create a database account for unknown OIDC logins on first sign-in (`MailAddress` from the `email` claim), so they can use the portal. Admin only via the claim until an admin grants the flag. |
| `Oidc:RequireHttpsMetadata` | `true` | Require HTTPS for the OIDC discovery endpoint. Disable only for local dev IdPs. Restart-tier. |

## `Cli` (loopback CLI-forwarding endpoint)

| Option | Default | Description |
|--------|---------|-------------|
| `Enabled` | `true` | Answer the `POST /cli` endpoint that the slim `eas` client forwards commands to (sealed with the Encryption master key). Set `false` and `eas` runs every command in process. See [How `eas` forwards](cli.md#how-eas-forwards). |

## `Plugins` (out-of-repo backend plugins)

| Option | Default | Description |
|--------|---------|-------------|
| `Directory` | `plugins` (container: `/app/plugins`) | Directory scanned for out-of-repo backend plugins — one subdirectory per plugin. Restart-tier. See **[docs/plugins.md](plugins.md)**. |

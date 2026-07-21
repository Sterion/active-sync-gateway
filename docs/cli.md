# Operator CLI (`eas`)

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

## Server

| Command | What it does |
| --- | --- |
| *(none)* | Show the banner for the config that WOULD run — including the declared users — and exit; nothing starts. |
| `serve` | Start the gateway (accepts `--ActiveSync:Section:Key=value` overrides). |
| `healthcheck` | Probe the running gateway's `/healthz`; exit 0 when healthy (backs the container `HEALTHCHECK`). |
| `help` | List all commands (`eas <command> --help` shows per-command arguments). |

## Inspection

| Command | What it does |
| --- | --- |
| `users` | List every user — declared accounts (origin, mail, admin, gateway pw, overrides) joined with state usage (devices, last-seen, folder/item counts, blocks). |
| `devices [user]` | List device registrations with last-seen and block state. |
| `folders <user>` | List a user's folder registry. |
| `items <user> [collection]` | List local item metadata (never decrypts). |
| `show <user> <collection> <uid>` | Decrypt and print one local item (needs the Encryption key). |
| `logs [--since 1h] [--level Warning] [--user <u>] [-n 100]` | Show recent logs persisted to the database (Information+; every replica on a shared database). |
| `device password <user> <deviceId>` | Print a device's escrowed recovery password (see [Device security policies](../README.md#device-security-policies)). |
| `device wipe <user> <deviceId> [--cancel]` | Arm a **16.1 account-only wipe**: the device removes this account (never a factory reset) and the partnership is auto-blocked after the acknowledgment. Warns when the device last spoke <16.1 — use `block` for those. |

## User management

See [Database-declared users](../README.md#database-declared-users-eas-user-) for the model
(three sources, per-login precedence, secret handling).

| Command | What it does |
| --- | --- |
| `user show <login>` | The effective entry for one login, secrets masked. (`eas users` lists them all.) |
| `user add <login>` | Declare a user in the database (an empty entry is an allowlist grant; copies a same-login config entry as the starting point). |
| `user remove <login>` | Delete the database entry — a same-login config entry becomes active again. |
| `user disable <login>` · `user enable <login>` | Turn an account off/on. A disabled account refuses **every** login — all devices, EAS and web — with 403 after valid credentials, until re-enabled (a persistent property of the account, distinct from an ad-hoc `block`). |
| `user set <login> <key> <value>` | Set one field by path (`MailAddress`, `Admin` — grants the web admin UI, `Enabled` — `false` disables the account, `Backends:Calendar:Enabled`, `Backends:MailStore:Settings:Host`, ...); password keys are hashed/sealed automatically. |
| `user unset <login> <key>` | Clear one field (an emptied entry remains an allowlist grant). |
| `user password <login>` | Set the gateway password from stdin (stored as a pbkdf2$ hash). |
| `user secret <login> <key>` | Set a backend password (`Backends:MailStore:Password`, ...) from stdin (stored sealed, enc:v1:). |

## Global settings

Stored in the database; the database wins over appsettings/env, which win over the built-in
defaults. Applies live within ~1s (a background change-stamp poll), except a few listener
settings that apply on restart. The two bootstrap sections (`Database`, `Encryption`) are
env/file only — they are needed to open and decrypt the database that stores everything else.
The settable keys, defaults and tiers are catalogued in
**[docs/configuration.md](configuration.md)**.

| Command | What it does |
| --- | --- |
| `config list` | Every setting with its effective value and source (default / config / db). |
| `config get <key>` | One setting's effective value and source (e.g. `config get ActiveSync:ReadOnly`). |
| `config set <key> <value>` | Store a setting (e.g. `config set ActiveSync:Eas:MaxHeartbeatSeconds 1800`); validated by type/range; live in ~1s (listener settings say "restart"). |
| `config unset <key>` | Clear a database setting — falls back to the config file, then the code default. |

## Access control & cleanup

| Command | What it does |
| --- | --- |
| `block <user> [deviceId]` | Refuse logins with **403** — the whole user, or one device. |
| `unblock <user> [deviceId]` | Remove a login block. |
| `share add <user> <collectionHref> [--read-only]` | Grant an extra CalDAV collection to a user as a shared calendar folder (`--read-only` = client edits silently reverted by the gateway). Applies at the next backend-session build (idle recycle or restart). |
| `share remove <user> <collectionHref>` | Remove a grant — the folder disappears at the next session build. |
| `share list [user]` | List shared-calendar grants. |
| `purge user <user>` | Delete ALL of a user's state (asks for confirmation, or `--yes`). |
| `purge device <user> <deviceId>` | Delete one device registration (it re-syncs from scratch). |

## Secret helpers

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

## How `eas` forwards

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

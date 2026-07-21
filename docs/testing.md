# Testing

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

## Backend stacks

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

## Where tests run

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

## Cutting a release

Actions → **Create Release** → enter the version (`1.0.7` or `v1.0.7`) and an optional
headline. The workflow validates the version, generates release notes from the commit
subjects since the previous release, pushes the tag, creates the release and dispatches
the tag build. The tag build then pushes the versioned docker image and attaches
the download zips to the release once the full test suite is green — so the release page
is complete a few minutes after dispatch. Manual tagging still works: a tag pushed by
hand gets a minimal auto-created release with the same assets.

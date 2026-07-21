# The web interfaces: `/admin` and `/user`

The `ActiveSync.WebUi` assembly serves two browser UIs from the gateway's normal listeners:

- **`/admin`** — the admin interface: everything the `eas` CLI can manage, in a browser.
- **`/user`** — the self-service portal: each user manages their **own** password and
  backend credentials, nothing else.

Both are **disabled by default** and gated **live** by two independent flags (a database
settings change applies within ~1 s, no restart — the same pipeline as every other live
setting):

```bash
eas config set ActiveSync:WebUi:Admin:Enabled true
eas config set ActiveSync:WebUi:UserPortal:Enabled true   # also on the admin Settings page
```

While a flag is off, everything under its prefix answers 404.

## Bootstrap on a fresh gateway

The admin UI works in **unconfigured mode** (no mail backend yet) — it is how you can set
the gateway up. A declared account with a **hashed gateway password** verifies locally, so
no backend is needed to log in:

```bash
echo -n 'admin-password' | eas user password admin   # stored as a pbkdf2$ hash
eas user set admin Admin true                        # grants /admin
eas config set ActiveSync:WebUi:Admin:Enabled true
# open http://host:5080/admin (or https://host:5443 with the self-signed certificate)
```

Then point the gateway at its backends on the **Backends** page: pick a provider per role
and fill the fields it asks for. Assigning MailStore and MailSubmit is what brings EAS
online — until then those endpoints answer 503, though the gateway itself reports ready
(`/readyz` shows `"configured": false` without failing, so an orchestrator will route the
traffic you need to reach this page).

## Who can log in

**Local mode** (no OIDC configured): the login form runs the exact verdict path the phones
get — the local password rule first, else the mail-backend probe — plus the web-only rules:

- Only **declared** accounts (config or database) may log in; a pure pass-through login has
  nothing to manage. The response is indistinguishable from a wrong password. (With
  `ActiveSync:AutoProvisionUsers` **on — the default** — a pass-through user becomes declared
  the first time a phone syncs, so this portal then accepts them, verifying the password
  against their backend exactly as the phone does. See the accounts section of the README.)
- `/admin` additionally requires the account's **`Admin` flag**
  (`eas user set <login> Admin true`, or the checkbox in the admin users editor).
- A **disabled** account (`eas user disable`, or the Enabled toggle / Disable button on the
  admin Users page) refuses the web exactly like EAS — 403 after valid credentials, every
  device — until re-enabled. A user-level `eas block` does the same as an ad-hoc/temporary
  measure. The CLI remains the un-lockable escape hatch.
- The same brute-force throttle as EAS applies, under web-specific keys.

**OIDC mode** (`Oidc:Authority` set): local web login is **disabled entirely** — the local
password stays what it really is, the ActiveSync connect password. The login views show a
"Sign in with SSO" button instead.

## OIDC

```bash
eas config set ActiveSync:WebUi:Oidc:Authority https://id.example.com/realms/main   # restart tier
eas config set ActiveSync:WebUi:Oidc:ClientId eas-gateway                           # restart tier
eas config set ActiveSync:WebUi:Oidc:ClientSecret <secret>                          # restart tier, stored sealed
# optional:
eas config set ActiveSync:WebUi:Oidc:LoginClaim preferred_username                  # restart tier (default)
eas config set ActiveSync:WebUi:Oidc:AdminClaim groups                              # live
eas config set ActiveSync:WebUi:Oidc:AdminClaimValue eas-admin                      # live
eas config set ActiveSync:WebUi:Oidc:AutoProvision true                             # live
```

- The **`LoginClaim`** (default `preferred_username`) maps the token to a gateway login.
  Register the redirect URI `https://<gateway>/oidc/callback` at the IdP (the callback is
  deliberately outside `/admin` and `/user` so a portal-only deployment works).
- **The login claim is not an identity.** `preferred_username` (and `email`) is user-mutable at
  several common IdPs — Keycloak's self-service account console edits it — so a login match
  alone would let any directory user claim someone else's gateway account, admin flag included.
  The gateway therefore binds each account to the IdP's immutable `sub`: the first OIDC sign-in
  of a **database** account records it (`OidcSubject`), and every later ticket must present the
  same subject or it is refused. A **config-declared** account is never written to, so it stays
  unbound until you set `OidcSubject` on it yourself — do that, or use a `LoginClaim` the IdP
  does not let users edit. Re-binding after a genuine IdP migration means clearing the field
  (`eas user set <login> OidcSubject ""`).
- **Behind a TLS-terminating proxy** (e.g. a Kubernetes ingress that forwards to the plain-HTTP
  port), the gateway must know it's serving https so the `redirect_uri` it sends the IdP — used
  at both the authorize step and the token exchange — is `https`, not `http`. Set
  `eas config set ActiveSync:PublicUrl https://<gateway>` (preferred; header-independent), or make
  the proxy send `X-Forwarded-Proto: https`. Without one of these the browser refuses the callback
  ("the information you're about to submit is not secure") because the IdP form-posts tokens to an
  `http` URL.
- **Admin** = the account's `Admin` flag **or** the `AdminClaim` carrying exactly
  `AdminClaimValue`. **`AdminClaimValue` is mandatory whenever `AdminClaim` is set** and startup
  refuses the pair without it: `AdminClaim: groups` alone would grant gateway admin to everyone
  who has *any* groups claim — the whole directory. Set `AdminClaimValue` to `*` if you really
  do mean "any value of this claim grants admin".
- **`AutoProvision`**: an unknown login is JIT-created as a plain database account
  (`MailAddress` from the `email` claim) and can then use the portal — it shows up in
  `eas users` like any declared user. A JIT account can only be admin **via the claim**
  until an admin grants the flag. With `AutoProvision` off, unknown logins are turned away.
- The web session cookie carries only the gateway login + admin bit — IdP claims never
  leak into it. Logout is local (no RP-initiated IdP logout yet).
- Authority/ClientId/ClientSecret/Scopes/LoginClaim are **restart tier** (the handler
  registers at startup); AdminClaim/AdminClaimValue/AutoProvision apply **live**.

## Security model

- One passive cookie scheme (`eas.webui`: HttpOnly, SameSite=Strict, `Secure`, sliding 12 h).
  It never challenges, so the EAS Basic-auth endpoints and `/metrics` are untouched.
  `WebUi:AllowInsecureCookies` drops `Secure` for plain-http local development only.
- **Live sessions are re-checked, at most once a minute each.** The ticket is self-contained,
  so every session re-resolves its account on the next request after that minute: an account
  that is gone, disabled or blocked is signed out immediately, and a cleared `Admin` flag drops
  the admin capability while leaving the portal session alive. `eas user disable` / `eas block`
  therefore reach an already-signed-in browser rather than waiting out the 12 hours.
  Two limits worth knowing: admin granted by the **OIDC admin claim** is carried forward (the
  IdP's claims never enter the session, so it cannot be re-derived — revoke it at the IdP), and
  the check **fails open** on a database fault so an outage does not log every operator out
  mid-incident (it retries on the very next request).
- **Logout is server-side.** Signing out records a per-login cut-off (`WebSessionRevocations`),
  so a copy of the cookie taken before the click stops working at its next revalidation rather
  than staying valid for the rest of the 12 hours. A portal password change does the same to
  every *other* session of that login and re-issues the current browser's. An admin-set or
  CLI-set password does **not** yet stamp a cut-off.
- CSRF: SameSite=Strict **plus** a required `X-EAS-WebUi: 1` header on every non-GET API
  call — a cross-site request can produce neither.
- Strict CSP (`default-src 'self'` — no inline **scripts**; inline style attributes are
  allowed for view layout), `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer` on the
  UI prefixes.
- Cookie signing keys live in the state database (`DataProtectionKeys`), sealed with the
  Encryption master key — sessions survive restarts and validate on every replica, and a
  database dump alone cannot forge web sessions.
- Stored secrets never leave the server: every API response reports passwords as
  set/unset only, and the OIDC client secret renders masked.

## The API (what the SPA talks to)

All under `/admin/api` (policy: admin session) and `/user/api` (any web session); JSON;
non-GET calls need the `X-EAS-WebUi` header. Login/logout/mode are anonymous.

| Endpoint | What it does |
| --- | --- |
| `GET/POST {portal}/api/auth/mode`, `login`, `logout`, `session` | Session lifecycle (login answers 404 under OIDC). |
| `GET /admin/api/settings` · `PUT/DELETE /admin/api/settings/{key}` | The `eas config` catalogue with per-key default/value/source (default · config · db) and tier (live · restart); same validation. |
| `GET /admin/api/backends/providers` | Every registered provider: the roles it serves, whether it can probe itself, and its config schema per role — what the forms are built from. |
| `GET /admin/api/backends` · `PUT/DELETE /admin/api/backends/{role}` | Role assignment and settings as database overrides over the config file, per-leaf source. PUT stores only real deviations (a value equal to the config file or the provider default is removed instead); DELETE resets the role to the file. |
| `POST /admin/api/backends/{role}/validate` · `/test` | Dry-run validation (per-field failures) and, where the provider implements `IReadinessSource`, a credential-less reachability probe. |
| `GET /user/api/backends/meta` | Per role: the provider serving this caller and the descriptions of the fields **that caller may set for themselves** (`SelfServiceEditable`), so the portal renders named fields. Descriptions only — no configured values. |
| `GET/PUT/DELETE /admin/api/users[/{login}]` | Declared users with provenance; PUT = full-replacement upsert with password sentinels (null = keep, `""` = clear, value = hash/seal) plus the `enabled` flag, validated like the CLI. |
| `POST /admin/api/users/{login}/disable·enable` | Flip the account master switch without a full-replacement PUT (parallel to devices block/unblock); a disabled account refuses every login (403). |
| `GET/POST/DELETE /admin/api/shares` | Shared-calendar grants (`eas share`). GET is paged: `{ total, entries }`, `limit` (default 200, max 500) and `offset`. |
| `GET /admin/api/devices` · `POST .../block·unblock·wipe·purge` | Device management; wipe/purge require the target echoed back in `confirm`. GET is paged: `{ total, entries }`, `limit` (default 200, max 500), `offset` and an optional `user` filter. |
| `GET /admin/api/logs` | History (time window, newest first) or tail (`?after=<id>`, chronological — poll every ~2 s); filters: `level` (minimum), `user`, `machine`, `source`, `text`. |
| `GET /admin/api/state` · `summary` | Live sessions/watchers/long-polls and DB-derived counts (readiness comes from the public `/readyz`). |
| `GET /user/api/me` · `PUT /user/api/password` · `PUT /user/api/backends/{role}` | Self-service: own account view, password change (re-verifies the current password), own role credentials and the settings the provider marks self-service. Provider, Enabled and every connection-shaped setting are admin-only — a PUT carrying one is refused 400 and settings an admin set are preserved. |

## The no-build SPA

`src/ActiveSync.WebUi/wwwroot/` is plain HTML/CSS/native ES modules — no bundler, no npm.
The files are embedded in the assembly; set the **`EAS_WEBUI_ASSETS`** environment variable
to a `wwwroot` directory on disk to serve live files instead (edit → refresh, the design
iteration loop).

- `shared/theme.css` is the single restyling surface: semantic CSS custom properties, dark
  default, light via `prefers-color-scheme` or the pinned `[data-theme]` toggle.
  `shared/app.css` consumes only those variables.
- The **default-as-placeholder** convention everywhere: an unset field renders empty with
  its default (or inherited value) as a dimmed placeholder / `(default: X)` option plus a
  badge; typing replaces it, clearing reverts to the default. Its corollary on save: a
  field equal to what it would inherit is **not stored**, and an existing override for it
  is removed — so typing the default is how you reset.
- `shared/schema-form.js` builds the backend forms from a provider's own
  `DescribeConfiguration` schema (Backends page, admin user editor, user portal). No view
  knows a provider's field names, so a plugin's settings render the day it ships. Settings
  a provider does not describe stay editable — as an "Advanced" section in the admin views,
  and carried through invisibly by the portal, since those PUTs replace settings wholesale.
- No inline scripts (`onclick=`, `<script>` blocks) — the CSP forbids them; keep it that
  way. Inline `style=` attributes are fine (the views lay themselves out with them).

## Token-auth forward compatibility

When XOAUTH2/Bearer backend auth lands, its per-role knobs go into the existing role
settings (`Backends:<Role>:Settings:...`): reserved keys `Auth:Mode`, `Auth:TokenEndpoint`,
`Auth:ClientId`, `Auth:ClientSecret`, `Auth:RefreshToken`. A provider that adopts them
should declare them in its `DescribeConfiguration` (with `BackendFieldType.Secret` for the
two credentials) so both UIs render them properly; undeclared, they still work as raw keys.
A browser-based consent flow would mount under the portal at `/user/oauth/{role}/connect`
+ `/user/oauth/callback`.

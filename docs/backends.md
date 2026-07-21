# Backend capability matrix

Each role is filled by a **provider** (chosen in [Configuration](configuration.md)). The
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

## Mail — `imap`+`smtp` vs `jmap`

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

## Contacts — `carddav` vs `jmap` vs `local`

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

## Calendar — `caldav` vs `jmap` vs `local`

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

## Tasks — `caldav` (VTODO) vs `local`

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

## Notes — `local` only

Notes have no standard mail/DAV/JMAP representation, so they are **always** served by the
`local` store: CRUD, body, categories and last-modified, encrypted at rest, one fixed
folder, visible only to the user's own ActiveSync devices.

## Out-of-office — `sieve` vs `jmap`

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

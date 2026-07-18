#!/usr/bin/env bash
# Self-contained provisioning entrypoint for the Stalwart 0.16 integration-test backend.
#
# Stalwart 0.16 dropped the mounted-TOML + REST provisioning the 0.13 stack used. All
# configuration now lives in the data store and is written through the server's own JMAP
# management API (the "urn:stalwart:jmap" x:* methods). Initial setup happens in a bootstrap
# mode that serves only that API, and store/listener changes take effect only after a restart.
# This wrapper drives that whole dance in-process using the curl + bash that already ship in
# the image, so a plain `docker compose up` yields a ready server — test users, plaintext
# listeners on the same ports the 0.13 stack used (IMAP 143, submission 587), and a relaxed
# password policy so the trivial test password keeps working — with no manual steps.
#
# The `.provisioned` marker makes it idempotent: on a persisted volume the second boot skips
# straight to serving. Auth uses the recovery admin pinned by STALWART_RECOVERY_ADMIN.
#
# TEST USE ONLY: plaintext auth, no strength policy, no IP banning. Never use in production.
set -uo pipefail

CONFIG="${STALWART_CONFIG:-/etc/stalwart/config.json}"
MARKER="${STALWART_MARKER:-/etc/stalwart/.provisioned}"
BASE="http://localhost:8080"
API="$BASE/jmap"
ADMIN="admin:secret"                       # must match STALWART_RECOVERY_ADMIN
DOMAIN="${TEST_DOMAIN:-example.com}"
USERS="${TEST_USERS:-user1 user2}"
PASSWORD="${TEST_PASSWORD:-pass}"          # trivial on purpose; policy is relaxed below
CAP='["urn:ietf:params:jmap:core","urn:stalwart:jmap"]'

SW_PID=""
log(){ echo "[provision] $*" >&2; }

run_server(){ stalwart --config "$CONFIG" & SW_PID=$!; }
kill_server(){ [ -n "$SW_PID" ] && kill "$SW_PID" 2>/dev/null; wait "$SW_PID" 2>/dev/null; SW_PID=""; }
restart_server(){ kill_server; run_server; wait_http; }

wait_http(){
  local i=0
  until curl -fsS -o /dev/null "$BASE/.well-known/jmap" 2>/dev/null; do
    i=$((i+1)); [ "$i" -gt 60 ] && { log "timed out waiting for HTTP"; return 1; }
    sleep 1
  done
}

# POST a JMAP request as the recovery admin (Basic auth); echo the response body.
jmap(){ curl -fsS "$API" -u "$ADMIN" -H 'Content-Type: application/json' --data-binary "$1"; }

# Last "id":"..." value in a JMAP response (each returned object carries exactly one).
last_id(){ grep -o '"id":"[^"]*"' | tail -1 | sed 's/.*:"\([^"]*\)".*/\1/'; }

set_singleton(){ # $1 = object type, $2 = JSON body
  jmap "{\"using\":$CAP,\"methodCalls\":[[\"$1/set\",{\"update\":{\"singleton\":$2}},\"0\"]]}" >/dev/null
}

bootstrap(){
  log "bootstrapping store + directory + domain ($DOMAIN)"
  jmap "{\"using\":$CAP,\"methodCalls\":[[\"x:Bootstrap/set\",{\"update\":{\"singleton\":{\
\"blobStore\":{\"@type\":\"Default\"},\
\"dataStore\":{\"@type\":\"RocksDb\",\"path\":\"/var/lib/stalwart/data\",\"blobSize\":16834,\"bufferSize\":134217728,\"poolWorkers\":null},\
\"directory\":{\"@type\":\"Internal\"},\
\"dnsServer\":{\"@type\":\"Manual\"},\
\"generateDkimKeys\":false,\
\"inMemoryStore\":{\"@type\":\"Default\"},\
\"requestTlsCertificate\":false,\
\"searchStore\":{\"@type\":\"Default\"},\
\"tracer\":{\"@type\":\"Stdout\",\"ansi\":false,\"enable\":true,\"eventsPolicy\":\"exclude\",\"level\":\"info\",\"events\":{}},\
\"serverHostname\":\"mail.$DOMAIN\",\
\"defaultDomain\":\"$DOMAIN\"}}},\"0\"]]}" >/dev/null
}

configure(){
  log "applying test settings (plaintext listeners, relaxed auth)"
  # Accept the trivial test password: no strength/length policy.
  set_singleton x:Authentication '{"passwordMinStrength":"zero","passwordMinLength":1}'
  # Allow AUTH over plaintext IMAP, never lock a test client out on auth failures, and lift
  # the concurrency/rate ceilings well above what the parallel suite drives (default 16
  # concurrent sessions is exhausted by the persistent IDLE watchers + per-op connections).
  set_singleton x:Imap '{"allowPlainTextAuth":true,"maxAuthFailures":100000,"maxConcurrent":100000,"maxRequestRate":{"count":1000000,"period":60000}}'
  # Offer PLAIN/LOGIN on the plaintext submission port (the default rule only does so over
  # TLS). match/0 is evaluated first, so it wins for any submission port (local_port != 25).
  set_singleton x:MtaStageAuth '{"saslMechanisms/match/0":{"if":"local_port != 25","then":"[plain, login]"}}'
  # Never IP-ban a test client for repeated connects/auth churn.
  set_singleton x:Security '{"authBanRate":{"count":1000000,"period":86400000},"scanBanRate":{"count":1000000,"period":86400000},"abuseBanRate":{"count":1000000,"period":86400000},"loiterBanRate":{"count":1000000,"period":86400000}}'
  # Plaintext IMAP (143) + submission (587) listeners, matching the retired 0.13 stack.
  jmap "{\"using\":$CAP,\"methodCalls\":[[\"x:NetworkListener/set\",{\"create\":{\
\"imap\":{\"name\":\"imap-plain\",\"bind\":{\"[::]:143\":true},\"protocol\":\"imap\",\"useTls\":false},\
\"smtp\":{\"name\":\"submission-plain\",\"bind\":{\"[::]:587\":true},\"protocol\":\"smtp\",\"useTls\":false}}},\"0\"]]}" >/dev/null
  # Disable the built-in inbound SMTP throttles that repeated test runs would exhaust.
  local ids id updates
  ids="$(jmap "{\"using\":$CAP,\"methodCalls\":[[\"x:MtaInboundThrottle/get\",{\"ids\":null},\"0\"]]}" | grep -o '"id":"[^"]*"' | sed 's/.*:"\([^"]*\)".*/\1/')"
  updates=""
  for id in $ids; do updates="$updates${updates:+,}\"$id\":{\"enable\":false}"; done
  [ -n "$updates" ] && jmap "{\"using\":$CAP,\"methodCalls\":[[\"x:MtaInboundThrottle/set\",{\"update\":{$updates}},\"0\"]]}" >/dev/null
}

ensure_user(){ # $1 = local part, $2 = domain id
  local name="$1" did="$2" id
  id="$(jmap "{\"using\":$CAP,\"methodCalls\":[[\"x:Account/set\",{\"create\":{\"u\":{\"@type\":\"User\",\"name\":\"$name\",\"domainId\":\"$did\"}}},\"0\"]]}" | last_id)"
  if [ -z "$id" ]; then
    id="$(jmap "{\"using\":$CAP,\"methodCalls\":[[\"x:Account/query\",{\"filter\":{\"text\":\"$name@$DOMAIN\"}},\"0\"]]}" | grep -o '"ids":\[[^]]*\]' | grep -o '"[^"]*"' | tail -1 | tr -d '"')"
  fi
  [ -z "$id" ] && { log "could not create or find $name"; return 1; }
  jmap "{\"using\":$CAP,\"methodCalls\":[[\"x:Account/set\",{\"update\":{\"$id\":{\"credentials/0\":{\"@type\":\"Password\",\"secret\":\"$PASSWORD\"}}}},\"0\"]]}" >/dev/null
  log "user ready: $name@$DOMAIN"
}

provision_users(){
  local did; did="$(jmap "{\"using\":$CAP,\"methodCalls\":[[\"x:Domain/get\",{\"ids\":null},\"0\"]]}" | last_id)"
  [ -z "$did" ] && { log "no domain found"; return 1; }
  for u in $USERS; do ensure_user "$u" "$did"; done
}

# --- boot sequence -----------------------------------------------------------
if [ ! -f "$MARKER" ]; then
  log "first run: provisioning the test backend"
  run_server; wait_http || { kill_server; exit 1; }
  [ -f "$CONFIG" ] || { bootstrap; restart_server; }   # reach normal mode
  configure
  restart_server                                        # bind new listeners + apply policy
  provision_users && touch "$MARKER"
  log "provisioning complete"
else
  run_server; wait_http || { kill_server; exit 1; }
  provision_users || log "user re-assertion reported an error (server is up)"
fi

trap 'kill_server; exit 0' TERM INT
wait "$SW_PID"

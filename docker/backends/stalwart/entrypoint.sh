#!/usr/bin/env bash
# Self-contained provisioning entrypoint for the Stalwart 0.16 integration-test backend.
#
# Provisioning is DECLARATIVE via stalwart-cli (baked into this test image, see Dockerfile).
# The CLI drives the server's schema-driven urn:stalwart:jmap management API, so the payloads
# adapt to the server's live schema on upgrade instead of being hand-rolled JSON. The result
# is the same as the retired 0.13 stack: plaintext IMAP 143 + submission 587, ManageSieve 4190,
# DAV+JMAP on 8080, users user1/user2@example.com password "pass".
#
# Flow (two restarts, because 0.16 applies store/listener changes only on restart):
#   run (bootstrap mode) -> `update Bootstrap` -> restart (normal mode)
#     -> `apply` settings + plaintext listeners -> restart (binds listeners, relaxes policy)
#     -> `apply` users -> serve.
# The `.provisioned` marker makes it idempotent: a second boot skips straight to serving.
# Auth uses the recovery admin pinned by STALWART_RECOVERY_ADMIN=admin:secret.
#
# TEST USE ONLY: plaintext auth, no password policy, no IP banning. Never use in production.
set -uo pipefail

CONFIG="${STALWART_CONFIG:-/etc/stalwart/config.json}"
MARKER="${STALWART_MARKER:-/etc/stalwart/.provisioned}"
PROV="${PROV_DIR:-/provision}"
DOMAIN="${TEST_DOMAIN:-example.com}"
# stalwart-cli reads these from the environment (recovery admin pinned by STALWART_RECOVERY_ADMIN).
export STALWART_URL="http://localhost:8080"
export STALWART_USER="${STALWART_ADMIN_USER:-admin}"
export STALWART_PASSWORD="${STALWART_ADMIN_SECRET:-secret}"

SW_PID=""
log(){ echo "[provision] $*" >&2; }
# HOME=/tmp: the server runs as an unprivileged user (uid 2000) whose home is not writable;
# stalwart-cli caches the downloaded schema under $HOME, so point it at a writable dir.
cli(){ HOME=/tmp stalwart-cli "$@"; }

run_server(){ stalwart --config "$CONFIG" & SW_PID=$!; }
kill_server(){ [ -n "$SW_PID" ] && kill "$SW_PID" 2>/dev/null; wait "$SW_PID" 2>/dev/null; SW_PID=""; }
restart_server(){ kill_server; run_server; wait_http; }

wait_http(){
  local i=0
  until curl -fsS -o /dev/null "$STALWART_URL/.well-known/jmap" 2>/dev/null; do
    i=$((i+1)); [ "$i" -gt 60 ] && { log "timed out waiting for HTTP"; return 1; }
    sleep 1
  done
}

bootstrap(){
  log "bootstrapping store + directory + domain ($DOMAIN)"
  cli update Bootstrap --stdin < "$PROV/bootstrap.json" >/dev/null
}

apply_settings(){
  log "applying test settings + plaintext listeners"
  cli apply --file "$PROV/provision-settings.ndjson" --quiet || return 1
  disable_inbound_throttles
}

# The built-in inbound SMTP throttles (sender-IP 5/1s, sender->rcpt 25/period) are server-created
# list objects; repeated local test runs would exhaust them. Disable each by id.
disable_inbound_throttles(){
  local id
  for id in $(cli query MtaInboundThrottle --fields id --json 2>/dev/null | grep -o '"id":"[^"]*"' | sed 's/.*:"\([^"]*\)".*/\1/'); do
    cli update MtaInboundThrottle "$id" --json '{"enable":false}' >/dev/null 2>&1 || true
  done
}

apply_users(){
  local did
  did="$(cli query Domain --where "name=$DOMAIN" --fields id --json 2>/dev/null | grep -o '"id":"[^"]*"' | head -1 | sed 's/.*:"\([^"]*\)".*/\1/')"
  [ -z "$did" ] && { log "domain $DOMAIN not found"; return 1; }
  log "applying users (domain id=$did)"
  sed "s/__DOMAIN_ID__/$did/g" "$PROV/provision-users.ndjson" | cli apply --stdin --quiet
}

provision(){
  [ -f "$CONFIG" ] || { bootstrap || return 1; restart_server || return 1; }  # reach normal mode
  apply_settings || return 1
  restart_server || return 1                            # bind new listeners + apply policy
  apply_users || return 1
}

# --- boot sequence -----------------------------------------------------------
if [ ! -f "$MARKER" ]; then
  log "first run: provisioning the test backend"
  run_server; wait_http || { kill_server; exit 1; }
  if provision; then
    touch "$MARKER"; log "provisioning complete"
  else
    log "PROVISIONING FAILED — see errors above; leaving the server up for inspection"
  fi
else
  run_server; wait_http || { kill_server; exit 1; }
  apply_users || log "user re-assertion reported an error (server is up)"
fi

trap 'kill_server; exit 0' TERM INT
wait "$SW_PID"

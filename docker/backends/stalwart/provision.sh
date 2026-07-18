#!/bin/sh
# Provisions the test domain and users via the Stalwart management API.
# Idempotent: re-running against an already-provisioned server is harmless.
set -e

HOST="${STALWART_URL:-http://stalwart:8080}"
AUTH="admin:secret"

echo "Waiting for Stalwart management API at $HOST ..."
i=0
until curl -fsS -u "$AUTH" "$HOST/api/principal" >/dev/null 2>&1; do
  i=$((i + 1))
  if [ "$i" -gt 60 ]; then
    echo "Stalwart API did not come up in time" >&2
    exit 1
  fi
  sleep 2
done

create() {
  # POST /api/principal returns an error body (but HTTP 200) when the principal
  # already exists — treat any 2xx as success and verify afterwards.
  curl -fsS -u "$AUTH" -X POST "$HOST/api/principal" \
    -H 'Content-Type: application/json' \
    -d "$1" >/dev/null || true
}

echo "Creating domain example.com"
create '{"type":"domain","name":"example.com"}'

echo "Creating user1@example.com"
create '{"type":"individual","name":"user1@example.com","secrets":["pass"],"emails":["user1@example.com"],"roles":["user"]}'

echo "Creating user2@example.com"
create '{"type":"individual","name":"user2@example.com","secrets":["pass"],"emails":["user2@example.com"],"roles":["user"]}'

echo "Verifying users exist"
curl -fsS -u "$AUTH" "$HOST/api/principal/user1@example.com" >/dev/null
curl -fsS -u "$AUTH" "$HOST/api/principal/user2@example.com" >/dev/null

# Stalwart ships built-in inbound SMTP rate limits (sender: 25 msgs/1h, ip: 5/1s) that
# repeated test runs exhaust, turning deliveries into "4.4.5 Rate limit exceeded".
# These settings live in the internal store (config.toml entries are ignored for
# queue.* keys), so they must be written via the management API + reload.
echo "Disabling built-in SMTP rate limits"
curl -fsS -u "$AUTH" -X POST "$HOST/api/settings" \
  -H 'Content-Type: application/json' \
  -d '[{"type":"insert","prefix":"queue.limiter.inbound.sender","values":[["enable","false"],["rate","1000000/1s"]],"assert_empty":false},{"type":"insert","prefix":"queue.limiter.inbound.ip","values":[["enable","false"],["rate","1000000/1s"]],"assert_empty":false}]' \
  >/dev/null
curl -fsS -u "$AUTH" "$HOST/api/reload" >/dev/null
echo "Provisioning complete"

#!/usr/bin/env bash
# Bring up the Stalwart test backend on the CANONICAL ports, so a plain `dotnet test` just works.
#
# scripts/test-fast runs stalwart AND axigen on dedicated ports (10143.., 20143..) so the two
# coexist -- but that means every run needs AS_TEST_* overrides, and it rebuilds + recreates both
# containers each time. For iterating on one change against one backend, that is a lot of docker
# work for nothing.
#
# This brings up stalwart alone on the canonical ports (IMAP 143, SMTP 587, ManageSieve 4190,
# DAV+JMAP 5232) -- which are exactly TestBackend's built-in defaults. No environment variables,
# no wrapper script for the test run:
#
#     dotnet test tests/ActiveSync.Integration.Tests --filter Category=Integration
#
# The image is NOT rebuilt unless -b is passed, so a warm container is reused in seconds.
#
# NOTE: stalwart is a single compose project. Running this switches the SAME container to the
# canonical ports, so scripts/test-fast will recreate it back onto 10143 next time it runs. Pick
# one workflow per session rather than alternating.
#
# Usage:
#   scripts/stalwart-up.sh [-b] [-d]
#     -b  rebuild the image first (only needed after editing docker/backends/stalwart/)
#     -d  tear the stack down (-v) and exit
set -u

BUILD=0
DOWN=0
while getopts "bdh" opt; do
	case "$opt" in
		b) BUILD=1 ;;
		d) DOWN=1 ;;
		h) grep '^#' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
		*) echo "unknown option" >&2; exit 2 ;;
	esac
done

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"
COMPOSE="docker/backends/stalwart/docker-compose.yml"

# The compose file falls back to the canonical ports only when these are unset. A shell that has
# run test-fast in the same session still has them exported, which would silently put the stack
# back on 10143 -- clear them so this script's whole point cannot be defeated by leftover state.
unset STALWART_IMAP_PORT STALWART_SMTP_PORT STALWART_SIEVE_PORT STALWART_HTTP_PORT

if [ "$DOWN" = 1 ]; then
	echo "==> Tearing down stalwart"
	docker compose -f "$COMPOSE" down -v
	exit $?
fi

echo "==> Bringing up stalwart on canonical ports (143 / 587 / 4190 / 5232)"
if [ "$BUILD" = 1 ]; then
	docker compose -f "$COMPOSE" up -d --build --wait --wait-timeout 300
else
	# No --build: a warm container is reused, and a cold one starts from the existing image.
	docker compose -f "$COMPOSE" up -d --wait --wait-timeout 300
fi
if [ $? -ne 0 ]; then
	echo "!! stalwart failed to become healthy" >&2
	echo "   If the image is missing or stale, re-run with -b." >&2
	exit 1
fi

# The compose healthcheck already gates on /etc/stalwart/.provisioned plus a JMAP probe, so a
# healthy container is a provisioned one. Confirm the published ports anyway -- a port collision
# surfaces here as a clear message rather than as 124 mysteriously skipped tests.
BAD=0
for port in 143 587 4190 5232; do
	if timeout 3 bash -c "echo > /dev/tcp/localhost/$port" 2>/dev/null; then
		printf '    port %-5s OK\n' "$port"
	else
		BAD=1
		printf '    port %-5s NOT REACHABLE\n' "$port"
	fi
done
if [ "$BAD" = 1 ]; then
	echo "!! Not all canonical ports are published — something else may be bound to them." >&2
	echo "   Integration tests would SKIP and still report green. Fix this before testing." >&2
	exit 1
fi

echo ""
echo "Ready. Run the integration suite with no environment setup:"
echo "    dotnet test tests/ActiveSync.Integration.Tests --filter Category=Integration"
echo ""
echo "Expect ~124 tests to run. If everything SKIPS, the backend is not reachable —"
echo "a skipped suite still exits 0, so treat \"0 passed\" as a failure, not a pass."
exit 0

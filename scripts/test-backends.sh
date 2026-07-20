#!/usr/bin/env bash
# Run the integration suite against every self-hosted backend stack, one after another.
#
# Mirrors the CI matrix on the developer box. Both compose stacks publish the SAME host ports
# (143/587/5232, plus 4190 on Stalwart) with the same users, so the only knob that changes
# between them is AS_TEST_STACK (which flips the CalDAV home-set style). Because the ports
# collide, the stacks run sequentially: up --wait -> dotnet test -> down -v.
#
# Backends without a capability (docker-mailserver has no JMAP/Sieve; Radicale has no
# free-busy-query) skip the relevant tests cleanly rather than failing.
#
# Usage:
#   scripts/test-backends.sh [-b stalwart,mailserver] [-p] [-f "Category=Integration"]
#     -b  comma-separated backends, in order (default: stalwart,mailserver)
#     -p  also stand up a throwaway postgres:17-alpine and set AS_TEST_PG (CI parity)
#     -f  dotnet test --filter expression (default: Category=Integration)
set -u

BACKENDS="stalwart,mailserver"
FILTER="Category=Integration"
USE_PG=0
PG_CONTAINER="as-local-pg"

while getopts "b:f:ph" opt; do
	case "$opt" in
		b) BACKENDS="$OPTARG" ;;
		f) FILTER="$OPTARG" ;;
		p) USE_PG=1 ;;
		h) grep '^#' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
		*) echo "unknown option" >&2; exit 2 ;;
	esac
done

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

declare -A RESULTS
FAILED=0

start_postgres() {
	echo "==> Starting throwaway Postgres ($PG_CONTAINER)"
	docker rm -f "$PG_CONTAINER" >/dev/null 2>&1 || true
	docker run -d --name "$PG_CONTAINER" -p 5432:5432 \
		-e POSTGRES_USER=activesync -e POSTGRES_PASSWORD=ci-pw -e POSTGRES_DB=activesync \
		postgres:17-alpine -c max_connections=300 >/dev/null
	for _ in $(seq 1 30); do
		if docker exec "$PG_CONTAINER" pg_isready -U activesync -d activesync -q 2>/dev/null; then
			return 0
		fi
		sleep 2
	done
	echo "Postgres did not become ready" >&2
	return 1
}

cleanup_postgres() {
	docker rm -f "$PG_CONTAINER" >/dev/null 2>&1 || true
	unset AS_TEST_PG
}

if [ "$USE_PG" = 1 ]; then
	start_postgres || exit 1
	export AS_TEST_PG="postgresql://activesync:ci-pw@localhost:5432/activesync"
fi
trap '[ "$USE_PG" = 1 ] && cleanup_postgres' EXIT

IFS=',' read -ra LIST <<< "$BACKENDS"
for backend in "${LIST[@]}"; do
	file="docker/backends/$backend/docker-compose.yml"
	if [ ! -f "$file" ]; then
		echo "!! No compose file for '$backend' at $file" >&2
		RESULTS[$backend]="no compose file"
		FAILED=1
		continue
	fi

	echo ""
	echo "==================== $backend ===================="
	echo "==> docker compose up --wait"
	if ! docker compose -f "$file" up -d --build --wait --wait-timeout 300; then
		RESULTS[$backend]="stack failed to start"
		FAILED=1
		docker compose -f "$file" down -v >/dev/null 2>&1 || true
		continue
	fi

	echo "==> dotnet test (AS_TEST_STACK=$backend, filter=$FILTER)"
	# Per-backend AS_TEST_* beyond AS_TEST_STACK (mirrors the CI matrix).
	extra_env=()
	case "$backend" in
		# cyrus is TEMPORARILY DISABLED (failing under investigation); it is not in the default
		# backend list, so it only runs if explicitly named. Config kept for easy re-enable.
		cyrus)
			extra_env=(
				'AS_TEST_DAV_HOMESET=/dav/calendars/user/{user}/'
				'AS_TEST_DAV_CONTACTS_HOMESET=/dav/addressbooks/user/{user}/'
				'AS_TEST_MAILSUBMIT=jmap'
				'AS_TEST_SIEVE_TLS=false'
			) ;;
		baikal)
			extra_env=(
				'AS_TEST_DAV_HOMESET=/dav.php/calendars/{user}/'
				'AS_TEST_DAV_CONTACTS_HOMESET=/dav.php/addressbooks/{user}/'
			) ;;
		axigen)
			extra_env=(
				'AS_TEST_DAV_HOMESET=/Calendar/'
				'AS_TEST_DAV_CONTACTS_HOMESET=/Contacts/'
			) ;;
		james)
			extra_env=(
				'AS_TEST_DAV_URL=none'
			) ;;
	esac
	env AS_TEST_STACK="$backend" "${extra_env[@]}" dotnet test ActiveSync.slnx --nologo --filter "$FILTER"
	code=$?
	if [ "$code" = 0 ]; then
		RESULTS[$backend]="PASS"
	else
		RESULTS[$backend]="FAIL (exit $code)"
		FAILED=1
	fi

	echo "==> docker compose down -v"
	docker compose -f "$file" down -v >/dev/null 2>&1 || true
done

echo ""
echo "==================== summary ===================="
for backend in "${LIST[@]}"; do
	printf '%-14s %s\n' "$backend" "${RESULTS[$backend]:-skipped}"
done

exit "$FAILED"

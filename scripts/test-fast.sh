#!/usr/bin/env bash
# Fast local integration check: run the suite against stalwart AND axigen in PARALLEL, with both
# stacks left running for the next change (start only if not already healthy; reuse when warm).
#
# The two most valuable backends -- stalwart (full IMAP/SMTP/DAV/JMAP/Sieve) and axigen (fast,
# full IMAP/SMTP/DAV) -- are the normal per-change check. They run on DEDICATED host ports
# (stalwart 10143/10587/10190/10232, axigen 20143/20587/20232) so they coexist and leave the
# canonical set (143/587/5232/4190) free for on-demand backends via scripts/test-backends. The
# port overrides are passed through the compose files' ${STALWART_*}/${AXIGEN_*} vars.
#
# Usage:
#   scripts/test-fast.sh [-f "Category=Integration"] [-d]
#     -f  dotnet test --filter expression (default: Category=Integration)
#     -d  tear both stacks down (-v) at the end (default: leave running)
set -u
export MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*'   # git-bash: don't mangle /Calendar/ etc.

FILTER="Category=Integration"
DOWN=0
while getopts "f:dh" opt; do
	case "$opt" in
		f) FILTER="$OPTARG" ;;
		d) DOWN=1 ;;
		h) grep '^#' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
		*) echo "unknown option" >&2; exit 2 ;;
	esac
done

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"
PROJ="tests/ActiveSync.Integration.Tests/ActiveSync.Integration.Tests.csproj"

# Dedicated host ports so both stacks coexist and the canonical set stays free.
export STALWART_IMAP_PORT=10143 STALWART_SMTP_PORT=10587 STALWART_SIEVE_PORT=10190 STALWART_HTTP_PORT=10232
export AXIGEN_IMAP_PORT=20143 AXIGEN_SMTP_PORT=20587 AXIGEN_HTTP_PORT=20232

up() {
	echo "==> up $1 (reused if already healthy)"
	if ! docker compose -f "docker/backends/$1/docker-compose.yml" up -d --build --wait --wait-timeout 300; then
		echo "!! $1 failed to start" >&2
		exit 1
	fi
}
up stalwart
up axigen

echo "==> Building integration test project once"
dotnet build "$PROJ" -c Release --nologo -v q || exit 1

# Pre-flight: every integration test is a [BackendFact], which xunit turns into a SKIP when
# TestBackend's IMAP probe fails -- and a run of nothing but skips still exits 0. That makes an
# unreachable backend indistinguishable from a green run, which is exactly how an unverified fix
# gets signed off. Probe the same host:port TestBackend will, and refuse to run if it is dead.
probe() {
	local name="$1" port="$2"
	if ! timeout 3 bash -c "echo > /dev/tcp/localhost/$port" 2>/dev/null; then
		echo "!! $name: no IMAP backend reachable at localhost:$port" >&2
		echo "   Every integration test would SKIP and the run would still report green." >&2
		echo "   Refusing to run -- a skipped suite is not verification." >&2
		exit 1
	fi
}
probe stalwart "$STALWART_IMAP_PORT"
probe axigen "$AXIGEN_IMAP_PORT"

LOGDIR="$(mktemp -d)"
run_leg() {
	local name="$1"; shift
	env "$@" dotnet test "$PROJ" -c Release --no-build --nologo --filter "$FILTER" > "$LOGDIR/$name.log" 2>&1
	echo $? > "$LOGDIR/$name.rc"
}

echo "==> Running stalwart + axigen suites in parallel (filter: $FILTER)"
run_leg stalwart \
	AS_TEST_STACK=stalwart \
	AS_TEST_IMAP_PORT="$STALWART_IMAP_PORT" AS_TEST_SMTP_PORT="$STALWART_SMTP_PORT" \
	AS_TEST_SIEVE_PORT="$STALWART_SIEVE_PORT" AS_TEST_DAV_URL="http://localhost:$STALWART_HTTP_PORT" &
run_leg axigen \
	AS_TEST_STACK=axigen \
	AS_TEST_IMAP_PORT="$AXIGEN_IMAP_PORT" AS_TEST_SMTP_PORT="$AXIGEN_SMTP_PORT" \
	AS_TEST_DAV_URL="http://localhost:$AXIGEN_HTTP_PORT" \
	AS_TEST_DAV_HOMESET=/Calendar/ AS_TEST_DAV_CONTACTS_HOMESET=/Contacts/ &
wait

FAILED=0
echo ""
echo "==================== summary ===================="
for name in stalwart axigen; do
	rc=$(cat "$LOGDIR/$name.rc" 2>/dev/null || echo 1)
	result=$(grep -hE 'Passed!|Failed!' "$LOGDIR/$name.log" | tail -1)

	# A suite that ran nothing, or skipped everything, exits 0 -- treat it as a failure rather
	# than let "PASS" stand for "verified". Pairs with the pre-flight probe above: that catches a
	# dead backend, this catches a filter or discovery problem that silently matched no tests.
	passed=$(sed -n 's/.*Passed:[[:space:]]*\([0-9]\+\).*/\1/p' <<<"$result")
	total=$(sed -n 's/.*Total:[[:space:]]*\([0-9]\+\).*/\1/p' <<<"$result")
	if [ "$rc" = 0 ] && { [ "${total:-0}" = 0 ] || [ "${passed:-0}" = 0 ]; }; then
		FAILED=1
		printf '%-10s FAIL  no tests actually ran (total=%s, passed=%s) -- filter %s matched nothing, or every test skipped\n' \
			"$name" "${total:-?}" "${passed:-?}" "'$FILTER'"
		continue
	fi

	if [ "$rc" = 0 ]; then
		printf '%-10s PASS  %s\n' "$name" "$result"
	else
		FAILED=1
		printf '%-10s FAIL  %s\n' "$name" "$result"
		grep -E '  Failed ' "$LOGDIR/$name.log" | sed 's/ \[.*//; s/^/   /' | sort -u | head -20
		echo "   (full log: $LOGDIR/$name.log)"
	fi
done

if [ "$DOWN" = 1 ]; then
	echo "==> Tearing down (-d)"
	docker compose -f docker/backends/stalwart/docker-compose.yml down -v >/dev/null 2>&1 || true
	docker compose -f docker/backends/axigen/docker-compose.yml down -v >/dev/null 2>&1 || true
else
	echo "==> Left stalwart + axigen running (re-run is fast; pass -d to tear down)"
fi

exit "$FAILED"

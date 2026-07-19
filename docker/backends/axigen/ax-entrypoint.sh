#!/bin/bash
# Self-provisioning entrypoint for a throwaway Axigen (trial/demo) test backend.
#
# A fresh Axigen demo starts with only the log/processing/cli/webadmin services and no
# domain -- it serves no IMAP/SMTP/DAV. This entrypoint provisions on first boot, then execs
# the server with the completed config:
#
#   1. boot Axigen once (defaults), wait for the CLI (port 7000);
#   2. drive provision.py -> domain example.com + users user1/user2 (pass), and open the
#      canonical listeners (IMAP 143, submission 587, HTTP/DAV 80);
#   3. SAVE CONFIG, then enable the imap/smtp/webmail services in the top-level `services`
#      list (there is no CLI verb for that list -- it is edited in the saved axigen.cfg);
#   4. stop that instance and exec the final foreground server, which now opens every service.
#
# A `.provisioned` marker makes re-entry idempotent; `docker compose down -v` wipes the
# volume so each run gets a fresh 3-day trial. Trial mode is evaluation-only -- test use only.
set -e

VAR=/axigen/var
ADMIN="${ADMIN_PASSWORD:-axigen-int-admin}"
SERVICES_DEFAULT='services = (log processing cli webadmin)'
SERVICES_FULL='services = (log processing cli webadmin imap smtpIncoming smtpOutgoing webmail)'

wait_cli() {
	for _ in $(seq 1 90); do
		(echo > /dev/tcp/127.0.0.1/7000) 2>/dev/null && return 0
		sleep 1
	done
	return 1
}

if [ ! -e "$VAR/.provisioned" ]; then
	echo "axigen-int: provisioning trial/demo instance"
	for d in domains log reporting serverData run queue; do
		[ -d "$VAR/$d" ] || mkdir -p "$VAR/$d"
	done
	chown -R axigen:axigen "$VAR" 2>/dev/null || true

	/axigen/bin/axigen -W "$VAR" -A "$ADMIN"
	/axigen/bin/axigen -W "$VAR" --foreground &
	AXPID=$!
	if ! wait_cli; then
		echo "axigen-int: CLI did not come up" >&2
		exit 1
	fi

	python3 /provision.py "$ADMIN"
	python3 - "$ADMIN" <<'PY'
import sys
import axigen.cli2 as cli2
cli2.CLI('localhost', 7000, 'admin', sys.argv[1]).cmdr('SAVE CONFIG')
PY
	sed -i "s/$SERVICES_DEFAULT/$SERVICES_FULL/" "$VAR/run/axigen.cfg"

	kill "$AXPID" 2>/dev/null || true
	wait "$AXPID" 2>/dev/null || true
	touch "$VAR/.provisioned"
	echo "axigen-int: provisioning complete"
fi

exec /axigen/bin/axigen -W "$VAR" --foreground

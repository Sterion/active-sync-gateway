#!/bin/bash
# Self-provisioning entrypoint for the Apache James (memory) test backend.
#
# The memory server keeps users in RAM, so they must be (re)created on every boot. James is
# started in the background; once its WebAdmin API (port 8000, no auth) answers, we create the
# domain + users, drop a marker, and hand the foreground back to the server. The compose
# healthcheck gates on the marker + the IMAP port, so `up --wait` blocks until provisioning is
# done -- no separate one-shot (which would trip `up --wait`, and could not depend on a
# single-service backend without a cycle).
set -e

MARKER=/root/.provisioned
rm -f "$MARKER"

# The stock image's ENTRYPOINT, run in the background so we can provision alongside it.
java -Dlogback.configurationFile=/root/conf/logback.xml \
	-Dworking.directory=/root/ \
	-Djdk.tls.ephemeralDHKeySize=2048 \
	-Dextra.props=/root/conf/jvm.properties \
	-cp /root/resources:/root/classes:/root/libs/* \
	org.apache.james.MemoryJamesServerMain &
JAMES_PID=$!

provision() {
	local admin="http://127.0.0.1:8000"
	# Wait for WebAdmin to answer.
	for _ in $(seq 1 90); do
		if curl -fsS -o /dev/null "$admin/domains" 2>/dev/null; then break; fi
		if ! kill -0 "$JAMES_PID" 2>/dev/null; then echo "james: server died during boot" >&2; exit 1; fi
		sleep 2
	done
	curl -fsS -XPUT "$admin/domains/example.com" || true
	for user in user1 user2; do
		curl -fsS -XPUT "$admin/users/${user}@example.com" \
			-H 'Content-Type: application/json' -d '{"password":"pass"}' || true
		# James creates only INBOX; the suite's MoveItems-to-Trash (and Sent/Drafts sync) need
		# the standard system mailboxes to exist. Create them via the WebAdmin mailbox API.
		for box in Trash Sent Drafts Outbox; do
			curl -fsS -XPUT "$admin/users/${user}@example.com/mailboxes/${box}" || true
		done
	done
	touch "$MARKER"
	echo "james: provisioning complete (domain example.com, users user1/user2, system mailboxes)"
}

provision &

wait "$JAMES_PID"

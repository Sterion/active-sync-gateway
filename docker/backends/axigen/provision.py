#!/usr/bin/env python3
"""
Provision a throwaway Axigen (trial/demo mode) for the integration suite.

The stock demo image opens only the LOG/CLI/WEBADMIN listeners -- no IMAP, SMTP
or WebMail (which serves CalDAV/CardDAV). This script drives the Axigen CLI
(port 7000) to:

  * create domain example.com with users user1/user2 (password "pass"),
  * open plaintext listeners on the canonical suite ports -- IMAP 143,
    submission SMTP 587, HTTP 80 (CalDAV/CardDAV) -- bound to 0.0.0.0,
  * lift the per-listener connection caps AND the per-account IMAP connection
    limit (default 16 -> "Too many connections for user"; the gateway keeps
    persistent IDLE watchers + per-device sync connections), so the suite's many
    connections are not throttled. The value must stay within Axigen's accepted
    range (1000 is rejected as invalid; 200 is ample headroom over the default).

Every context is committed explicitly (COMMIT), so the flow does not depend on
the provyaml auto-context heuristics. Idempotent: re-running skips objects that
already exist. Trial mode is evaluation-only -- test use exclusively.
"""
import sys
import axigen.cli2 as cli2

DOMAIN = "example.com"
USERS = ["user1", "user2"]
PASSWORD = "pass"
# IMAP 143, submission 587, HTTP(DAV) 80 -- the canonical ports every stack publishes.
LISTENERS = [("CONFIG IMAP", 143), ("CONFIG SMTP-INCOMING", 587), ("CONFIG WEBMAIL", 80)]


def main(admin_password: str) -> int:
	c = cli2.CLI("localhost", 7000, "admin", admin_password)

	def cmd(command: str, ok_substrings=()):
		resp = c.cmdr(command)
		first = resp.strip().splitlines()[0] if resp.strip() else ""
		ok = first.startswith("+OK") or "committing changes" in first
		if not ok and any(s in resp for s in ok_substrings):
			ok = True
		print(f"  [{command[:56]:<56}] {first}", flush=True)
		return ok, resp

	# --- domain + accounts (idempotent) --------------------------------------
	existing = c.cmdr("LIST Domains")
	if DOMAIN not in existing:
		ok, _ = cmd(f"CREATE Domain name {DOMAIN} "
		            f"domainLocation /axigen/var/domains/{DOMAIN} postmasterPassword pmPass123")
		if not ok:
			return fail("create domain")
		cmd("COMMIT")

	cmd(f"UPDATE Domain name {DOMAIN}")

	# The gateway keeps persistent per-user IMAP IDLE watchers plus per-device sync
	# connections, which blow past Axigen's default per-account cap (imapConnectionCount=16
	# -> "Too many connections for user"). Raise the domain default so accounts created below
	# inherit it. NB: these limits contexts exit with DONE, not COMMIT.
	cmd("CONFIG accountDefaultLimits")
	cmd("SET imapConnectionCount 200")
	cmd("SET pop3ConnectionCount 200")
	cmd("DONE")

	listed = c.cmdr("LIST Accounts")
	for user in USERS:
		if f"{user}@{DOMAIN}" in listed or f"\n{user}\n" in listed or f" {user} " in listed:
			print(f"  account {user} already exists", flush=True)
		else:
			ok, _ = cmd(f"ADD Account name {user} password {PASSWORD}")
			if not ok:
				return fail(f"add account {user}")
			cmd("COMMIT")  # account -> domain
		# Apply the raised cap to the account itself too (covers idempotent re-runs where the
		# account pre-existed the default change).
		cmd(f"UPDATE Account name {user}")
		cmd("CONFIG Limits")
		cmd("SET imapConnectionCount 200")
		cmd("SET pop3ConnectionCount 200")
		cmd("DONE")
		cmd("COMMIT")  # account -> domain
	cmd("COMMIT")      # domain -> root

	# --- service listeners on the canonical ports ----------------------------
	cmd("CONFIG SERVER")
	for service, port in LISTENERS:
		cmd(service)
		listeners = c.cmdr("LIST Listeners")
		if f"0.0.0.0:{port}" in listeners:
			print(f"  listener 0.0.0.0:{port} already present", flush=True)
			cmd("COMMIT")
			continue
		ok, _ = cmd(f"ADD Listener address 0.0.0.0:{port}")
		if not ok:
			return fail(f"add listener {port}")
		cmd("SET enable yes")
		cmd("SET maxConnections 1000")
		cmd("SET peerMaxConnections 1000")
		cmd("SET maxIntervalConnections 100000")
		cmd("SET peerMaxIntervalConnections 100000")
		cmd("COMMIT")  # listener -> service
		cmd("COMMIT")  # service  -> server
	cmd("COMMIT")      # server  -> root

	print("Axigen provisioning complete", flush=True)
	return 0


def fail(where: str) -> int:
	print(f"PROVISIONING FAILED at: {where}", file=sys.stderr, flush=True)
	return 1


if __name__ == "__main__":
	password = sys.argv[1] if len(sys.argv) > 1 else "admin"
	sys.exit(main(password))

#!/bin/sh
# Radicale ships no default collections, so the gateway's CalDAV/CardDAV discovery finds
# nothing and every DAV test silently no-ops (false green). Create one calendar + one address
# book per test user so the mailserver stack exercises DAV for real. Idempotent: re-creating
# an existing collection just returns 405, which we tolerate and verify past.
set -u
BASE="${RADICALE_URL:-http://radicale:5232}"
USERS="user1@example.com user2@example.com"
PASS="pass"

# Wait until Radicale answers an authenticated PROPFIND (htpasswd accounts loaded).
code=""
ready=0
for _ in $(seq 1 60); do
	code=$(curl -s -o /dev/null -w '%{http_code}' -u "user1@example.com:$PASS" \
		-X PROPFIND -H 'Depth: 0' "$BASE/user1@example.com/")
	if [ "$code" = "207" ]; then ready=1; break; fi
	sleep 2
done
if [ "$ready" != "1" ]; then
	echo "radicale not ready (last code=$code)" >&2
	exit 1
fi

mkcalendar() {
	curl -s -o /dev/null -w '%{http_code}' -u "$1:$PASS" -X MKCALENDAR \
		-H 'Content-Type: application/xml' \
		--data '<?xml version="1.0" encoding="utf-8"?><C:mkcalendar xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav"><D:set><D:prop><D:displayname>Calendar</D:displayname><C:supported-calendar-component-set><C:comp name="VEVENT"/><C:comp name="VTODO"/></C:supported-calendar-component-set></D:prop></D:set></C:mkcalendar>' \
		"$BASE/$1/calendar/"
}
mkaddressbook() {
	curl -s -o /dev/null -w '%{http_code}' -u "$1:$PASS" -X MKCOL \
		-H 'Content-Type: application/xml' \
		--data '<?xml version="1.0" encoding="utf-8"?><D:mkcol xmlns:D="DAV:" xmlns:CR="urn:ietf:params:xml:ns:carddav"><D:set><D:prop><D:resourcetype><D:collection/><CR:addressbook/></D:resourcetype><D:displayname>Contacts</D:displayname></D:prop></D:set></D:mkcol>' \
		"$BASE/$1/addressbook/"
}

for u in $USERS; do
	echo "provisioning $u: calendar=$(mkcalendar "$u") addressbook=$(mkaddressbook "$u")"
	home=$(curl -s -u "$u:$PASS" -X PROPFIND -H 'Depth: 1' "$BASE/$u/")
	case "$home" in
		*calendar*addressbook*|*addressbook*calendar*) : ;;
		*) echo "collections missing for $u" >&2; exit 1 ;;
	esac
done
echo "radicale provisioned"

# Self-provisioning Baikal (sabre/dav) for the CalDAV/CardDAV integration leg.
#
# Bakes a pre-seeded config + SQLite DB into the image so the container serves
# DAV on first boot with no web installer and no runtime provisioner. Because the
# base image declares /var/www/baikal/{config,Specific} as VOLUMEs, Docker
# initialises the fresh anonymous volumes from these baked layers at `up` time —
# so `docker compose down -v` between runs restores a clean, seeded state.
FROM ckulka/baikal:0.9.5-nginx

COPY baikal.yaml /var/www/baikal/config/baikal.yaml
COPY seed.php /tmp/seed.php

# Build the seeded database, then hand config + data to the runtime (nginx) user.
RUN php /tmp/seed.php \
	&& rm -f /tmp/seed.php \
	&& chown -R nginx:nginx /var/www/baikal/config /var/www/baikal/Specific \
	&& chmod -R u+rwX /var/www/baikal/Specific

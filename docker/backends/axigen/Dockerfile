# Self-provisioning Axigen (trial/demo mode) for the full-groupware integration leg.
#
# Axigen is proprietary; this image runs its built-in 3-day TRIAL/DEMO mode (no license key),
# which is licensed for EVALUATION ONLY -- running it in CI on every push is an accepted
# trade-off (see build.yaml). The clock resets on every fresh container (`down -v`), so no
# trial is ever extended.
#
# Pinned by digest for reproducibility (axigen/axigen:latest as of 2026-07, trial build).
FROM axigen/axigen@sha256:729e0e042bb05bf20704c108656703c4ef4c219c98f9f6b029573d5cc1593152

COPY provision.py /provision.py
COPY ax-entrypoint.sh /ax-entrypoint.sh
RUN chmod +x /ax-entrypoint.sh

ENTRYPOINT ["/ax-entrypoint.sh"]

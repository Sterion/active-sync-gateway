# Test-only Stalwart image: the pinned 0.16 server with the schema-driven stalwart-cli baked
# in. The entrypoint provisions the test backend declaratively through the CLI (against the
# server's own urn:stalwart:jmap management API), so the payloads adapt to the server's live
# schema on upgrade instead of being hand-rolled JSON. TEST USE ONLY — plaintext auth, no
# password policy, no IP banning; never use this image in production.
FROM ghcr.io/stalwartlabs/cli:1.0.10 AS cli

FROM stalwartlabs/stalwart:v0.16.13
# The CLI is a self-contained binary; drop it next to the server binary.
COPY --from=cli /usr/local/bin/stalwart-cli /usr/local/bin/stalwart-cli
COPY entrypoint.sh bootstrap.json provision-settings.ndjson provision-users.ndjson /provision/
ENTRYPOINT ["/usr/bin/bash", "/provision/entrypoint.sh"]

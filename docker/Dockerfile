# BUILDPLATFORM pin: the SDK stages always run natively on the build host and
# CROSS-publish for TARGETARCH (.NET does this without emulation) — only the small
# runtime-stage RUNs below go through QEMU on a foreign-arch build.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH=amd64
# Map docker arch -> .NET RID (amd64->linux-x64, arm64->linux-arm64).
RUN case "$TARGETARCH" in \
      amd64) echo linux-x64 > /rid ;; \
      arm64) echo linux-arm64 > /rid ;; \
      *) echo "unsupported TARGETARCH: $TARGETARCH" >&2; exit 1 ;; \
    esac
WORKDIR /src
COPY nuget.config Directory.Build.props Directory.Packages.props ActiveSync.slnx .editorconfig ./
COPY src/ src/
COPY tests/ tests/
RUN dotnet restore ActiveSync.slnx -r "$(cat /rid)"
# Version stamp for tagged releases: CI passes the git tag (v1.0.3 -> 1.0.3) so the
# startup banner reports the real version. Empty (local/branch builds) keeps the default.
ARG VERSION=
# RID-specific publish: the image is Linux-only (aspnet base), so ship only the target
# platform's native libraries instead of one copy per platform, and only English resource
# DLLs. --self-contained false: the aspnet base provides the runtime.
# -p:UseAppHost=false: the entrypoint/healthcheck/eas all invoke `dotnet ...dll`, the
# native apphost would be unused.
RUN dotnet publish src/ActiveSync.Server/ActiveSync.Server.csproj -c Release -o /app \
    --no-restore -r "$(cat /rid)" --self-contained false -p:UseAppHost=false \
    -p:SatelliteResourceLanguages=en ${VERSION:+-p:Version=$VERSION}

# The slim `eas` forwarding client ships next to the server in /app (native apphost + ReadyToRun,
# references only the BCL). It POSTs `eas <verb>` command lines to the running gateway's loopback
# /cli endpoint so everyday commands skip a cold start; when no gateway answers it falls back to
# `dotnet /app/ActiveSync.Server.dll <args>` beside it. UseAppHost stays on for a real `eas` binary.
RUN dotnet publish src/ActiveSync.Cli/ActiveSync.Cli.csproj -c Release -o /app \
    --no-restore -r "$(cat /rid)" --self-contained false \
    -p:SatelliteResourceLanguages=en ${VERSION:+-p:Version=$VERSION}

# Unit tests gate every image build (natively — the stage inherits the BUILDPLATFORM
# pin). Integration tests need sibling backend containers, which do not exist inside
# `docker build` — run those via docker/docker-compose.ci.yml.
FROM build AS test
RUN dotnet test ActiveSync.slnx -c Release --nologo --filter "Category!=Integration" \
    && touch /tests-passed

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
# Npgsql probes for Kerberos/GSSAPI on its first Postgres connection; without this
# library the native loader prints "Cannot load library libgssapi_krb5.so.2" to stderr.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*
# Copying the marker forces BuildKit to execute the test stage on every image build.
COPY --from=test /tests-passed /tmp/tests-passed
COPY --from=build /app .

# Operator convenience: `eas <verb>` from any exec session — e.g.
#   echo -n 'imap-password' | kubectl exec -i <pod> -- eas protect
#   echo -n 'phone-password' | docker exec -i <container> eas hash-password
#   docker exec -i <container> eas user set admin Admin true
# `eas` is the slim forwarding client (it resolves paths via its own /app location, so the exec
# working directory doesn't matter); a symlink onto PATH is enough.
RUN ln -s /app/eas /usr/local/bin/eas

# Run as the image's built-in non-root user; give it ownership of the state volume.
# /app/plugins is where out-of-repo backend plugins are dropped (one subdir each) —
# bind-mount it or COPY into a derived image; see docs/plugins.md.
RUN mkdir -p /data /app/plugins && chown -R app:app /data /app/plugins
VOLUME /data
USER app

# The listen address is defined once, by the Kestrel endpoint in appsettings.json
# (:5080; override with Kestrel__Endpoints__Http__Url). Blank the base image's
# ASPNETCORE_HTTP_PORTS=8080 so startup does not log override warnings.
ENV ActiveSync__Database__ConnectionString="Data Source=/data/activesync.db" \
    ASPNETCORE_HTTP_PORTS=""
EXPOSE 5080 5443

# The image ships no curl/wget; use the runtime to probe the health endpoint.
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD ["dotnet", "/app/ActiveSync.Server.dll", "healthcheck"]

# Bare `eas` shows the config banner instead of serving, so the default container command
# must say `serve` explicitly; any user-supplied args (CLI commands) replace it.
ENTRYPOINT ["dotnet", "ActiveSync.Server.dll"]
CMD ["serve"]

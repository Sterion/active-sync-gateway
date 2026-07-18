FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY nuget.config Directory.Build.props Directory.Packages.props ActiveSync.slnx .editorconfig ./
COPY src/ src/
COPY tests/ tests/
RUN dotnet restore ActiveSync.slnx -r linux-x64
# Version stamp for tagged releases: CI passes the git tag (v1.0.3 -> 1.0.3) so the
# startup banner reports the real version. Empty (local/branch builds) keeps the default.
ARG VERSION=
# RID-specific publish: the image is Linux-only (aspnet base), so ship only the linux-x64
# native libraries instead of one copy per platform, and only English resource DLLs.
# --self-contained false: the aspnet base provides the runtime. -p:UseAppHost=false: the
# entrypoint/healthcheck/eas all invoke `dotnet ...dll`, the native apphost would be unused.
# (A multi-arch build would map TARGETARCH to the RID instead of hardcoding linux-x64.)
RUN dotnet publish src/ActiveSync.Server/ActiveSync.Server.csproj -c Release -o /app \
    --no-restore -r linux-x64 --self-contained false -p:UseAppHost=false \
    -p:SatelliteResourceLanguages=en ${VERSION:+-p:Version=$VERSION}

# Unit tests gate every image build. Integration tests need sibling backend containers,
# which do not exist inside `docker build` — run those via docker/docker-compose.ci.yml.
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
# A wrapper script (not a symlink): the app is dll-hosted, and cd /app makes the verbs
# find appsettings.json regardless of the exec working directory.
RUN printf '#!/bin/sh\ncd /app\nexec dotnet /app/ActiveSync.Server.dll "$@"\n' \
      > /usr/local/bin/eas \
    && chmod 755 /usr/local/bin/eas

# Run as the image's built-in non-root user; give it ownership of the state volume.
RUN mkdir -p /data && chown -R app:app /data
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

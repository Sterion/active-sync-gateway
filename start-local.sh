#!/usr/bin/env bash
# Runs the gateway with ASPNETCORE_ENVIRONMENT=Development, which layers
# src/ActiveSync.Server/appsettings.Development.json over appsettings.json: plaintext-at-rest,
# the local docker/backends stack, the web admin UI enabled, and a preconfigured admin/admin
# login for testing the UI. Extra args pass through, e.g. ./start-local.sh --ActiveSync:ReadOnly=true
set -euo pipefail
export ASPNETCORE_ENVIRONMENT=Development
# --no-launch-profile: Properties/launchSettings.json's "http" profile pins
# ASPNETCORE_ENVIRONMENT=Local and would silently override the line above.
exec dotnet run --project "$(dirname "$0")/src/ActiveSync.Server" --no-launch-profile -- serve "$@"

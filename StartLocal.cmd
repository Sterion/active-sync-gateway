@echo off
rem Runs the gateway with the personal Local profile: ASPNETCORE_ENVIRONMENT=Local picks up
rem src\ActiveSync.Server\appsettings.Local.json (gitignored — local DB, web UIs enabled,
rem bootstrap admin user). Extra args pass through, e.g. StartLocal --ActiveSync:ReadOnly=true
set ASPNETCORE_ENVIRONMENT=Local
dotnet run --project "%~dp0src\ActiveSync.Server" -- serve %*

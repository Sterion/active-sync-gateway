#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Bring up the Stalwart test backend on the CANONICAL ports, so a plain `dotnet test` just works.

.DESCRIPTION
  scripts/test-fast.ps1 runs stalwart AND axigen on dedicated ports (10143.., 20143..) so the two
  coexist -- but that means every run needs AS_TEST_* overrides, and it rebuilds + recreates both
  containers each time. For iterating on one change against one backend, that is a lot of docker
  work for nothing.

  This brings up stalwart alone on the canonical ports (IMAP 143, SMTP 587, ManageSieve 4190,
  DAV+JMAP 5232) -- which are exactly TestBackend's built-in defaults. No environment variables,
  no wrapper script for the test run:

      dotnet test tests/ActiveSync.Integration.Tests --filter Category=Integration

  The image is NOT rebuilt unless -Build is passed, so a warm container is reused in seconds.

  NOTE: stalwart is a single compose project. Running this switches the SAME container to the
  canonical ports, so scripts/test-fast.ps1 will recreate it back onto 10143 next time it runs.
  Pick one workflow per session rather than alternating.

.PARAMETER Build
  Rebuild the image before starting. Needed only after editing docker/backends/stalwart/.

.PARAMETER Down
  Tear the stack down (-v, so the provisioned state is discarded) and exit.

.EXAMPLE
  ./scripts/stalwart-up.ps1
.EXAMPLE
  ./scripts/stalwart-up.ps1 -Build
.EXAMPLE
  ./scripts/stalwart-up.ps1 -Down
#>
[CmdletBinding()]
param(
	[switch]$Build,
	[switch]$Down
)

$ErrorActionPreference = 'Continue'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$Compose = Join-Path $RepoRoot 'docker/backends/stalwart/docker-compose.yml'

# The compose file falls back to the canonical ports only when these are unset. A shell that has
# run test-fast in the same session still has them exported, which would silently put the stack
# back on 10143 -- clear them so this script's whole point cannot be defeated by leftover state.
foreach ($name in 'STALWART_IMAP_PORT', 'STALWART_SMTP_PORT', 'STALWART_SIEVE_PORT', 'STALWART_HTTP_PORT') {
	Remove-Item "Env:$name" -ErrorAction SilentlyContinue
}

if ($Down) {
	Write-Host '==> Tearing down stalwart' -ForegroundColor Cyan
	docker compose -f $Compose down -v
	exit $LASTEXITCODE
}

Write-Host '==> Bringing up stalwart on canonical ports (143 / 587 / 4190 / 5232)' -ForegroundColor Cyan
if ($Build) {
	docker compose -f $Compose up -d --build --wait --wait-timeout 300
}
else {
	# No --build: a warm container is reused, and a cold one starts from the existing image.
	docker compose -f $Compose up -d --wait --wait-timeout 300
}
if ($LASTEXITCODE -ne 0) {
	Write-Host '!! stalwart failed to become healthy' -ForegroundColor Red
	Write-Host '   If the image is missing or stale, re-run with -Build.' -ForegroundColor Red
	exit 1
}

# The compose healthcheck already gates on /etc/stalwart/.provisioned plus a JMAP probe, so a
# healthy container is a provisioned one. Confirm the published ports anyway -- a port collision
# surfaces here as a clear message rather than as 124 mysteriously skipped tests.
$bad = $false
foreach ($port in 143, 587, 4190, 5232) {
	$client = [System.Net.Sockets.TcpClient]::new()
	try { $ok = $client.ConnectAsync('localhost', $port).Wait(3000) } catch { $ok = $false } finally { $client.Dispose() }
	if ($ok) {
		Write-Host ("    port {0,-5} OK" -f $port) -ForegroundColor Green
	}
	else {
		$bad = $true
		Write-Host ("    port {0,-5} NOT REACHABLE" -f $port) -ForegroundColor Red
	}
}
if ($bad) {
	Write-Host '!! Not all canonical ports are published — something else may be bound to them.' -ForegroundColor Red
	Write-Host '   Integration tests would SKIP and still report green. Fix this before testing.' -ForegroundColor Red
	exit 1
}

Write-Host ''
Write-Host 'Ready. Run the integration suite with no environment setup:' -ForegroundColor Green
Write-Host '    dotnet test tests/ActiveSync.Integration.Tests --filter Category=Integration' -ForegroundColor White
Write-Host ''
Write-Host 'Expect ~124 tests to run. If everything SKIPS, the backend is not reachable —' -ForegroundColor DarkGray
Write-Host 'a skipped suite still exits 0, so treat "0 passed" as a failure, not a pass.' -ForegroundColor DarkGray
exit 0

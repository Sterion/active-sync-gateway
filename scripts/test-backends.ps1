#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Run the integration suite against every self-hosted backend stack, one after another.

.DESCRIPTION
  Mirrors the CI matrix on the developer box. Both compose stacks publish the SAME host
  ports (143/587/5232, plus 4190 on Stalwart) with the same users, so the only knob that
  changes between them is AS_TEST_STACK (which flips the CalDAV home-set style). Because the
  ports collide, the stacks run sequentially: up --wait -> dotnet test -> down -v.

  Backends without a capability (docker-mailserver has no JMAP/Sieve; Radicale has no
  free-busy-query) skip the relevant tests cleanly rather than failing.

.PARAMETER Backends
  Which stacks to run, in order. Default: stalwart, mailserver.

.PARAMETER Postgres
  Also stand up a throwaway postgres:17-alpine and point AS_TEST_PG at it, exercising the
  Npgsql provider + migrations (CI parity). Default: SQLite temp files (no container).

.PARAMETER Filter
  dotnet test --filter expression. Default: Category=Integration.

.EXAMPLE
  ./scripts/test-backends.ps1
.EXAMPLE
  ./scripts/test-backends.ps1 -Backends mailserver -Postgres
#>
[CmdletBinding()]
param(
	[string[]]$Backends = @('stalwart', 'mailserver'),
	[switch]$Postgres,
	[string]$Filter = 'Category=Integration'
)

$ErrorActionPreference = 'Continue'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$PgContainer = 'as-local-pg'
$results = [ordered]@{}

function Compose-File([string]$backend) {
	return Join-Path $RepoRoot "docker/backends/$backend/docker-compose.yml"
}

function Start-Postgres {
	Write-Host "==> Starting throwaway Postgres ($PgContainer)" -ForegroundColor Cyan
	docker rm -f $PgContainer 2>$null | Out-Null
	docker run -d --name $PgContainer -p 5432:5432 `
		-e POSTGRES_USER=activesync -e POSTGRES_PASSWORD=ci-pw -e POSTGRES_DB=activesync `
		postgres:17-alpine | Out-Null
	for ($i = 0; $i -lt 30; $i++) {
		docker exec $PgContainer pg_isready -U activesync -d activesync -q 2>$null
		if ($LASTEXITCODE -eq 0) { return $true }
		Start-Sleep -Seconds 2
	}
	Write-Host "Postgres did not become ready" -ForegroundColor Red
	return $false
}

function Stop-Postgres {
	docker rm -f $PgContainer 2>$null | Out-Null
	Remove-Item Env:AS_TEST_PG -ErrorAction SilentlyContinue
}

# Per-backend AS_TEST_* beyond AS_TEST_STACK (mirrors the CI matrix). Backends whose DAV lives
# under non-default roots, whose mail submits over JMAP, or whose ManageSieve is plaintext set
# these; others inherit TestBackend's defaults.
$BackendEnv = @{
	cyrus = @{
		AS_TEST_DAV_HOMESET          = '/dav/calendars/user/{user}/'
		AS_TEST_DAV_CONTACTS_HOMESET = '/dav/addressbooks/user/{user}/'
		AS_TEST_MAILSUBMIT           = 'jmap'
		AS_TEST_SIEVE_TLS            = 'false'
	}
	baikal = @{
		AS_TEST_DAV_HOMESET          = '/dav.php/calendars/{user}/'
		AS_TEST_DAV_CONTACTS_HOMESET = '/dav.php/addressbooks/{user}/'
	}
	axigen = @{
		AS_TEST_DAV_HOMESET          = '/Calendar/'
		AS_TEST_DAV_CONTACTS_HOMESET = '/Contacts/'
	}
	james = @{
		AS_TEST_DAV_URL = 'none'
	}
}

try {
	if ($Postgres) {
		if (-not (Start-Postgres)) { exit 1 }
		$env:AS_TEST_PG = 'postgresql://activesync:ci-pw@localhost:5432/activesync'
	}

	foreach ($backend in $Backends) {
		$file = Compose-File $backend
		if (-not (Test-Path $file)) {
			Write-Host "!! No compose file for '$backend' at $file" -ForegroundColor Red
			$results[$backend] = 'no compose file'
			continue
		}

		Write-Host ""
		Write-Host "==================== $backend ====================" -ForegroundColor Yellow
		try {
			Write-Host "==> docker compose up --wait" -ForegroundColor Cyan
			docker compose -f $file up -d --build --wait --wait-timeout 300
			if ($LASTEXITCODE -ne 0) {
				$results[$backend] = 'stack failed to start'
				continue
			}

			$env:AS_TEST_STACK = $backend
			if ($BackendEnv.ContainsKey($backend)) {
				foreach ($kv in $BackendEnv[$backend].GetEnumerator()) {
					Set-Item "Env:$($kv.Key)" $kv.Value
				}
			}
			Write-Host "==> dotnet test (AS_TEST_STACK=$backend, filter=$Filter)" -ForegroundColor Cyan
			Push-Location $RepoRoot
			dotnet test ActiveSync.slnx --nologo --filter $Filter
			$code = $LASTEXITCODE
			Pop-Location
			$results[$backend] = if ($code -eq 0) { 'PASS' } else { "FAIL (exit $code)" }
		}
		finally {
			Write-Host "==> docker compose down -v" -ForegroundColor Cyan
			docker compose -f $file down -v 2>$null | Out-Null
			Remove-Item Env:AS_TEST_STACK -ErrorAction SilentlyContinue
			if ($BackendEnv.ContainsKey($backend)) {
				foreach ($key in $BackendEnv[$backend].Keys) {
					Remove-Item "Env:$key" -ErrorAction SilentlyContinue
				}
			}
		}
	}
}
finally {
	if ($Postgres) { Stop-Postgres }
}

Write-Host ""
Write-Host "==================== summary ====================" -ForegroundColor Yellow
$failed = $false
foreach ($backend in $results.Keys) {
	$status = $results[$backend]
	$color = if ($status -eq 'PASS') { 'Green' } else { 'Red' }
	if ($status -ne 'PASS') { $failed = $true }
	Write-Host ("{0,-14} {1}" -f $backend, $status) -ForegroundColor $color
}

if ($failed) { exit 1 }
exit 0

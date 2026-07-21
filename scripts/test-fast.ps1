#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Fast local integration check: run the suite against stalwart AND axigen in PARALLEL, with both
  stacks left running for the next change (start only if not already healthy; reuse when warm).

.DESCRIPTION
  The two most valuable backends -- stalwart (full IMAP/SMTP/DAV/JMAP/Sieve) and axigen (fast,
  full IMAP/SMTP/DAV) -- are the normal per-change check. They run on DEDICATED host ports
  (stalwart 10143/10587/10190/10232, axigen 20143/20587/20232) so they coexist and leave the
  canonical set (143/587/5232/4190) free for on-demand backends via scripts/test-backends.ps1.
  The port overrides are passed through the compose files' ${STALWART_*}/${AXIGEN_*} vars.

.PARAMETER Filter
  dotnet test --filter expression. Default: Category=Integration.

.PARAMETER Down
  Tear both stacks down (-v) at the end. Default: leave them running.

.PARAMETER Sequential
  Run the two legs one after another instead of in parallel. Slower, but avoids the CPU contention
  of two dotnet-test processes sharing one machine -- steadier on a constrained box. (That parallel
  contention is what makes timing-sensitive tests flake locally; CI is unaffected, as each backend
  leg runs on its own runner.)

.EXAMPLE
  ./scripts/test-fast.ps1
.EXAMPLE
  ./scripts/test-fast.ps1 -Filter "FullyQualifiedName~DavRoundTrip"
.EXAMPLE
  ./scripts/test-fast.ps1 -Sequential
#>
[CmdletBinding()]
param(
	[string]$Filter = 'Category=Integration',
	[switch]$Down,
	[switch]$Sequential
)

$ErrorActionPreference = 'Continue'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$Proj = Join-Path $RepoRoot 'tests/ActiveSync.Integration.Tests/ActiveSync.Integration.Tests.csproj'

# Dedicated host ports so both stacks coexist and the canonical set stays free. Set for the whole
# script so `docker compose up` publishes them (the compose files read ${STALWART_*}/${AXIGEN_*}).
$env:STALWART_IMAP_PORT = '10143'; $env:STALWART_SMTP_PORT = '10587'
$env:STALWART_SIEVE_PORT = '10190'; $env:STALWART_HTTP_PORT = '10232'
$env:AXIGEN_IMAP_PORT = '20143'; $env:AXIGEN_SMTP_PORT = '20587'; $env:AXIGEN_HTTP_PORT = '20232'

function Start-Stack([string]$name) {
	Write-Host "==> up $name (reused if already healthy)" -ForegroundColor Cyan
	docker compose -f (Join-Path $RepoRoot "docker/backends/$name/docker-compose.yml") `
		up -d --build --wait --wait-timeout 300
	if ($LASTEXITCODE -ne 0) { Write-Host "!! $name failed to start" -ForegroundColor Red; exit 1 }
}
Start-Stack 'stalwart'
Start-Stack 'axigen'

Write-Host "==> Building integration test project once" -ForegroundColor Cyan
dotnet build $Proj -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { exit 1 }

$legs = @(
	@{ name = 'stalwart'; env = @{
		AS_TEST_STACK = 'stalwart'; AS_TEST_IMAP_PORT = '10143'; AS_TEST_SMTP_PORT = '10587'
		AS_TEST_SIEVE_PORT = '10190'; AS_TEST_DAV_URL = 'http://localhost:10232' } },
	@{ name = 'axigen'; env = @{
		AS_TEST_STACK = 'axigen'; AS_TEST_IMAP_PORT = '20143'; AS_TEST_SMTP_PORT = '20587'
		AS_TEST_DAV_URL = 'http://localhost:20232'
		AS_TEST_DAV_HOMESET = '/Calendar/'; AS_TEST_DAV_CONTACTS_HOMESET = '/Contacts/' } }
)

# Pre-flight: every integration test is a [BackendFact], which xunit turns into a SKIP when
# TestBackend's IMAP probe fails -- and a run of nothing but skips still exits 0. That makes an
# unreachable backend indistinguishable from a green run, which is exactly how an unverified fix
# gets signed off. Probe the same host:port TestBackend will, and refuse to run if it is dead.
foreach ($leg in $legs) {
	$probePort = [int]$leg.env.AS_TEST_IMAP_PORT
	$client = [System.Net.Sockets.TcpClient]::new()
	try {
		$ok = $client.ConnectAsync('localhost', $probePort).Wait(3000)
	}
	catch { $ok = $false }
	finally { $client.Dispose() }
	if (-not $ok) {
		Write-Host "!! $($leg.name): no IMAP backend reachable at localhost:$probePort" -ForegroundColor Red
		Write-Host "   Every integration test would SKIP and the run would still report green." -ForegroundColor Red
		Write-Host "   Refusing to run -- a skipped suite is not verification." -ForegroundColor Red
		exit 1
	}
}

if ($Sequential) {
	Write-Host "==> Running stalwart + axigen suites SEQUENTIALLY (filter: $Filter)" -ForegroundColor Cyan
	$results = foreach ($leg in $legs) {
		Write-Host "==> leg: $($leg.name)" -ForegroundColor Cyan
		foreach ($k in $leg.env.Keys) { Set-Item "Env:$k" $leg.env[$k] }
		$out = dotnet test $Proj -c Release --no-build --nologo --filter $Filter 2>&1 | Out-String
		[pscustomobject]@{ name = $leg.name; rc = $LASTEXITCODE; out = $out }
	}
}
else {
	Write-Host "==> Running stalwart + axigen suites in parallel (filter: $Filter)" -ForegroundColor Cyan
	$jobs = foreach ($leg in $legs) {
		Start-Job -Name $leg.name -ScriptBlock {
			param($root, $proj, $filter, $envMap)
			foreach ($k in $envMap.Keys) { Set-Item "Env:$k" $envMap[$k] }
			Set-Location $root
			$out = dotnet test $proj -c Release --no-build --nologo --filter $filter 2>&1 | Out-String
			[pscustomobject]@{ rc = $LASTEXITCODE; out = $out }
		} -ArgumentList $RepoRoot, $Proj, $Filter, $leg.env
	}
	$jobs | Wait-Job | Out-Null
	$results = foreach ($job in $jobs) {
		$r = Receive-Job $job
		Remove-Job $job
		[pscustomobject]@{ name = $job.Name; rc = $r.rc; out = $r.out }
	}
}

$failed = $false
Write-Host "`n==================== summary ====================" -ForegroundColor Yellow
foreach ($res in $results) {
	$line = ($res.out -split "`n" | Select-String -Pattern 'Passed!|Failed!' | Select-Object -Last 1).Line

	# A suite that ran nothing, or skipped everything, exits 0 -- treat it as a failure rather
	# than let "PASS" stand for "verified". Pairs with the pre-flight probe above: that catches a
	# dead backend, this catches a filter or discovery problem that silently matched no tests.
	$passedCount = if ($line -match 'Passed:\s*(\d+)') { [int]$Matches[1] } else { -1 }
	$totalCount = if ($line -match 'Total:\s*(\d+)') { [int]$Matches[1] } else { -1 }
	if ($res.rc -eq 0 -and ($totalCount -eq 0 -or $passedCount -eq 0)) {
		$failed = $true
		Write-Host ("{0,-10} FAIL  no tests actually ran (total={1}, passed={2}) -- filter '{3}' matched nothing, or every test skipped" -f $res.name, $totalCount, $passedCount, $Filter) -ForegroundColor Red
		continue
	}

	if ($res.rc -eq 0) {
		Write-Host ("{0,-10} PASS  {1}" -f $res.name, ($line ?? '').Trim()) -ForegroundColor Green
	}
	else {
		$failed = $true
		Write-Host ("{0,-10} FAIL  {1}" -f $res.name, ($line ?? '').Trim()) -ForegroundColor Red
		$res.out -split "`n" | Select-String -Pattern '^\s*Failed ' |
			ForEach-Object { ($_.Line -replace ' \[.*', '').Trim() } | Sort-Object -Unique |
			Select-Object -First 20 | ForEach-Object { Write-Host "   $_" -ForegroundColor Red }
	}
}

if ($Down) {
	Write-Host "==> Tearing down (-Down)" -ForegroundColor Cyan
	docker compose -f (Join-Path $RepoRoot 'docker/backends/stalwart/docker-compose.yml') down -v 2>$null | Out-Null
	docker compose -f (Join-Path $RepoRoot 'docker/backends/axigen/docker-compose.yml') down -v 2>$null | Out-Null
}
else {
	Write-Host "==> Left stalwart + axigen running (re-run is fast; pass -Down to tear down)" -ForegroundColor Cyan
}

if ($failed) { exit 1 }
exit 0

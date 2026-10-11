# Runs the dev build against a throwaway database, with its own settings folder
# and a stand-in Tally, so a click-through touches nothing real: not the saved
# settings on this PC, not production PostgreSQL, not TallyPrime's books.
#
#   .\tools\run-scratch.ps1             prepare (first time: new database) and start the app
#   .\tools\run-scratch.ps1 -NoLaunch   prepare only
#   .\tools\run-scratch.ps1 -Reset      throw the database and settings away, then prepare again
#   .\tools\run-scratch.ps1 -RestoreFrom C:\path\copy.dump
#                                       start over from a pg_dump file (see below)
#   .\tools\run-scratch.ps1 -Remove     stop the container, delete the scratch folder, and exit
#
# What it sets up, all under artifacts\scratch (git-ignored):
#   - a postgres:18 container named tw-scratch-postgres with a random password,
#     migrated to the current schema and marked DEV;
#   - SHOWROOM_BILLING_APPDATA / SHOWROOM_BILLING_LOCALAPPDATA pointing at empty
#     folders, and SHOWROOM_BILLING_POSTGRES pointing at that container. The API
#     gives the variable precedence over every saved connection string;
#   - tools\scratch-tally.ps1 on 127.0.0.1:9000, where a new database expects Tally;
#   - the dev build's API on 127.0.0.1:5108, started here and stopped when the app
#     closes. The Desktop only ever connects to an API; it does not start one.
#
# The app starts like a fresh install: no admin passcode, placeholder company.
# The database keeps its data between runs until the container stops.
#
# -RestoreFrom loads a dump instead: a file made with
# "pg_dump --format=custom --no-owner", for example a copy of the real database
# that you took yourself. This script never connects to that database; it only
# reads the file. After the restore it points the copy's Tally settings at the
# stand-in, marks the copy DEV, drops the admin sessions that came with it, and
# applies whatever migrations the copy has not had, which rehearses an upgrade
# on real data. The stand-in Tally then serves the copy's own company and
# master lists. Such a copy holds real customer data: -Remove or -Reset deletes it.

[CmdletBinding()]
param(
    [switch]$Reset,
    [switch]$Remove,
    [switch]$NoLaunch,
    [string]$RestoreFrom,
    # Do not start the stand-in Tally and do not stop when something already
    # listens on the Tally port. The app will then talk to whatever is there.
    [switch]$AllowRealTally,
    [string]$DockerContext = 'proxmox-docker',
    [string]$Image = 'postgres:18',
    [string]$HostAddress,
    [int]$TallyPort = 9000
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'PostgresContainer.ps1')

$repoRoot = Split-Path -Parent $PSScriptRoot
$scratch = Join-Path $repoRoot 'artifacts\scratch'
$statePath = Join-Path $scratch 'scratch.json'
$mastersPath = Join-Path $scratch 'tally-masters.json'
$containerName = 'tw-scratch-postgres'
$database = 'tw_scratch'
# Where a Development Desktop looks for its API (its appsettings.Development.json).
$apiPort = 5108

function Test-PortInUse([int]$port) {
    $client = [System.Net.Sockets.TcpClient]::new()
    try {
        return $client.ConnectAsync('127.0.0.1', $port).Wait(300) -and $client.Connected
    }
    catch {
        return $false
    }
    finally {
        $client.Dispose()
    }
}

function Test-ContainerRunning {
    $names = & docker --context $DockerContext ps --filter "name=^$containerName$" --format '{{.Names}}'
    if ($LASTEXITCODE -ne 0) { throw "Could not list containers on Docker context '$DockerContext'." }
    return @($names) -contains $containerName
}

function Stop-ScratchContainer {
    if (Test-ContainerRunning) {
        Write-Host "Stopping container '$containerName'..." -ForegroundColor Cyan
        & docker --context $DockerContext stop $containerName | Out-Null
    }
}

# Runs SQL in the scratch database and returns what psql prints (unaligned, rows only).
function Invoke-ScratchSql([string]$sql) {
    Invoke-Docker -Context $DockerContext -Arguments @(
        'exec', $containerName, 'psql', '-U', 'postgres', '-d', $database, '-v', 'ON_ERROR_STOP=1', '-q', '-At', '-c', $sql)
}

# Loads a pg_dump custom-format file into the empty scratch database.
function Restore-IntoScratch([string]$dumpFile) {
    Write-Host "Restoring $dumpFile into the scratch database..." -ForegroundColor Cyan
    Invoke-Docker -Context $DockerContext -Arguments @('cp', $dumpFile, "${containerName}:/tmp/source.dump") | Out-Null
    try {
        $restore = & docker --context $DockerContext exec $containerName pg_restore --username=postgres `
            "--dbname=$database" --no-owner --no-privileges /tmp/source.dump 2>&1
        # The target already has a "public" schema; a dump that recreates it
        # reports that one error and carries on. Anything else is a failure.
        $problems = @($restore | ForEach-Object { "$_" } |
            Where-Object { $_ -match 'pg_restore: error:' -and $_ -notmatch 'schema "public" already exists' })
        if ($problems.Count -gt 0) {
            throw "pg_restore reported errors: $($problems -join ' | ')"
        }
    }
    finally {
        & docker --context $DockerContext exec --user root $containerName rm -f /tmp/source.dump | Out-Null
    }

    # A copy must never act as the original: mark it DEV, send its Tally calls
    # to the stand-in, and drop the admin sessions that came with it.
    Invoke-ScratchSql @"
DO `$copy`$
BEGIN
    IF to_regclass('public.database_identity') IS NOT NULL THEN
        UPDATE public.database_identity SET value = 'DEV', updated_at_utc = now() WHERE key = 'environment';
    END IF;
    IF to_regclass('public.cloud_settings') IS NOT NULL THEN
        UPDATE public.cloud_settings SET "Host" = '127.0.0.1', "Port" = $TallyPort;
    END IF;
    IF to_regclass('public.admin_sessions') IS NOT NULL THEN
        DELETE FROM public.admin_sessions;
    END IF;
END
`$copy`$;
"@ | Out-Null
    $bills = Invoke-ScratchSql "SELECT CASE WHEN to_regclass('public.bills') IS NULL THEN -1 ELSE (SELECT count(*) FROM public.bills) END"
    Write-Host "Restored. Bills in the copy: $bills." -ForegroundColor Cyan
}

# The master lists a restored copy holds, for the stand-in Tally to serve, so
# "Refresh from Tally" gives back what the copy has instead of test names.
function Export-MastersForStandIn {
    $json = Invoke-ScratchSql @'
SELECT json_build_object(
    'activeCompany', (SELECT "ActiveCompanyName" FROM public.cloud_settings LIMIT 1),
    'companies', (SELECT coalesce(json_agg(json_build_object('name', s."Name", 'active', s."IsActive") ORDER BY s."Name"), '[]'::json)
                  FROM public.tally_company_snapshots s JOIN public.tally_master_snapshot_batches b ON b."Id" = s."BatchId" WHERE b."Status" = 'active'),
    'ledgers', (SELECT coalesce(json_agg(json_build_object('name', s."Name", 'parent', s."Parent", 'primaryGroup', s."PrimaryGroup", 'isRevenue', s."IsRevenue", 'gstin', s."Gstin") ORDER BY s."Name"), '[]'::json)
                FROM public.tally_ledger_snapshots s JOIN public.tally_master_snapshot_batches b ON b."Id" = s."BatchId" WHERE b."Status" = 'active'),
    'stockItems', (SELECT coalesce(json_agg(json_build_object('name', s."Name", 'alias', s."Alias", 'unit', s."BaseUnit", 'hsn', s."HsnCode") ORDER BY s."Name"), '[]'::json)
                   FROM public.tally_stock_item_snapshots s JOIN public.tally_master_snapshot_batches b ON b."Id" = s."BatchId" WHERE b."Status" = 'active'),
    'voucherTypes', (SELECT coalesce(json_agg(json_build_object('name', s."Name", 'parent', s."ParentType", 'deemedPositive', s."IsDeemedPositive") ORDER BY s."Name"), '[]'::json)
                     FROM public.tally_voucher_type_snapshots s JOIN public.tally_master_snapshot_batches b ON b."Id" = s."BatchId" WHERE b."Status" = 'active'))
'@
    ($json -join '') | Set-Content -LiteralPath $mastersPath -Encoding utf8
}

$restoring = -not [string]::IsNullOrWhiteSpace($RestoreFrom)
if ($restoring) {
    if (-not (Test-Path -LiteralPath $RestoreFrom -PathType Leaf)) { throw "Dump file '$RestoreFrom' was not found." }
    $RestoreFrom = (Resolve-Path -LiteralPath $RestoreFrom).Path
    if ($RestoreFrom.StartsWith($scratch, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Keep the dump file outside artifacts\scratch: that folder is emptied before the restore.'
    }
}

if ($Reset -or $Remove -or $restoring) {
    Stop-ScratchContainer
    if (Test-Path -LiteralPath $scratch) {
        Remove-Item -LiteralPath $scratch -Recurse -Force
    }
    if ($Remove) {
        Write-Host 'Scratch database and settings removed.'
        return
    }
}

foreach ($folder in @($scratch, (Join-Path $scratch 'appdata'), (Join-Path $scratch 'localappdata'))) {
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
}

# ── database ────────────────────────────────────────────────────────────────
$state = if (Test-Path -LiteralPath $statePath) { Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json } else { $null }
if ($null -eq $state -or -not (Test-ContainerRunning)) {
    # Either half missing means the other is useless: the container holds the
    # data and the state file holds its password.
    Stop-ScratchContainer
    $password = [Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(16)).ToLowerInvariant()
    $resolvedHost = Resolve-DockerHostAddress -Context $DockerContext -HostAddress $HostAddress

    Write-Host "Starting $Image as '$containerName' on Docker context '$DockerContext'..." -ForegroundColor Cyan
    Invoke-Docker -Context $DockerContext -Arguments @(
        'run', '-d', '--rm',
        '--name', $containerName,
        '-e', "POSTGRES_PASSWORD=$password",
        '-e', "POSTGRES_DB=$database",
        '-p', '0:5432',
        $Image
    ) | Out-Null
    $port = Get-PublishedPostgresPort -Context $DockerContext -ContainerName $containerName
    Wait-ForPostgres -Context $DockerContext -ContainerName $containerName -Database $database

    $state = [pscustomobject]@{ host = $resolvedHost; port = $port; database = $database; password = $password }
    $state | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding utf8
    Write-Host "New scratch database at $resolvedHost`:$port." -ForegroundColor Cyan
}
else {
    Write-Host "Reusing scratch database at $($state.host):$($state.port)." -ForegroundColor Cyan
}

if ($restoring) {
    Restore-IntoScratch $RestoreFrom
}

$connectionString = "Host=$($state.host);Port=$($state.port);Database=$($state.database);Username=postgres;Password=$($state.password)"

# Everything started from here on (the EF tool, the API and the Desktop)
# inherits these, so none of them reads this PC's real settings.
$env:SHOWROOM_BILLING_APPDATA = Join-Path $scratch 'appdata'
$env:SHOWROOM_BILLING_LOCALAPPDATA = Join-Path $scratch 'localappdata'
$env:SHOWROOM_BILLING_POSTGRES = $connectionString
$env:DOTNET_ENVIRONMENT = 'Development'
$env:ASPNETCORE_ENVIRONMENT = 'Development'

# ── build and migrate ───────────────────────────────────────────────────────
if (Test-PortInUse $apiPort) {
    throw "Something already listens on 127.0.0.1:$apiPort. The dev Desktop would connect to that API instead of the one started here, and that API may use another database. Close it and run this again."
}

Write-Host 'Building...' -ForegroundColor Cyan
& dotnet build (Join-Path $repoRoot 'ShowroomBilling.sln') --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

Write-Host 'Applying migrations to the scratch database...' -ForegroundColor Cyan
& dotnet ef database update `
    --project (Join-Path $repoRoot 'src\ShowroomBilling.Infrastructure') `
    --startup-project (Join-Path $repoRoot 'src\ShowroomBilling.Api') `
    --connection $connectionString `
    --no-build
if ($LASTEXITCODE -ne 0) { throw 'Migration failed. Is dotnet-ef installed (dotnet tool install --global dotnet-ef)?' }

Invoke-ScratchSql "UPDATE public.database_identity SET value = 'DEV', updated_at_utc = now() WHERE key = 'environment'" | Out-Null

if ($restoring) {
    Export-MastersForStandIn
}

# Whatever is in the database, the app must not be able to reach a real Tally.
$expectedEndpoint = "127.0.0.1:$TallyPort"
foreach ($endpoint in @(Invoke-ScratchSql 'SELECT "Host" || '':'' || "Port" FROM public.cloud_settings')) {
    if ($endpoint -ne $expectedEndpoint -and -not $AllowRealTally) {
        throw "The scratch database's Tally connection is '$endpoint', not the stand-in at $expectedEndpoint, so pushes would go there. Change it back in Settings > Connection, start over with -Reset, or pass -AllowRealTally if that is what you want."
    }
}

if ($NoLaunch) {
    Write-Host 'Scratch database is ready. Run this again without -NoLaunch to start the app.'
    return
}

# ── stand-in Tally ──────────────────────────────────────────────────────────
$tally = $null
if (Test-PortInUse $TallyPort) {
    if (-not $AllowRealTally) {
        throw "Something already listens on 127.0.0.1:$TallyPort, probably TallyPrime. A new database points at that port, so test vouchers could reach real books. Close it, or pass -AllowRealTally if that is what you want."
    }
    Write-Warning "Using whatever listens on 127.0.0.1:$TallyPort as Tally."
}
elseif (-not $AllowRealTally) {
    $tallyLog = Join-Path $scratch 'tally.log'
    $tally = Start-Process -FilePath 'pwsh' -PassThru -WindowStyle Hidden -ArgumentList @(
        '-NoProfile', '-File', (Join-Path $PSScriptRoot 'scratch-tally.ps1'),
        '-Port', $TallyPort, '-LogPath', $tallyLog, '-MastersPath', $mastersPath)
    $deadline = (Get-Date).AddSeconds(10)
    while (-not (Test-PortInUse $TallyPort)) {
        if ($tally.HasExited -or (Get-Date) -gt $deadline) { throw 'The stand-in Tally did not start.' }
        Start-Sleep -Milliseconds 200
    }
    Write-Host "Stand-in Tally on 127.0.0.1:$TallyPort (log: $tallyLog)." -ForegroundColor Cyan
}

# ── the API, then the app ───────────────────────────────────────────────────
$apiDir = Join-Path $repoRoot 'src\ShowroomBilling.Api\bin\Debug\net10.0'
$apiExe = Join-Path $apiDir 'ShowroomBilling.Api.exe'
$desktopDir = Join-Path $repoRoot 'src\ShowroomBilling.Desktop\bin\Debug\net10.0-windows'
$desktopExe = Join-Path $desktopDir 'TallyWrapper.exe'
$api = $null
try {
    $api = Start-Process -FilePath $apiExe -WorkingDirectory $apiDir -PassThru -WindowStyle Hidden `
        -ArgumentList @('--urls', "http://127.0.0.1:$apiPort")
    $deadline = (Get-Date).AddSeconds(30)
    while (-not (Test-PortInUse $apiPort)) {
        if ($api.HasExited -or (Get-Date) -gt $deadline) {
            throw "The API did not start on 127.0.0.1:$apiPort. Its log is under $($env:SHOWROOM_BILLING_APPDATA)\logs."
        }
        Start-Sleep -Milliseconds 200
    }
    Write-Host "API on 127.0.0.1:$apiPort." -ForegroundColor Cyan

    Write-Host 'Starting the app. This window waits until it is closed.' -ForegroundColor Green
    Write-Host "  settings:  $($env:SHOWROOM_BILLING_APPDATA)"
    Write-Host "  database:  $($state.host):$($state.port)/$($state.database) (marked DEV)"
    Write-Host '  In a bill, a party name or note containing FAIL or UNKNOWN makes the stand-in Tally refuse or drop the voucher.'
    $app = Start-Process -FilePath $desktopExe -WorkingDirectory $desktopDir -PassThru
    $app.WaitForExit()
}
finally {
    if ($null -ne $api -and -not $api.HasExited) {
        Stop-Process -Id $api.Id -Force
    }
    if ($null -ne $tally -and -not $tally.HasExited) {
        Stop-Process -Id $tally.Id -Force
    }
}

Write-Host "App closed. The scratch database is still running; '-Remove' deletes it."

# Runs the dev build against a throwaway database, with its own settings folder
# and a stand-in Tally, so a click-through touches nothing real: not the saved
# settings on this PC, not production PostgreSQL, not TallyPrime's books.
#
#   .\tools\run-scratch.ps1             prepare (first time: new database) and start the app
#   .\tools\run-scratch.ps1 -NoLaunch   prepare only
#   .\tools\run-scratch.ps1 -Reset      throw the database and settings away, then prepare again
#   .\tools\run-scratch.ps1 -Remove     stop the container, delete the scratch folder, and exit
#
# What it sets up, all under artifacts\scratch (git-ignored):
#   - a postgres:18 container named tw-scratch-postgres with a random password,
#     migrated to the current schema and marked DEV;
#   - SHOWROOM_BILLING_APPDATA / SHOWROOM_BILLING_LOCALAPPDATA pointing at empty
#     folders, and SHOWROOM_BILLING_POSTGRES pointing at that container. The API
#     gives the variable precedence over every saved connection string;
#   - tools\scratch-tally.ps1 on 127.0.0.1:9000, where a new database expects Tally.
#
# The app starts like a fresh install: no admin passcode, placeholder company.
# The database keeps its data between runs until the container stops.

[CmdletBinding()]
param(
    [switch]$Reset,
    [switch]$Remove,
    [switch]$NoLaunch,
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
$containerName = 'tw-scratch-postgres'
$database = 'tw_scratch'
# The dev build's API: appsettings.Development.json of the Desktop.
$apiPorts = @(5108, 7108)

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

if ($Reset -or $Remove) {
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

$connectionString = "Host=$($state.host);Port=$($state.port);Database=$($state.database);Username=postgres;Password=$($state.password)"

# Everything started from here on (the EF tool, the Desktop and the API it
# spawns) inherits these, so none of them reads this PC's real settings.
$env:SHOWROOM_BILLING_APPDATA = Join-Path $scratch 'appdata'
$env:SHOWROOM_BILLING_LOCALAPPDATA = Join-Path $scratch 'localappdata'
$env:SHOWROOM_BILLING_POSTGRES = $connectionString
$env:DOTNET_ENVIRONMENT = 'Development'
$env:ASPNETCORE_ENVIRONMENT = 'Development'

# ── build and migrate ───────────────────────────────────────────────────────
foreach ($port in $apiPorts) {
    if (Test-PortInUse $port) {
        throw "Something already listens on 127.0.0.1:$port. The dev Desktop would attach to that API instead of starting its own, and that API may use another database. Close it and run this again."
    }
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

Invoke-Docker -Context $DockerContext -Arguments @(
    'exec', $containerName, 'psql', '-U', 'postgres', '-d', $database, '-v', 'ON_ERROR_STOP=1', '-q', '-c',
    "UPDATE public.database_identity SET value = 'DEV', updated_at_utc = now() WHERE key = 'environment'"
) | Out-Null

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
        '-NoProfile', '-File', (Join-Path $PSScriptRoot 'scratch-tally.ps1'), '-Port', $TallyPort, '-LogPath', $tallyLog)
    $deadline = (Get-Date).AddSeconds(10)
    while (-not (Test-PortInUse $TallyPort)) {
        if ($tally.HasExited -or (Get-Date) -gt $deadline) { throw 'The stand-in Tally did not start.' }
        Start-Sleep -Milliseconds 200
    }
    Write-Host "Stand-in Tally on 127.0.0.1:$TallyPort (log: $tallyLog)." -ForegroundColor Cyan
}

# ── the app ─────────────────────────────────────────────────────────────────
$desktopDir = Join-Path $repoRoot 'src\ShowroomBilling.Desktop\bin\Debug\net10.0-windows'
$desktopExe = Join-Path $desktopDir 'TallyWrapper.exe'
try {
    Write-Host 'Starting the app. This window waits until it is closed.' -ForegroundColor Green
    Write-Host "  settings:  $($env:SHOWROOM_BILLING_APPDATA)"
    Write-Host "  database:  $($state.host):$($state.port)/$($state.database) (marked DEV)"
    Write-Host '  In a bill, a party name or note containing FAIL or UNKNOWN makes the stand-in Tally refuse or drop the voucher.'
    $app = Start-Process -FilePath $desktopExe -WorkingDirectory $desktopDir -PassThru
    $app.WaitForExit()
}
finally {
    if ($null -ne $tally -and -not $tally.HasExited) {
        Stop-Process -Id $tally.Id -Force
    }
}

Write-Host "App closed. The scratch database is still running; '-Remove' deletes it."

[CmdletBinding()]
param(
    [string]$DockerContext = 'proxmox-docker',
    [string]$Image = 'postgres:17',
    [string]$ContainerName = "tw-postgres-tests-$([Guid]::NewGuid().ToString('N').Substring(0, 12))",
    [string]$PostgresPassword = 'postgres',
    [string]$HostAddress,
    [string]$TestProject = (Join-Path $PSScriptRoot '..\tests\ShowroomBilling.Tests'),
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'

function Invoke-Docker {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    & docker --context $DockerContext @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "docker --context $DockerContext $($Arguments -join ' ') failed."
    }
}

function Get-DockerEndpoint {
    $endpoint = (& docker context inspect $DockerContext --format '{{.Endpoints.docker.Host}}').Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "Could not inspect Docker context '$DockerContext'."
    }
    if ([string]::IsNullOrWhiteSpace($endpoint) -or $endpoint -eq '<no value>') {
        throw "Docker context '$DockerContext' does not expose a docker endpoint."
    }
    return $endpoint
}

function Resolve-DockerHostAddress {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Endpoint
    )

    if (-not [string]::IsNullOrWhiteSpace($HostAddress)) {
        return $HostAddress
    }

    if ($Endpoint.StartsWith('ssh://', [StringComparison]::OrdinalIgnoreCase)) {
        $uri = [Uri]$Endpoint
        $sshTarget = if ([string]::IsNullOrWhiteSpace($uri.UserInfo)) {
            $uri.Host
        } else {
            "$($uri.UserInfo)@$($uri.Host)"
        }

        $sshArgs = @('-G')
        if (-not $uri.IsDefaultPort) {
            $sshArgs += @('-p', [string]$uri.Port)
        }
        $sshArgs += $sshTarget

        $sshConfig = & ssh @sshArgs
        if ($LASTEXITCODE -ne 0) {
            throw "Could not resolve SSH config for '$sshTarget'. Pass -HostAddress explicitly."
        }

        $hostnameLine = $sshConfig | Where-Object { $_ -match '^hostname\s+' } | Select-Object -First 1
        if ($hostnameLine -match '^hostname\s+(.+)$') {
            return $Matches[1].Trim()
        }

        return $uri.Host
    }

    if ($Endpoint -match '^tcp://([^:/]+)') {
        return $Matches[1]
    }

    if ($Endpoint -match '^(npipe|unix)://') {
        return '127.0.0.1'
    }

    throw "Unsupported Docker endpoint '$Endpoint'. Pass -HostAddress explicitly."
}

function Get-PublishedPort {
    $portOutput = @(Invoke-Docker @('port', $ContainerName, '5432/tcp'))
    $portLine = $portOutput | Where-Object { $_ -match '^0\.0\.0\.0:' } | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($portLine)) {
        $portLine = $portOutput | Select-Object -First 1
    }
    if ($portLine -notmatch ':(\d+)$') {
        throw "Could not parse published Postgres port from: $($portOutput -join '; ')"
    }
    return $Matches[1]
}

function Wait-ForPostgres {
    $deadline = (Get-Date).AddSeconds(60)
    do {
        & docker --context $DockerContext exec $ContainerName pg_isready -U postgres -d postgres *> $null
        if ($LASTEXITCODE -eq 0) {
            return
        }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)

    Invoke-Docker @('logs', '--tail', '80', $ContainerName) | Out-Host
    throw "Postgres container '$ContainerName' did not become ready within 60 seconds."
}

$containerStarted = $false
$previousRunFlag = $env:SHOWROOM_BILLING_RUN_POSTGRES_TESTS
$previousConnection = $env:SHOWROOM_BILLING_POSTGRES_TEST_CONNECTION

try {
    $endpoint = Get-DockerEndpoint
    $resolvedHost = Resolve-DockerHostAddress $endpoint

    Write-Host "Starting $Image on Docker context '$DockerContext'..." -ForegroundColor Cyan
    $containerId = (Invoke-Docker @(
        'run', '-d', '--rm',
        '--name', $ContainerName,
        '-e', "POSTGRES_PASSWORD=$PostgresPassword",
        '-p', '0:5432',
        $Image
    )).Trim()
    $containerStarted = $true

    $port = Get-PublishedPort
    Wait-ForPostgres

    $connectionString = "Host=$resolvedHost;Port=$port;Database=postgres;Username=postgres;Password=$PostgresPassword"
    Write-Host "Running Postgres tests against $resolvedHost`:$port (container $containerId)..." -ForegroundColor Cyan

    $env:SHOWROOM_BILLING_RUN_POSTGRES_TESTS = '1'
    $env:SHOWROOM_BILLING_POSTGRES_TEST_CONNECTION = $connectionString

    $testArgs = @('test', $TestProject, '--filter', 'Category=Postgres')
    if ($NoBuild) {
        $testArgs += '--no-build'
    }

    & dotnet @testArgs
    if ($LASTEXITCODE -ne 0) {
        throw 'Postgres test run failed.'
    }
}
finally {
    $env:SHOWROOM_BILLING_RUN_POSTGRES_TESTS = $previousRunFlag
    $env:SHOWROOM_BILLING_POSTGRES_TEST_CONNECTION = $previousConnection

    if ($containerStarted) {
        Write-Host "Stopping Postgres container '$ContainerName'..." -ForegroundColor Cyan
        & docker --context $DockerContext stop $ContainerName | Out-Host
    }
}

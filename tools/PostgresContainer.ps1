# Shared by run-postgres-tests.ps1 and run-scratch.ps1: start a throwaway
# PostgreSQL container on a Docker context and find out where it listens.
# Dot-source it; it defines functions only.

function Invoke-Docker {
    param(
        [Parameter(Mandatory = $true)][string]$Context,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    & docker --context $Context @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "docker --context $Context $($Arguments -join ' ') failed."
    }
}

# The address this PC reaches the Docker host on: the SSH host name for an
# ssh:// context, the TCP host for tcp://, the loopback for a local engine.
function Resolve-DockerHostAddress {
    param(
        [Parameter(Mandatory = $true)][string]$Context,
        [string]$HostAddress
    )

    if (-not [string]::IsNullOrWhiteSpace($HostAddress)) {
        return $HostAddress
    }

    $endpoint = (& docker context inspect $Context --format '{{.Endpoints.docker.Host}}').Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "Could not inspect Docker context '$Context'."
    }
    if ([string]::IsNullOrWhiteSpace($endpoint) -or $endpoint -eq '<no value>') {
        throw "Docker context '$Context' does not expose a docker endpoint."
    }

    if ($endpoint.StartsWith('ssh://', [StringComparison]::OrdinalIgnoreCase)) {
        $uri = [Uri]$endpoint
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

    if ($endpoint -match '^tcp://([^:/]+)') {
        return $Matches[1]
    }

    if ($endpoint -match '^(npipe|unix)://') {
        return '127.0.0.1'
    }

    throw "Unsupported Docker endpoint '$endpoint'. Pass -HostAddress explicitly."
}

function Get-PublishedPostgresPort {
    param(
        [Parameter(Mandatory = $true)][string]$Context,
        [Parameter(Mandatory = $true)][string]$ContainerName
    )

    $portOutput = @(Invoke-Docker -Context $Context -Arguments @('port', $ContainerName, '5432/tcp'))
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
    param(
        [Parameter(Mandatory = $true)][string]$Context,
        [Parameter(Mandatory = $true)][string]$ContainerName,
        [string]$Database = 'postgres'
    )

    $deadline = (Get-Date).AddSeconds(60)
    do {
        # Over TCP, not the local socket: while the image runs its first-time
        # set-up it answers on the socket only, and clients connect over TCP.
        & docker --context $Context exec $ContainerName pg_isready -h 127.0.0.1 -U postgres -d $Database *> $null
        if ($LASTEXITCODE -eq 0) {
            return
        }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)

    Invoke-Docker -Context $Context -Arguments @('logs', '--tail', '80', $ContainerName) | Out-Host
    throw "Postgres container '$ContainerName' did not become ready within 60 seconds."
}

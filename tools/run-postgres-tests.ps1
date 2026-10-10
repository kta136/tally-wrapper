[CmdletBinding()]
param(
    [string]$DockerContext = 'proxmox-docker',
    [string]$Image = 'postgres:18',
    [string]$ContainerName = "tw-postgres-tests-$([Guid]::NewGuid().ToString('N').Substring(0, 12))",
    [string]$PostgresPassword = 'postgres',
    [string]$HostAddress,
    [string]$TestProject = (Join-Path $PSScriptRoot '..\tests\ShowroomBilling.Tests\ShowroomBilling.Tests.csproj'),
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'PostgresContainer.ps1')

$containerStarted = $false
$previousRunFlag = $env:SHOWROOM_BILLING_RUN_POSTGRES_TESTS
$previousConnection = $env:SHOWROOM_BILLING_POSTGRES_TEST_CONNECTION

try {
    $resolvedHost = Resolve-DockerHostAddress -Context $DockerContext -HostAddress $HostAddress

    Write-Host "Starting $Image on Docker context '$DockerContext'..." -ForegroundColor Cyan
    $containerId = (Invoke-Docker -Context $DockerContext -Arguments @(
        'run', '-d', '--rm',
        '--name', $ContainerName,
        '-e', "POSTGRES_PASSWORD=$PostgresPassword",
        '-p', '0:5432',
        $Image
    )).Trim()
    $containerStarted = $true

    $port = Get-PublishedPostgresPort -Context $DockerContext -ContainerName $ContainerName
    Wait-ForPostgres -Context $DockerContext -ContainerName $ContainerName

    $connectionString = "Host=$resolvedHost;Port=$port;Database=postgres;Username=postgres;Password=$PostgresPassword"
    Write-Host "Running Postgres tests against $resolvedHost`:$port (container $containerId)..." -ForegroundColor Cyan

    $env:SHOWROOM_BILLING_RUN_POSTGRES_TESTS = '1'
    $env:SHOWROOM_BILLING_POSTGRES_TEST_CONNECTION = $connectionString

    $testArgs = @('test', '--project', $TestProject, '--filter', 'Category=Postgres')
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

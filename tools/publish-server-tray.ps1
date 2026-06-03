[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\publish\server')
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$trayProj = Join-Path $repoRoot 'src\ShowroomBilling.ServerTray\ShowroomBilling.ServerTray.csproj'
$apiProj = Join-Path $repoRoot 'src\ShowroomBilling.Api\ShowroomBilling.Api.csproj'
$apiStaging = Join-Path $repoRoot 'publish\.server-api-embedded'
$embeddedDir = Join-Path $repoRoot 'src\ShowroomBilling.ServerTray\Resources\Embedded'
$embeddedApi = Join-Path $embeddedDir 'ShowroomBilling.Api.exe'
$resolvedOutput = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)

foreach ($path in @($resolvedOutput, $apiStaging)) {
    if (Test-Path $path) {
        Remove-Item -Recurse -Force $path
    }
}
if (Test-Path $embeddedApi) { Remove-Item -Force $embeddedApi }

New-Item -ItemType Directory -Force -Path $embeddedDir | Out-Null

$apiPublishArgs = @(
    '-c', $Configuration,
    '-r', $Runtime,
    '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    '-p:PublishReadyToRun=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true',
    '-p:ShowroomBillingExcludeAppSettings=true',
    '-p:DebugType=embedded'
)

dotnet publish $apiProj @apiPublishArgs -o $apiStaging
if ($LASTEXITCODE -ne 0) { throw 'Embedded API publish failed.' }

$stagedApi = Join-Path $apiStaging 'ShowroomBilling.Api.exe'
if (-not (Test-Path $stagedApi)) { throw "Expected $stagedApi but it was not produced." }
Copy-Item -Force $stagedApi $embeddedApi

$publishArgs = @(
    '-c', $Configuration,
    '-r', $Runtime,
    '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    '-p:PublishReadyToRun=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true',
    '-p:DebugType=embedded'
)

dotnet publish $trayProj @publishArgs -o $resolvedOutput
if ($LASTEXITCODE -ne 0) { throw 'Server tray publish failed.' }

$serverExe = Join-Path $resolvedOutput 'ShowroomBilling.Server.exe'
$publishedTray = Join-Path $resolvedOutput 'ShowroomBilling.ServerTray.exe'
if (-not (Test-Path $publishedTray)) { throw "Expected $publishedTray but it was not produced." }
Move-Item -Force $publishedTray $serverExe

Get-ChildItem -Path $resolvedOutput -Force |
    Where-Object { $_.FullName -ne $serverExe } |
    Remove-Item -Recurse -Force

Remove-Item -Recurse -Force $apiStaging
Remove-Item -Force $embeddedApi

Write-Host "Published one-file server installer/tray to $serverExe"
Write-Host "Run ShowroomBilling.Server.exe on the Tally server. It installs/repairs the API service and keeps the tray running."

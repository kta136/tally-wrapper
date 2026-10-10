[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\publish\server')
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'PublishCommon.ps1')
$repoRoot = Split-Path -Parent $PSScriptRoot
$trayProj = Join-Path $repoRoot 'src\ShowroomBilling.ServerTray\ShowroomBilling.ServerTray.csproj'
$apiProj = Join-Path $repoRoot 'src\ShowroomBilling.Api\ShowroomBilling.Api.csproj'
$apiStaging = Join-Path $repoRoot 'publish\.server-api-embedded'
$embeddedDir = Join-Path $repoRoot 'src\ShowroomBilling.ServerTray\Resources\Embedded'
$embeddedApi = Join-Path $embeddedDir 'ShowroomBilling.Api.exe'
$resolvedOutput = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)

foreach ($path in @($resolvedOutput, $apiStaging)) {
    Remove-PublishFolder -Path $path -RepoRoot $repoRoot
}
if (Test-Path $embeddedApi) { Remove-Item -Force $embeddedApi }

New-Item -ItemType Directory -Force -Path $embeddedDir | Out-Null

$apiPublishArgs = Get-PublishArguments -Configuration $Configuration -Runtime $Runtime -SingleFile -ExcludeAppSettings

dotnet publish $apiProj @apiPublishArgs -o $apiStaging
if ($LASTEXITCODE -ne 0) { throw 'Embedded API publish failed.' }

$stagedApi = Join-Path $apiStaging 'ShowroomBilling.Api.exe'
if (-not (Test-Path $stagedApi)) { throw "Expected $stagedApi but it was not produced." }
Copy-Item -Force $stagedApi $embeddedApi

$publishArgs = Get-PublishArguments -Configuration $Configuration -Runtime $Runtime -SingleFile

dotnet publish $trayProj @publishArgs -o $resolvedOutput
if ($LASTEXITCODE -ne 0) { throw 'Server tray publish failed.' }

$serverExe = Join-Path $resolvedOutput 'TallyWrapper.Server.exe'
$publishedTray = Join-Path $resolvedOutput 'TallyWrapper.ServerTray.exe'
if (-not (Test-Path $publishedTray)) { throw "Expected $publishedTray but it was not produced." }
Move-Item -Force $publishedTray $serverExe

Get-ChildItem -Path $resolvedOutput -Force |
    Where-Object { $_.FullName -ne $serverExe } |
    Remove-Item -Recurse -Force

Remove-PublishFolder -Path $apiStaging -RepoRoot $repoRoot
Remove-Item -Force $embeddedApi

Write-Host "Published one-file server installer/tray to $serverExe"
Write-Host "Run TallyWrapper.Server.exe on the Tally server. It installs/repairs the API service and keeps the tray running."

[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\publish\server\api')
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$apiProj = Join-Path $repoRoot 'src\ShowroomBilling.Api\ShowroomBilling.Api.csproj'
$resolvedOutput = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)

if (Test-Path $resolvedOutput) {
    Remove-Item -Recurse -Force $resolvedOutput
}

$publishArgs = @(
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

dotnet publish $apiProj @publishArgs -o $resolvedOutput
if ($LASTEXITCODE -ne 0) { throw 'API publish failed.' }

$exePath = Join-Path $resolvedOutput 'ShowroomBilling.Api.exe'
if (-not (Test-Path $exePath)) {
    throw "Expected $exePath but it was not produced."
}

Get-ChildItem -Path $resolvedOutput -Force |
    Where-Object { $_.FullName -ne $exePath } |
    Remove-Item -Recurse -Force

$leftovers = Get-ChildItem -Path $resolvedOutput -Force
if ($leftovers.Count -ne 1 -or $leftovers[0].FullName -ne $exePath) {
    throw "Server API publish should contain only ShowroomBilling.Api.exe."
}

Write-Host "Published sanitized server API to $resolvedOutput"

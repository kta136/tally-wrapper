[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\publish\server\api')
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'PublishCommon.ps1')
$repoRoot = Split-Path -Parent $PSScriptRoot
$apiProj = Join-Path $repoRoot 'src\ShowroomBilling.Api\ShowroomBilling.Api.csproj'
$resolvedOutput = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)

Remove-PublishFolder -Path $resolvedOutput -RepoRoot $repoRoot

$publishArgs = Get-PublishArguments -Configuration $Configuration -Runtime $Runtime -SingleFile -ExcludeAppSettings

dotnet publish $apiProj @publishArgs -o $resolvedOutput
if ($LASTEXITCODE -ne 0) { throw 'API publish failed.' }

$exePath = Join-Path $resolvedOutput 'ShowroomBilling.Api.exe'
if (-not (Test-Path $exePath)) {
    throw "Expected $exePath but it was not produced."
}
$publicExePath = Join-Path $resolvedOutput 'TallyWrapper.Api.exe'
Move-Item -Force $exePath $publicExePath

Get-ChildItem -Path $resolvedOutput -Force |
    Where-Object { $_.FullName -ne $publicExePath } |
    Remove-Item -Recurse -Force

$leftovers = Get-ChildItem -Path $resolvedOutput -Force
if ($leftovers.Count -ne 1 -or $leftovers[0].FullName -ne $publicExePath) {
    throw "Server API publish should contain only TallyWrapper.Api.exe."
}

Write-Host "Published sanitized server API to $resolvedOutput"

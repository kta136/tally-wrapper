# Writes the local DPAPI-protected database override used by the API at runtime.
#
# This keeps production DB credentials out of Git and out of published Desktop
# artifacts. Run it once on the Windows user account that will run Billing.exe.
#
# Examples:
#   .\tools\configure-local-db.ps1
#   .\tools\configure-local-db.ps1 -ConnectionString "Host=...;Database=...;Username=...;Password=..."
#   .\tools\configure-local-db.ps1 -Environment Production -SettingsPath .\src\ShowroomBilling.Api\appsettings.Production.json

[CmdletBinding()]
param(
    [string]$Environment = 'Production',
    [string]$ConnectionString,
    [string]$SettingsPath = (Join-Path $PSScriptRoot '..\src\ShowroomBilling.Api\appsettings.Production.json'),
    [string]$AppDataRoot = $env:APPDATA
)

$ErrorActionPreference = 'Stop'

function Normalize-EnvironmentName([string]$name) {
    if ([string]::IsNullOrWhiteSpace($name)) { return 'Production' }

    $invalidChars = [System.IO.Path]::GetInvalidFileNameChars()
    $chars = $name.Trim().ToCharArray() | ForEach-Object {
        if ($invalidChars -contains $_) { '_' } else { $_ }
    }
    -join $chars
}

if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
    $resolvedSettingsPath = Resolve-Path $SettingsPath -ErrorAction SilentlyContinue
    if (-not $resolvedSettingsPath) {
        throw "No connection string supplied and settings file was not found at $SettingsPath."
    }

    $settings = Get-Content -Raw $resolvedSettingsPath | ConvertFrom-Json
    $ConnectionString = [string]$settings.ConnectionStrings.Postgres
}

if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
    throw 'PostgreSQL connection string is required.'
}

if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) {
    throw 'DPAPI local database settings require Windows.'
}

if ([string]::IsNullOrWhiteSpace($AppDataRoot)) {
    throw 'APPDATA is not set. Provide -AppDataRoot explicitly.'
}

Add-Type -AssemblyName System.Security

$environmentName = Normalize-EnvironmentName $Environment
$configDir = Join-Path $AppDataRoot 'ShowroomBilling'
$configPath = Join-Path $configDir "database.$environmentName.local.json"

New-Item -ItemType Directory -Force -Path $configDir | Out-Null

$bytes = [System.Text.Encoding]::UTF8.GetBytes($ConnectionString.Trim())
$protected = [System.Security.Cryptography.ProtectedData]::Protect(
    $bytes,
    $null,
    [System.Security.Cryptography.DataProtectionScope]::CurrentUser)

$payload = [ordered]@{
    version = 1
    environment = $environmentName
    protection = 'windows-dpapi-current-user'
    connectionStringProtected = [Convert]::ToBase64String($protected)
}

$payload | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 $configPath

Write-Host "Wrote DPAPI-protected $environmentName database config to $configPath"

# Publish the WPF Desktop production client with a sanitized embedded API.
#
# IMPORTANT: this script must not embed production database credentials in the
# Desktop artifact. The Desktop package may contain the API binaries, but never
# a production database connection string.
#
# Pipeline:
#   1. Publish a sanitized API payload. The staged API appsettings.json has an
#      empty ConnectionStrings:Postgres value; runtime DB config must come from
#      env vars or the DPAPI local override.
#   2. Zip the sanitized API into the Desktop embedded resource.
#   3. Publish Desktop self-contained, single-file, win-x64.
#   4. Emit publish/prod/appsettings.Production.json with only non-secret Desktop
#      settings: ApiBaseUrl and child-process mode.
#   5. Output: publish/prod/Billing.exe + appsettings.Production.json.
#
# Examples:
#   .\tools\publish-prod.ps1
#   .\tools\publish-prod.ps1 -NoEmbeddedApi -DesktopApiBaseUrl http://showroom-server:5107

[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$DesktopApiBaseUrl = 'http://127.0.0.1:5107',
    [switch]$NoEmbeddedApi,
    [switch]$KeepStaging
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$publishRoot = Join-Path $repoRoot 'publish\prod'
$apiStaging = Join-Path $repoRoot 'publish\.api-staging'
$embeddedDir = Join-Path $repoRoot 'src\ShowroomBilling.Desktop\Resources\Embedded'
$embeddedZip = Join-Path $embeddedDir 'api.zip'
$desktopOut = $publishRoot

$apiProj = Join-Path $repoRoot 'src\ShowroomBilling.Api\ShowroomBilling.Api.csproj'
$desktopProj = Join-Path $repoRoot 'src\ShowroomBilling.Desktop\ShowroomBilling.Desktop.csproj'
$embedApi = -not [bool]$NoEmbeddedApi

foreach ($d in @($publishRoot, $apiStaging)) {
    if (Test-Path $d) {
        Write-Host "Cleaning $d" -ForegroundColor Yellow
        Remove-Item -Recurse -Force $d
    }
}
if (Test-Path $embeddedZip) { Remove-Item -Force $embeddedZip }

if ($embedApi) {
    New-Item -ItemType Directory -Force -Path $embeddedDir | Out-Null

    # --- 1. Publish sanitized API to staging -----------------------------------
    Write-Host "`n=== Publishing sanitized API → $apiStaging ===" -ForegroundColor Cyan
    # PublishReadyToRun pre-JITs IL to native code at publish time, eliminating most
    # JIT cost on first run. R2R coexists with single-file (the precompiled native
    # sections live alongside the IL inside the assembly).
    $apiPublishArgs = @(
        "-c", $Configuration,
        "-r", $Runtime,
        "--self-contained", "true",
        "-p:PublishSingleFile=false",
        "-p:PublishReadyToRun=true",
        "-p:DebugType=embedded"
    )
    & dotnet publish $apiProj @apiPublishArgs -o $apiStaging
    if ($LASTEXITCODE -ne 0) { throw "API publish failed." }

    # Never embed production DB credentials in the Desktop artifact. The API can
    # still be configured at runtime via SHOWROOMBILLING_CONNECTIONSTRINGS__POSTGRES
    # or the app's DPAPI-protected local database override.
    $stagedApiSettingsPath = Join-Path $apiStaging 'appsettings.json'
    $apiSettings = Get-Content -Raw $stagedApiSettingsPath | ConvertFrom-Json
    if ($null -eq $apiSettings.ConnectionStrings) {
        $apiSettings | Add-Member -NotePropertyName ConnectionStrings -NotePropertyValue ([pscustomobject]@{})
    }
    $apiSettings.ConnectionStrings | Add-Member -Force -NotePropertyName Postgres -NotePropertyValue ''
    if ($null -eq $apiSettings.Database) {
        $apiSettings | Add-Member -NotePropertyName Database -NotePropertyValue ([pscustomobject]@{})
    }
    $apiSettings.Database | Add-Member -Force -NotePropertyName AutoMigrateOnStartup -NotePropertyValue $false
    $apiSettings | ConvertTo-Json -Depth 20 | Set-Content -Encoding UTF8 $stagedApiSettingsPath

    $devLeftover = Join-Path $apiStaging 'appsettings.Development.json'
    if (Test-Path $devLeftover) { Remove-Item $devLeftover -Force }
    $prodLeftover = Join-Path $apiStaging 'appsettings.Production.json'
    if (Test-Path $prodLeftover) { Remove-Item $prodLeftover -Force }
    Write-Host "Sanitized staged API config; no Postgres connection string embedded." -ForegroundColor Green

    # --- 2. Zip staged API → Desktop's embedded resource ------------------------
    Write-Host "`n=== Zipping sanitized API → $embeddedZip ===" -ForegroundColor Cyan
    Compress-Archive -Path (Join-Path $apiStaging '*') -DestinationPath $embeddedZip -CompressionLevel Optimal -Force
    $apiZipMb = [math]::Round((Get-Item $embeddedZip).Length / 1MB, 1)
    Write-Host "Embedded sanitized API zip: $apiZipMb MB" -ForegroundColor Green
}

# --- 3. Publish Desktop, single-file -------------------------------------------
Write-Host "`n=== Publishing Desktop → $desktopOut ===" -ForegroundColor Cyan
# PublishReadyToRun on the Desktop is the biggest user-felt startup win — pre-JIT
# eliminates the JIT cost on the host build + DI graph compile + WPF static init,
# all of which run before window.Show(). Validate via the [startup-timing] log
# entry (in `%APPDATA%\ShowroomBilling\logs\desktop-*.log`): expect
# `hostBuild` and `resolveWindow` to drop noticeably vs a non-R2R prod build.
# R2R is compatible with PublishSingleFile + EnableCompressionInSingleFile.
$desktopPublishArgs = @(
    "-c", $Configuration,
    "-r", $Runtime,
    "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-p:PublishReadyToRun=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true",
    "-p:DebugType=embedded"
)
& dotnet publish $desktopProj @desktopPublishArgs -o $desktopOut
if ($LASTEXITCODE -ne 0) { throw "Desktop publish failed." }

# --- 4. Write non-secret runtime Desktop override -------------------------------
$desktopProdSettings = [ordered]@{
    DesktopBootstrap = [ordered]@{
        ApiBaseUrl = $DesktopApiBaseUrl
        StartupMode = 'OnlineOnly'
    }
    ChildProcesses = [ordered]@{
        Enabled = $embedApi
        Api = [ordered]@{
            Enabled = $embedApi
        }
    }
}
$desktopProdSettingsPath = Join-Path $desktopOut 'appsettings.Production.json'
$desktopProdSettings | ConvertTo-Json -Depth 20 | Set-Content -Encoding UTF8 $desktopProdSettingsPath
Write-Host "Wrote non-secret Desktop production config: $desktopProdSettingsPath" -ForegroundColor Green

# --- 5. Tidy up ----------------------------------------------------------------
# Final layout in publish/prod/:
#   Billing.exe                  — the single-file Desktop exe
#   appsettings.Production.json  — non-secret API URL + child-process mode
#   Billing.deps.json            — .NET runtime: assembly resolution manifest, required
#   Billing.runtimeconfig.json   — .NET runtime: framework/runtime options, required
# The Desktop's baseline appsettings.json is an EmbeddedResource and is loaded via
# AddJsonStream in App.OnStartup. The .pdb is already gone via DebugType=embedded;
# the LatoFont folder is removed below.

if (-not $KeepStaging) {
    if (Test-Path $apiStaging) { Remove-Item -Recurse -Force $apiStaging }
    if (Test-Path $embeddedZip) { Remove-Item -Force $embeddedZip }
}

# Belt-and-braces: the Desktop csproj has a RemoveLatoFontFromPublish target that
# already deletes this folder, but if a future build skips that target (or a
# different SDK alters target ordering) we still want the published artifact
# stripped. The print template uses Fonts.Calibri (Windows-native), so QuestPDF's
# Lato fallback bundle is dead weight here.
$publishedLato = Join-Path $desktopOut 'LatoFont'
if (Test-Path $publishedLato) { Remove-Item -Recurse -Force $publishedLato }
$publishedDevAppSettings = Join-Path $desktopOut 'appsettings.Development.json'
if (Test-Path $publishedDevAppSettings) { Remove-Item -Force $publishedDevAppSettings }
# appsettings.json is now an EmbeddedResource (loaded via AddJsonStream), so dotnet
# publish should not emit it. Defensive cleanup in case a future SDK change or a
# stale incremental copy puts one back next to the exe — we always want the prod
# folder to be exe + the two runtime .json files only.
$publishedAppSettings = Join-Path $desktopOut 'appsettings.json'
if (Test-Path $publishedAppSettings) { Remove-Item -Force $publishedAppSettings }

# --- Sanity report --------------------------------------------------------------
$desktopExe = Join-Path $desktopOut 'Billing.exe'
if (-not (Test-Path $desktopExe)) { throw "Expected $desktopExe but it wasn't produced." }
$desktopMb = [math]::Round((Get-Item $desktopExe).Length / 1MB, 1)

Write-Host "`nBuild complete." -ForegroundColor Green
Write-Host "  Single-exe : $desktopExe ($desktopMb MB)"
Write-Host "  API URL    : $DesktopApiBaseUrl"
if ($embedApi) {
    Write-Host "  Embedded API is sanitized and contains no Postgres connection string."
} else {
    Write-Host "  Embedded API: disabled."
}
Write-Host "`nDouble-click the Desktop exe to run."

# Publish the WPF Desktop client: one self-contained, single-file exe.
#
# The Desktop carries no API and no database settings. It connects to the
# Tally Wrapper server, whose installer is built by publish-server-tray.ps1;
# the server address is set on each workstation in Settings -> Server.
#
# Output: publish/prod/TallyWrapper.exe
#
# Example:
#   .\tools\publish-prod.ps1

[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'PublishCommon.ps1')
$repoRoot = Split-Path -Parent $PSScriptRoot
$desktopOut = Join-Path $repoRoot 'publish\prod'
$desktopProj = Join-Path $repoRoot 'src\ShowroomBilling.Desktop\ShowroomBilling.Desktop.csproj'

if (Test-Path $desktopOut) {
    Write-Host "Cleaning $desktopOut" -ForegroundColor Yellow
}
Remove-PublishFolder -Path $desktopOut -RepoRoot $repoRoot

# --- Publish Desktop, single-file ------------------------------------------------
Write-Host "`n=== Publishing Desktop → $desktopOut ===" -ForegroundColor Cyan
# PublishReadyToRun on the Desktop is the biggest user-felt startup win — pre-JIT
# eliminates the JIT cost on the host build + DI graph compile + WPF static init,
# all of which run before window.Show(). Validate via the [startup-timing] log
# entry (in `%APPDATA%\ShowroomBilling\logs\desktop-*.log`): expect
# `hostBuild` and `resolveWindow` to drop noticeably vs a non-R2R prod build.
# R2R is compatible with PublishSingleFile + EnableCompressionInSingleFile.
$desktopPublishArgs = Get-PublishArguments -Configuration $Configuration -Runtime $Runtime -SingleFile
& dotnet publish $desktopProj @desktopPublishArgs -o $desktopOut
if ($LASTEXITCODE -ne 0) { throw "Desktop publish failed." }

# --- Tidy up ----------------------------------------------------------------------
# Final layout in publish/prod/:
#   TallyWrapper.exe                  — the single-file Desktop exe
#   TallyWrapper.deps.json            — .NET runtime: assembly resolution manifest, required
#   TallyWrapper.runtimeconfig.json   — .NET runtime: framework/runtime options, required
# The Desktop's baseline appsettings.json is an EmbeddedResource and is loaded via
# AddJsonStream in App.OnStartup. The .pdb is already gone via DebugType=embedded;
# the LatoFont folder is removed below.

# Belt-and-braces: the Desktop csproj has a RemoveLatoFontFromPublish target that
# already deletes this folder, but if a future build skips that target (or a
# different SDK alters target ordering) we still want the published artifact
# stripped. The print template uses Fonts.Calibri (Windows-native), so QuestPDF's
# Lato fallback bundle is dead weight here.
$publishedLato = Join-Path $desktopOut 'LatoFont'
if (Test-Path $publishedLato) { Remove-Item -Recurse -Force $publishedLato }
$publishedDevAppSettings = Join-Path $desktopOut 'appsettings.Development.json'
if (Test-Path $publishedDevAppSettings) { Remove-Item -Force $publishedDevAppSettings }
$publishedProdAppSettings = Join-Path $desktopOut 'appsettings.Production.json'
if (Test-Path $publishedProdAppSettings) { Remove-Item -Force $publishedProdAppSettings }
# appsettings.json is now an EmbeddedResource (loaded via AddJsonStream), so dotnet
# publish should not emit it. Defensive cleanup in case a future SDK change or a
# stale incremental copy puts one back next to the exe — we always want the prod
# folder to be exe + the two runtime .json files only.
$publishedAppSettings = Join-Path $desktopOut 'appsettings.json'
if (Test-Path $publishedAppSettings) { Remove-Item -Force $publishedAppSettings }

# --- Sanity report ------------------------------------------------------------------
$desktopExe = Join-Path $desktopOut 'TallyWrapper.exe'
if (-not (Test-Path $desktopExe)) { throw "Expected $desktopExe but it wasn't produced." }
$desktopMb = [math]::Round((Get-Item $desktopExe).Length / 1MB, 1)

Write-Host "`nBuild complete." -ForegroundColor Green
Write-Host "  Single-exe : $desktopExe ($desktopMb MB)"
Write-Host "`nCopy it to a workstation and set the server address in Settings -> Server."

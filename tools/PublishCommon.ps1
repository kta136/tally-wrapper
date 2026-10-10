# Shared by the publish scripts. Dot-source it:
#   . (Join-Path $PSScriptRoot 'PublishCommon.ps1')

function Get-PublishArguments {
    <#
    .SYNOPSIS
    The `dotnet publish` switches every shipped executable shares.

    .DESCRIPTION
    Self-contained, ReadyToRun, debug info embedded. ReadyToRun lives here and
    nowhere else on purpose: it has to be on (or off) for the API, the Desktop
    and the server tray together, or start-up timings stop being comparable.
    #>
    param(
        [Parameter(Mandatory)][string]$Configuration,
        [Parameter(Mandatory)][string]$Runtime,
        # One compressed .exe. Without it the output is a folder of files.
        [switch]$SingleFile,
        # Leave appsettings*.json out of the package (the server API is configured by its installer).
        [switch]$ExcludeAppSettings
    )

    $arguments = @(
        '-c', $Configuration,
        '-r', $Runtime,
        '--self-contained', 'true',
        '-p:PublishReadyToRun=true',
        '-p:DebugType=embedded'
    )
    if ($SingleFile) {
        $arguments += @(
            '-p:PublishSingleFile=true',
            '-p:IncludeNativeLibrariesForSelfExtract=true',
            '-p:EnableCompressionInSingleFile=true'
        )
    }
    else {
        $arguments += '-p:PublishSingleFile=false'
    }
    if ($ExcludeAppSettings) {
        $arguments += '-p:ShowroomBillingExcludeAppSettings=true'
    }
    return $arguments
}

function Remove-PublishFolder {
    <#
    .SYNOPSIS
    Deletes a publish output or staging folder, and only that.

    .DESCRIPTION
    The publish scripts start by deleting their output folder. This refuses any
    path that is not strictly inside the repository's `publish` folder, so a
    mistyped -OutputPath cannot delete something else.
    #>
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$RepoRoot
    )

    $separator = [IO.Path]::DirectorySeparatorChar
    $publishRoot = [IO.Path]::GetFullPath((Join-Path $RepoRoot 'publish')).TrimEnd($separator) + $separator
    $target = [IO.Path]::GetFullPath($Path).TrimEnd($separator)
    if (-not ($target + $separator).StartsWith($publishRoot, [StringComparison]::OrdinalIgnoreCase) `
        -or ($target + $separator) -eq $publishRoot) {
        throw "Refusing to delete '$target': a publish folder must be inside '$publishRoot'."
    }

    if (Test-Path -LiteralPath $target) {
        Remove-Item -LiteralPath $target -Recurse -Force
    }
}

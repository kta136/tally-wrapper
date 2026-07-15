param(
    [double]$MinimumLinePercent = 27,
    [string]$Configuration = "Release",
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$resultsDirectory = Join-Path $repoRoot "artifacts\coverage"
$resolvedRepoRoot = [IO.Path]::GetFullPath($repoRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$resolvedResults = [IO.Path]::GetFullPath($resultsDirectory)
if (-not $resolvedResults.StartsWith($resolvedRepoRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Coverage results directory resolved outside the repository: $resolvedResults"
}
if (Test-Path -LiteralPath $resolvedResults) {
    Remove-Item -LiteralPath $resolvedResults -Recurse -Force
}
New-Item -ItemType Directory -Path $resolvedResults | Out-Null

$testArguments = @(
    "test",
    (Join-Path $repoRoot "ShowroomBilling.sln"),
    "--configuration", $Configuration,
    '--collect:XPlat Code Coverage',
    "--results-directory", $resolvedResults,
    "--logger", "trx"
)
if ($NoBuild) {
    $testArguments += "--no-build"
}
& dotnet @testArguments
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$reports = Get-ChildItem -LiteralPath $resolvedResults -Recurse -Filter "coverage.cobertura.xml"
if ($reports.Count -eq 0) {
    throw "No Cobertura coverage reports were produced."
}

$covered = 0
$valid = 0
foreach ($report in $reports) {
    [xml]$document = Get-Content -LiteralPath $report.FullName
    $covered += [int]$document.coverage.'lines-covered'
    $valid += [int]$document.coverage.'lines-valid'
}
if ($valid -le 0) {
    throw "Coverage reports did not contain any valid source lines."
}

$percent = 100.0 * $covered / $valid
Write-Host ("Aggregate line coverage: {0:N2}% ({1}/{2})" -f $percent, $covered, $valid)
if ($percent + 0.0001 -lt $MinimumLinePercent) {
    throw ("Line coverage {0:N2}% is below the required {1:N2}%." -f $percent, $MinimumLinePercent)
}

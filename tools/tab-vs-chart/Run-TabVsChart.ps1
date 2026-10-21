#Requires -Version 7.0
<#
.SYNOPSIS
    Runs tools/tab-vs-chart/TabVsChart.cs: Guitar Pro import vs the owner's finished charts (#91, level 2a).

.DESCRIPTION
    Every source comes from an environment variable (or a parameter); nothing names a
    machine path:
        DOTCHARTER_CDLC_CSV             the CustomsForge "my CDLC" CSV export
        DOTCHARTER_TABS_DIR             the Guitar Pro tab corpus
        DOTCHARTER_CHART_PROJECTS_DIR   the EOF / RS2 XML project folders ("Artist - Title")
        DOTCHARTER_TAB_CHART_OVERRIDES  the manual match file (default: DotCharter-local\level2-overrides.tsv)
        DOTCHARTER_LEVEL2_REPORT_DIR    the report (default: DotCharter-local\level2-report)
        DOTCHARTER_LEVEL2_FOCUS         artists compared and reported first, ';'-separated
    The defaults sit beside the MAIN checkout (also from a worktree), outside the repository.
    The program drops itself to idle priority on CPUs 8-15; start this script hidden and low:

        cmd //c start '""' //b //low //wait //affinity FF00 pwsh -NoProfile -File tools/tab-vs-chart/Run-TabVsChart.ps1

    Console output also lands in <out>\run.log.
#>
param(
    [string]$Csv = $env:DOTCHARTER_CDLC_CSV,
    [string]$Tabs = $env:DOTCHARTER_TABS_DIR,
    [string]$Charts = $env:DOTCHARTER_CHART_PROJECTS_DIR,
    [string]$Overrides = $env:DOTCHARTER_TAB_CHART_OVERRIDES,
    [string]$Out = $env:DOTCHARTER_LEVEL2_REPORT_DIR,
    [string]$Focus = $env:DOTCHARTER_LEVEL2_FOCUS,
    [string]$Filter,
    [int]$Parallel = 4,
    [switch]$MatchOnly
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
foreach ($pair in @(@('DOTCHARTER_CDLC_CSV', $Csv), @('DOTCHARTER_TABS_DIR', $Tabs), @('DOTCHARTER_CHART_PROJECTS_DIR', $Charts))) {
    if (-not $pair[1] -or -not (Test-Path -LiteralPath $pair[1])) { throw "Set $($pair[0]) (or pass the parameter)." }
}

# The MAIN checkout's sibling, also from a worktree: <main>\.git is the common dir.
$common = (git -C $repo rev-parse --path-format=absolute --git-common-dir).Trim()
$local = Join-Path (Split-Path -Parent (Split-Path -Parent $common)) 'DotCharter-local'
if (-not $Out) { $Out = Join-Path $local 'level2-report' }
if (-not $Overrides) { $Overrides = Join-Path $local 'level2-overrides.tsv' }

$full = [IO.Path]::GetFullPath($Out)
if ($full.StartsWith([IO.Path]::GetFullPath($repo), [StringComparison]::OrdinalIgnoreCase)) {
    throw "The report must stay outside the repository: $full"
}

New-Item -ItemType Directory -Force -Path $full | Out-Null
$importer = (git -C $repo log -1 --format='%h %s').Trim()
$arguments = @('run', (Join-Path $PSScriptRoot 'TabVsChart.cs'), '--', '--csv', $Csv, '--tabs', $Tabs, '--charts', $Charts,
    '--out', $full, '--overrides', $Overrides, '--parallel', $Parallel)
if ($Focus) { $arguments += @('--focus', $Focus) }
if ($Filter) { $arguments += @('--filter', $Filter) }
if ($MatchOnly) { $arguments += '--match-only' }

"importer: $importer" | Tee-Object -FilePath (Join-Path $full 'run.log')
& dotnet @arguments *>&1 | Tee-Object -FilePath (Join-Path $full 'run.log') -Append
exit $LASTEXITCODE

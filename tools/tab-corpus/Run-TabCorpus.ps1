#Requires -Version 7.0
<#
.SYNOPSIS
    Runs tools/tab-corpus/TabCorpusReport.cs over the local Guitar Pro corpus.

.DESCRIPTION
    The corpus folder comes from DOTCHARTER_TABS_DIR (or -Tabs); nothing names a
    machine path. The report goes OUTSIDE the repository: -Out, else
    DOTCHARTER_TAB_REPORT_DIR, else DotCharter-local\tab-corpus-report beside the
    main checkout. The program itself drops to idle priority on CPUs 8-15; start
    this script hidden and low, e.g. from Git Bash:

        cmd //c start '""' //b //low //wait //affinity FF00 pwsh -NoProfile -File tools/tab-corpus/Run-TabCorpus.ps1

    Console output also lands in <out>\run.log.

.PARAMETER Parallel
    Files probed at once (default 6).

.PARAMETER Limit
    Probe only the first N files (sorted by path) — a smoke run.

.PARAMETER NoRoundTrip
    Skip the Guitar Pro export + re-import of every track.
#>
param(
    [string]$Tabs = $env:DOTCHARTER_TABS_DIR,
    [string]$Out = $env:DOTCHARTER_TAB_REPORT_DIR,
    [int]$Parallel = 6,
    [int]$Limit = 0,
    [switch]$NoRoundTrip
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $Tabs -or -not (Test-Path -LiteralPath $Tabs)) {
    throw 'Set DOTCHARTER_TABS_DIR (or pass -Tabs) to the Guitar Pro corpus folder.'
}

if (-not $Out) {
    # The MAIN checkout's sibling, also from a worktree: <main>\.git is the common dir.
    $common = (git -C $repo rev-parse --path-format=absolute --git-common-dir).Trim()
    $main = Split-Path -Parent $common
    $Out = Join-Path (Split-Path -Parent $main) 'DotCharter-local\tab-corpus-report'
}

$full = [IO.Path]::GetFullPath($Out)
if ($full.StartsWith([IO.Path]::GetFullPath($repo), [StringComparison]::OrdinalIgnoreCase)) {
    throw "The report must stay outside the repository: $full"
}

New-Item -ItemType Directory -Force -Path $full | Out-Null
$arguments = @('run', (Join-Path $PSScriptRoot 'TabCorpusReport.cs'), '--', '--tabs', $Tabs, '--out', $full, '--parallel', $Parallel)
if ($Limit -gt 0) { $arguments += @('--limit', $Limit) }
if ($NoRoundTrip) { $arguments += '--no-roundtrip' }

& dotnet @arguments *>&1 | Tee-Object -FilePath (Join-Path $full 'run.log')
exit $LASTEXITCODE

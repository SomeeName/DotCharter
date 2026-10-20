<#
.SYNOPSIS
    Runs the headless-UI suite (`*UiPerfSmokeTests`) as its OWN testhost, the
    way CI and a developer both need it, and grades the result the way the
    release script does.

.DESCRIPTION
    The suite cannot share a testhost with the default gate: Avalonia.Headless
    owns a process-global dispatcher and xunit runs the other collections in
    parallel with `HeadlessUI`, so an unfiltered run fails ~314 of its 330 cases
    and takes an unrelated gate test down with them (root AGENTS.md
    § Validation). It is display-free and GPU-free, so a plain `windows-latest`
    runner can host it — this script is what `.github/workflows/build.yml`'s
    second job calls, and what to run locally after a view/XAML change.

    It mirrors `tools/release/New-Release.ps1`'s Invoke-HeadlessUiGate:

      * a case that dies INSIDE the Avalonia session (`EnsureIsolatedApplication`,
        "You've caused dispatcher loop") never ran its assertions, so its CLASS
        is re-run once in a fresh process; only a real failure blocks.
      * a class the filter DISCOVERS that reports no result at all is re-run the
        same way, and blocks if it still reports nothing — "0 failed" on a
        223-case run of a 330-case suite has happened and read as green.
      * a real failure's class is re-run once in isolation; a case that passes
        there is ORDER-DEPENDENT and is printed loudly but does not block.

    …and adds the two numbers nobody had measured on a runner: wall time and
    the testhosts' PEAK working set, written to the step summary when
    `$env:GITHUB_STEP_SUMMARY` is set and always to stdout.

    The FIRST run is split across -Shards concurrent testhosts
    (tools/ci/HeadlessShards.psm1: discovery first, whole classes balanced by
    measured class time, TRX files merged into headless-ui-run1.trx). A shard
    that dies writes no TRX, so its classes are absent from the merge and go
    through the same re-run-then-block path as any class that never reported.
    Retries and the isolated re-run stay one fresh process each.

.PARAMETER Configuration
    Build configuration the test assembly was built in (Debug locally, Release
    in CI). The assembly must already be BUILT — this script passes --no-build.

.PARAMETER Runtime
    Optional RID (`win-x64`) when the assembly was built with `-r`.

.PARAMETER ResultsDir
    Where the trx/log files go. Default: TestResults/headless-ui under the repo.

.PARAMETER Filter
    The xunit filter. Default is the whole suite; a narrower one is for local
    debugging of one class.

.EXAMPLE
    dotnet build DotCharter.Tests/DotCharter.Tests.csproj
    pwsh tools/ci/Invoke-HeadlessUiCi.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Debug',
    [string]$Runtime = '',
    [string]$ResultsDir = '',
    # xunit v3 simple-filter arguments (Microsoft.Testing.Platform runner):
    # repeat --filter-class to OR several classes; '*' wildcards at either end.
    # The golden screenshots are their own invocation (Invoke-Gate.ps1 -Goldens) and
    # are excluded here by their EXACT class name. Their CI step is still a TODO
    # (docs/agents/ui/target-architecture.md section 5).
    [string[]]$Filter = @('--filter-class', '*UiPerfSmokeTests', '--filter-not-class', 'DotCharter.Tests.Perf.ScreenGoldenUiPerfSmokeTests'),
    # How many concurrent testhosts the FIRST run is split across
    # (tools/ci/HeadlessShards.psm1: whole classes, balanced by measured class
    # time). Retries and isolated re-runs stay one fresh process each. CI passes
    # 2: the private repo's windows-latest runner has 2 vCPUs, and each shard is
    # one busy core plus its own app heap.
    [ValidateRange(1, 16)]
    [int]$Shards = 2
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'HeadlessShards.psm1') -Force
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
$TestProject = Join-Path $RepoRoot 'DotCharter.Tests/DotCharter.Tests.csproj'
if (-not $ResultsDir) { $ResultsDir = Join-Path $RepoRoot 'TestResults/headless-ui' }
New-Item -ItemType Directory -Force -Path $ResultsDir | Out-Null
$ResultsDir = (Resolve-Path $ResultsDir).Path

function Write-Info([string]$Message)  { Write-Host "    $Message" -ForegroundColor Gray }
function Write-Warn2([string]$Message) { Write-Host "    WARNING: $Message" -ForegroundColor Yellow }

function Get-ClassName([string]$TestName) {
    $bare = ($TestName -split '\(', 2)[0]
    if ($bare -match '^(.*)\.[^.]+$') { $Matches[1] } else { $bare }
}

function Read-TrxResults([string]$TrxPath) {
    [xml]$trx = Get-Content -LiteralPath $TrxPath -Raw
    $ns = @{ t = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010' }
    $counters = (Select-Xml -Xml $trx -XPath '//t:ResultSummary/t:Counters' -Namespace $ns).Node
    $results = @((Select-Xml -Xml $trx -XPath '//t:UnitTestResult' -Namespace $ns).Node)

    $failures = foreach ($node in ($results | Where-Object { $_.outcome -eq 'Failed' })) {
        $message = ($node.Output.ErrorInfo.Message    | Out-String).Trim()
        $stack   = ($node.Output.ErrorInfo.StackTrace | Out-String).Trim()
        $frame   = ($stack -split "`n" | Where-Object { $_ -match ':line \d+' } | Select-Object -First 1)
        [pscustomobject]@{
            Name      = [string]$node.testName
            Class     = Get-ClassName ([string]$node.testName)
            Message   = $message
            Frame     = if ($frame) { $frame.Trim() } else { '' }
            # The shared needles (HeadlessShards.psm1), the same the release
            # preflight and Invoke-Gate.ps1 -Headless use.
            IsHarness = Test-HeadlessHarnessFault $message $stack
        }
    }

    [pscustomobject]@{
        Total      = [int]$counters.total
        Passed     = [int]$counters.passed
        Failed     = [int]$counters.failed
        Skipped    = [int]$counters.total - [int]$counters.executed
        Failures   = @($failures)
        RanClasses = @($results | ForEach-Object { Get-ClassName ([string]$_.testName) } | Sort-Object -Unique)
    }
}

function Get-DotnetTestArgs([string[]]$RunFilter, [string]$TrxName) {
    $args = @('test', '--project', $TestProject, '--no-build', '-c', $Configuration) + $RunFilter +
            @('--report-xunit-trx', '--report-xunit-trx-filename', $TrxName,
              '--results-directory', $ResultsDir)
    if ($Runtime) { $args += @('-r', $Runtime) }
    return $args
}

function Get-DotnetTestBaseArgs {
    $args = @('test', '--project', $TestProject, '--no-build', '-c', $Configuration)
    if ($Runtime) { $args += @('-r', $Runtime) }
    return $args
}

# class -> discovered case count. One indented fully-qualified name per line;
# keyed on the SHAPE of the line, not the (localized) banner above it.
function Get-DiscoveredTestCounts([string[]]$RunFilter) {
    return Get-HeadlessDiscovery (Get-DotnetTestBaseArgs) $RunFilter (Join-Path $ResultsDir 'headless-ui-discovery.log')
}

# The first, full run: $Shards concurrent testhosts, their TRX files merged
# into one and read exactly like a single run's. A shard that died writes no
# TRX, so its classes are ABSENT from the merge and the coverage check below
# re-runs them — a crashed shard can never read as green.
function Invoke-HeadlessUiShardedRun([string[]]$RunFilter, [string]$Label, [hashtable]$Discovered) {
    $started = Get-Date
    $weights = Read-HeadlessShardWeights (Join-Path $PSScriptRoot 'headless-shard-weights.json')
    $plan = @(New-HeadlessShardPlan $Discovered $weights $Shards)
    $runs = @(Invoke-HeadlessShards -Plan $plan -DotnetTestArgs (Get-DotnetTestBaseArgs) -ResultsDir $ResultsDir `
        -Label "headless-ui-$Label" -WorkingDirectory $RepoRoot)
    $elapsed = (Get-Date) - $started
    foreach ($r in $runs) {
        Write-Info ('[{0}] shard {1}: {2} classes (est {3:0}s), {4:mm\:ss}, peak RSS {5:0.0} GB, exit {6}{7}' -f `
            $Label, $r.Index, $r.Classes.Count, $r.Weight, $r.Elapsed, ($r.PeakBytes / 1GB), $r.ExitCode,
            $(if ($r.TrxPath) { '' } else { ' - NO RESULTS FILE (its classes count as absent)' }))
    }
    $trxPath = Join-Path $ResultsDir "headless-ui-$Label.trx"
    if (Test-Path $trxPath) { Remove-Item $trxPath -Force }
    if (-not (Merge-HeadlessTrx @($runs | ForEach-Object { $_.TrxPath }) $trxPath)) {
        throw "The headless UI suite produced no results file in ANY shard — the run never started. See $($runs[0].LogPath)."
    }
    $results = Read-TrxResults $trxPath
    $peak = if ($runs.Count -gt 0) { [long]$runs[0].PeakSumBytes } else { [long]0 }
    Add-Member -InputObject $results -NotePropertyName Elapsed -NotePropertyValue $elapsed
    Add-Member -InputObject $results -NotePropertyName PeakRssBytes -NotePropertyValue $peak
    Add-Member -InputObject $results -NotePropertyName LogPath -NotePropertyValue (($runs | ForEach-Object { $_.LogPath }) -join ', ')
    Add-Member -InputObject $results -NotePropertyName Shards -NotePropertyValue $runs
    Write-Info ('[{0}] {1} cases over {2} shard(s): {3} passed, {4} failed, {5} skipped in {6:mm\:ss}; peak RSS of all shards together {7:0.0} GB' -f `
        $Label, $results.Total, $runs.Count, $results.Passed, $results.Failed, $results.Skipped, $elapsed, ($peak / 1GB))
    return $results
}

# One `dotnet test` as a CHILD process, with the peak working set of every
# testhost sampled while it runs. Console output goes to a file: ~8 min of
# xunit noise would bury the lines that matter.
function Invoke-HeadlessUiRun([string[]]$RunFilter, [string]$Label) {
    $trxName = "headless-ui-$Label.trx"
    $trxPath = Join-Path $ResultsDir $trxName
    $logPath = Join-Path $ResultsDir "headless-ui-$Label.log"
    if (Test-Path $trxPath) { Remove-Item $trxPath -Force }

    $started = Get-Date
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = 'dotnet'
    foreach ($a in (Get-DotnetTestArgs $RunFilter $trxName)) { $psi.ArgumentList.Add($a) }
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.WorkingDirectory = $RepoRoot
    $proc = [System.Diagnostics.Process]::Start($psi)
    $stdout = $proc.StandardOutput.ReadToEndAsync()
    $stderr = $proc.StandardError.ReadToEndAsync()

    $peakBytes = [long]0
    while (-not $proc.HasExited) {
        Start-Sleep -Seconds 2
        foreach ($p in (Get-Process -Name 'testhost', 'DotCharter.Tests' -ErrorAction SilentlyContinue)) {
            try { if ($p.WorkingSet64 -gt $peakBytes) { $peakBytes = $p.WorkingSet64 } } catch { }
        }
    }
    $proc.WaitForExit()
    Set-Content -LiteralPath $logPath -Value ($stdout.Result + "`n" + $stderr.Result)
    $elapsed = (Get-Date) - $started

    if (-not (Test-Path $trxPath)) {
        throw "The headless UI suite produced no results file — the run never started. See $logPath."
    }
    $results = Read-TrxResults $trxPath
    Add-Member -InputObject $results -NotePropertyName Elapsed -NotePropertyValue $elapsed
    Add-Member -InputObject $results -NotePropertyName PeakRssBytes -NotePropertyValue $peakBytes
    Add-Member -InputObject $results -NotePropertyName LogPath -NotePropertyValue $logPath
    Write-Info ('[{0}] {1} cases: {2} passed, {3} failed, {4} skipped in {5:mm\:ss}; peak testhost RSS {6:0.0} GB' -f `
        $Label, $results.Total, $results.Passed, $results.Failed, $results.Skipped, $elapsed, ($peakBytes / 1GB))
    return $results
}

function Write-Failure($Failure) {
    Write-Host "    FAILED  $($Failure.Name)" -ForegroundColor Red
    foreach ($line in ($Failure.Message -split "`n" | Select-Object -First 4)) {
        if ($line.Trim()) { Write-Host "            $($line.Trim())" -ForegroundColor Gray }
    }
    if ($Failure.Frame) { Write-Host "            $($Failure.Frame)" -ForegroundColor DarkGray }
}

function Join-ClassFilter($Classes) { [string[]]@($Classes | ForEach-Object { @('--filter-class', $_) }) }

# ---------------------------------------------------------------------------
Write-Host "==> Headless UI suite (own testhost): $($Filter -join ' ')" -ForegroundColor Cyan
$overallStarted = Get-Date
# Discovery FIRST: it is both the shard plan's input and the coverage check's.
$discoveredCounts = Get-DiscoveredTestCounts $Filter
$discovered = @($discoveredCounts.Keys | Sort-Object)
if ($discovered.Count -eq 0) { throw 'test discovery listed no classes — there is nothing to shard and the coverage check would look at nothing.' }
$run = Invoke-HeadlessUiShardedRun -RunFilter $Filter -Label 'run1' -Discovered $discoveredCounts
$peak = [long]$run.PeakRssBytes
$product = @($run.Failures | Where-Object { -not $_.IsHarness })
$harness = @($run.Failures | Where-Object { $_.IsHarness })
$unverified = @()

$absent = @($discovered | Where-Object { @($run.RanClasses) -notcontains $_ })
if ($absent.Count -gt 0) {
    Write-Warn2 "$($absent.Count) DISCOVERED class(es) reported no result at all: $($absent -join ', ')"
}

if ($harness.Count -gt 0 -or $absent.Count -gt 0) {
    $classes = @(@(@($harness | Select-Object -ExpandProperty Class) + $absent) | Sort-Object -Unique)
    Write-Warn2 ("$($harness.Count) case(s) died inside the Avalonia headless session and $($absent.Count) class(es) " +
                 "never reported. Re-running $($classes.Count) class(es) in a fresh process.")
    $retry = Invoke-HeadlessUiRun -RunFilter (Join-ClassFilter $classes) -Label 'retry'
    if ($retry.PeakRssBytes -gt $peak) { $peak = $retry.PeakRssBytes }
    $product += @($retry.Failures | Where-Object { -not $_.IsHarness })
    $unverified = @($retry.Failures | Where-Object { $_.IsHarness } | Select-Object -ExpandProperty Name)
    $stillAbsent = @($absent | Where-Object { @($retry.RanClasses) -notcontains $_ })
    foreach ($class in $stillAbsent) {
        $product += [pscustomobject]@{
            Name = $class; Class = $class; Frame = ''; IsHarness = $false
            Message = 'DISCOVERED but never executed, in the full run AND in its own retry process. Nothing in this class was graded.'
        }
    }
}

$orderDependent = @()
$orderCandidates = @($product | Where-Object { $_.Frame -ne '' -or $_.Class -ne $_.Name })
if ($orderCandidates.Count -gt 0) {
    $classes = @($orderCandidates | Select-Object -ExpandProperty Class | Sort-Object -Unique)
    Write-Warn2 "$($orderCandidates.Count) case(s) FAILED their assertions. Re-running $($classes.Count) class(es) in isolation."
    $isolated = Invoke-HeadlessUiRun -RunFilter (Join-ClassFilter $classes) -Label 'isolated'
    if ($isolated.PeakRssBytes -gt $peak) { $peak = $isolated.PeakRssBytes }
    $isolatedFailed = @($isolated.Failures | Select-Object -ExpandProperty Name)
    $orderDependent = @($orderCandidates | Where-Object {
        (@($isolated.RanClasses) -contains $_.Class) -and ($isolatedFailed -notcontains $_.Name) })
    if ($orderDependent.Count -gt 0) {
        $names = @($orderDependent | Select-Object -ExpandProperty Name)
        Write-Warn2 ("$($names.Count) case(s) passed in a fresh process and are ORDER-DEPENDENT in the full run — " +
                     "not blocking, but a class earlier in the run leaves state behind: $($names -join ', ')")
        $product = @($product | Where-Object { $names -notcontains $_.Name })
    }
}

$overallElapsed = (Get-Date) - $overallStarted
$summary = @(
    "## Headless UI suite",
    "",
    "| | |",
    "|---|---|",
    ("| cases | {0} ({1} passed, {2} failed, {3} skipped) |" -f $run.Total, $run.Passed, $run.Failed, $run.Skipped),
    ("| first-run wall time | {0:mm\:ss} over {1} shard(s) |" -f $run.Elapsed, @($run.Shards).Count),
    ("| total wall time (incl. retries) | {0:mm\:ss} |" -f $overallElapsed),
    ("| peak testhost RSS (all shards together) | {0:0.00} GB |" -f ($peak / 1GB)),
    ("| per shard (classes / wall / peak RSS) | {0} |" -f (@($run.Shards | ForEach-Object { '{0} / {1:mm\:ss} / {2:0.0} GB' -f $_.Classes.Count, $_.Elapsed, ($_.PeakBytes / 1GB) }) -join '; ')),
    ("| session-race cases | {0} (re-run: {1} still unverified) |" -f $harness.Count, $unverified.Count),
    ("| classes absent from the first run | {0} |" -f $absent.Count),
    ("| order-dependent (non-blocking) | {0} |" -f $orderDependent.Count),
    ("| BLOCKING product failures | {0} |" -f $product.Count)
)
$summary | ForEach-Object { Write-Host $_ }
if ($env:GITHUB_STEP_SUMMARY) { $summary | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY }
"peak_rss_bytes=$peak" | Set-Content -LiteralPath (Join-Path $ResultsDir 'headless-ui-metrics.txt')
"first_run_seconds=$([int]$run.Elapsed.TotalSeconds)" | Add-Content -LiteralPath (Join-Path $ResultsDir 'headless-ui-metrics.txt')

if ($product.Count -gt 0) {
    Write-Host ''
    Write-Host "  $($product.Count) headless UI test(s) FAILED:" -ForegroundColor Red
    foreach ($failure in $product) { Write-Failure $failure }
    Write-Host "  Full output: $($run.LogPath)" -ForegroundColor DarkGray
    exit 1
}

Write-Host "    headless UI suite green ($($run.Passed) passed, $($run.Skipped) skipped)" -ForegroundColor Green
exit 0

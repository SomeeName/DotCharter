<#
.SYNOPSIS
    Quiet build + test gate for agents. Prints counts only; on failure, the test
    name and the first line of its assertion message. Full console output goes
    to a log file whose path is printed once.

.DESCRIPTION
    Wraps the three commands in AGENTS.md > Validation:

        dotnet build DotCharter.sln --nologo -v:m
        dotnet test --project DotCharter.Tests\DotCharter.Tests.csproj --no-build --filter-not-class "*UiPerfSmokeTests"
        dotnet test --project DotCharter.Tests\DotCharter.Tests.csproj --no-build --filter-class "*UiPerfSmokeTests" --filter-not-class <golden class>

    -Headless does not run that third line as ONE process: it splits the suite
    across -Shards concurrent testhosts (default 4) through
    tools/ci/HeadlessShards.psm1 - discovery names the classes, whole classes are
    balanced by tools/ci/headless-shard-weights.json, each shard runs its share
    with exact --filter-class names, and the TRX files are merged. A discovered
    class that reported nothing (a crashed shard, a class that never started) is
    named and fails the run. -UpdateShardWeights rewrites the weights file from
    the run.

    -Headless RACE RE-RUN (#192): when EVERY failed case carries a harness-fault
    needle (the ONE list in tools/ci/HeadlessShards.psm1 - the setup race or
    Avalonia's 5 s teardown drain) and no class is absent, those classes run
    again, alone, in a fresh process, up to -RaceReruns times (default 2). The
    run is green only if they then pass, and the re-run is printed as one: the
    first run's counts stay on screen, followed by a `headless race re-run:`
    line. A failure WITHOUT a needle, or an absent class, is never re-run away
    (Get-HeadlessRaceRerunDecision). -RaceReruns 0 reports the raw first run.
    Merge-Train.ps1 runs the headless suite through here and logs that line.

    -Public builds with -p:DotCharterPlugins=false: the git-ignored plugins/
    folder is ignored even when something is there, so the build and the gate are
    the public variant's. It implies -Gate unless another step is named, and fails
    when the assembly of a plugin checked out under plugins/ is left in the UI or
    test output (a stale copy would be scanned as a public assembly and the run
    would not be public).

    A plugin's own tests are not part of DotCharter.sln: run them with -Project and
    -Solution pointing at the plugin's test project (a plugin repository wraps that
    in its own script). A change to a seam a plugin registers against
    (IPluginModule, IGameInstallReader, the game-table types) is not done until both
    the plugin's tests and the default gate are green.

    -Goldens is a FOURTH invocation, the golden screenshots
    (docs/agents/ui/target-architecture.md section 5): only
    DotCharter.Tests.Perf.ScreenGoldenUiPerfSmokeTests, with DOTCHARTER_UI_SWEEP_DUMP=1
    (Skia drawing) and the culture pinned to en-US. -Headless excludes that one
    class by its EXACT name, so the exclusion cannot swallow any other class.

    Every test run is preceded by a build of the solution unless -NoBuild is
    given: a failed or skipped build leaves the previous binaries in place and
    a green run against them proves nothing. The incremental no-op build costs
    well under a minute; a staleness probe on file times would miss generated
    sources and copied resources, which is the wrong direction to be wrong in.

    Each `dotnet test` is its own process (the headless suite must never share a
    testhost with the gate). Results are read from the TRX the runner writes,
    the same schema tools/release/New-Release.ps1 parses.

    Runs under Windows PowerShell 5.1 and pwsh 7.

.EXAMPLE
    powershell -NoProfile -File tools/agent/Invoke-Gate.ps1                 # build + gate
    powershell -NoProfile -File tools/agent/Invoke-Gate.ps1 -Headless       # build + headless suite (4 shards, own processes)
    powershell -NoProfile -File tools/agent/Invoke-Gate.ps1 -Headless -Shards 1   # ...as one process
    powershell -NoProfile -File tools/agent/Invoke-Gate.ps1 -Goldens        # build + golden screenshots (own process)
    powershell -NoProfile -File tools/agent/Invoke-Gate.ps1 -Build          # build only
    powershell -NoProfile -File tools/agent/Invoke-Gate.ps1 -Public         # public build (no plugins) + gate
    powershell -NoProfile -File tools/agent/Invoke-Gate.ps1 -Project plugins\X\X.Tests\X.Tests.csproj -Solution plugins\X\X.Tests\X.Tests.csproj   # a plugin's own tests
    powershell -NoProfile -File tools/agent/Invoke-Gate.ps1 -FilterClass "*DocReferenceLintTests,*LanPortRangeTests"
    powershell -NoProfile -File tools/agent/Invoke-Gate.ps1 -Gate -NoBuild  # you just built; skip the rebuild

.OUTPUTS
    Green run, build + gate:
        build OK (0 warnings) - log: C:\...\Temp\dotcharter-gate\build.log
        gate: 12960 passed, 0 failed, 57 skipped, 13017 total, 264s (exit 0) - log: ...\gate.log
    Failure:
        gate: 12958 passed, 2 failed, 57 skipped, 13017 total, 261s (exit 2) - log: ...\gate.log
          FAIL DotCharter.Tests.UI.DialogWidthLadderTests.EveryDialogCard_IsOnTheLadder
               DialogCard(s) off the width ladder (docs/agents/ui/dialogs.md): ...
    Exit code: 0 when every requested step is green, otherwise the first failing step's code.
#>
[CmdletBinding()]
param(
    [switch]$Build,
    [switch]$Gate,
    [switch]$Headless,
    # The golden screenshots, their own process (Skia drawing, en-US).
    [switch]$Goldens,
    # Build without the optional plugins (-p:DotCharterPlugins=false) and run the gate on that build.
    [switch]$Public,
    # Extra --filter-class patterns, comma-separated in ONE string (ORed). Wildcards only at the ends.
    [string[]]$FilterClass,
    # Skip the implied build before a test run (only when you have JUST built).
    [switch]$NoBuild,
    [string]$Project = 'DotCharter.Tests\DotCharter.Tests.csproj',
    [string]$Solution = 'DotCharter.sln',
    [int]$MaxFailures = 20,
    # Also print the first repo frame of each failure's stack.
    [switch]$ShowFrame,
    # Parse an existing TRX and print the summary, running nothing (self-test seam).
    [string]$ParseTrx,
    # -Headless: how many concurrent testhosts the suite is split across
    # (tools/ci/HeadlessShards.psm1). 1 = one process, the old behaviour.
    [ValidateRange(1, 16)]
    [int]$Shards = 4,
    # -Headless: rewrite tools/ci/headless-shard-weights.json from this run's TRX.
    [switch]$UpdateShardWeights,
    # -Headless: how many times classes whose EVERY failure is a harness fault
    # are re-run alone in a fresh process before the run is called red. 0 = never.
    [ValidateRange(0, 5)]
    [int]$RaceReruns = 2
)

$ErrorActionPreference = 'Stop'
# Under `powershell -File` a comma-separated argument arrives as ONE string; split it here.
if ($FilterClass) { $FilterClass = @($FilterClass | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim(' ', '"', "'") } | Where-Object { $_ }) }
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location $repoRoot
# One log folder PER CHECKOUT: agent worktrees run the gate at the same time,
# and a shared folder made one run's Out-File fail on the other's open log.
$checkoutName = Split-Path -Leaf $repoRoot
$inWorktree = (Split-Path -Leaf (Split-Path -Parent $repoRoot)) -eq 'worktrees'
$logDir = if ($inWorktree) { Join-Path $env:TEMP "dotcharter-gate-$checkoutName" } else { Join-Path $env:TEMP 'dotcharter-gate' }
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

function Invoke-Build {
    $log = Join-Path $logDir 'build.log'
    # -v:q prints only errors and warnings; the log keeps them all.
    $buildArgs = @('build', $Solution, '--nologo', '-v:q')
    if ($Public) { $buildArgs += '-p:DotCharterPlugins=false' }
    & dotnet @buildArgs 2>&1 | Out-File -LiteralPath $log -Encoding utf8
    $code = $LASTEXITCODE
    $errors = @(Select-String -Path $log -Pattern ': error ' | ForEach-Object { $_.Line.Trim() } | Sort-Object -Unique)
    $warnings = @(Select-String -Path $log -Pattern ': warning ' | ForEach-Object { $_.Line.Trim() } | Sort-Object -Unique)
    if ($code -ne 0 -or $errors.Count -gt 0) {
        Write-Host "BUILD FAILED: $($errors.Count) errors, $($warnings.Count) warnings (exit $code) - log: $log"
        $errors | Select-Object -First $MaxFailures | ForEach-Object { Write-Host "  $_" }
        if ($errors.Count -eq 0) { Get-Content $log -Tail 10 | ForEach-Object { Write-Host "  $_" } }
        if ($code -eq 0) { return 1 } else { return $code }
    }
    if ($Public) {
        Import-Module (Join-Path $repoRoot 'tools\release\PluginPayloadCheck.psm1') -Force
        $names = @(Get-PluginAssemblyNames $repoRoot)
        $stale = @()
        if ($names.Count -gt 0) {
            $stale = @(Get-ChildItem -Path (Join-Path $repoRoot 'DotCharter.UI\bin'), (Join-Path $repoRoot 'DotCharter.Tests\bin') `
                -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $names -contains $_.Name })
        }
        if ($stale.Count -gt 0) {
            Write-Host "PUBLIC BUILD NOT PUBLIC: $($stale.Count) plugin assemblies in the output (stale from a plugin build) - delete the bin folders and rerun:"
            $stale | Select-Object -First 5 | ForEach-Object { Write-Host "  $($_.FullName)" }
            return 1
        }
        Write-Host "build OK, public variant: DotCharterPlugins=false, no plugin in the output ($($warnings.Count) warnings) - log: $log"
        return 0
    }
    Write-Host "build OK ($($warnings.Count) warnings) - log: $log"
    return 0
}

# Prints the summary line and, on failure, the failed cases. Returns the exit code to use.
function Show-TrxResults([string]$Label, [string]$Trx, [string]$Log, [int]$Code, [int]$Elapsed) {
    [xml]$doc = Get-Content -LiteralPath $Trx -Raw
    $ns = @{ t = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010' }
    $c = (Select-Xml -Xml $doc -XPath '//t:ResultSummary/t:Counters' -Namespace $ns).Node
    $total = [int]$c.total; $passed = [int]$c.passed; $failed = [int]$c.failed
    $skipped = $total - [int]$c.executed

    Write-Host "$Label`: $passed passed, $failed failed, $skipped skipped, $total total, ${Elapsed}s (exit $Code) - log: $Log"

    if ($failed -eq 0 -and $Code -eq 0) { return 0 }

    $nodes = @((Select-Xml -Xml $doc -XPath "//t:UnitTestResult[@outcome='Failed']" -Namespace $ns) | ForEach-Object { $_.Node })
    foreach ($n in ($nodes | Select-Object -First $MaxFailures)) {
        $message = ''
        $stack = ''
        if ($n.Output -and $n.Output.ErrorInfo) {
            $message = ([string]$n.Output.ErrorInfo.Message).Trim()
            $stack = [string]$n.Output.ErrorInfo.StackTrace
        }
        $firstLine = ($message -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 1)
        Write-Host "  FAIL $($n.testName)"
        if ($firstLine) { Write-Host "       $($firstLine.Trim())" } else { Write-Host "       (no message in the TRX)" }
        if ($ShowFrame) {
            # The stack starts in xunit/Avalonia plumbing; the first frame naming a file in the repo is the useful one.
            $frames = @($stack -split "`r?`n" | Where-Object { $_ -match ':line \d+' } | ForEach-Object { $_.Trim() -replace '^at\s+', '' })
            $frame = ($frames | Where-Object { $_ -match [regex]::Escape($repoRoot) } | Select-Object -First 1)
            if (-not $frame) { $frame = ($frames | Where-Object { $_ -notmatch 'in /_/' } | Select-Object -First 1) }
            if ($frame) { Write-Host "       at $frame" } else { Write-Host "       (no repo frame in the stack)" }
        }
    }
    if ($nodes.Count -gt $MaxFailures) { Write-Host "  ... $($nodes.Count - $MaxFailures) more failures in $Trx" }
    if ($failed -eq 0) {
        # Non-zero exit with no failed case: a crashed testhost, zero matching tests (exit 8), or a session race.
        Write-Host "  (no failed case in the TRX; tail of the log follows)"
        if (Test-Path -LiteralPath $Log) { Get-Content $Log -Tail 10 | ForEach-Object { Write-Host "  $_" } }
    }
    if ($Code -eq 0) { return 1 } else { return $Code }
}

function Invoke-TestRun([string]$Label, [string[]]$Filter) {
    $results = Join-Path $logDir $Label
    if (Test-Path $results) { Remove-Item $results -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $results | Out-Null
    $log = Join-Path $logDir "$Label.log"
    $trxName = "$Label.trx"

    $started = Get-Date
    $testArgs = @('test', '--project', $Project, '--no-build') + $Filter + @(
        '--report-xunit-trx', '--report-xunit-trx-filename', $trxName, '--results-directory', $results)
    & dotnet @testArgs 2>&1 | Out-File -LiteralPath $log -Encoding utf8
    $code = $LASTEXITCODE
    $elapsed = [int]((Get-Date) - $started).TotalSeconds

    $trx = Join-Path $results $trxName
    if (-not (Test-Path $trx)) {
        Write-Host "$Label`: NO RESULTS FILE (exit $code, ${elapsed}s) - the run never reported; tail of $log`:"
        Get-Content $log -Tail 15 | ForEach-Object { Write-Host "  $_" }
        if ($code -eq 0) { return 1 } else { return $code }
    }
    return Show-TrxResults $Label $trx $log $code $elapsed
}

# The headless suite, split across $Shards concurrent testhosts by
# tools/ci/HeadlessShards.psm1 (whole classes, balanced by measured class time,
# new classes estimated from their case count). Discovery names the classes;
# each shard runs its share with one exact --filter-class per class; the TRX
# files are merged and graded like a single run. A discovered class that
# reported NOTHING (a crashed or killed shard, a class that never started)
# fails the run by name - absence is never read as green.
function Invoke-HeadlessShardedRun([string]$Label, [string[]]$Filter) {
    Import-Module (Join-Path $repoRoot 'tools\ci\HeadlessShards.psm1') -Force
    $weightsPath = Join-Path $repoRoot 'tools\ci\headless-shard-weights.json'
    $results = Join-Path $logDir $Label
    if (Test-Path $results) { Remove-Item $results -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $results | Out-Null
    $started = Get-Date
    $base = @('test', '--project', $Project, '--no-build')

    $discoveryLog = Join-Path $results 'discovery.log'
    $discovered = Get-HeadlessDiscovery $base $Filter $discoveryLog
    if ($discovered.Count -eq 0) {
        Write-Host "$Label`: DISCOVERY LISTED NO CLASSES - nothing to shard; tail of $discoveryLog`:"
        Get-Content $discoveryLog -Tail 15 | ForEach-Object { Write-Host "  $_" }
        return 1
    }
    $plan = @(New-HeadlessShardPlan $discovered (Read-HeadlessShardWeights $weightsPath) $Shards)
    $runs = @(Invoke-HeadlessShards -Plan $plan -DotnetTestArgs $base -ResultsDir $results -Label $Label -WorkingDirectory $repoRoot)
    $elapsed = [int]((Get-Date) - $started).TotalSeconds

    $code = 0; $log = $discoveryLog
    $ns = @{ t = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010' }
    foreach ($r in $runs) {
        $counts = 'NO RESULTS FILE'
        if ($r.TrxPath) {
            $c = (Select-Xml -Path $r.TrxPath -XPath '//t:ResultSummary/t:Counters' -Namespace $ns).Node
            $counts = "$($c.passed) passed, $($c.failed) failed, $([int]$c.total - [int]$c.executed) skipped, $($c.total) total"
        }
        Write-Host ("  shard {0}: {1} classes (est {2:0}s) - {3}, {4:0}s, peak {5:0.00} GB (exit {6}) - log: {7}" -f `
            $r.Index, $r.Classes.Count, $r.Weight, $counts, $r.Elapsed.TotalSeconds, ($r.PeakBytes / 1GB), $r.ExitCode, $r.LogPath)
        if ($code -eq 0 -and $r.ExitCode -ne 0) { $code = $r.ExitCode; $log = $r.LogPath }
    }
    if ($runs.Count -gt 0) { Write-Host ("  {0} shard(s) concurrent; peak working set of all shards together {1:0.00} GB" -f $runs.Count, ($runs[0].PeakSumBytes / 1GB)) }

    $trx = Merge-HeadlessTrx @($runs | ForEach-Object { $_.TrxPath }) (Join-Path $results "$Label.trx")
    if (-not $trx) {
        Write-Host "$Label`: NO RESULTS FILE from any shard (exit $code, ${elapsed}s) - the run never reported; tail of $log`:"
        Get-Content $log -Tail 15 | ForEach-Object { Write-Host "  $_" }
        if ($code -eq 0) { return 1 } else { return $code }
    }
    $exit = Show-TrxResults $Label $trx $log $code $elapsed

    # Discovered vs reported, by class: the only check that sees a class that never ran.
    $ran = @{}
    foreach ($n in @((Select-Xml -Path $trx -XPath '//t:UnitTestResult' -Namespace $ns) | ForEach-Object { $_.Node })) {
        $ran[(Get-HeadlessClassName ([string]$n.testName))] = $true
    }
    $absent = @($discovered.Keys | Where-Object { -not $ran.ContainsKey($_) } | Sort-Object)
    if ($absent.Count -gt 0) {
        Write-Host "  ABSENT: $($absent.Count) DISCOVERED class(es) reported NO result - nothing in them was graded:"
        foreach ($a in $absent) {
            $owner = @($runs | Where-Object { $_.Classes -contains $a } | ForEach-Object { "shard $($_.Index), exit $($_.ExitCode)" })
            Write-Host "    $a ($($owner -join '; '))"
        }
        if ($exit -eq 0) { $exit = 1 }
    }
    # Keep the ORIGINAL failure's TRX, messages, stacks and stdout before any re-run
    # can overwrite or outvote them (#55); a green re-run does not delete this.
    $script:HeadlessEvidenceDir = $null
    if ($exit -ne 0) {
        $script:HeadlessEvidenceDir = Save-HeadlessFailureEvidence -TrxPath @($runs | ForEach-Object { $_.TrxPath }) `
            -LogPath @($runs | ForEach-Object { $_.LogPath }) -Label 'first-run' -Always
    }
    if ($exit -ne 0 -and $RaceReruns -gt 0) {
        $exit = Invoke-HeadlessRaceReruns $Label $trx $absent $discovered $weightsPath $results $base $exit
    }
    if ($script:HeadlessEvidenceDir) { Write-Host "  headless failure evidence kept: $($script:HeadlessEvidenceDir)" }
    if ($UpdateShardWeights) {
        Write-HeadlessShardWeights @($trx) $weightsPath
        Write-Host "  shard weights rewritten: $weightsPath"
    }
    return $exit
}

# The race re-run (#192). Acts ONLY on Get-HeadlessRaceRerunDecision's 'Rerun'
# (every failure carries a harness-fault needle, no class absent): those classes
# run again, alone, in ONE fresh process, up to $RaceReruns times. Returns 0 only
# if a re-run passes them all; otherwise the first run's exit code (or the
# re-run's, when the re-run itself fails for real).
function Invoke-HeadlessRaceReruns([string]$Label, [string]$Trx, [string[]]$Absent, [hashtable]$Discovered,
        [string]$WeightsPath, [string]$ResultsDir, [string[]]$Base, [int]$FirstExit) {
    $decision = Get-HeadlessRaceRerunDecision @(Read-HeadlessTrxFailures $Trx) $Absent
    if ($decision.Action -ne 'Rerun') {
        if ($decision.Action -eq 'Stop') { Write-Host "  headless race re-run: NOT attempted - $($decision.Reason)" }
        return $FirstExit
    }
    $classes = @($decision.Classes)
    Write-Host "  headless race re-run: $($decision.Reason); re-running $($classes.Count) class(es) alone, up to $RaceReruns time(s)"
    for ($attempt = 1; $attempt -le $RaceReruns; $attempt++) {
        $counts = @{}
        foreach ($c in $classes) { $counts[$c] = $(if ($Discovered.ContainsKey($c)) { $Discovered[$c] } else { 1 }) }
        $plan = @(New-HeadlessShardPlan $counts (Read-HeadlessShardWeights $WeightsPath) 1)
        $run = @(Invoke-HeadlessShards -Plan $plan -DotnetTestArgs $Base -ResultsDir $ResultsDir `
            -Label "$Label-rerun$attempt" -WorkingDirectory $repoRoot)[0]
        if (-not $run.TrxPath) {
            Write-Host "  headless race re-run $attempt/$RaceReruns`: NO RESULTS FILE (exit $($run.ExitCode)) - RED; log: $($run.LogPath)"
            return $FirstExit
        }
        $failures = @(Read-HeadlessTrxFailures $run.TrxPath)
        if ($failures.Count -gt 0 -and $script:HeadlessEvidenceDir) {
            $null = Save-HeadlessFailureEvidence -TrxPath @($run.TrxPath) -LogPath @($run.LogPath) -Label "rerun$attempt" -Folder $script:HeadlessEvidenceDir
        }
        $ran = @{}
        foreach ($n in @((Select-Xml -Path $run.TrxPath -XPath '//t:UnitTestResult' -Namespace @{ t = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010' }) | ForEach-Object { $_.Node })) {
            $ran[(Get-HeadlessClassName ([string]$n.testName))] = $true
        }
        $missing = @($classes | Where-Object { -not $ran.ContainsKey($_) })
        $next = Get-HeadlessRaceRerunDecision $failures $missing
        Write-Host ("  headless race re-run {0}/{1}: {2} class(es), {3} failed, exit {4}, {5:0}s - {6} - log: {7}" -f `
            $attempt, $RaceReruns, $classes.Count, $failures.Count, $run.ExitCode, $run.Elapsed.TotalSeconds, $next.Reason, $run.LogPath)
        foreach ($f in ($failures | Select-Object -First $MaxFailures)) { Write-Host "    FAIL $($f.Name)"; Write-Host "         $($f.Message)" }
        if ($next.Action -eq 'Pass' -and $run.ExitCode -eq 0) {
            Write-Host "  headless race re-run: PASSED on attempt $attempt - $($classes -join ', ') green alone; the first run's harness-fault cases are NOT counted as passes"
            return 0
        }
        if ($next.Action -ne 'Rerun') {
            Write-Host "  headless race re-run: RED on attempt $attempt - $($next.Reason)"
            if ($run.ExitCode -ne 0) { return $run.ExitCode } else { return $FirstExit }
        }
        $classes = @($next.Classes)
    }
    Write-Host "  headless race re-run: RED - still racing after $RaceReruns re-run(s): $($classes -join ', ')"
    return $FirstExit
}

if ($ParseTrx) {
    $code = Show-TrxResults 'parsed' (Resolve-Path -LiteralPath $ParseTrx).Path '(none)' 1 0
    exit $code
}

# The golden-screenshot class: excluded from -Headless by this EXACT name, run alone by -Goldens.
$GoldenClass = 'DotCharter.Tests.Perf.ScreenGoldenUiPerfSmokeTests'

$wantsTests = $Gate -or $Headless -or $Goldens -or [bool]$FilterClass
if ($Public -and -not ($Build -or $wantsTests)) { $Gate = $true; $wantsTests = $true }
if (-not ($Build -or $wantsTests)) { $Build = $true; $Gate = $true; $wantsTests = $true }
if ($wantsTests -and -not $NoBuild) { $Build = $true }

$exit = 0
if ($Build) { $exit = Invoke-Build; if ($exit -ne 0) { exit $exit } }
if ($Gate) { $exit = Invoke-TestRun 'gate' @('--filter-not-class', '*UiPerfSmokeTests'); if ($exit -ne 0) { exit $exit } }
if ($Headless) { $exit = Invoke-HeadlessShardedRun 'headless' @('--filter-class', '*UiPerfSmokeTests', '--filter-not-class', $GoldenClass); if ($exit -ne 0) { exit $exit } }
if ($Goldens) {
    $saved = @{ Dump = $env:DOTCHARTER_UI_SWEEP_DUMP; Culture = $env:DOTCHARTER_HEADLESS_CULTURE }
    $env:DOTCHARTER_UI_SWEEP_DUMP = '1'
    $env:DOTCHARTER_HEADLESS_CULTURE = 'en-US'
    try { $exit = Invoke-TestRun 'goldens' @('--filter-class', $GoldenClass) }
    finally { $env:DOTCHARTER_UI_SWEEP_DUMP = $saved.Dump; $env:DOTCHARTER_HEADLESS_CULTURE = $saved.Culture }
    if ($exit -ne 0) { exit $exit }
}
if ($FilterClass) {
    $filter = @()
    foreach ($f in $FilterClass) { $filter += @('--filter-class', $f) }
    $exit = Invoke-TestRun 'filtered' $filter
}
exit $exit

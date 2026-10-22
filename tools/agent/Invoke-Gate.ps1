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

    -Gate runs the DEFAULT GATE: every test class except the headless suite and
    the RELEASE LANE (tools/ci/release-lane.json: the heaviest corpus /
    integration / determinism classes, each with its reason). -ReleaseLane runs
    exactly those classes, sharded the same way; the merge train, the release
    preflight and CI's test job always run it, so a class moved out of the gate
    is never dropped. Give both (-Gate -ReleaseLane) for the old full set.
    A plain `dotnet test` with no filter runs the default gate too
    (DotCharter.Tests/TestEntryPoint.cs). Infrastructure/ReleaseLaneLintTests
    proves every class runs in exactly one lane, and -SelfTest proves the two
    lanes together list the pre-lane gate's cases.

    -Gate does not run the second line as ONE process either: it splits the
    gate across -Shards concurrent testhosts (default 4 on CPUs 8-15, 8 on all cores) through
    tools/ci/GateShards.psm1. Shards 1..N-1 each run an explicit list of whole
    classes, dealt by tools/ci/gate-shard-weights.json (measured seconds);
    shard N, the remainder, runs the gate's own filter minus every listed
    class. The shards together are the gate's case set exactly, by
    construction - a class added since the weights were measured runs in the
    remainder - and -SelfTest proves it against the runner's own listing.
    Every shard must report a TRX; the TRX files are merged and graded as one
    run, with the same summary line. -Shards 1 is the old single process.
    -UpdateShardWeights rewrites the weights file(s) of the suite(s) that ran.

    After the gate, the 20 slowest classes are printed (seconds and share of
    the summed case time) and every class's duration is written to
    gate-durations.json next to gate.trx (also for -FilterClass:
    filtered-durations.json).

    -Gate -Headless runs the two suites' shards AT THE SAME TIME (separate
    testhosts already), when at least -MinFreeGB of physical memory is free
    (default 12; the headless shards alone peak around 5-7 GB), otherwise one
    after the other, as before (-Sequential forces that). Concurrent runs
    report both suites, even when the gate is red, then a
    `gate + headless: gate exit G, headless exit H` line; the exit code is the
    gate's when it failed, else the headless suite's.

    -Headless does not run that third line as ONE process: it splits the suite
    across -Shards concurrent testhosts (default as above) through
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

    -Affected is the ITERATION mode: from the diff against -Base (default
    origin/main, its merge-base with HEAD; committed, uncommitted and untracked
    files) it picks the test classes that cover the changed files, prints each
    pick with its reason, builds, and runs only those (the headless ones through the
    headless path). The rules are tools/agent/AffectedTests.psm1's header; a
    change to tools/, a project, props, targets or global config runs the
    full -Gate instead and says so. It is a LOOP tool: it records no green tree and
    never replaces the full gate, which the merge train still runs once.
    -SelectOnly prints the selection and stops; -Head <ref> diffs Base..Head
    (committed only) instead of the working tree.

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
    powershell -NoProfile -File tools/agent/Invoke-Gate.ps1 -ReleaseLane    # build + the release lane only (the slow classes)
    powershell -NoProfile -File tools/agent/Invoke-Gate.ps1 -Gate -ReleaseLane   # the default gate, then the release lane
    powershell -NoProfile -File tools/agent/Invoke-Gate.ps1 -Gate -Headless # both suites, concurrently when RAM allows
    powershell -NoProfile -File tools/agent/Invoke-Gate.ps1 -SelfTest -NoBuild   # the gate shards list exactly the gate's cases
    powershell -NoProfile -File tools/agent/Invoke-Gate.ps1 -Affected       # build + only the classes that cover the diff vs origin/main
    powershell -NoProfile -File tools/agent/Invoke-Gate.ps1 -Affected -SelectOnly   # ...print the selection and why, run nothing

.OUTPUTS
    Green run, build + gate:
        build OK (0 warnings) - log: C:\...\Temp\dotcharter-gate\build.log
        gate: 12960 passed, 0 failed, 57 skipped, 13017 total, 264s (exit 0) - log: ...\gate.log
          slowest classes: top 20 of 2082 = 57.0 % of 1986.0 s summed case time (22003 cases)
             1.   186.0 s   9.3 %  Audio.RsToneFidelityHarnessTests (45)
             ...
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
    # The release lane: the classes tools/ci/release-lane.json lists (the default gate leaves them out).
    [switch]$ReleaseLane,
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
    # -Gate, -ReleaseLane and -Headless: how many concurrent testhosts each suite is split
    # across (tools/ci/GateShards.psm1, tools/ci/HeadlessShards.psm1). 1 = one process.
    # 0 (default) follows the CPU plan: 4 on CPUs 8-15, 8 with -AllCores (DevBuild.psm1).
    [ValidateRange(0, 16)]
    [int]$Shards = 0,
    # CPU plan (#171): default CPUs 8-15 at BelowNormal; -AllCores by hand uses every core.
    [switch]$AllCores,
    # Rewrite tools/ci/gate-shard-weights.json (-Gate), tools/ci/release-lane.json's
    # seconds (-ReleaseLane) and/or tools/ci/headless-shard-weights.json (-Headless)
    # from this run's TRX.
    [switch]$UpdateShardWeights,
    # List the gate unsharded and shard by shard and prove the shards together
    # are exactly the gate's cases. Runs no test.
    [switch]$SelfTest,
    # -Gate -Headless: run the two suites one after the other, never at the same time.
    [switch]$Sequential,
    # -Gate -Headless: the free physical memory (GB) below which they run one after the other.
    [double]$MinFreeGB = 12,
    # Build configuration and runtime of the tested output (CI: Release, win-x64).
    [string]$Configuration = '',
    [string]$Runtime = '',
    # Where logs, TRX files and the durations JSON go (default: a folder under %TEMP%).
    [string]$ResultsDir = '',
    # -Headless: how many times classes whose EVERY failure is a harness fault
    # are re-run alone in a fresh process before the run is called red. 0 = never.
    [ValidateRange(0, 5)]
    [int]$RaceReruns = 2,
    # -Affected: run only the test classes that cover the diff (tools/agent/AffectedTests.psm1).
    [switch]$Affected,
    # -Affected: the ref the diff starts from (its merge-base with HEAD), and optionally the commit it
    # ends at instead of the working tree.
    [string]$Base = 'origin/main',
    [string]$Head = '',
    # -Affected: print the selection and run nothing.
    [switch]$SelectOnly,
    # -Affected: a selection of more classes than this runs the full gate instead.
    [int]$MaxAffectedClasses = 600
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
$logDir = if ($ResultsDir) { [System.IO.Path]::GetFullPath((Join-Path $repoRoot $ResultsDir)) } `
    elseif ($inWorktree) { Join-Path $env:TEMP "dotcharter-gate-$checkoutName" } else { Join-Path $env:TEMP 'dotcharter-gate' }
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
# -c / -r for both the build and every `dotnet test`.
$outputArgs = @()
if ($Configuration) { $outputArgs += @('-c', $Configuration) }
if ($Runtime) { $outputArgs += @('-r', $Runtime) }
$testBase = @('test', '--project', $Project, '--no-build') + $outputArgs
# The default gate's exclusion: the same pattern DefaultRunFilter.ExcludedClasses
# adds to a plain `dotnet test` (DotCharter.Tests/TestEntryPoint.cs).
$GateExclusion = '*UiPerfSmokeTests'
# The release lane's classes (tools/ci/GateShards.psm1 Read-ReleaseLane): the
# default gate leaves them out and the release lane runs exactly them.
$ReleaseLanePath = Join-Path $repoRoot 'tools\ci\release-lane.json'
Import-Module (Join-Path $repoRoot 'tools\ci\GateShards.psm1') -Force

# The default gate's exclusions: the headless suite and every release-lane class
# (the lane runs exactly those). Read-ReleaseLane throws on a missing list.
function Get-GateExclusions {
    Import-Module (Join-Path $repoRoot 'tools\ci\GateShards.psm1') -Force
    return , [string[]](@($GateExclusion) + @((Read-ReleaseLane $ReleaseLanePath).Classes.Keys))
}

Import-Module (Join-Path $PSScriptRoot 'DevBuild.psm1') -Force
# Pin this process (testhosts inherit) and print the choice: DevBuild.psm1 > Set-DevCpuPlan.
if (-not $ParseTrx) {
    $cpuPlan = Set-DevCpuPlan -AllCores:$AllCores
    if ($Shards -eq 0) { $Shards = $cpuPlan.Shards }
}
elseif ($Shards -eq 0) { $Shards = 4 }

function Invoke-Build {
    # A commit must not recompile every project (#153): SourceLink off, see DevBuild.psm1.
    Enable-IncrementalDevBuild
    $log = Join-Path $logDir 'build.log'
    # -v:q prints only errors and warnings; the log keeps them all.
    $buildArgs = @('build', $Solution, '--nologo', '-v:q') + $outputArgs
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
    $testArgs = $testBase + $Filter + @(
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
    $exit = Show-TrxResults $Label $trx $log $code $elapsed
    Show-ClassDurations $Label @($trx) $results
    return $exit
}

# The slowest classes of a run, on screen, and every class's duration as
# <label>-durations.json next to the TRX (tools/ci/GateShards.psm1).
function Show-ClassDurations([string]$Label, [string[]]$TrxPaths, [string]$Dir) {
    Import-Module (Join-Path $repoRoot 'tools\ci\GateShards.psm1') -Force
    $durations = Read-TrxClassDurations $TrxPaths
    if ($durations.Count -eq 0) { return }
    $summary = Get-ClassDurationSummary $durations 20
    foreach ($line in (Format-ClassDurationSummary $summary)) { Write-Host $line }
    $json = Join-Path $Dir "$Label-durations.json"
    Write-ClassDurationJson $summary $durations $json
    Write-Host "  every class's duration: $json"
    $script:LastDurations = $durations
}

# One line per shard, as the headless suite has always printed them.
function Show-ShardLines([object[]]$Runs) {
    $ns = @{ t = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010' }
    foreach ($r in $Runs) {
        $counts = 'NO RESULTS FILE'
        if ($r.TrxPath) {
            $c = (Select-Xml -Path $r.TrxPath -XPath '//t:ResultSummary/t:Counters' -Namespace $ns).Node
            $counts = "$($c.passed) passed, $($c.failed) failed, $([int]$c.total - [int]$c.executed) skipped, $($c.total) total"
        }
        $what = if ($r.PSObject.Properties['Remainder'] -and $r.Remainder) { "remainder, $($r.Classes.Count)+ classes" } else { "$($r.Classes.Count) classes" }
        Write-Host ("  shard {0}: {1} (est {2:0}s) - {3}, {4:0}s, peak {5:0.00} GB (exit {6}) - log: {7}" -f `
            $r.Index, $what, $r.Weight, $counts, $r.Elapsed.TotalSeconds, ($r.PeakBytes / 1GB), $r.ExitCode, $r.LogPath)
    }
}

# ---- the default gate, sharded (tools/ci/GateShards.psm1) --------------------
# Start: plan from the weights file and start every shard; returns the context
# Complete-GateShardedRun grades once the shards have exited.
function Start-GateShardedRun([string]$Label, [string]$Lane = 'gate') {
    Import-Module (Join-Path $repoRoot 'tools\ci\HeadlessShards.psm1') -Force
    Import-Module (Join-Path $repoRoot 'tools\ci\GateShards.psm1') -Force
    if ($Lane -eq 'release') {
        # The release lane: exactly the declared classes, whole, dealt by their measured seconds.
        $weights = [pscustomobject]@{ Classes = (Read-ReleaseLane $ReleaseLanePath).Classes; OtherSeconds = 0.0 }
        $plan = @(New-ReleaseLaneShardPlan $weights.Classes $Shards)
    }
    else {
        $weights = Read-GateShardWeights $script:GateWeightsPath
        $plan = @(New-GateShardPlan $weights.Classes $weights.OtherSeconds $Shards (Get-GateExclusions))
    }
    $results = Join-Path $logDir $Label
    if (Test-Path $results) { Remove-Item $results -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $results | Out-Null
    $started = Get-Date
    $runs = @(Start-TestShards -Plan $plan -DotnetTestArgs $testBase -ResultsDir $results -Label $Label -WorkingDirectory $repoRoot)
    foreach ($r in $runs) {
        $p = $plan | Where-Object { $_.Index -eq $r.Index }
        Add-Member -InputObject $r -NotePropertyName Remainder -NotePropertyValue $p.Remainder -Force
    }
    return [pscustomobject]@{ Label = $Label; Results = $results; Runs = $runs; Started = $started; Weights = $weights; Lane = $Lane }
}

function Complete-GateShardedRun($Run, [switch]$Together) {
    $label = $Run.Label
    $elapsed = [int]((Get-Date) - $Run.Started).TotalSeconds
    Show-ShardLines $Run.Runs
    if (-not $Together -and $Run.Runs.Count -gt 0) {
        Write-Host ("  {0} shard(s) concurrent; peak working set of all shards together {1:0.00} GB" -f $Run.Runs.Count, ($Run.Runs[0].PeakSumBytes / 1GB))
    }
    $code = 0; $log = $null
    foreach ($r in $Run.Runs) { if ($code -eq 0 -and $r.ExitCode -ne 0) { $code = $r.ExitCode; $log = $r.LogPath } }
    if (-not $log -and $Run.Runs.Count -gt 0) { $log = $Run.Runs[0].LogPath }
    $silent = @($Run.Runs | Where-Object { -not $_.TrxPath })

    $trx = Merge-HeadlessTrx @($Run.Runs | ForEach-Object { $_.TrxPath }) (Join-Path $Run.Results "$label.trx")
    if (-not $trx) {
        Write-Host "$label`: NO RESULTS FILE from any shard (exit $code, ${elapsed}s) - the run never reported; tail of $log`:"
        if ($log -and (Test-Path -LiteralPath $log)) { Get-Content $log -Tail 15 | ForEach-Object { Write-Host "  $_" } }
        if ($code -eq 0) { return 1 } else { return $code }
    }
    $exit = Show-TrxResults $label $trx $log $code $elapsed
    # A shard without a TRX ran cases nobody graded: never green.
    if ($silent.Count -gt 0) {
        foreach ($s in $silent) {
            Write-Host "  SHARD $($s.Index) REPORTED NOTHING (exit $($s.ExitCode)) - its cases are not in the counts above; tail of $($s.LogPath):"
            Get-Content $s.LogPath -Tail 10 | ForEach-Object { Write-Host "    $_" }
        }
        if ($exit -eq 0) { $exit = 1 }
    }
    Show-ClassDurations $label @($trx) $Run.Results
    if ($Run.Lane -eq 'release') {
        # The lane is whole classes by name: a listed class that reported nothing never ran.
        $ranClasses = @{}
        foreach ($d in @($script:LastDurations)) { $ranClasses[$d.Class] = $true }
        $never = @($Run.Weights.Classes.Keys | Where-Object { -not $ranClasses.ContainsKey($_) })
        if ($never.Count -gt 0) {
            Write-Host "  RELEASE LANE: $($never.Count) listed class(es) reported NO result (renamed or deleted? fix tools/ci/release-lane.json):"
            foreach ($n in $never) { Write-Host "    $n" }
            if ($exit -eq 0) { $exit = 1 }
        }
        if ($UpdateShardWeights) {
            Write-ReleaseLaneWeights $script:LastDurations $ReleaseLanePath
            Write-Host "  release lane seconds rewritten: $ReleaseLanePath"
        }
        return $exit
    }
    # Classes the weights file does not list ran in the remainder: worth a
    # refresh once they weigh clearly more than the file's otherSeconds
    # predicts, scaled by how much slower this run was than the measured one
    # (concurrent shards stretch every case).
    $known = $Run.Weights.OtherSeconds; foreach ($v in $Run.Weights.Classes.Values) { $known += $v }
    $observed = 0.0; $unweighted = 0.0
    foreach ($d in @($script:LastDurations)) {
        $observed += $d.Seconds
        if (-not $Run.Weights.Classes.ContainsKey($d.Class)) { $unweighted += $d.Seconds }
    }
    $scale = if ($known -gt 0) { $observed / $known } else { 1.0 }
    if ($unweighted -gt 2 * $scale * $Run.Weights.OtherSeconds + 60) {
        Write-Host ("  classes not in gate-shard-weights.json took {0:0} s here, {1:0} s expected - refresh with -Gate -Shards 1 -UpdateShardWeights" -f $unweighted, ($scale * $Run.Weights.OtherSeconds))
    }
    if ($UpdateShardWeights) {
        Write-GateShardWeights $script:LastDurations $script:GateWeightsPath
        Write-Host "  gate shard weights rewritten: $($script:GateWeightsPath)"
    }
    return $exit
}

# The gate: one process (-Shards 1, the old command line) or sharded.
function Invoke-GateRun([string]$Label) {
    if ($Shards -le 1) {
        $exit = Invoke-TestRun $Label (Get-TestLaneFilter 'default' @((Read-ReleaseLane $ReleaseLanePath).Classes.Keys) $GateExclusion)
        if ($UpdateShardWeights -and $script:LastDurations) {
            Write-GateShardWeights $script:LastDurations $script:GateWeightsPath
            Write-Host "  gate shard weights rewritten: $($script:GateWeightsPath)"
        }
        return $exit
    }
    $run = Start-GateShardedRun $Label
    $all = Wait-TestShards -Runs $run.Runs
    $run.Runs = @($all)
    return Complete-GateShardedRun $run
}

# The release lane: its classes sharded like the gate (Start-GateShardedRun -Lane release).
function Invoke-ReleaseLaneRun([string]$Label) {
    $run = Start-GateShardedRun $Label 'release'
    $run.Runs = @(Wait-TestShards -Runs $run.Runs)
    return Complete-GateShardedRun $run
}

# -SelfTest: the pre-lane gate (everything but the headless suite) listed whole,
# and the default gate's shards plus the release lane's shards each listed with
# their own filter, all at once; together they must list exactly its cases.
function Invoke-GateShardSelfTest {
    Import-Module (Join-Path $repoRoot 'tools\ci\HeadlessShards.psm1') -Force
    Import-Module (Join-Path $repoRoot 'tools\ci\GateShards.psm1') -Force
    $weights = Read-GateShardWeights $script:GateWeightsPath
    $plan = @(New-GateShardPlan $weights.Classes $weights.OtherSeconds $Shards (Get-GateExclusions))
    # The release lane's shards continue the numbering, so every listing has its own file.
    $lanePlan = @(New-ReleaseLaneShardPlan (Read-ReleaseLane $ReleaseLanePath).Classes $Shards)
    foreach ($l in $lanePlan) { $l.Index += $plan.Count }
    $plan = @($plan) + @($lanePlan)
    $dir = Join-Path $logDir 'selftest'
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $started = Get-Date
    # Index 0 is the unsharded gate, on the command line exactly as -Shards 1 runs it.
    $whole = [pscustomobject]@{ Index = 0; Classes = [string[]]@(); Weight = 0.0; CommandArgs = [string[]]@('--filter-not-class', $GateExclusion) }
    $runs = @(Start-TestShards -Plan (@($whole) + $plan) -DotnetTestArgs ($testBase + @('--list-tests')) -ResultsDir $dir -Label 'list' -WorkingDirectory $repoRoot)
    $runs = @(Wait-TestShards -Runs $runs)
    $wholeNames = Read-TestListingNames $runs[0].LogPath
    $parts = @(); foreach ($r in ($runs | Select-Object -Skip 1)) { $parts += , (Read-TestListingNames $r.LogPath) }
    $cmp = Compare-TestListings $wholeNames $parts
    $elapsed = [int]((Get-Date) - $started).TotalSeconds
    $sizes = @($parts | ForEach-Object { @($_).Count }) -join ' + '
    if ($cmp.Equal -and $cmp.WholeCount -gt 0) {
        Write-Host "gate shard self-test: $($plan.Count) shard(s) (default gate + release lane) list $sizes = $($cmp.PartsCount) cases, the unsharded pre-lane gate lists $($cmp.WholeCount) - EQUAL, case for case (${elapsed}s) - logs: $dir"
        return 0
    }
    Write-Host "gate shard self-test: FAILED - shards list $sizes = $($cmp.PartsCount), the unsharded gate $($cmp.WholeCount) (${elapsed}s) - logs: $dir"
    foreach ($kind in 'Missing', 'Extra', 'Duplicated') {
        $names = @($cmp.$kind)
        if ($names.Count -gt 0) { Write-Host "  $kind ($($names.Count)):"; $names | Select-Object -First $MaxFailures | ForEach-Object { Write-Host "    $_" } }
    }
    return 1
}

# The headless suite, split across $Shards concurrent testhosts by
# tools/ci/HeadlessShards.psm1 (whole classes, balanced by measured class time,
# new classes estimated from their case count). Discovery names the classes;
# each shard runs its share with one exact --filter-class per class; the TRX
# files are merged and graded like a single run. A discovered class that
# reported NOTHING (a crashed or killed shard, a class that never started)
# fails the run by name - absence is never read as green.
function Invoke-HeadlessShardedRun([string]$Label, [string[]]$Filter) {
    $run = Start-HeadlessShardedRun $Label $Filter
    if ($run.Failed) { return $run.Failed }
    $run.Runs = @(Wait-TestShards -Runs $run.Runs)
    return Complete-HeadlessShardedRun $run
}

# Discovery, plan, and every shard started; returns the context
# Complete-HeadlessShardedRun grades once the shards have exited (Failed is
# the exit code when discovery found nothing to run).
function Start-HeadlessShardedRun([string]$Label, [string[]]$Filter) {
    Import-Module (Join-Path $repoRoot 'tools\ci\HeadlessShards.psm1') -Force
    $weightsPath = Join-Path $repoRoot 'tools\ci\headless-shard-weights.json'
    $results = Join-Path $logDir $Label
    if (Test-Path $results) { Remove-Item $results -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $results | Out-Null
    $started = Get-Date
    $base = $testBase

    $discoveryLog = Join-Path $results 'discovery.log'
    $discovered = Get-HeadlessDiscovery $base $Filter $discoveryLog
    if ($discovered.Count -eq 0) {
        Write-Host "$Label`: DISCOVERY LISTED NO CLASSES - nothing to shard; tail of $discoveryLog`:"
        Get-Content $discoveryLog -Tail 15 | ForEach-Object { Write-Host "  $_" }
        return [pscustomobject]@{ Failed = 1 }
    }
    $plan = @(New-HeadlessShardPlan $discovered (Read-HeadlessShardWeights $weightsPath) $Shards)
    $runs = @(Start-TestShards -Plan $plan -DotnetTestArgs $base -ResultsDir $results -Label $Label -WorkingDirectory $repoRoot)
    return [pscustomobject]@{ Failed = 0; Label = $Label; Results = $results; Runs = $runs; Started = $started
        Discovered = $discovered; DiscoveryLog = $discoveryLog; WeightsPath = $weightsPath; Base = $base }
}

function Complete-HeadlessShardedRun($Run, [switch]$Together) {
    $Label = $Run.Label; $runs = $Run.Runs; $results = $Run.Results; $discovered = $Run.Discovered
    $weightsPath = $Run.WeightsPath; $base = $Run.Base
    $elapsed = [int]((Get-Date) - $Run.Started).TotalSeconds

    $code = 0; $log = $Run.DiscoveryLog
    $ns = @{ t = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010' }
    Show-ShardLines $runs
    foreach ($r in $runs) { if ($code -eq 0 -and $r.ExitCode -ne 0) { $code = $r.ExitCode; $log = $r.LogPath } }
    if (-not $Together -and $runs.Count -gt 0) { Write-Host ("  {0} shard(s) concurrent; peak working set of all shards together {1:0.00} GB" -f $runs.Count, ($runs[0].PeakSumBytes / 1GB)) }

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

# ---- -Affected (tools/agent/AffectedTests.psm1) ------------------------------
# The selection decides before the build, so a fallback or an empty diff never pays for one.
function Get-AffectedRunSelection {
    Import-Module (Join-Path $PSScriptRoot 'AffectedTests.psm1') -Force
    $selecting = [Diagnostics.Stopwatch]::StartNew()
    $baseLabel = if ($Head) { "$Base..$Head" } else { "$Base (merge-base; committed, uncommitted and untracked files)" }
    $changed = Get-AffectedChangedPaths $repoRoot $Base $Head
    # An empty diff and a full-gate fallback decide before any test source is read, so neither pays for the index.
    $needsIndex = (@($changed.Paths).Count -gt 0) -and -not (Get-AffectedFallbackReason $changed.Paths)
    $index = if ($needsIndex) { New-AffectedTestIndex $repoRoot } else { $null }
    $selection = Get-AffectedSelection $changed.Paths $index @((Read-ReleaseLane $ReleaseLanePath).Classes.Keys) $MaxAffectedClasses
    foreach ($line in (Format-AffectedSelection $selection $baseLabel ([int]$index.Classes.Count))) { Write-Host $line }
    Write-Host ("affected: selection took {0:0.0} s; an iteration aid - it records no green tree and does not replace the full gate, which the merge train runs once" -f $selecting.Elapsed.TotalSeconds)
    return $selection
}

# The gate-side classes: one process while the estimate is small, else the same whole-class shards the release lane uses.
function Invoke-AffectedGateRun([string[]]$Classes) {
    Import-Module (Join-Path $repoRoot 'tools\ci\HeadlessShards.psm1') -Force
    $measured = (Read-GateShardWeights $script:GateWeightsPath).Classes
    $lane = (Read-ReleaseLane $ReleaseLanePath).Classes
    $weights = [ordered]@{}
    foreach ($c in $Classes) { $weights[$c] = if ($measured.Contains($c)) { $measured[$c] } elseif ($lane.Contains($c)) { $lane[$c] } else { 0.1 } }
    $processes = Get-AffectedShardCount (Get-AffectedEstimateSeconds $Classes $weights) $Shards
    Write-Host "affected: gate part, $($Classes.Count) class(es), ~$([int](Get-AffectedEstimateSeconds $Classes $weights)) s of measured case time, $processes process(es)"
    if ($processes -le 1) {
        $filter = @(); foreach ($c in $Classes) { $filter += @('--filter-class', $c) }
        return Invoke-TestRun 'affected' $filter
    }
    $results = Join-Path $logDir 'affected'
    if (Test-Path $results) { Remove-Item $results -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $results | Out-Null
    $started = Get-Date
    $plan = @(New-ReleaseLaneShardPlan $weights $processes)
    $runs = @(Wait-TestShards -Runs @(Start-TestShards -Plan $plan -DotnetTestArgs $testBase -ResultsDir $results -Label 'affected' -WorkingDirectory $repoRoot))
    Show-ShardLines $runs
    $code = 0; $log = $runs[0].LogPath
    foreach ($r in $runs) { if ($code -eq 0 -and $r.ExitCode -ne 0) { $code = $r.ExitCode; $log = $r.LogPath } }
    $elapsed = [int]((Get-Date) - $started).TotalSeconds
    $trx = Merge-HeadlessTrx @($runs | ForEach-Object { $_.TrxPath }) (Join-Path $results 'affected.trx')
    if (-not $trx) {
        Write-Host "affected: NO RESULTS FILE from any shard (exit $code, ${elapsed}s); tail of $log`:"
        Get-Content $log -Tail 15 | ForEach-Object { Write-Host "  $_" }
        if ($code -eq 0) { return 1 } else { return $code }
    }
    $exit = Show-TrxResults 'affected' $trx $log $code $elapsed
    foreach ($s in @($runs | Where-Object { -not $_.TrxPath })) {
        Write-Host "  SHARD $($s.Index) REPORTED NOTHING (exit $($s.ExitCode)); tail of $($s.LogPath):"
        Get-Content $s.LogPath -Tail 10 | ForEach-Object { Write-Host "    $_" }
        if ($exit -eq 0) { $exit = 1 }
    }
    Show-ClassDurations 'affected' @($trx) $results
    return $exit
}

# A class the index picked from a file that holds a helper beside the tests runs no case: said, not failed.
function Show-AffectedQuietClasses([string[]]$Classes) {
    $ran = @{}; foreach ($d in @($script:LastDurations)) { $ran[$d.Class] = $true }
    $quiet = @($Classes | Where-Object { -not $ran.ContainsKey($_) })
    if ($quiet.Count -gt 0) { Write-Host "affected: $($quiet.Count) selected class(es) ran no case: $(($quiet | Select-Object -First 8) -join ', ')" }
}

function Invoke-AffectedRun($Selection) {
    $started = Get-Date
    $gateClasses = @($Selection.Gate | ForEach-Object { $_.FullName })
    $headlessClasses = @($Selection.Headless | ForEach-Object { $_.FullName })
    $exit = 0
    if ($gateClasses.Count -gt 0) {
        $script:LastDurations = @()
        $exit = Invoke-AffectedGateRun $gateClasses
        Show-AffectedQuietClasses $gateClasses
    }
    if ($exit -eq 0 -and $headlessClasses.Count -gt 0) {
        $filter = @(); foreach ($c in $headlessClasses) { $filter += @('--filter-class', $c) }
        $exit = Invoke-HeadlessShardedRun 'affected-headless' $filter
    }
    $verdict = if ($exit -eq 0) { 'green' } else { "RED (exit $exit)" }
    Write-Host ("affected: {0} - {1} gate + {2} headless class(es), {3}s; this is not the full gate" -f $verdict, $gateClasses.Count, $headlessClasses.Count, [int]((Get-Date) - $started).TotalSeconds)
    return $exit
}

if ($ParseTrx) {
    $code = Show-TrxResults 'parsed' (Resolve-Path -LiteralPath $ParseTrx).Path '(none)' 1 0
    exit $code
}

# The golden-screenshot class: excluded from -Headless by this EXACT name, run alone by -Goldens.
$GoldenClass = 'DotCharter.Tests.Perf.ScreenGoldenUiPerfSmokeTests'

$script:GateWeightsPath = Join-Path $repoRoot 'tools\ci\gate-shard-weights.json'
$HeadlessFilter = @('--filter-class', '*UiPerfSmokeTests', '--filter-not-class', $GoldenClass)

$affectedSelection = $null
if ($Affected) {
    if ($Build -or $Gate -or $ReleaseLane -or $Headless -or $Goldens -or $Public -or $FilterClass -or $SelfTest -or $UpdateShardWeights) {
        Write-Host '-Affected picks its own classes: do not combine it with -Build, -Gate, -ReleaseLane, -Headless, -Goldens, -Public, -FilterClass, -SelfTest or -UpdateShardWeights.'
        exit 2
    }
    $affectedSelection = Get-AffectedRunSelection
    if ($SelectOnly -or $affectedSelection.NoChanges) { exit 0 }
    if ($affectedSelection.Fallback) { Write-Host 'affected: running the full default gate (-Gate); add -Headless after a view change'; $Gate = $true; $Affected = $false }
}

$wantsTests = $Gate -or $ReleaseLane -or $Headless -or $Goldens -or [bool]$FilterClass -or $SelfTest -or $Affected
if ($Public -and -not ($Build -or $wantsTests)) { $Gate = $true; $wantsTests = $true }
if (-not ($Build -or $wantsTests)) { $Build = $true; $Gate = $true; $wantsTests = $true }
if ($wantsTests -and -not $NoBuild) { $Build = $true }

$exit = 0
if ($Build) { $exit = Invoke-Build; if ($exit -ne 0) { exit $exit } }
if ($SelfTest) { $exit = Invoke-GateShardSelfTest; if ($exit -ne 0) { exit $exit } }
if ($Affected) { $exit = Invoke-AffectedRun $affectedSelection; exit $exit }

# Both suites at once when memory allows (each in its own testhosts already).
$together = $false
if ($Gate -and $Headless -and -not $Sequential) {
    Import-Module (Join-Path $repoRoot 'tools\ci\GateShards.psm1') -Force
    $decision = Get-GateConcurrencyDecision (Get-FreePhysicalMemoryBytes) $MinFreeGB
    Write-Host "gate + headless: $($decision.Reason)"
    $together = $decision.Concurrent
}
if ($together) {
    $h = Start-HeadlessShardedRun 'headless' $HeadlessFilter
    $g = Start-GateShardedRun 'gate'
    $all = @($g.Runs)
    if (-not $h.Failed) { $all += @($h.Runs) }
    $all = @(Wait-TestShards -Runs $all)
    Write-Host ("gate + headless: {0} testhost(s) concurrent; peak working set of all together {1:0.00} GB" -f $all.Count, ($all[0].PeakSumBytes / 1GB))
    $gateExit = Complete-GateShardedRun $g -Together
    $headlessExit = if ($h.Failed) { $h.Failed } else { Complete-HeadlessShardedRun $h -Together }
    Write-Host "gate + headless: gate exit $gateExit, headless exit $headlessExit"
    $exit = if ($gateExit -ne 0) { $gateExit } else { $headlessExit }
    if ($exit -ne 0) { exit $exit }
}
else {
    if ($Gate) { $exit = Invoke-GateRun 'gate'; if ($exit -ne 0) { exit $exit } }
    if ($Headless) { $exit = Invoke-HeadlessShardedRun 'headless' $HeadlessFilter; if ($exit -ne 0) { exit $exit } }
}
# After the default gate, so a red gate stops first; before the goldens.
if ($ReleaseLane) { $exit = Invoke-ReleaseLaneRun 'release'; if ($exit -ne 0) { exit $exit } }
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

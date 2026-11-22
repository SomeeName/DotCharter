<#
.SYNOPSIS
    Gate several ready pull requests ONCE, together, then merge them in order.

.DESCRIPTION
    Reused green evidence is never re-saved by this attempt. It is re-read on
    its original tree and inputs before acceptance and, after all network preflight,
    directly before the ref update;
    lost evidence stops the operation and names the lane to re-run.

    Gate-record reads/updates use one exclusive store lock (10-second bounded
    contention). Planned lanes revoke before execution and unaccepted lanes
    revoke again in finally: a later red completion removes concurrent greens.

    The merge bar asks that a branch contains current main and passed the gate.
    Merged one at a time, every merge makes the next branch stale, so each PR
    needs a rebase and another ~8 minute gate. A TRAIN tests the exact tree the
    merges will produce instead:

      1. the train worktree, .claude/worktrees/train, reset to origin/main;
      2. each PR's head merged into it, in the given order, with `--no-ff` -
         the same three-way merges GitHub's "Create a merge commit" performs;
      3. the full default gate, plus the headless suite when the train touches
         a view (.axaml, Views/) or when -Headless is given, plus the RELEASE
         LANE (tools/ci/release-lane.json: the slow corpus / integration classes
         the default gate leaves out) - or, when the train changes only
         documentation, the docs fast path (below);
      4. with -Merge and everything green: push the immutable tested tip ONCE,
         without force, to origin/main (MergeTrain.psm1 Invoke-MergeTrainMerges).
         A divergent base move rejects the whole train; no PR lands or receives
         a success comment. Confirm every PR MERGED before commenting on any.
         Local merge messages follow GitHub's form, and their author/committer
         use repository user.name/user.email (a GitHub noreply address required).

    ONE REUSED WORKTREE. The train worktree is kept between trains: each run
    resets it to origin/main and deletes untracked files, but keeps the ignored
    bin/ and obj/, so the build is incremental instead of a whole-solution
    compile. It is rebuilt from scratch (deleted, re-created by the
    dotcharter-worktree script, empty bin/obj) only when main's JuceSharp
    gitlink differs from the one it was created at - recorded in a marker file
    in the worktree's git directory - or with -Fresh. A lock file in the git
    directory, held open exclusively for the whole run (Enter-MergeTrainLock,
    #610), stops a second train from resetting the worktree under a running
    one; it is deleted when its handle closes, also when the train dies.

    DOCS FAST PATH. When the train's combined change against origin/main is
    only `*.md` files and files under `docs/` (a .resx is NOT docs), the gate
    is the build plus the lints that read those files
    (Get-MergeTrainDocsOnlyClasses in tools/agent/MergeTrain.psm1, among them
    DocReferenceLintTests), not the full suite or the release lane. -Headless
    asks for the full path.

    THE RELEASE LANE runs on every train that is not docs-only, so the classes
    moved out of the default gate are never dropped from a merge. Since #325 it
    runs BESIDE the gate (and the headless suite) in ONE Invoke-Gate.ps1
    -Gate -ReleaseLane [-Headless] call on a RELEASE build (as CI): the lanes
    share -Shards testhosts per lane, split by tools/ci/lane-weights.json, and
    the call's `lanes:` line gives each lane's verdict
    (Get-MergeTrainLaneVerdicts). Short of memory, Invoke-Gate runs them one
    after the other and stops at the first red lane. The green tree is recorded
    with it (ReleaseCache.psm1 -ReleaseLane), which New-Release.ps1 reuses.

    Every planned lane revokes older green evidence BEFORE build/execution,
    including forced runs that fail or crash. Records are configuration/input scoped.

    RED TRAINS KEEP THEIR GREEN LANES (#614). A red train records the lanes
    that actually ran and passed against the tested tree. Run the same train again on the same
    main (same tree) and only the lanes without a green record run - the red
    one, in full, alone (Get-MergeTrainLanePlan); -ForceGates runs every lane.

    OWN PROCESSES ONLY. Before the reset and after the gates, the train stops
    stray test hosts whose executable or command line lies inside the train
    worktree (Select-MergeTrainOwnProcesses), and no others: other agents'
    gates run at the same time.

    CPUS (#171). On a box with 16 or more logical processors the train pins
    itself at below-normal priority before it starts anything: CPUs 8-15 by
    default (the owner games on CPUs 0-7), ALL cores only with -AllCores, passed
    by hand when the owner is not gaming (it widens even a run started under a
    `/affinity FF00` wrapper, and doubles the default -Shards). Git, the build
    and every test host inherit it, the child gates get the same switch, and
    the summary line ends with `cpus half|all`.
    tools/agent/DevBuild.psm1 holds the decision.

    The headless suite's known harness faults (the needles in
    tools/ci/HeadlessShards.psm1: the setup race of #192, Avalonia's 5 s
    teardown drain) do not stop a train on their own: when EVERY headless
    failure carries one, Invoke-Gate.ps1 re-runs those classes alone in a fresh
    process, up to -RaceReruns times, and the train is green only if they then
    pass. The re-run is logged, in the verdict line and in each merged PR's
    comment. A failure without a needle is never re-run away.

    Because GitHub repeats exactly those merges in exactly that order, the
    tested tree is the tree that lands, so a branch "behind main" inside a green
    train is safe. A conflict, a red gate or a moved head stops the train before
    anything is merged.

    Not for a PR that moves the JuceSharp gitlink: that needs a native build of
    the new submodule commit, which a train worktree does not do. Merge such a
    PR on its own (JuceSharp PR first, as a merge commit).

.PARAMETER Prs
    Pull request numbers, in merge order.
.PARAMETER Merge
    Merge on GitHub when everything is green. Without it the train only reports.
.PARAMETER Headless
    Run the headless suite even when no PR touches a view (and never take the
    docs fast path).
.PARAMETER Shards
    Testhosts per lane (default: 4 on CPUs 8-15, 8 on all cores). The lanes run
    in ONE Invoke-Gate.ps1 call, concurrently when enough memory is free, out of
    -Shards times the lane count testhosts, dealt by tools/ci/lane-weights.json.
.PARAMETER RaceReruns
    How many times a headless class whose every failure is a harness fault is
    re-run alone before the train stops (default 2; 0 = stop on the first).
.PARAMETER Fresh
    Delete the train worktree and build it again from scratch.
.PARAMETER ForceGates
    Run every lane even when a green run of it is on record for the tested tree.

.EXAMPLE
    powershell -NoProfile -File tools/agent/Merge-Train.ps1 -Prs 142,143,144
    powershell -NoProfile -File tools/agent/Merge-Train.ps1 -Prs 142,143,144 -Merge
    powershell -NoProfile -File tools/agent/Merge-Train.ps1 -Prs 142,143,144 -AllCores   # every core
#>
param(
    # Strings, split below: `powershell -File … -Prs 142,143` passes "142,143" as ONE string.
    [Parameter(Mandatory)][string[]]$Prs,
    [switch]$Merge,
    [switch]$Headless,
    # 0 (default) follows the CPU plan: 4 on CPUs 8-15, 8 on all cores.
    [ValidateRange(0, 16)]
    [int]$Shards = 0,
    # CPU plan (#171): default CPUs 8-15; -AllCores by hand uses every core (children too).
    [switch]$AllCores,
    [ValidateRange(0, 5)]
    [int]$RaceReruns = 2,
    [switch]$Fresh,
    # Run every lane even when a green run of it is on record for the tested tree (#614).
    [switch]$ForceGates
)

$ErrorActionPreference = 'Stop'
[int[]]$Prs = @($Prs | ForEach-Object { $_ -split '[,\s]+' } | Where-Object { $_ } | ForEach-Object { [int]$_ })
Import-Module (Join-Path $PSScriptRoot 'MergeTrain.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'DevBuild.psm1') -Force
# The MAIN checkout, even when this runs from a worktree: worktrees are created
# under it (the dotcharter-worktree script resolves the same way).
$common = "$(git -C $PSScriptRoot rev-parse --path-format=absolute --git-common-dir)".Trim() -replace '/', '\'
$main = Split-Path $common -Parent
$repo = 'SomeeName/DotCharter'
$wt = Join-Path $main '.claude\worktrees\train'
$branch = 'train/current'
$lock = Join-Path $common 'dotcharter-merge-train.lock'

# CPU plan (#171, DevBuild.psm1): CPUs 8-15 by default, all cores only with -AllCores;
# below-normal priority, inherited by every process started below. The child
# gates get the same choice as a switch.
$cpuPlan = Set-DevCpuPlan -AllCores:$AllCores
if ($Shards -eq 0) { $Shards = $cpuPlan.Shards }
$cpuSwitch = @(if ($cpuPlan.Name -eq 'all') { '-AllCores' })

# Dev builds keep bin/obj incremental (SourceLink off: tools/agent/DevBuild.psm1).
Enable-IncrementalDevBuild

function Invoke-Git([string[]]$GitArgs, [string]$Dir = $main) {
    $ErrorActionPreference = 'Continue'
    $out = & git -C $Dir @GitArgs 2>&1
    $code = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    return @{ Out = ($out | Out-String).Trim(); Code = $code }
}

function Stop-TrainTestHosts {
    $hosts = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue `
            -Filter "Name='testhost.exe' OR Name='DotCharter.Tests.exe' OR Name='JuceSharp.Tests.exe'")
    foreach ($p in @(Select-MergeTrainOwnProcesses $hosts $wt)) {
        Write-Host "  stopping a stray test host of this train: $($p.Name) (PID $($p.ProcessId))"
        Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
    }
}

function Remove-TrainWorktree {
    # Right after a gate the build-server nodes can still hold a file open for a
    # few seconds; retry rather than leave a half-deleted worktree behind.
    for ($try = 0; $try -lt 5 -and (Test-Path $wt); $try++) {
        if ($try -gt 0) { Start-Sleep -Seconds 5 }
        Remove-Item -LiteralPath "\\?\$wt" -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path $wt) { throw "Could not delete $wt (a process holds a file); close it and re-run." }
    Invoke-Git @('worktree', 'prune') | Out-Null
}

function Get-TrainMarkerPath {
    $gitDir = Invoke-Git @('rev-parse', '--absolute-git-dir') $wt
    if ($gitDir.Code -ne 0) { return '' }
    return Join-Path ($gitDir.Out -replace '/', '\') 'dotcharter-train-juce'
}

Write-Host "== Merge train: $($Prs -join ', ')"
$fetched = Invoke-Git @('fetch', '--quiet', 'origin')
if ($fetched.Code -ne 0) { throw "git fetch origin failed, so origin/main may be stale: $($fetched.Out)" }

# ---- 1. what each PR is ------------------------------------------------------
$info = @()
foreach ($n in $Prs) {
    $prJson = gh pr view $n --repo $repo --json number,state,baseRefName,headRefOid,title,files,headRepositoryOwner,headRefName
    if ($LASTEXITCODE -ne 0) { throw "gh pr view #$n failed (exit $LASTEXITCODE)." }
    $pr = $prJson | ConvertFrom-Json
    if ($pr.state -ne 'OPEN') { throw "#${n} is $($pr.state), not OPEN." }
    if ($pr.baseRefName -ne 'main') { throw "#${n} targets $($pr.baseRefName), not main." }
    $paths = @($pr.files | ForEach-Object { $_.path })
    if ($paths -contains 'JuceSharp') {
        throw "#${n} moves the JuceSharp gitlink - merge it on its own (it needs a native build)."
    }
    $info += [pscustomobject]@{ Number = $n; Head = $pr.headRefOid; Title = $pr.title; Owner = $pr.headRepositoryOwner.login; Branch = $pr.headRefName; MergeCommit = $null }
    Write-Host ("  #{0} {1} @ {2}" -f $n, $pr.title, $pr.headRefOid.Substring(0, 9))
}

# ---- 2. the train worktree ---------------------------------------------------
# One exclusive handle, held until the train ends (MergeTrain.psm1 Enter-MergeTrainLock):
# a second train cannot open it, and a dead train's handle closes with it.
$lockHandle = Enter-MergeTrainLock $lock
$recordTrees = @(); $plannedKinds = @(); $acceptedKinds = @(); $recordScope = @{}
try {
    $prepared = Get-Date
    # The main every PR is merged onto, fixed once: the reset, the change list and the
    # merge-time base check all name this commit, not whatever origin/main is later.
    $based = Invoke-Git @('rev-parse', '--verify', 'origin/main^{commit}')
    if ($based.Code -ne 0) { throw "Could not resolve origin/main: $($based.Out)" }
    $testedBase = $based.Out
    $wantJuce = (Invoke-Git @('rev-parse', "$testedBase`:JuceSharp")).Out
    $listed = @((Invoke-Git @('worktree', 'list', '--porcelain')).Out -split "`r?`n" |
            Where-Object { $_ -like 'worktree *' } | ForEach-Object { $_.Substring(9).Trim() -replace '/', '\' })
    $marker = if ((Test-Path $wt) -and ($listed -contains $wt)) { Get-TrainMarkerPath } else { '' }
    $rebuild = if ($Fresh) { '-Fresh' }
    elseif (-not (Test-Path $wt)) { 'no train worktree yet' }
    elseif (-not $marker) { 'not a registered worktree' }
    elseif (-not (Test-Path -LiteralPath $marker)) { 'no JuceSharp marker' }
    elseif ((Get-Content -LiteralPath $marker -Raw).Trim() -ne $wantJuce) {
        "main's JuceSharp moved ($((Get-Content -LiteralPath $marker -Raw).Trim().Substring(0, 9)) -> $($wantJuce.Substring(0, 9)))"
    }
    else { '' }

    Stop-TrainTestHosts
    if ($rebuild) {
        Write-Host "  train worktree: FRESH ($rebuild)"
        if (Test-Path $wt) { Remove-TrainWorktree }
        Invoke-Git @('worktree', 'prune') | Out-Null
        if ((Invoke-Git @('rev-parse', '--verify', '--quiet', "refs/heads/$branch")).Code -eq 0) {
            Invoke-Git @('branch', '-D', $branch) | Out-Null
        }
        # The worktree script that ships WITH this one (its checkout), not the main
        # checkout's copy, which can be older (it once lacked the DLL check and the
        # train's gate failed on a DLL that predated the native code).
        $worktreeScript = Join-Path $PSScriptRoot '..\..\.claude\skills\dotcharter-worktree\scripts\New-AgentWorktree.ps1'
        & powershell -NoProfile -File $worktreeScript -Name 'train' -Branch $branch -Base origin/main | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'Could not create the train worktree.' }
        $marker = Get-TrainMarkerPath
        if (-not $marker) { throw "$wt is not a git worktree after creating it." }
        Set-Content -LiteralPath $marker -Value $wantJuce -Encoding ascii
        $r = Invoke-Git @('reset', '--hard', '--quiet', $testedBase) $wt
        if ($r.Code -ne 0) { throw "Could not reset $wt to $testedBase`: $($r.Out)" }
    }
    else {
        Write-Host "  train worktree: INCREMENTAL (reset to origin/main, bin/obj kept)"
        Invoke-Git @('merge', '--abort') $wt | Out-Null   # a conflict the last train left
        $r = Invoke-Git @('reset', '--hard', '--quiet', $testedBase) $wt
        if ($r.Code -ne 0) { throw "Could not reset $wt to origin/main ($testedBase): $($r.Out)" }
        # Untracked files only; the ignored bin/obj (and the JuceSharp copy, a gitlink path) stay.
        $c = Invoke-Git @('clean', '-fdq') $wt
        if ($c.Code -ne 0) { throw "Could not clean ${wt}: $($c.Out)" }
    }
    $prepareSeconds = [int]((Get-Date) - $prepared).TotalSeconds

    $invokeTrainGit = ${function:Invoke-Git}
    $trainGit = { param([string[]]$GitArgs) & $invokeTrainGit -GitArgs $GitArgs -Dir $wt }.GetNewClosure()
    $mergedSoFar = @()
    foreach ($p in $info) {
        $f = Invoke-Git @('fetch', '--quiet', 'origin', "pull/$($p.Number)/head") $wt
        if ($f.Code -ne 0) { throw "fetch of #$($p.Number) failed: $($f.Out)" }
        $m = New-MergeTrainCommit -Pr $p -Git $trainGit
        if ($m.Code -ne 0) {
            Invoke-Git @('merge', '--abort') $wt | Out-Null
            $before = if ($mergedSoFar) { "after #$($mergedSoFar -join ', #')" } else { 'onto main' }
            Write-Host "CONFLICT merging #$($p.Number) $before - rebase it (or reorder the train)."
            Write-Host $m.Out
            exit 1
        }
        $p.MergeCommit = $m.Commit
        $mergedSoFar += $p.Number
        Write-Host "  merged #$($p.Number)"
    }

    $tipResult = Invoke-Git @('rev-parse', '--verify', 'HEAD^{commit}') $wt
    if ($tipResult.Code -ne 0) { throw "Could not fix the train tip: $($tipResult.Out)" }
    $testedTip = $tipResult.Out

    # What the merged tree changes against main: the exact set (no 100-file cap,
    # unlike the PR file lists) that decides which gates run.
    $d = Invoke-Git @('-c', 'core.quotepath=off', 'diff', '--name-only', $testedBase, 'HEAD') $wt
    if ($d.Code -ne 0) { throw "git diff against origin/main failed: $($d.Out)" }
    $changed = @($d.Out -split "`r?`n" | Where-Object { $_ })
    if ($changed -contains 'JuceSharp') { throw 'The train moves the JuceSharp gitlink - merge that PR on its own (it needs a native build).' }
    $docsOnly = (-not $Headless) -and (Test-MergeTrainDocsOnly $changed)
    $touchesViews = Test-MergeTrainTouchesViews $changed

    # Verify the merged tree on every run, including reused worktrees and cached lanes.
    Import-Module (Join-Path $PSScriptRoot '../../.claude/skills/dotcharter-worktree/scripts/AgentWorktree.psm1') -Force
    Import-Module (Join-Path $wt 'tools/setup/NativeRuntime.psm1') -Force
    $nativeGitlink = Get-JuceSharpGitlink $wt
    $nativeState = Get-JuceNativePairState -RepoRoot $wt -Gitlink $nativeGitlink
    $nativePlan = Get-JuceNativePairPlan -Gitlink $nativeGitlink -Train `
        -ManifestEntry $nativeState.ManifestEntry -Hashes $nativeState.Hashes -FailureReason $nativeState.FailureReason
    if ($nativePlan.Action -eq 'install pinned') {
        $installer = Join-Path $wt 'tools/setup/Get-NativeRuntime.ps1'
        $installFailure = ''
        try {
            $installOutput = & powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $installer -Force 2>&1
            $installCode = $LASTEXITCODE
            $installOutput | Out-Host
            if ($installCode -ne 0) { $installFailure = "Pinned native runtime installation failed (exit ${installCode}): $(($installOutput | Out-String).Trim())" }
        }
        catch { $installFailure = "Pinned native runtime installation failed: $($_.Exception.Message)" }
        $nativeState = Get-JuceNativePairState -RepoRoot $wt -Gitlink $nativeGitlink
        $failure = (@($nativeState.FailureReason, $installFailure) | Where-Object { $_ }) -join '; '
        $nativePlan = Get-JuceNativePairPlan -Gitlink $nativeGitlink -Train -InstallAttempted `
            -ManifestEntry $nativeState.ManifestEntry -Hashes $nativeState.Hashes -FailureReason $failure
    }
    if ($nativePlan.Action -ne 'keep') { throw $nativePlan.Reason }
    Write-Host "  native pair: $($nativePlan.Reason)"

    # ---- 3. the gates --------------------------------------------------------
    # ONE Invoke-Gate.ps1 call for every lane the train needs (#325): the default
    # gate, the release lane and, when the train touches a view, the headless
    # suite run side by side when memory allows, and the call ends with
    # `lanes: gate exit G[, headless exit H], release exit R`. Release build, as
    # CI: the managed DSP and analysis classes run ~2-3x faster than on Debug.
    $gate = Join-Path $wt 'tools\agent\Invoke-Gate.ps1'
    # The tested tree, and the lanes a green run on record for it already covers
    # (#614): a re-run after a red lane runs only the lanes not yet green.
    $trainTree = $null
    $reused = @(); $reusedRecords = @{}; $executedGreenLanes = @()
    if (-not $docsOnly) {
        Import-Module (Join-Path $PSScriptRoot '..\release\ReleaseCache.psm1') -Force
        $trainTree = Get-CleanTreeId -Repo $wt
        $scope = Get-GateRecordInputs -RepoRoot $wt -Configuration Release
        $laneInputKey = $scope.InputKey
    }
    $gated = Get-Date
    Push-Location $wt
    try {
        $headlessOk = $true
        $releaseOk = $true
        $raceRerun = ''
        $ranHeadless = $false
        $ranRelease = $false
        if ($docsOnly) {
            $docsClasses = (Get-MergeTrainDocsOnlyClasses) -join ','
            Write-Host "  docs only ($($changed.Count) file(s)): build + $docsClasses"
            & powershell -NoProfile -File $gate @cpuSwitch -Configuration Release -FilterClass $docsClasses | Out-Host
            $gateOk = $LASTEXITCODE -eq 0
        }
        else {
            $wantHeadless = $Headless -or $touchesViews
            $laneArgs = @('-Gate', '-ReleaseLane') + @(if ($wantHeadless) { '-Headless' })
            $treeIds = @(if ($trainTree) { $trainTree })
            $laneRecords = @{ gate = (Find-GateRecord -Trees $treeIds -Kind gate @scope); headless = (Find-GateRecord -Trees $treeIds -Kind headless @scope); release = (Find-GateRecord -Trees $treeIds -Kind releaseLane @scope) }
            $lanePlan = Get-MergeTrainLanePlan -WantHeadless $wantHeadless -Force ([bool]$ForceGates) -Configuration Release -InputKey $laneInputKey `
                -GateRecord $laneRecords.gate -HeadlessRecord $laneRecords.headless -ReleaseRecord $laneRecords.release
            $plannedKinds = @($lanePlan.Run | ForEach-Object { if ($_ -eq 'release') { 'releaseLane' } else { $_ } })
            $recordTrees = $treeIds; $recordScope = $scope
            Start-GateRecordRun -Trees $treeIds -Kinds $plannedKinds @scope
            $reused = @($lanePlan.Reused)
            foreach ($lane in $reused) { $kind = if ($lane -eq 'release') { 'releaseLane' } else { $lane }; $reusedRecords[$kind] = $laneRecords[$lane] }
            $laneArgs = @($laneArgs | Where-Object { $lanePlan.Args -contains $_ })
            if ($reused.Count -gt 0) { Write-Host "  green on record for tree ${trainTree}, not run again: $($reused -join ', ') (-ForceGates runs them)" }
            $gateOut = @()
            $code = 0
            if ($laneArgs.Count -gt 0) {
                & powershell -NoProfile -File $gate @cpuSwitch -Configuration Release @laneArgs -Shards $Shards -RaceReruns $RaceReruns |
                    Tee-Object -Variable gateOut | Out-Host
                $code = $LASTEXITCODE
            }
            $verdicts = Get-MergeTrainLaneVerdicts @($gateOut) $code $wantHeadless $reused
            $executedGreenLanes = @(Get-MergeTrainGreenLanes $verdicts | Where-Object { $lanePlan.Run -contains $_ })
            $acceptedKinds = @($executedGreenLanes | ForEach-Object { if ($_ -eq 'release') { 'releaseLane' } else { $_ } })
            $gateOk = $verdicts.GateOk -and $verdicts.Complete
            $headlessOk = $verdicts.HeadlessOk
            $releaseOk = $verdicts.ReleaseOk
            $ranHeadless = $verdicts.RanHeadless
            $ranRelease = $verdicts.RanRelease
            $lost = @(Get-LostGateRecordKinds -Records $reusedRecords @scope)
            foreach ($kind in $lost) { Write-Host "Reused $kind evidence lost. Re-run will execute this lane; nothing will land." }
            if ($lost -contains 'gate') { $gateOk = $false }
            if ($lost -contains 'headless') { $headlessOk = $false }
            if ($lost -contains 'releaseLane') { $releaseOk = $false }
            # Invoke-Gate prints `headless race re-run: PASSED ...` when the run is
            # green only because harness-fault classes passed alone afterwards.
            $raceRerun = [string](@($gateOut) | Where-Object { "$_" -match 'headless race re-run: PASSED' } | Select-Object -First 1)
        }
    }
    finally { Pop-Location }
    $gateSeconds = [int]((Get-Date) - $gated).TotalSeconds
    Stop-TrainTestHosts

    $verdict = if (-not $gateOk) { 'gate RED' } elseif (-not $headlessOk) { 'headless RED' } elseif (-not $releaseOk) { 'release lane RED' } else { 'GREEN' }
    $gateKind = if ($docsOnly) { 'docs fast path' } else { 'full gate' }
    $headlessNote = if ($reused -contains 'headless') { 'green on record for this tree' } elseif ($ranHeadless -and $raceRerun) { "run; GREEN AFTER A RACE RE-RUN -$($raceRerun.Trim() -replace '^headless race re-run: PASSED', '')" } `
        elseif ($ranHeadless -and -not $gateOk) { 'run alongside the red gate' } elseif ($ranHeadless) { 'run' } elseif (-not $gateOk) { 'skipped: the gate is red' } elseif ($docsOnly) { 'not needed: docs only' } `
        else { 'not needed: no view changes' }
    # Invoke-Gate keeps a failed headless run's TRX and messages (#55) and prints where.
    $evidenceLine = [string](@($gateOut) | Where-Object { "$_" -match 'headless failure evidence kept:' } | Select-Object -First 1)
    if ($evidenceLine) { Write-Host "== $($evidenceLine.Trim())" }
    $worktreeNote = if ($rebuild) { "fresh: $rebuild" } else { 'incremental' }
    $releaseNote = if ($reused -contains 'release') { 'green on record for this tree' } elseif ($ranRelease -and $releaseOk) { 'run' } elseif ($ranRelease) { 'RED' } elseif ($docsOnly) { 'not needed: docs only' } else { 'skipped: an earlier gate is red' }
    Write-Host "== Train $($Prs -join ', '): $verdict ($gateKind; headless $headlessNote; release lane $releaseNote) - worktree $worktreeNote ${prepareSeconds}s, gates ${gateSeconds}s, cpus $($cpuPlan.Name)"
    if ($verdict -ne 'GREEN') {
        # The lanes that DID pass stay on record for this exact tree (#614): re-running the
        # same train on the same main runs only the red lane, in full, alone.
        $greenLanes = if ($docsOnly) { @() } else { @($executedGreenLanes) }
        if ($trainTree -and $greenLanes.Count -gt 0) {
            $partialArgs = @{ Tree = $trainTree; Commit = $testedTip; Gate = ($greenLanes -contains 'gate')
                Headless = ($greenLanes -contains 'headless'); ReleaseLane = ($greenLanes -contains 'release'); Source = "Merge-Train.ps1 ($($Prs -join ', '), red train)"; Configuration = 'Release'; InputKey = $laneInputKey }
            $partialPath = Save-GateRecord @partialArgs
            Write-Host "== Recorded the green lanes of this red train ($($greenLanes -join ', ')) on tree $trainTree ($partialPath)"
            Write-Host '   Re-run the same train to run only the red lane again (-ForceGates runs every lane).'
        }
        Write-Host "Nothing merged. Re-run a red class alone before calling it a flake; the train worktree is $wt"
        exit 1
    }

    # The green tree on record (tools/release/ReleaseCache.psm1), so the public export's
    # -Gate and New-Release's headless preflight skip the identical test run on it.
    if (-not $docsOnly) {
        if ($trainTree -and $executedGreenLanes.Count -gt 0) {
            $recordArgs = @{ Tree = $trainTree; Commit = $testedTip; Gate = ($executedGreenLanes -contains 'gate'); Headless = ($executedGreenLanes -contains 'headless'); ReleaseLane = ($executedGreenLanes -contains 'release'); Source = "Merge-Train.ps1 ($($Prs -join ', '))"; Configuration = 'Release'; InputKey = $laneInputKey }
            $recordPath = Save-GateRecord @recordArgs
            Write-Host "== Recorded: executed green lanes $($executedGreenLanes -join ', ') on tree $trainTree ($recordPath)"
        }
        elseif (-not $trainTree) { Write-Host '== Not recorded: the train worktree is not clean HEAD.' }
        else { Write-Host '== Reused evidence was read, not rewritten: no lane executed.' }
    }

    # ---- 4. merge in the tested order ----------------------------------------
    if (-not $Merge) {
        Write-Host 'Report only (-Merge not given). Nothing merged.'
        exit 0
    }

    $tested = if ($docsOnly) { 'the build and the docs lints (a docs-only train)' } else { "the full gate and the release lane$(if ($ranHeadless) { ' and the headless suite' })" }
    $comment = "Merged in a train with $($Prs -join ', ') (in that order). The combined tree passed $tested as one, so this branch did not need its own rebase.$(if ($raceRerun) { ' The headless suite needed a race re-run: every first-run failure carried the known harness-fault signature (#192), and those classes then passed alone in a fresh process.' })"
    # Land exactly the gated commit chain with one non-forced ref update.
    # Git/gh are injectable; every confirmation completes before any comment.
    $gh = {
        param([string[]]$GhArgs)
        $ErrorActionPreference = 'Continue'
        $out = & gh @GhArgs 2>&1
        @{ Out = ($out | Out-String).Trim(); Code = $LASTEXITCODE }
    }
    $beforePush = $null
    if (-not $docsOnly) {
        # Capture the function itself: the callback executes from the landing module.
        $assertReuse = ${function:Assert-GateRecordsCurrent}
        $beforePush = { & $assertReuse -Records $reusedRecords @scope }.GetNewClosure()
    }
    $null = Invoke-MergeTrainMerges -Prs $info -Repo $repo -ExpectedBase $testedBase -Comment $comment -Gh $gh -Git $trainGit -Tip $testedTip -BeforePush $beforePush
    Write-Host "== Train merged: $($Prs -join ', ')"
}
finally {
    try {
        if ($recordTrees.Count -gt 0) { Complete-GateRecordRun -Trees $recordTrees -Kinds $plannedKinds -AcceptedKinds $acceptedKinds @recordScope }
    }
    finally { Exit-MergeTrainLock $lockHandle }
}

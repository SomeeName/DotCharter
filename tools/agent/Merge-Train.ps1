<#
.SYNOPSIS
    Gate several ready pull requests ONCE, together, then merge them in order.

.DESCRIPTION
    The merge bar asks that a branch contains current main and passed the gate.
    Merged one at a time, every merge makes the next branch stale, so each PR
    needs a rebase and another ~8 minute gate. A TRAIN tests the exact tree the
    merges will produce instead:

      1. the train worktree, .claude/worktrees/train, reset to origin/main;
      2. each PR's head merged into it, in the given order, with `--no-ff` -
         the same three-way merges GitHub's "Create a merge commit" performs;
      3. the full default gate, plus the headless suite when the train touches
         a view (.axaml, Views/) or when -Headless is given - or, when the train
         changes only documentation, the docs fast path (below);
      4. with -Merge and everything green: each PR merged on GitHub in the SAME
         order, after checking its head did not move since it was tested.

    ONE REUSED WORKTREE. The train worktree is kept between trains: each run
    resets it to origin/main and deletes untracked files, but keeps the ignored
    bin/ and obj/, so the build is incremental instead of a whole-solution
    compile. It is rebuilt from scratch (deleted, re-created by the
    dotcharter-worktree script, empty bin/obj) only when main's JuceSharp
    gitlink differs from the one it was created at - recorded in a marker file
    in the worktree's git directory - or with -Fresh. A lock file in the git
    directory stops a second train from resetting the worktree under a running
    one.

    DOCS FAST PATH. When the train's combined change against origin/main is
    only `*.md` files and files under `docs/` (a .resx is NOT docs), the gate
    is the build plus the lints that read those files
    (Get-MergeTrainDocsOnlyClasses in tools/agent/MergeTrain.psm1, among them
    DocReferenceLintTests), not the full suite. -Headless asks for the full
    path.

    OWN PROCESSES ONLY. Before the reset and after the gates, the train stops
    stray test hosts whose executable or command line lies inside the train
    worktree (Select-MergeTrainOwnProcesses), and no others: other agents'
    gates run at the same time.

    CPUS. On a box with 16 or more logical processors the train moves itself
    to CPUs 8-15 at below-normal priority before it starts anything; git, the
    build and every test host inherit both (the owner games on CPUs 0-7).

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
    Headless shards (default 4, as Invoke-Gate.ps1 -Headless and the release
    preflight).
.PARAMETER RaceReruns
    How many times a headless class whose every failure is a harness fault is
    re-run alone before the train stops (default 2; 0 = stop on the first).
.PARAMETER Fresh
    Delete the train worktree and build it again from scratch.

.EXAMPLE
    powershell -NoProfile -File tools/agent/Merge-Train.ps1 -Prs 142,143,144
    powershell -NoProfile -File tools/agent/Merge-Train.ps1 -Prs 142,143,144 -Merge
#>
param(
    # Strings, split below: `powershell -File … -Prs 142,143` passes "142,143" as ONE string.
    [Parameter(Mandatory)][string[]]$Prs,
    [switch]$Merge,
    [switch]$Headless,
    [ValidateRange(1, 16)]
    [int]$Shards = 4,
    [ValidateRange(0, 5)]
    [int]$RaceReruns = 2,
    [switch]$Fresh
)

$ErrorActionPreference = 'Stop'
[int[]]$Prs = @($Prs | ForEach-Object { $_ -split '[,\s]+' } | Where-Object { $_ } | ForEach-Object { [int]$_ })
Import-Module (Join-Path $PSScriptRoot 'MergeTrain.psm1') -Force
# The MAIN checkout, even when this runs from a worktree: worktrees are created
# under it (the dotcharter-worktree script resolves the same way).
$common = "$(git -C $PSScriptRoot rev-parse --path-format=absolute --git-common-dir)".Trim() -replace '/', '\'
$main = Split-Path $common -Parent
$repo = 'SomeeName/DotCharter'
$wt = Join-Path $main '.claude\worktrees\train'
$branch = 'train/current'
$lock = Join-Path $common 'dotcharter-merge-train.lock'

# CPUs 8-15, below-normal priority, inherited by every process started below.
# Only ever narrowed and lowered: a caller already pinned (or at idle) stays so.
if ([int]$env:NUMBER_OF_PROCESSORS -ge 16) {
    $self = [Diagnostics.Process]::GetCurrentProcess()
    $mask = [int64]$self.ProcessorAffinity -band 0xFF00
    if ($mask -ne 0) { $self.ProcessorAffinity = [IntPtr]$mask }
    if (@('Idle', 'BelowNormal') -notcontains [string]$self.PriorityClass) { $self.PriorityClass = 'BelowNormal' }
}

# The SDK's implicit SourceLink stamps HEAD's commit id into every project's
# AssemblyInformationalVersion (and its SourceLink file, a compiler input). A
# train's HEAD is a NEW merge commit every run, so with it on, every project
# recompiles and the kept bin/obj save nothing (measured on #668: 187 s both
# cold and "incremental"). Off for the train's builds only; product code reads
# the version with or without the `+sha` suffix (RscpFormatContract, AppVersionTests).
$env:SuppressImplicitGitSourceLink = 'true'

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
Invoke-Git @('fetch', '--quiet', 'origin') | Out-Null

# ---- 1. what each PR is ------------------------------------------------------
$info = @()
foreach ($n in $Prs) {
    $pr = gh pr view $n --repo $repo --json number,state,baseRefName,headRefOid,title,files | ConvertFrom-Json
    if ($pr.state -ne 'OPEN') { throw "#${n} is $($pr.state), not OPEN." }
    if ($pr.baseRefName -ne 'main') { throw "#${n} targets $($pr.baseRefName), not main." }
    $paths = @($pr.files | ForEach-Object { $_.path })
    if ($paths -contains 'JuceSharp') {
        throw "#${n} moves the JuceSharp gitlink - merge it on its own (it needs a native build)."
    }
    $info += [pscustomobject]@{ Number = $n; Head = $pr.headRefOid; Title = $pr.title }
    Write-Host ("  #{0} {1} @ {2}" -f $n, $pr.title, $pr.headRefOid.Substring(0, 9))
}

# ---- 2. the train worktree ---------------------------------------------------
if (Test-Path $lock) {
    $holder = (Get-Content -LiteralPath $lock -Raw) -as [int]
    if ($holder -and $holder -ne $PID -and (Get-Process -Id $holder -ErrorAction SilentlyContinue)) {
        throw "Another merge train (PID $holder) is using $wt. Wait for it; if no train is running, delete $lock."
    }
}
Set-Content -LiteralPath $lock -Value $PID -Encoding ascii
try {
    $prepared = Get-Date
    $wantJuce = (Invoke-Git @('rev-parse', 'origin/main:JuceSharp')).Out
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
    }
    else {
        Write-Host "  train worktree: INCREMENTAL (reset to origin/main, bin/obj kept)"
        Invoke-Git @('merge', '--abort') $wt | Out-Null   # a conflict the last train left
        $r = Invoke-Git @('reset', '--hard', '--quiet', 'origin/main') $wt
        if ($r.Code -ne 0) { throw "Could not reset $wt to origin/main: $($r.Out)" }
        # Untracked files only; the ignored bin/obj (and the JuceSharp copy, a gitlink path) stay.
        $c = Invoke-Git @('clean', '-fdq') $wt
        if ($c.Code -ne 0) { throw "Could not clean ${wt}: $($c.Out)" }
    }
    $prepareSeconds = [int]((Get-Date) - $prepared).TotalSeconds

    $mergedSoFar = @()
    foreach ($p in $info) {
        $f = Invoke-Git @('fetch', '--quiet', 'origin', "pull/$($p.Number)/head") $wt
        if ($f.Code -ne 0) { throw "fetch of #$($p.Number) failed: $($f.Out)" }
        $m = Invoke-Git @('merge', '--no-ff', '--no-edit', '-m', "train: merge #$($p.Number)", $p.Head) $wt
        if ($m.Code -ne 0) {
            Invoke-Git @('merge', '--abort') $wt | Out-Null
            $before = if ($mergedSoFar) { "after #$($mergedSoFar -join ', #')" } else { 'onto main' }
            Write-Host "CONFLICT merging #$($p.Number) $before - rebase it (or reorder the train)."
            Write-Host $m.Out
            exit 1
        }
        $mergedSoFar += $p.Number
        Write-Host "  merged #$($p.Number)"
    }

    # What the merged tree changes against main: the exact set (no 100-file cap,
    # unlike the PR file lists) that decides which gates run.
    $d = Invoke-Git @('-c', 'core.quotepath=off', 'diff', '--name-only', 'origin/main', 'HEAD') $wt
    if ($d.Code -ne 0) { throw "git diff against origin/main failed: $($d.Out)" }
    $changed = @($d.Out -split "`r?`n" | Where-Object { $_ })
    if ($changed -contains 'JuceSharp') { throw 'The train moves the JuceSharp gitlink - merge that PR on its own (it needs a native build).' }
    $docsOnly = (-not $Headless) -and (Test-MergeTrainDocsOnly $changed)
    $touchesViews = Test-MergeTrainTouchesViews $changed

    # ---- 3. the gates --------------------------------------------------------
    $gate = Join-Path $wt 'tools\agent\Invoke-Gate.ps1'
    $gated = Get-Date
    Push-Location $wt
    try {
        if ($docsOnly) {
            $docsClasses = (Get-MergeTrainDocsOnlyClasses) -join ','
            Write-Host "  docs only ($($changed.Count) file(s)): build + $docsClasses"
            & powershell -NoProfile -File $gate -FilterClass $docsClasses | Out-Host
        }
        else {
            & powershell -NoProfile -File $gate -Gate | Out-Host
        }
        $gateOk = $LASTEXITCODE -eq 0
        $headlessOk = $true
        $raceRerun = ''
        $ranHeadless = $gateOk -and ($Headless -or $touchesViews)
        if ($ranHeadless) {
            & powershell -NoProfile -File $gate -Headless -Shards $Shards -NoBuild -RaceReruns $RaceReruns |
                Tee-Object -Variable headlessOut | Out-Host
            $headlessOk = $LASTEXITCODE -eq 0
            # Invoke-Gate prints `headless race re-run: PASSED ...` when the run is
            # green only because harness-fault classes passed alone afterwards.
            $raceRerun = [string](@($headlessOut) | Where-Object { "$_" -match 'headless race re-run: PASSED' } | Select-Object -First 1)
        }
    }
    finally { Pop-Location }
    $gateSeconds = [int]((Get-Date) - $gated).TotalSeconds
    Stop-TrainTestHosts

    $verdict = if (-not $gateOk) { 'gate RED' } elseif (-not $headlessOk) { 'headless RED' } else { 'GREEN' }
    $gateKind = if ($docsOnly) { 'docs fast path' } else { 'full gate' }
    $headlessNote = if ($ranHeadless -and $raceRerun) { "run; GREEN AFTER A RACE RE-RUN -$($raceRerun.Trim() -replace '^headless race re-run: PASSED', '')" } `
        elseif ($ranHeadless) { 'run' } elseif (-not $gateOk) { 'skipped: the gate is red' } elseif ($docsOnly) { 'not needed: docs only' } `
        else { 'not needed: no view changes' }
    # Invoke-Gate keeps a failed headless run's TRX and messages (#55) and prints where.
    $evidenceLine = [string](@($headlessOut) | Where-Object { "$_" -match 'headless failure evidence kept:' } | Select-Object -First 1)
    if ($evidenceLine) { Write-Host "== $($evidenceLine.Trim())" }
    $worktreeNote = if ($rebuild) { "fresh: $rebuild" } else { 'incremental' }
    Write-Host "== Train $($Prs -join ', '): $verdict ($gateKind; headless $headlessNote) - worktree $worktreeNote ${prepareSeconds}s, gates ${gateSeconds}s"
    if ($verdict -ne 'GREEN') {
        Write-Host "Nothing merged. Re-run a red class alone before calling it a flake; the train worktree is $wt"
        exit 1
    }

    # ---- 4. merge in the tested order ----------------------------------------
    if (-not $Merge) {
        Write-Host 'Report only (-Merge not given). Nothing merged.'
        exit 0
    }

    $tested = if ($docsOnly) { 'the build and the docs lints (a docs-only train)' } else { "the full gate$(if ($ranHeadless) { ' and the headless suite' })" }
    foreach ($p in $info) {
        $now = (gh pr view $p.Number --repo $repo --json headRefOid | ConvertFrom-Json).headRefOid
        if ($now -ne $p.Head) {
            throw "#$($p.Number) moved since it was tested ($($p.Head.Substring(0, 9)) -> $($now.Substring(0, 9))). Merged so far: stop here and re-run the train for the rest."
        }
        gh pr merge $p.Number --repo $repo --merge | Out-Host
        gh pr comment $p.Number --repo $repo --body "Merged in a train with $($Prs -join ', ') (in that order). The combined tree passed $tested as one, so this branch did not need its own rebase.$(if ($raceRerun) { ' The headless suite needed a race re-run: every first-run failure carried the known harness-fault signature (#192), and those classes then passed alone in a fresh process.' })" | Out-Null
        Write-Host "  merged #$($p.Number) on GitHub"
    }
    Write-Host "== Train merged: $($Prs -join ', ')"
}
finally {
    Remove-Item -LiteralPath $lock -Force -ErrorAction SilentlyContinue
}
